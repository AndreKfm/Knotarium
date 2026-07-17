// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Pure builder: an evaluated draft TREE → the node + edge DATA the flow editor renders on
// @xyflow/react, auto-laid-out left→right with Dagre (Phase 8 redesign, N1 auto-laid-out dataflow).
// Recursive: comparator leaves wire into their parent group/NOT, nested groups wire into their parent,
// and the root wires into the TRUE/FALSE output. Per-node status (from evaluateDraftTreeNodes) colors
// each outgoing boolean wire. No React Flow dependency here — fully unit-testable.

import dagre from '@dagrejs/dagre';
import type { Combinator, ConditionStatus, OperandType, ResolvedOperand } from './conditionEval';
import { evaluateDraftTreeNodes, toResolvedOperand, type PreviewValueProvider } from './conditionPreview';
import type { DraftCmpNode, DraftNode, DraftTree } from './conditionTree';
import type { DraftOperand } from './conditionModel';
import { getOperator, isBinary, isListRight } from './operators';
import type { KnownType } from './operatorFilter';

export type FlowNodeKind = 'input' | 'comparator' | 'group' | 'not' | 'output' | 'placeholder';
// 'awaiting' = Incomplete ONLY because a configured reference has no design-time value yet (runtime
// signal field, no sample / no last run). The condition is valid and runs; the preview just can't show
// true/false. Rendered as a calm "runtime" state, distinct from a genuinely-unwired 'incomplete'.
export type WireStatus = 'true' | 'false' | 'incomplete' | 'awaiting' | 'error' | 'neutral';

export interface InputNodeData {
  kind: 'input';
  cmpId: string;
  slot: 'a' | 'b';
  variant: 'ref' | 'lit';
  label: string;
  badge: string;
  valueType: OperandType;
  typeColor: string;
  operand: DraftOperand;
  /** True when this is the B operand of a list-right op ('Is one of' …) — edited as a comma list. */
  isList: boolean;
}
export interface ComparatorNodeData {
  kind: 'comparator';
  cmpId: string;
  op: string;
  symbol: string;
  label: string;
  status: ConditionStatus;
  /** Incomplete only because a configured ref has no design-time value → present as "runtime", not broken. */
  awaiting?: boolean;
  leftType: KnownType;
  rightType: KnownType;
}
export interface GroupNodeData {
  kind: 'group';
  id: string;
  op: Combinator;
  status: ConditionStatus;
  awaiting?: boolean;
  childCount: number;
}
export interface NotNodeData {
  kind: 'not';
  id: string;
  status: ConditionStatus;
  awaiting?: boolean;
}
export interface OutputNodeData {
  kind: 'output';
  status: ConditionStatus;
  awaiting?: boolean;
}
export interface PlaceholderNodeData {
  kind: 'placeholder';
}
export type FlowNodeData =
  | InputNodeData
  | ComparatorNodeData
  | GroupNodeData
  | NotNodeData
  | OutputNodeData
  | PlaceholderNodeData;

export interface FlowNode {
  id: string;
  kind: FlowNodeKind;
  x: number;
  y: number;
  width: number;
  height: number;
  data: FlowNodeData;
}
export interface FlowEdge {
  id: string;
  source: string;
  target: string;
  wire: 'value' | 'boolean';
  status: WireStatus;
  typeColor?: string;
  label: string | null;
}
export interface ConditionTreeFlow {
  nodes: FlowNode[];
  edges: FlowEdge[];
}

// Node sizes (for Dagre); must match the components' rendered footprints so nodes don't overlap.
const SIZE: Record<FlowNodeKind, { w: number; h: number }> = {
  input: { w: 204, h: 44 },
  comparator: { w: 150, h: 118 },
  // Taller since the operator became a hero word (+ the "switch" caption). The output height is kept
  // EQUAL to this on purpose: the output-alignment math centers the output on the group, and if one
  // node is measured while the other still uses this estimate, mismatched heights slope the final wire.
  group: { w: 160, h: 152 },
  not: { w: 108, h: 74 },
  // Height matches the group's so the output-alignment math lines them up even when measurement of the
  // real rendered size doesn't kick in (the two render at ~the same height).
  output: { w: 210, h: 152 },
  placeholder: { w: 214, h: 150 },
};

