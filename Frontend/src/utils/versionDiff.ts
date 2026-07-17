// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Pure, unit-tested client-side diff between two workflow-version payloads (plan §7.4).
//
// Compares two definitions (the backend NodeDefinition / EdgeDefinition shape that
// getWorkflowVersionDetail returns) and reports what changed, with two hard rules:
//
//   1. BEHAVIORAL vs LAYOUT are separated. Behavioral changes (node added/removed,
//      node type, configuration/properties, connections) are what actually changes
//      execution; layout changes (position x/y, width/height, viewport) are cosmetic.
//      The renderer can collapse the cosmetic set so a node nudge doesn't read as a
//      real edit.
//   2. Comparison is canonical and default-normalized: object keys are sorted, the
//      visual `_metadata` blob is split out of properties, empty/undefined defaults
//      are dropped, and credential identifiers are MASKED before anything is shown.
//
// Edge identity uses the persistent source+output+target+input composite key (the
// plan's source/sourceHandle/target/targetHandle), so reordering edges or churning
// generated edge ids is not reported as a change, and duplicate parallel edges collapse.

import type { NodeDefinition, EdgeDefinition } from '../types';

/** Minimal version-payload shape the diff needs (a full WorkflowVersion satisfies it). */
export interface DiffablePayload {
  nodes: NodeDefinition[];
  edges: EdgeDefinition[];
}

export type ChangeKind = 'added' | 'removed' | 'changed';

/** A single field-level difference inside a changed node's configuration. */
export interface FieldChange {
  path: string;
  before: unknown;
  after: unknown;
}

export interface NodeDiff {
  nodeId: string;
  kind: ChangeKind;
  /** Present when the node type changed (always a behavioral change). */
  typeBefore?: string;
  typeAfter?: string;
  /** Behavioral config field changes (credential ids already masked). */
  fieldChanges: FieldChange[];
  /** True when only layout (_metadata position/size) differs — no behavioral change. */
  layoutOnly: boolean;
}

export interface EdgeDiff {
  key: string;
  kind: ChangeKind;
  source: string;
  sourceHandle: string;
  target: string;
  targetHandle: string;
}

export interface VersionDiff {
  nodes: NodeDiff[];
  edges: EdgeDiff[];
  /** Convenience flags for the renderer / collapse affordance. */
  hasBehavioralChanges: boolean;
  hasLayoutChanges: boolean;
}

const MASK = '••••••••';

// Property keys whose VALUE is a credential/secret identifier and must be masked
// in the displayed diff. Matched case-insensitively against the leaf key name.
const CREDENTIAL_KEY_RE = /(credential|secret|token|apikey|api_key|password|connectionid)/i;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// A value is "empty default" when it carries no information for comparison: null,
// undefined, empty string, empty array, empty object. Normalizing these away means
// `{ retries: 0 }` vs `{ retries: 0, note: '' }` doesn't read as a behavioral change.
function isEmptyDefault(value: unknown): boolean {
  if (value === null || value === undefined || value === '') return true;
  if (Array.isArray(value)) return value.length === 0;
  if (isRecord(value)) return Object.keys(value).length === 0;
  return false;
}

/**
 * Canonicalize an arbitrary properties value: recursively sort object keys, drop
 * empty defaults, and mask any value whose key looks like a credential identifier.
 * The result is a stable, comparable, secret-free structure.
 */
export function canonicalize(value: unknown, keyName?: string): unknown {
  if (keyName && CREDENTIAL_KEY_RE.test(keyName) && typeof value === 'string' && value !== '') {
    return MASK;
  }
  if (Array.isArray(value)) {
    return value.map((item) => canonicalize(item));
  }
  if (isRecord(value)) {
    const out: Record<string, unknown> = {};
    for (const key of Object.keys(value).sort()) {
      if (key === '_metadata') continue; // layout — never part of the behavioral payload
      const canon = canonicalize(value[key], key);
      if (isEmptyDefault(canon)) continue;
      out[key] = canon;
    }
    return out;
  }
  return value;
}

// Stable stringification (canonicalize already sorts keys) for cheap deep-equality.
function stableStringify(value: unknown): string {
  return JSON.stringify(value ?? null);
}

/** Persistent composite edge key: source+output+target+input (plan §7.4). */
export function edgeKey(edge: EdgeDefinition): string {
  return [edge.from?.value ?? '', edge.output ?? '', edge.to?.value ?? '', edge.input ?? ''].join(' ');
}

