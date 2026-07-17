// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Pure read-only summary of a persisted ConditionLogic for the properties panel (Phase 4). Turns the
// stored logic into human-readable rows (operand A · operator · operand B) so the panel can show what
// the condition does without opening the full editor or running an evaluation. No React, no live
// values — purely structural, and unit-tested.

import type { Combinator } from './conditionEval';
import type { ConditionLogic, PersistedOperand } from './conditionModel';
import type { ConditionLogicTree, PersistedCmpNode, PersistedNode } from './conditionTree';
import { getOperator, isBinary, isListRight } from './operators';

export interface SummaryRow {
  id: string;
  /** Operator glyph (e.g. '=', '>', '∋'); '?' for an unknown operator id. */
  symbol: string;
  /** Operator label (e.g. 'Equals'); falls back to the raw op id when unknown. */
  opLabel: string;
  /** Left operand, display-formatted. */
  a: string;
  /** Right operand, display-formatted; null for unary operators. */
  b: string | null;
}

export interface ConditionSummary {
  comb: Combinator;
  rows: SummaryRow[];
}

export function summarizeLogic(logic: ConditionLogic): ConditionSummary {
  return {
    comb: logic.comb,
    rows: logic.cmps.map((c) => {
      const def = getOperator(c.op);
      return {
        id: c.id,
        symbol: def?.symbol ?? '?',
        opLabel: def?.label ?? c.op,
        a: formatOperand(c.a),
        b: isBinary(c.op) && c.b ? formatRightOperand(c.b, c.op) : null,
      };
    }),
  };
}

// ── v2 tree (Phase 8): a one-line parenthesized expression, e.g. `count > 5 AND (NOT x exists OR y = "z")`. ──

/** Render a persisted v2 logic tree as a single human-readable boolean expression with precedence parens. */
export function summarizeTree(root: PersistedNode): string {
  return renderNode(root, false);
}

/**
 * One-line summary of a persisted `logic` blob (v1 OR v2) for the canvas card subtitle — e.g.
 * `counter > 4` or `a = b AND (NOT c exists)`. Returns null when there is no valid logic (the caller
 * then shows the legacy summary or "Not configured" instead of the meaningless `left Equal right`).
 */
export function summarizeConditionLine(logicRaw: unknown): string | null {
  const logic = readLogic(logicRaw);
  if (!logic) return null;
  if (logic.version === 2) return summarizeTree(logic.root);
  const s = summarizeLogic(logic);
  return s.rows
    .map((r) => (r.b !== null ? `${r.a} ${r.symbol} ${r.b}` : `${r.a} ${r.opLabel}`))
    .join(` ${s.comb.toUpperCase()} `);
}

/**
 * Like {@link summarizeConditionLine}, but split into LINES at the top-level AND/OR connectors (one
 * comparator/branch per line) so the canvas card can break at `AND`/`OR` instead of mid-expression. The
 * connector is kept as a prefix on each line after the first (e.g. `['counter ∈ {…}', 'AND 3 = 5']`).
 * Nested groups stay inline (parenthesized) on their own line. Returns null when there's no valid logic.
 */
export function summarizeConditionLines(logicRaw: unknown): string[] | null {
  const logic = readLogic(logicRaw);
  if (!logic) return null;
  if (logic.version === 2) return treeLines(logic.root);
  const comb = logic.comb.toUpperCase();
  return logic.cmps.map((c, i) => {
    const expr = leafExprWrapped(c.a, c.op, c.b);
    return i === 0 ? expr : `${comb} ${expr}`;
  });
}

// Top-level lines for a v2 tree: a group breaks at its own AND/OR (each child a line); a bare
// comparator or NOT root is a single line. A comparator child renders via the wrapping canvas leaf
// renderer; nested groups stay inline (parenthesized) via the shared renderNode, so only the OUTERMOST
// connector becomes a line break.
function treeLines(root: PersistedNode): string[] {
  if (root.kind === 'cmp') return [leafExprWrapped(root.a, root.op, root.b)];
  if (root.kind === 'not') return [`NOT ${renderNode(root.child, true)}`];
  const op = root.op === 'and' ? 'AND' : 'OR';
  return root.children.map((c, i) => {
    const expr = c.kind === 'cmp' ? leafExprWrapped(c.a, c.op, c.b) : renderNode(c, true);
    return i === 0 ? expr : `${op} ${expr}`;
  });
}