const TYPE_COLORS: Record<string, string> = {
  string: '#34d399',
  number: '#22d3ee',
  boolean: '#f0b429',
  object: '#7c6cf0',
  array: '#38bdf8',
  any: '#8593a6',
};
export function typeColor(type: string): string {
  return TYPE_COLORS[type] ?? TYPE_COLORS.any;
}

function statusToWire(status: ConditionStatus): WireStatus {
  return status === 'True' ? 'true' : status === 'False' ? 'false' : status === 'Incomplete' ? 'incomplete' : 'error';
}
function booleanLabel(status: ConditionStatus): string {
  return status === 'True' ? 'true' : status === 'False' ? 'false' : status === 'Incomplete' ? 'incomplete' : 'error';
}

/**
 * Per-node flag: is this node's `Incomplete` caused ONLY by configured references awaiting a runtime
 * value (→ true, present as "runtime"), as opposed to a genuinely-unwired operand/empty group (→ false,
 * the real "incomplete")? Only meaningful where status is 'Incomplete'. Mirrors the evaluator's
 * required-operand set (binary → a,b; unary → a) and folds up: a group/not is awaiting iff every
 * Incomplete descendant is awaiting and none is genuinely unwired.
 */
function computeAwaiting(
  draft: DraftTree,
  status: Record<string, ConditionStatus>,
  provider: PreviewValueProvider,
): Record<string, boolean> {
  const out: Record<string, boolean> = {};
  const walk = (node: DraftNode): 'awaiting' | 'genuine' | 'ok' => {
    if (node.kind === 'cmp') {
      if (status[node.id] !== 'Incomplete') return (out[node.id] = false), 'ok';
      const operands: DraftOperand[] = isBinary(node.op) && node.b ? [node.a, node.b] : [node.a];
      let anyAwaiting = false;
      let anyGenuine = false;
      for (const op of operands) {
        if (toResolvedOperand(op, provider).state !== 'unset') continue;
        // A non-empty ref with no design-time value is benign (runtime); an empty literal or empty ref
        // is the author genuinely not having finished wiring the operand.
        if (op.kind === 'ref' && op.ref.trim().length > 0) anyAwaiting = true;
        else anyGenuine = true;
      }
      const awaiting = anyAwaiting && !anyGenuine;
      return (out[node.id] = awaiting), awaiting ? 'awaiting' : 'genuine';
    }
    if (node.kind === 'group') {
      const childResults = node.children.map(walk); // always recurse so descendants get flagged
      if (status[node.id] !== 'Incomplete') return (out[node.id] = false), 'ok';
      if (node.children.length === 0) return (out[node.id] = false), 'genuine';
      const awaiting = childResults.includes('awaiting') && !childResults.includes('genuine');
      return (out[node.id] = awaiting), awaiting ? 'awaiting' : 'genuine';
    }
    // not — transparent to the runtime/genuine distinction (B8: it propagates Incomplete).
    const child = walk(node.child);
    if (status[node.id] !== 'Incomplete') return (out[node.id] = false), 'ok';
    const awaiting = child === 'awaiting';
    return (out[node.id] = awaiting), awaiting ? 'awaiting' : 'genuine';
  };
  if (draft.root) walk(draft.root);
  return out;
}

export function buildConditionTreeFlow(
  draft: DraftTree,
  provider: PreviewValueProvider,
  liveLabels = true,
): ConditionTreeFlow {
  const nodes: FlowNode[] = [];
  const edges: FlowEdge[] = [];
  const { outcome, status } = evaluateDraftTreeNodes(draft, provider);
  const awaiting = computeAwaiting(draft, status, provider);

  if (!draft.root) {
    push(nodes, 'placeholder', 'placeholder', { kind: 'placeholder' });
    push(nodes, 'out', 'output', { kind: 'output', status: outcome.status });
    edges.push(boolEdge('placeholder', 'out', outcome.status, false, liveLabels));
    return layout({ nodes, edges });
  }

  emit(draft.root, 'out', nodes, edges, provider, status, awaiting, liveLabels);
  const rootAwaiting = outcome.status === 'Incomplete' && !!awaiting[draft.root.id];
  push(nodes, 'out', 'output', { kind: 'output', status: outcome.status, awaiting: rootAwaiting });
  return layout({ nodes, edges });
}

