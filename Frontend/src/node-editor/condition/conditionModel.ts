// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// The editor's Condition models and the pure transforms between them. Three shapes:
//   • DraftCondition  — what the editor edits: textual, possibly incomplete (literals held as strings).
//   • ConditionLogic  — the PERSISTED runtime model (mirror of the backend ConditionLogic records;
//                       Backend/Knotarium.Features/Nodes/Condition/ConditionLogic.cs). Literals typed.
//   • Legacy { left, operator, right } — the old single-comparator node, seeded best-effort on open.
//
// All functions here are pure. Coercion (draft→persisted) uses the SAME literal-parsing rules as the
// runtime evaluator (imported from conditionEval) so a literal the editor accepts can't fail at run
// time. Schema limits mirror ConditionLogicParser.cs.

import { parseBool, parseInvariantNumber, type Combinator, type OperandType } from './conditionEval';
import { isBinary, isKnownOperator, isListRight } from './operators';

// Schema limits — mirror ConditionLogicParser.cs (kept in sync by intent; the backend parser is the
// authoritative gate, these give the editor early, matching feedback).
export const MAX_COMPARATORS = 50;
export const MAX_LITERAL_LENGTH = 10_000;
export const MAX_REF_LENGTH = 2_000;
export const MAX_REGEX_LENGTH = 512;

// ── Draft model (editor; textual, possibly incomplete) ──

export type OperandKind = 'lit' | 'ref';

/** A literal operand mid-edit: its declared type plus the raw text (coerced to a typed value on Save). */
export interface DraftLiteral {
  kind: 'lit';
  type: OperandType;
  text: string;
}

/** A reference operand: a variable expression (`{{ … }}`) or variable name, resolved task-side (D7). */
export interface DraftReference {
  kind: 'ref';
  type: OperandType;
  ref: string;
}

export type DraftOperand = DraftLiteral | DraftReference;

export interface DraftComparator {
  id: string;
  op: string;
  a: DraftOperand;
  b: DraftOperand | null; // null for unary operators (spec §2 — b ignored)
}

export interface DraftCondition {
  comb: Combinator;
  cmps: DraftComparator[];
}

// ── Persisted model (mirror of backend ConditionLogic) ──

export interface PersistedLiteral {
  kind: 'lit';
  type: OperandType;
  value: string | number | boolean;
}

export interface PersistedReference {
  kind: 'ref';
  type: OperandType;
  ref: string;
}

export type PersistedOperand = PersistedLiteral | PersistedReference;

export interface PersistedComparator {
  id: string;
  op: string;
  a: PersistedOperand;
  b?: PersistedOperand; // absent for unary operators
}

export interface ConditionLogic {
  version: 1;
  comb: Combinator;
  cmps: PersistedComparator[];
}

// ── Defaults / construction ──

export function newLiteral(type: OperandType = 'string'): DraftLiteral {
  return { kind: 'lit', type, text: '' };
}

export function newReference(type: OperandType = 'string'): DraftReference {
  return { kind: 'ref', type, ref: '' };
}

/** A fresh comparator. Binary ops get an empty B; unary ops get none (B-drop, spec §2). */
export function newComparator(op = 'eq', id = 'c1'): DraftComparator {
  return { id, op, a: newLiteral(), b: isBinary(op) ? newLiteral() : null };
}

/** A fresh single-comparator condition (AND of one). */
export function newCondition(): DraftCondition {
  return { comb: 'and', cmps: [newComparator('eq', 'c1')] };
}

/** An empty condition — nothing built yet (the editor's "empty-first" open state). */
export function emptyCondition(): DraftCondition {
  return { comb: 'and', cmps: [] };
}

// Deterministic next id: max existing `c<n>` suffix + 1 (stable, no Date/random — ids are only used
// for uniqueness + per-operand error mapping, never referenced elsewhere, so we never renumber).
export function nextId(cmps: { id: string }[]): string {
  let max = 0;
  for (const c of cmps) {
    const m = /^c(\d+)$/.exec(c.id);
    if (m) max = Math.max(max, Number.parseInt(m[1], 10));
  }
  return `c${max + 1}`;
}

// ── Structural edits (pure; return new objects) ──

export function addComparator(draft: DraftCondition, op = 'eq'): DraftCondition {
  return { ...draft, cmps: [...draft.cmps, newComparator(op, nextId(draft.cmps))] };
}

/**
 * Remove a comparator. May leave the list empty — the editor guards removing the last one, and
 * coerceDraftToLogic reports the empty list as a structural issue (persisted cmps must be ≥ 1).
 */
export function removeComparator(draft: DraftCondition, id: string): DraftCondition {
  return { ...draft, cmps: draft.cmps.filter((c) => c.id !== id) };
}