// Flatten a canonical properties object into dotted leaf paths so config changes
// can be reported field-by-field rather than as one opaque blob.
function flatten(value: unknown, prefix: string, out: Map<string, unknown>): void {
  if (isRecord(value)) {
    for (const key of Object.keys(value)) {
      flatten(value[key], prefix ? `${prefix}.${key}` : key, out);
    }
  } else {
    out.set(prefix, value);
  }
}

function diffProperties(
  before: Record<string, unknown>,
  after: Record<string, unknown>,
): FieldChange[] {
  const beforeFlat = new Map<string, unknown>();
  const afterFlat = new Map<string, unknown>();
  flatten(canonicalize(before), '', beforeFlat);
  flatten(canonicalize(after), '', afterFlat);

  const paths = new Set([...beforeFlat.keys(), ...afterFlat.keys()]);
  const changes: FieldChange[] = [];
  for (const path of [...paths].sort()) {
    const b = beforeFlat.get(path);
    const a = afterFlat.get(path);
    if (stableStringify(b) !== stableStringify(a)) {
      changes.push({ path, before: b, after: a });
    }
  }
  return changes;
}

// Layout signature of a node: only the visual _metadata (position + size).
function layoutSignature(node: NodeDefinition): string {
  const meta = isRecord(node.properties?._metadata) ? node.properties._metadata : {};
  return stableStringify({
    x: meta.x ?? null,
    y: meta.y ?? null,
    width: meta.width ?? null,
    height: meta.height ?? null,
    parentId: meta.parentId ?? null,
  });
}

function nodeMap(payload: DiffablePayload): Map<string, NodeDefinition> {
  return new Map((payload.nodes ?? []).map((n) => [n.id?.value ?? '', n]));
}

/**
 * Diff `left` (e.g. the active version, or the working draft) against `right`.
 * Behavioral and layout changes are reported distinctly so the renderer can let
 * the user collapse the cosmetic layout diffs. Credentials are masked; edge
 * identity is the persistent composite key.
 */
export function diffVersions(left: DiffablePayload, right: DiffablePayload): VersionDiff {
  const leftNodes = nodeMap(left);
  const rightNodes = nodeMap(right);
  const nodeIds = new Set([...leftNodes.keys(), ...rightNodes.keys()]);

  const nodeDiffs: NodeDiff[] = [];
  for (const id of [...nodeIds].sort()) {
    const l = leftNodes.get(id);
    const r = rightNodes.get(id);

    if (l && !r) {
      nodeDiffs.push({ nodeId: id, kind: 'removed', typeBefore: l.type, fieldChanges: [], layoutOnly: false });
      continue;
    }
    if (!l && r) {
      nodeDiffs.push({ nodeId: id, kind: 'added', typeAfter: r.type, fieldChanges: [], layoutOnly: false });
      continue;
    }
    if (!l || !r) continue;

    const typeChanged = l.type !== r.type;
    const fieldChanges = diffProperties(l.properties ?? {}, r.properties ?? {});
    const layoutChanged = layoutSignature(l) !== layoutSignature(r);
    const behavioral = typeChanged || fieldChanges.length > 0;

    if (!behavioral && !layoutChanged) continue;

    nodeDiffs.push({
      nodeId: id,
      kind: 'changed',
      ...(typeChanged ? { typeBefore: l.type, typeAfter: r.type } : {}),
      fieldChanges,
      layoutOnly: !behavioral && layoutChanged,
    });
  }

  const leftEdges = new Map((left.edges ?? []).map((e) => [edgeKey(e), e]));
  const rightEdges = new Map((right.edges ?? []).map((e) => [edgeKey(e), e]));
  const edgeKeys = new Set([...leftEdges.keys(), ...rightEdges.keys()]);

  const edgeDiffs: EdgeDiff[] = [];
  for (const key of [...edgeKeys].sort()) {
    const l = leftEdges.get(key);
    const r = rightEdges.get(key);
    if (l && r) continue; // present on both sides under the same identity → unchanged
    const edge = (l ?? r)!;
    edgeDiffs.push({
      key,
      kind: l ? 'removed' : 'added',
      source: edge.from?.value ?? '',
      sourceHandle: edge.output ?? '',
      target: edge.to?.value ?? '',
      targetHandle: edge.input ?? '',
    });
  }

  const hasBehavioralChanges =
    edgeDiffs.length > 0 || nodeDiffs.some((d) => !d.layoutOnly);
  const hasLayoutChanges = nodeDiffs.some((d) => d.layoutOnly || d.kind === 'changed');

  return { nodes: nodeDiffs, edges: edgeDiffs, hasBehavioralChanges, hasLayoutChanges };
}