// Emits the node(s) for `node`, wires its boolean output into `parentId`, and recurses. Returns nothing
// — the caller already knows the child id pattern; statuses come from the precomputed `status` map.
function emit(
  node: DraftNode,
  parentId: string,
  nodes: FlowNode[],
  edges: FlowEdge[],
  provider: PreviewValueProvider,
  status: Record<string, ConditionStatus>,
  awaiting: Record<string, boolean>,
  liveLabels: boolean,
): void {
  const st = status[node.id] ?? 'Incomplete';
  const aw = !!awaiting[node.id];

  if (node.kind === 'cmp') {
    const cmpId = `cmp:${node.id}`;
    emitInputs(node, nodes, edges, provider, liveLabels);
    const def = getOperator(node.op);
    push(nodes, cmpId, 'comparator', {
      kind: 'comparator',
      cmpId: node.id,
      op: node.op,
      symbol: def?.symbol ?? '?',
      label: def?.label ?? node.op,
      status: st,
      awaiting: aw,
      leftType: node.a.type,
      rightType: isBinary(node.op) && node.b ? node.b.type : 'any',
    });
    edges.push(boolEdge(cmpId, parentId, st, aw, liveLabels));
    return;
  }

  if (node.kind === 'group') {
    const gId = `group:${node.id}`;
    push(nodes, gId, 'group', { kind: 'group', id: node.id, op: node.op, status: st, awaiting: aw, childCount: node.children.length });
    edges.push(boolEdge(gId, parentId, st, aw, liveLabels));
    for (const child of node.children) emit(child, gId, nodes, edges, provider, status, awaiting, liveLabels);
    return;
  }

  // not
  const nId = `not:${node.id}`;
  push(nodes, nId, 'not', { kind: 'not', id: node.id, status: st, awaiting: aw });
  edges.push(boolEdge(nId, parentId, st, aw, liveLabels));
  emit(node.child, nId, nodes, edges, provider, status, awaiting, liveLabels);
}

function emitInputs(
  node: DraftCmpNode,
  nodes: FlowNode[],
  edges: FlowEdge[],
  provider: PreviewValueProvider,
  liveLabels: boolean,
): void {
  const slots: ('a' | 'b')[] = isBinary(node.op) && node.b ? ['a', 'b'] : ['a'];
  for (const slot of slots) {
    const operand = slot === 'a' ? node.a : (node.b as DraftOperand);
    const id = `in:${node.id}:${slot}`;
    const isList = slot === 'b' && isListRight(node.op);
    const rop = toResolvedOperand(operand, provider);
    const badge = valueBadge(rop);
    push(nodes, id, 'input', {
      kind: 'input',
      cmpId: node.id,
      slot,
      variant: operand.kind,
      label:
        operand.kind === 'ref'
          ? refLabel(operand.ref)
          : isList
            ? listLabel(operand.text)
            : literalLabel(operand.type, operand.text),
      badge,
      valueType: operand.type,
      typeColor: typeColor(operand.type),
      operand,
      isList,
    });
    edges.push({
      id: `e:${id}->cmp:${node.id}`,
      source: id,
      target: `cmp:${node.id}`,
      wire: 'value',
      status: 'neutral',
      typeColor: typeColor(operand.type),
      // Only label a value wire when it actually carries a value — a "—" on every unset wire is noise.
      // Clamp it so a long list value ("3, 4, 5, …") doesn't sprawl across the canvas on the wire.
      label: liveLabels && badge !== '—' ? clampLabel(badge) : null,
    });
  }
}

function boolEdge(source: string, target: string, status: ConditionStatus, awaiting: boolean, liveLabels: boolean): FlowEdge {
  // An awaiting node's wire reads "runtime" (calm) rather than "incomplete" (looks broken).
  const wireStatus: WireStatus = awaiting ? 'awaiting' : statusToWire(status);
  const label = awaiting ? 'runtime' : booleanLabel(status);
  return {
    id: `e:${source}->${target}`,
    source,
    target,
    wire: 'boolean',
    status: wireStatus,
    label: liveLabels ? label : null,
  };
}

function push(nodes: FlowNode[], id: string, kind: FlowNodeKind, data: FlowNodeData): void {
  const { w, h } = SIZE[kind];
  nodes.push({ id, kind, x: 0, y: 0, width: w, height: h, data });
}