/** Change a comparator's operator, re-flowing the B operand: dropped for unary, seeded for binary. */
export function setOperator(cmp: DraftComparator, op: string): DraftComparator {
  if (!isBinary(op)) return { ...cmp, op, b: null };
  // List-right ops ('Is one of' …) take a comma-separated list as B, held as raw 'string' text (each
  // element is typed against A at eval time, §5.3). So a literal B for a list op is always 'string';
  // coerce an existing typed literal (e.g. switching eq→in) rather than leave it mis-typed.
  if (isListRight(op)) {
    const b = cmp.b ?? newLiteral('string');
    return { ...cmp, op, b: b.kind === 'lit' && b.type !== 'string' ? { ...b, type: 'string' } : b };
  }
  return { ...cmp, op, b: cmp.b ?? newLiteral(cmp.a.type) };
}

/** Change an operand's declared type, preserving its text/ref. */
export function setOperandType(operand: DraftOperand, type: OperandType): DraftOperand {
  return { ...operand, type };
}

/** Toggle an operand between literal and reference, preserving its declared type (content resets). */
export function setOperandKind(operand: DraftOperand, kind: OperandKind): DraftOperand {
  if (operand.kind === kind) return operand;
  return kind === 'lit' ? newLiteral(operand.type) : newReference(operand.type);
}

// ── Draft → persisted coercion (on Save) ──

export type DraftIssueKind =
  | 'unset' // an empty literal / ref with no target → Incomplete at run time (§2.2)
  | 'invalid' // a literal that can't be parsed to its declared type, or over a length limit
  | 'structure'; // unknown operator, missing required operand, empty/oversized comparator list

export interface DraftIssue {
  comparatorId: string | null;
  operand: 'a' | 'b' | null;
  kind: DraftIssueKind;
  message: string;
}

export interface CoerceResult {
  /** The persisted logic — non-null ONLY when the draft is fully valid (no issues). */
  logic: ConditionLogic | null;
  issues: DraftIssue[];
}

/**
 * Coerce an editor draft into persisted ConditionLogic. Returns the logic only when the draft is
 * complete and valid; otherwise `logic` is null and `issues` lists every problem (so the editor can
 * surface them and the Save/publish gate can block). Mirrors the backend ConditionLogicParser checks.
 */
export function coerceDraftToLogic(draft: DraftCondition): CoerceResult {
  const issues: DraftIssue[] = [];

  if (draft.cmps.length < 1) {
    issues.push(structure(null, null, 'A condition needs at least one comparator.'));
  }
  if (draft.cmps.length > MAX_COMPARATORS) {
    issues.push(structure(null, null, `A condition allows at most ${MAX_COMPARATORS} comparators.`));
  }

  const seen = new Set<string>();
  const cmps: PersistedComparator[] = [];

  for (const c of draft.cmps) {
    if (seen.has(c.id)) {
      issues.push(structure(c.id, null, `Duplicate comparator id '${c.id}'.`));
    }
    seen.add(c.id);

    if (!isKnownOperator(c.op)) {
      issues.push(structure(c.id, null, `Unknown operator '${c.op}'.`));
      continue;
    }

    const a = coerceOperand(c.a, c.op, 'a', c.id, issues);

    let b: PersistedOperand | undefined;
    if (isBinary(c.op)) {
      if (c.b === null) {
        issues.push(structure(c.id, 'b', `Operator '${c.op}' needs a second operand.`));
      } else {
        b = coerceOperand(c.b, c.op, 'b', c.id, issues);
      }
    }

    if (a) {
      cmps.push(b ? { id: c.id, op: c.op, a, b } : { id: c.id, op: c.op, a });
    }
  }

  const logic: ConditionLogic | null =
    issues.length === 0 ? { version: 1, comb: draft.comb, cmps } : null;
  return { logic, issues };
}

export function coerceOperand(
  operand: DraftOperand,
  op: string,
  slot: 'a' | 'b',
  cmpId: string,
  issues: DraftIssue[],
): PersistedOperand | undefined {
  if (operand.kind === 'ref') {
    const ref = operand.ref.trim();
    if (ref.length === 0) {
      issues.push(unset(cmpId, slot));
      return undefined;
    }
    if (ref.length > MAX_REF_LENGTH) {
      issues.push(invalid(cmpId, slot, `Reference exceeds ${MAX_REF_LENGTH} characters.`));
      return undefined;
    }
    return { kind: 'ref', type: operand.type, ref };
  }

  // literal
  if (operand.text.trim().length === 0) {
    issues.push(unset(cmpId, slot));
    return undefined;
  }

  switch (operand.type) {
    case 'string': {
      if (operand.text.length > MAX_LITERAL_LENGTH) {
        issues.push(invalid(cmpId, slot, `Value exceeds ${MAX_LITERAL_LENGTH} characters.`));
        return undefined;
      }
      if (op === 'regex' && slot === 'b' && operand.text.length > MAX_REGEX_LENGTH) {
        issues.push(invalid(cmpId, slot, `Regex pattern exceeds ${MAX_REGEX_LENGTH} characters.`));
        return undefined;
      }
      return { kind: 'lit', type: 'string', value: operand.text };
    }
    case 'number': {
      const p = parseInvariantNumber(operand.text);
      if (!p.ok) {
        issues.push(invalid(cmpId, slot, `'${operand.text}' is not a number.`));
        return undefined;
      }
      return { kind: 'lit', type: 'number', value: p.value };
    }
    case 'boolean': {
      const p = parseBool(operand.text);
      if (!p.ok) {
        issues.push(invalid(cmpId, slot, `'${operand.text}' is not a boolean (true/false).`));
        return undefined;
      }
      return { kind: 'lit', type: 'boolean', value: p.value };
    }
  }
}

