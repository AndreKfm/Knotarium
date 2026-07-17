// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Bridge between the editor's draft model and the pure evaluator, for the always-on LIVE preview.
// Turns a DraftCondition + a value provider into a ResolvedCondition that evaluateCondition can run.
//
// The provider abstracts WHERE preview values come from: Phase 3 backs it with a manual-sample map;
// Phase 5/6 swap in last-run / dry-run resolution without touching this bridge.
//
// Key rule — a missing reference is source-dependent (this is the "what you see == what runs" seam):
//   • manual source: you simply haven't typed a sample yet → benign → Incomplete (state 'unset').
//   • authoritative source (last run / dry run): a ref genuinely absent from a real run is the direct
//     predictor of runtime RESOLUTION_FAILED → fail-node. Surfacing it as Incomplete would show
//     "incomplete → falls to false" while the published workflow hard-fails — a divergence sitting
//     exactly on the safety-critical case. So an authoritative miss → 'unresolved' → RESOLUTION_FAILED.
// The provider reports which kind of miss it is via PreviewResolution.authoritativeMiss. Literals hand
// their value through so the evaluator's own coercion surfaces COERCION_FAILED exactly as at run time.

import {
  evaluateComparator,
  evaluateCondition,
  evaluateTree,
  parseBool,
  parseInvariantNumber,
  type ComparatorResult,
  type ConditionError,
  type ConditionOutcome,
  type ConditionStatus,
  type OperandType,
  type ResolvedCondition,
  type ResolvedComparator,
  type ResolvedLogicNode,
  type ResolvedOperand,
} from './conditionEval';
import type { DraftCondition, DraftLiteral, DraftOperand } from './conditionModel';
import type { DraftNode, DraftTree } from './conditionTree';
import { isBinary } from './operators';

export interface PreviewResolution {
  /** False ⇒ this reference has no value from the current source. */
  found: boolean;
  /** The sample value (may legitimately be null). Ignored when found is false. */
  value?: unknown;
  /**
   * Only meaningful when `found` is false. True ⇒ an AUTHORITATIVE source (last run / dry run)
   * genuinely lacked this ref → preview it as RESOLUTION_FAILED (it will fail-node at runtime).
   * False/undefined ⇒ a benign not-yet-sampled miss (manual source) → Incomplete.
   */
  authoritativeMiss?: boolean;
}

/** Resolves a reference operand to a preview value. `type` is the operand's declared type (a hint). */
export type PreviewValueProvider = (ref: string, type: OperandType) => PreviewResolution;

// Hand the evaluator the typed literal where parseable; otherwise the raw text, so the evaluator's
// coercion produces the same COERCION_FAILED the runtime would (empty text is handled by the caller).
function literalRaw(operand: DraftLiteral): unknown {
  switch (operand.type) {
    case 'string':
      return operand.text;
    case 'number': {
      const p = parseInvariantNumber(operand.text);
      return p.ok ? p.value : operand.text;
    }
    case 'boolean': {
      const p = parseBool(operand.text);
      return p.ok ? p.value : operand.text;
    }
  }
}

export function toResolvedOperand(operand: DraftOperand, provider: PreviewValueProvider): ResolvedOperand {
  if (operand.kind === 'lit') {
    if (operand.text.trim().length === 0) return { type: operand.type, state: 'unset', raw: null };
    return { type: operand.type, state: 'value', raw: literalRaw(operand) };
  }

  // reference
  const ref = operand.ref.trim();
  if (ref.length === 0) return { type: operand.type, state: 'unset', raw: null };

  const res = provider(ref, operand.type);
  if (!res.found) {
    // Benign manual miss → Incomplete; authoritative (last-run/dry-run) miss → RESOLUTION_FAILED.
    return { type: operand.type, state: res.authoritativeMiss ? 'unresolved' : 'unset', raw: null };
  }
  return { type: operand.type, state: 'value', raw: res.value ?? null };
}

/** Build a ResolvedCondition (evaluator input) from a draft, resolving references via the provider. */
export function toResolvedCondition(draft: DraftCondition, provider: PreviewValueProvider): ResolvedCondition {
  return {
    comb: draft.comb,
    comparators: draft.cmps.map<ResolvedComparator>((c) => ({
      id: c.id,
      op: c.op,
      a: toResolvedOperand(c.a, provider),
      b: isBinary(c.op) && c.b ? toResolvedOperand(c.b, provider) : null,
    })),
  };
}