// Dagre LR auto-layout: assigns each node a top-left x/y from its center, leaving the recursion above
// purely about graph structure.
function layout(flow: ConditionTreeFlow): ConditionTreeFlow {
  const g = new dagre.graphlib.Graph();
  g.setGraph({ rankdir: 'LR', nodesep: 28, ranksep: 64, marginx: 16, marginy: 16 });
  g.setDefaultEdgeLabel(() => ({}));
  for (const n of flow.nodes) g.setNode(n.id, { width: n.width, height: n.height });
  for (const e of flow.edges) g.setEdge(e.source, e.target);
  dagre.layout(g);
  const positioned = flow.nodes.map((n) => {
    const p = g.node(n.id);
    return { ...n, x: p.x - n.width / 2, y: p.y - n.height / 2 };
  });

  // Dagre orders same-rank nodes by its own heuristic, so a comparator's two inputs can land B-above-A —
  // which reads backwards ("A > B" should be top > bottom). Pin operand A above operand B for every
  // comparator by swapping their Y when needed.
  const byId = new Map(positioned.map((n) => [n.id, n]));
  for (const n of positioned) {
    if (n.kind !== 'comparator') continue;
    const cmpId = n.id.slice('cmp:'.length);
    const a = byId.get(`in:${cmpId}:a`);
    const b = byId.get(`in:${cmpId}:b`);
    if (a && b && a.y > b.y) {
      const t = a.y;
      a.y = b.y;
      b.y = t;
    }
  }

  // Boolean-children map, in EMIT (= authored tree) order, since edges were pushed during the tree walk.
  const childrenOf = new Map<string, string[]>();
  for (const e of flow.edges) {
    if (e.wire !== 'boolean') continue;
    const arr = childrenOf.get(e.target);
    if (arr) arr.push(e.source);
    else childrenOf.set(e.target, [e.source]);
  }

  // Restore AUTHORED vertical order. Dagre orders same-rank nodes by its own crossing heuristic, so two
  // sibling comparators can land in the reverse of the order they were written — which silently relabels
  // them (the summary letters A/B follow the visual top-to-bottom order) and reads as "wrong" names. For
  // every parent, re-stack its children's whole sub-clusters (each child + everything feeding it, incl.
  // input cards) top-to-bottom in tree order, keeping each band's height and the existing gaps so spacing
  // is untouched. Disjoint across siblings (it's a tree), so order of processing parents doesn't matter.
  const sourcesByTarget = new Map<string, string[]>();
  for (const e of flow.edges) {
    const arr = sourcesByTarget.get(e.target);
    if (arr) arr.push(e.source);
    else sourcesByTarget.set(e.target, [e.source]);
  }
  const clusterOf = (id: string): string[] => {
    const set = new Set([id]);
    const stack = [id];
    while (stack.length) {
      const cur = stack.pop() as string;
      for (const s of sourcesByTarget.get(cur) ?? []) if (!set.has(s)) { set.add(s); stack.push(s); }
    }
    return [...set];
  };
  for (const kids of childrenOf.values()) {
    if (kids.length < 2) continue;
    const bands = kids.map((k) => {
      const cluster = clusterOf(k);
      let top = Infinity, bottom = -Infinity;
      for (const id of cluster) {
        const n = byId.get(id);
        if (!n) continue;
        top = Math.min(top, n.y);
        bottom = Math.max(bottom, n.y + n.height);
      }
      return { cluster, top, bottom, height: bottom - top };
    });
    const sorted = [...bands].sort((p, q) => p.top - q.top);
    if (sorted.every((b, i) => b === bands[i])) continue; // already authored order
    const gaps: number[] = [];
    for (let i = 0; i < sorted.length - 1; i++) gaps.push(sorted[i + 1].top - sorted[i].bottom);
    let cursor = sorted[0].top;
    bands.forEach((band, i) => {
      const delta = cursor - band.top;
      if (delta !== 0) for (const id of band.cluster) { const n = byId.get(id); if (n) n.y += delta; }
      cursor += band.height + (gaps[i] ?? 0);
    });
  }

  // Vertical centering of the combiner column. Dagre drifts a tall parent off the midpoint of the
  // children feeding it, so the right side sits high/low of the inputs. Center every combiner (group/NOT)
  // on the vertical midpoint of its boolean children, BOTTOM-UP (so nested groups settle before their
  // parent), then center the output terminal on the root — comparator → combiner → output reads as one
  // straight, balanced spine regardless of nesting.
  const centerOnChildren = (id: string, seen: Set<string>): void => {
    if (seen.has(id)) return;
    seen.add(id);
    const kids = childrenOf.get(id);
    if (!kids || kids.length === 0) return; // a comparator leaf — keep its Dagre y
    for (const k of kids) centerOnChildren(k, seen);
    const node = byId.get(id);
    if (!node) return;
    const centers = kids.map((k) => byId.get(k)).filter((k): k is NonNullable<typeof k> => !!k).map((k) => k.y + k.height / 2);
    if (centers.length === 0) return;
    const mid = centers.reduce((s, y) => s + y, 0) / centers.length;
    node.y = mid - node.height / 2;
  };

  const out = byId.get('out');
  const feeder = flow.edges.find((e) => e.target === 'out');
  const src = feeder ? byId.get(feeder.source) : undefined;
  if (feeder) centerOnChildren(feeder.source, new Set());
  if (out && src) {
    out.y = src.y + src.height / 2 - out.height / 2;
  }
  return { nodes: positioned, edges: flow.edges };
}