// ── Persisted → draft hydration (on Open) ──

export function logicToDraft(logic: ConditionLogic): DraftCondition {
  return {
    comb: logic.comb,
    cmps: logic.cmps.map((c) => ({
      id: c.id,
      op: c.op,
      a: persistedToDraft(c.a),
      b: c.b ? persistedToDraft(c.b) : null,
    })),
  };
}

export function persistedToDraft(operand: PersistedOperand): DraftOperand {
  if (operand.kind === 'ref') return { kind: 'ref', type: operand.type, ref: operand.ref };
  return { kind: 'lit', type: operand.type, text: literalText(operand.value) };
}

function literalText(value: string | number | boolean): string {
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  return String(value);
}

// ── Legacy { left, operator, right } → best-effort draft seed (on Open) ──

/**
 * The legacy operator-name → OperatorId map. The canonical, exhaustive source is the backend
 * LegacyConditionMap.cs (keyed by the shipped ConditionOperator enum); this mirrors it. Keys are the
 * lower-cased enum names, matched case-insensitively (the legacy `operator` property stores the enum
 * name, e.g. "Equal"). not-equals → "ne" per the spec.
 */
export const LEGACY_OPERATOR_IDS: Readonly<Record<string, string>> = {
  equal: 'eq',
  notequal: 'ne',
  greaterthan: 'gt',
  lessthan: 'lt',
  greaterthanorequal: 'gte',
  lessthanorequal: 'lte',
  contains: 'contains',
};

/** The shipped legacy enum names (canonical source: ConditionNodeTask.ConditionOperator). */
export const LEGACY_OPERATOR_NAMES = [
  'Equal',
  'NotEqual',
  'GreaterThan',
  'LessThan',
  'GreaterThanOrEqual',
  'LessThanOrEqual',
  'Contains',
] as const;

export function mapLegacyOperator(name: unknown): string | null {
  if (typeof name !== 'string') return null;
  return LEGACY_OPERATOR_IDS[name.trim().toLowerCase()] ?? null;
}

export interface LegacyCondition {
  left?: unknown;
  operator?: unknown;
  right?: unknown;
}

export interface LegacySeedResult {
  /** A seeded draft, or null when the legacy operator is unmappable (caller falls back to a fresh draft). */
  draft: DraftCondition | null;
  operatorMapped: boolean;
}

/**
 * Best-effort seed a draft from a legacy node. `left`/`right` are design-time expression strings: a
 * `{{ … }}` expression becomes a reference; anything else becomes a literal with an inferred type
 * (boolean/number/string). Inference is intentionally lossy — the author reviews before Save, which
 * writes `logic` and strips the legacy fields.
 */
export function legacyToDraft(legacy: LegacyCondition): LegacySeedResult {
  const opId = mapLegacyOperator(legacy.operator);
  if (!opId) return { draft: null, operatorMapped: false };

  const a = legacyOperand(legacy.left);
  const b = isBinary(opId) ? legacyOperand(legacy.right) : null;
  return {
    draft: { comb: 'and', cmps: [{ id: 'c1', op: opId, a, b }] },
    operatorMapped: true,
  };
}

function legacyOperand(expr: unknown): DraftOperand {
  const raw = typeof expr === 'string' ? expr : expr == null ? '' : String(expr);
  const s = raw.trim();
  if (s.length === 0) return newLiteral('string'); // unset
  if (looksLikeExpression(s)) return { kind: 'ref', type: 'string', ref: s };
  if (/^(true|false)$/i.test(s)) return { kind: 'lit', type: 'boolean', text: s.toLowerCase() };
  if (parseInvariantNumber(s).ok) return { kind: 'lit', type: 'number', text: s };
  return { kind: 'lit', type: 'string', text: s };
}

function looksLikeExpression(s: string): boolean {
  return s.includes('{{');
}

// ── Issue constructors ──

function unset(cmpId: string, operand: 'a' | 'b'): DraftIssue {
  return { comparatorId: cmpId, operand, kind: 'unset', message: `Operand '${operand}' is not set.` };
}

function invalid(cmpId: string, operand: 'a' | 'b', message: string): DraftIssue {
  return { comparatorId: cmpId, operand, kind: 'invalid', message };
}

export function structure(cmpId: string | null, operand: 'a' | 'b' | null, message: string): DraftIssue {
  return { comparatorId: cmpId, operand, kind: 'structure', message };
}