/** Convenience: resolve + evaluate a draft in one call for the live preview. */
export function evaluateDraft(draft: DraftCondition, provider: PreviewValueProvider): ConditionOutcome {
  return evaluateCondition(toResolvedCondition(draft, provider));
}

// ── v2 tree preview (Phase 8): same operand-resolution rule, recursed over the draft tree. ──

/** Build a resolved tree (evaluator input) from a draft node, resolving references via the provider. */
export function toResolvedTree(node: DraftNode, provider: PreviewValueProvider): ResolvedLogicNode {
  switch (node.kind) {
    case 'cmp':
      return {
        kind: 'cmp',
        comparator: {
          id: node.id,
          op: node.op,
          a: toResolvedOperand(node.a, provider),
          b: isBinary(node.op) && node.b ? toResolvedOperand(node.b, provider) : null,
        },
      };
    case 'group':
      return { kind: 'group', op: node.op, children: node.children.map((c) => toResolvedTree(c, provider)) };
    case 'not':
      return { kind: 'not', child: toResolvedTree(node.child, provider) };
  }
}

/** Resolve + evaluate a draft TREE in one call for the live preview. Empty tree → Incomplete. */
export function evaluateDraftTree(draft: DraftTree, provider: PreviewValueProvider): ConditionOutcome {
  if (!draft.root) return { status: 'Incomplete', error: null, comparators: [] };
  return evaluateTree(toResolvedTree(draft.root, provider));
}

/**
 * Like {@link evaluateDraftTree}, but also returns the folded status of EVERY draft node keyed by its
 * id (leaves + groups + nots) — the flow editor needs each node's status to color its outgoing wire.
 * Mirrors the recursion in ConditionEvaluator.EvaluateTree (B8/B9); the resolved tree drops ids, so we
 * walk the id-bearing draft directly.
 */
export function evaluateDraftTreeNodes(
  draft: DraftTree,
  provider: PreviewValueProvider,
): { outcome: ConditionOutcome; status: Record<string, ConditionStatus> } {
  const status: Record<string, ConditionStatus> = {};
  if (!draft.root) {
    return { outcome: { status: 'Incomplete', error: null, comparators: [] }, status };
  }
  const leaves: ComparatorResult[] = [];
  const root = walkNodeStatus(draft.root, provider, status, leaves);
  return { outcome: { status: root.status, error: root.error, comparators: leaves }, status };
}

function walkNodeStatus(
  node: DraftNode,
  provider: PreviewValueProvider,
  status: Record<string, ConditionStatus>,
  leaves: ComparatorResult[],
): { status: ConditionStatus; error: ConditionError | null } {
  if (node.kind === 'cmp') {
    const r = evaluateComparator({
      id: node.id,
      op: node.op,
      a: toResolvedOperand(node.a, provider),
      b: isBinary(node.op) && node.b ? toResolvedOperand(node.b, provider) : null,
    });
    leaves.push(r);
    status[node.id] = r.status;
    return { status: r.status, error: r.error };
  }
  if (node.kind === 'not') {
    const c = walkNodeStatus(node.child, provider, status, leaves);
    const s: ConditionStatus = c.status === 'True' ? 'False' : c.status === 'False' ? 'True' : c.status;
    status[node.id] = s;
    return { status: s, error: c.status === 'Error' ? c.error : null };
  }
  // group — strict dominance (B9): Error → Incomplete → boolean fold.
  const outs = node.children.map((child) => walkNodeStatus(child, provider, status, leaves));
  let s: ConditionStatus;
  let error: ConditionError | null = null;
  const firstError = outs.find((o) => o.status === 'Error');
  if (node.children.length === 0) {
    s = 'Incomplete';
  } else if (firstError) {
    s = 'Error';
    error = firstError.error;
  } else if (outs.some((o) => o.status === 'Incomplete')) {
    s = 'Incomplete';
  } else {
    const value = node.op === 'and' ? outs.every((o) => o.status === 'True') : outs.some((o) => o.status === 'True');
    s = value ? 'True' : 'False';
  }
  status[node.id] = s;
  return { status: s, error };
}