/**
 * Re-run the layout using each node's REAL rendered size (from React Flow's measurement) instead of the
 * estimated SIZE map — so centers align and wires stay straight regardless of card content. Nodes
 * without a measurement keep their estimated size.
 */
export function relayoutFlow(
  flow: ConditionTreeFlow,
  measured: Map<string, { width: number; height: number }>,
): ConditionTreeFlow {
  const nodes = flow.nodes.map((n) => {
    const m = measured.get(n.id);
    return m && m.width > 0 && m.height > 0 ? { ...n, width: m.width, height: m.height } : n;
  });
  return layout({ nodes, edges: flow.edges });
}

// ── display helpers ──

// The DISTINCTIVE tail of a ref. Boilerplate prefixes (`signal.params.`, `$node.X.output.`,
// `$variables.`) are the same across every operand and just eat the fixed card width, ellipsizing the one
// part that identifies the variable. Show only the last path segment; the full path stays on the node's
// hover tooltip (see InputNode). `refPath` exposes that full (brace-stripped) path for callers.
export function refPath(ref: string): string {
  return ref.trim().replace(/^\{\{\s*/, '').replace(/\s*\}\}$/, '');
}
function refLabel(ref: string): string {
  const inner = refPath(ref);
  const m = /^\$node\.[^.]+\.output\.(.+)$/.exec(inner) ?? /^\$variables\.(.+)$/.exec(inner);
  const path = m ? m[1] : inner;
  const tail = path.split('.').filter(Boolean).pop();
  return tail || path || '—';
}
function literalLabel(type: OperandType, text: string): string {
  if (text.trim().length === 0) return '—';
  return type === 'string' ? `"${text}"` : text;
}
// A list operand reads as a set: comma-split, trimmed, rendered `{a, b, c}` so it doesn't look like a
// single quoted string (which is what confused authors entering "0, 2, 3").
function listLabel(text: string): string {
  const items = text.split(',').map((s) => s.trim()).filter(Boolean);
  return items.length === 0 ? '—' : `{${items.join(', ')}}`;
}
// Edge value labels float on the wire with no container to clip them, so cap long values (e.g. a big
// membership list) with an ellipsis. The card badge keeps the full value (CSS-clipped + tooltip).
const MAX_EDGE_LABEL = 18;
function clampLabel(text: string): string {
  return text.length > MAX_EDGE_LABEL ? `${text.slice(0, MAX_EDGE_LABEL - 1)}…` : text;
}
function valueBadge(rop: ResolvedOperand | null): string {
  if (!rop || rop.state !== 'value') return '—';
  const raw = rop.raw;
  if (raw === null || raw === undefined) return 'null';
  if (typeof raw === 'string') return `"${raw}"`;
  if (typeof raw === 'boolean') return raw ? 'true' : 'false';
  if (typeof raw === 'number') return String(raw);
  if (Array.isArray(raw)) return `[${raw.length}]`;
  return '{…}';
}