// Canvas-only: how many list elements per line before a membership set wraps. A big enumeration then
// grows the card downward (a few short rows) instead of into one very wide line. The structured
// properties panel keeps the single-line `{…}` (it uses formatRightOperand, untouched).
const SET_WRAP_EVERY = 5;
const SET_WRAP_INDENT = '    '; // continuation rows are indented so the set reads as one block
function formatListWrapped(value: string): string {
  const items = value.split(',').map((s) => s.trim()).filter(Boolean);
  if (items.length <= SET_WRAP_EVERY) return `{${items.join(', ')}}`;
  const rows: string[] = [];
  for (let i = 0; i < items.length; i += SET_WRAP_EVERY) rows.push(items.slice(i, i + SET_WRAP_EVERY).join(', '));
  const body = rows.map((r, i) => (i === 0 ? r : SET_WRAP_INDENT + r)).join(',\n');
  return `{${body}}`;
}

// Like renderLeaf, but wraps a long membership list across lines (canvas cards only).
function leafExprWrapped(a: PersistedOperand, op: string, b: PersistedOperand | undefined): string {
  const def = getOperator(op);
  const aStr = formatOperand(a);
  if (isBinary(op) && b) {
    const bStr = isListRight(op) && b.kind === 'lit' ? formatListWrapped(String(b.value)) : formatRightOperand(b, op);
    return `${aStr} ${def?.symbol ?? op} ${bStr}`;
  }
  return `${aStr} ${def?.label ?? op}`;
}

// Read a persisted logic value (stored object or stringified JSON), validating it's a v1/v2 blob.
function readLogic(raw: unknown): ConditionLogic | ConditionLogicTree | null {
  let obj: unknown = raw;
  if (typeof raw === 'string') {
    if (!raw.trim()) return null;
    try {
      obj = JSON.parse(raw);
    } catch {
      return null;
    }
  }
  if (obj && typeof obj === 'object' && 'version' in obj) {
    const v = (obj as { version?: unknown }).version;
    if (v === 1 || v === 2) return obj as ConditionLogic | ConditionLogicTree;
  }
  return null;
}

// `parenthesizeGroup` ⇒ this node, if a group, is nested under another group/not and needs wrapping.
function renderNode(node: PersistedNode, parenthesizeGroup: boolean): string {
  if (node.kind === 'cmp') return renderLeaf(node);
  if (node.kind === 'not') return `NOT ${renderNode(node.child, true)}`;
  const op = node.op === 'and' ? 'AND' : 'OR';
  const inner = node.children.map((c) => renderNode(c, true)).join(` ${op} `);
  return parenthesizeGroup ? `(${inner})` : inner;
}

function renderLeaf(node: PersistedCmpNode): string {
  const def = getOperator(node.op);
  const a = formatOperand(node.a);
  if (isBinary(node.op) && node.b) {
    return `${a} ${def?.symbol ?? node.op} ${formatRightOperand(node.b, node.op)}`;
  }
  return `${a} ${def?.label ?? node.op}`;
}

function formatOperand(operand: PersistedOperand): string {
  if (operand.kind === 'ref') return shortRefPath(operand.ref);
  // literal: quote strings, render number/boolean bare.
  return operand.type === 'string' ? `"${operand.value}"` : String(operand.value);
}

// The B operand of a list-right op ('Is one of' …) is a comma-separated set held as string text;
// render it as `{a, b, c}` so the summary reads `x ∈ {4, 2, 3}` rather than the misleading `x ∈ "4, 2, 3"`.
function formatRightOperand(operand: PersistedOperand, op: string): string {
  if (!isListRight(op) || operand.kind === 'ref') return formatOperand(operand);
  const items = String(operand.value).split(',').map((s) => s.trim()).filter(Boolean);
  return `{${items.join(', ')}}`;
}

// Strip the `{{ … }}` / `$variables.` / `$node.<id>.output.` ceremony to a short field path for
// display (mirrors conditionFlow.refLabel — kept local so the summary has no flow-builder dependency).
function shortRefPath(ref: string): string {
  const inner = ref.trim().replace(/^\{\{\s*/, '').replace(/\s*\}\}$/, '');
  const m = /^\$node\.[^.]+\.output\.(.+)$/.exec(inner) ?? /^\$variables\.(.+)$/.exec(inner);
  return (m ? m[1] : inner) || '—';
}
