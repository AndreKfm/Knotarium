// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// The pure, type-aware Condition evaluator — the frontend half of the spec, mirroring the backend
// Backend/Knotarium.Features/Nodes/Condition/ConditionEvaluator.cs. Both halves are pinned by the
// shared conformance fixture (test-fixtures/condition/condition-conformance.fixture.json, B2) so the editor's
// live preview produces the same status the server will at run time.
//
// Authoritative implementation of docs/design/condition-operator-semantics.md (§2 precedence,
// §3 coercion, §5 operators, §6 aggregation). No I/O. The BACKEND is authoritative on regex: JS and
// .NET regex flavors differ, so non-trivial patterns may diverge in the live preview (the leading
// inline-flag form `(?i)…` is translated to a JS flag; everything else uses the JS engine). The FE
// has no regex timeout, so to keep a pathological author pattern from freezing the editor thread it
// REFUSES to execute patterns with a catastrophic-backtracking shape (R8 — see `regexMatch`); the
// server still runs the real regex under its ReDoS cap. Preview is best-effort.

import { isBinary, isKnownOperator } from './operators';

// ── The status model (B3) and per-operand error shape (B7). ──

export type ConditionStatus = 'True' | 'False' | 'Incomplete' | 'Error';

export type ConditionErrorCode =
  | 'INVALID_LOGIC'
  | 'RESOLUTION_FAILED'
  | 'COERCION_FAILED'
  | 'TYPE_MISMATCH'
  // Backend-only runtime backstop: a structurally-valid condition reaching an impossible state
  // (e.g. Incomplete at runtime). The FE preview never emits it; listed for FE/BE taxonomy parity.
  | 'INTERNAL_INVARIANT';

export type OperandType = 'string' | 'number' | 'boolean';

/** Operand state at evaluate time (spec §2). */
export type OperandState =
  | 'value' // a resolved value is present (possibly a legitimate null)
  | 'unset' // design-time unset (empty literal draft / no ref target) → Incomplete (§2.2)
  | 'unresolved' // a configured ref failed to resolve at run time → RESOLUTION_FAILED (§2.3)
  | 'absent'; // the operand slot is missing in persisted logic (binary op with no b)

export type Combinator = 'and' | 'or';

/** Per-operand error shape (B7): identical for runtime + the Phase-6 dry-run. */
export interface ConditionError {
  code: ConditionErrorCode;
  message: string;
  comparatorId: string | null;
  operand: 'a' | 'b' | null;
}

/**
 * An operand as the evaluator sees it: its declared type, its state, and (when state is 'value') the
 * resolved RAW value — a scalar (number/string/boolean), null, an array, or a plain object. A 'value'
 * with raw === null is a LEGITIMATE null, distinct from 'unresolved' (§2.3, §5.4).
 */
export interface ResolvedOperand {
  type: OperandType;
  state: OperandState;
  raw: unknown;
}

export interface ResolvedComparator {
  id: string;
  op: string;
  a: ResolvedOperand;
  b: ResolvedOperand | null;
}

export interface ResolvedCondition {
  comb: Combinator;
  comparators: ResolvedComparator[];
}

export interface ComparatorResult {
  comparatorId: string;
  status: ConditionStatus;
  error: ConditionError | null;
}

export interface ConditionOutcome {
  status: ConditionStatus;
  error: ConditionError | null;
  comparators: ComparatorResult[];
}

/** Regex hard cap (spec §5.2 / §8). The FE has no timeout; the backend enforces the ReDoS cap. */
const MAX_REGEX_LENGTH = 512;

// R8 — preview ReDoS guard. JS regex runs synchronously with no timeout, so a pathological author
// pattern (catastrophic backtracking) entered in the live editor would freeze the UI thread. We
// detect the classic exponential shape — an unbounded quantifier applied to a subexpression that
// itself contains an unbounded quantifier, e.g. (a+)+, (a*)*, (.*)*, (\d+)+, (a{2,})+ — and refuse to
// execute it in preview rather than risk the hang. Conservative by design (it scans only a single
// flat group level): a false positive merely defers a benign pattern to the server, which remains
// authoritative and runs the real regex under its own ReDoS cap. The detector is itself linear (no
// nested quantifier in its own pattern), so it is safe on inputs up to MAX_REGEX_LENGTH.
const UNBOUNDED_QUANTIFIER = '(?:[*+]|\\{\\d+,\\})';
const CATASTROPHIC_SHAPE = new RegExp(
  `\\([^()]*${UNBOUNDED_QUANTIFIER}[^()]*\\)${UNBOUNDED_QUANTIFIER}`,
);

function isLikelyCatastrophicRegex(pattern: string): boolean {
  return CATASTROPHIC_SHAPE.test(pattern);
}

// ── Result constructors ──

function ok(id: string, value: boolean): ComparatorResult {
  return { comparatorId: id, status: value ? 'True' : 'False', error: null };
}

function incomplete(id: string): ComparatorResult {
  return { comparatorId: id, status: 'Incomplete', error: null };
}

function fail(id: string, error: ConditionError): ComparatorResult {
  return { comparatorId: id, status: 'Error', error };
}

function err(
  code: ConditionErrorCode,
  message: string,
  id: string | null,
  operand: 'a' | 'b' | null,
): ConditionError {
  return { code, message, comparatorId: id, operand };
}

// ── Aggregation (spec §6) — strict: Error dominates, then Incomplete, then the boolean. ──

export function aggregate(comb: Combinator, results: ComparatorResult[]): ConditionOutcome {
  const firstError = results.find((r) => r.status === 'Error');
  if (firstError) {
    return { status: 'Error', error: firstError.error, comparators: results };
  }
  if (results.some((r) => r.status === 'Incomplete')) {
    return { status: 'Incomplete', error: null, comparators: results };
  }
  const value =
    comb === 'and'
      ? results.every((r) => r.status === 'True')
      : results.some((r) => r.status === 'True');
  return { status: value ? 'True' : 'False', error: null, comparators: results };
}

export function evaluateCondition(condition: ResolvedCondition): ConditionOutcome {
  // An empty condition is the editor's "empty-first" state, not a vacuous truth. (`aggregate` would
  // fold zero comparators to True for AND; persisted logic always has ≥1, so this only guards the
  // editor draft.)
  if (condition.comparators.length === 0) {
    return { status: 'Incomplete', error: null, comparators: [] };
  }
  const results = condition.comparators.map((c) => evaluateComparator(c));
  return aggregate(condition.comb, results);
}

// ── v2 tree evaluation (spec §10): recursive fold mirroring ConditionEvaluator.EvaluateTree. ──

/** A resolved boolean-tree node (Phase 8): a comparator leaf, an and/or group, or a not. */
export type ResolvedLogicNode =
  | { kind: 'cmp'; comparator: ResolvedComparator }
  | { kind: 'group'; op: Combinator; children: ResolvedLogicNode[] }
  | { kind: 'not'; child: ResolvedLogicNode };

export function evaluateTree(root: ResolvedLogicNode): ConditionOutcome {
  const leaves: ComparatorResult[] = [];
  const { status, error } = evalNode(root, leaves);
  return { status, error, comparators: leaves };
}

// Folds a node to its status + surfaced B7 error, pushing each visited LEAF result onto `leaves`
// (depth-first). Strict per-node dominance (B9: Error→Incomplete→boolean); NOT propagates the
// non-boolean statuses (B8). Mirrors the backend EvalNode exactly.
function evalNode(
  node: ResolvedLogicNode,
  leaves: ComparatorResult[],
): { status: ConditionStatus; error: ConditionError | null } {
  if (node.kind === 'cmp') {
    const r = evaluateComparator(node.comparator);
    leaves.push(r);
    return { status: r.status, error: r.error };
  }
  if (node.kind === 'not') {
    const c = evalNode(node.child, leaves);
    switch (c.status) {
      case 'True':
        return { status: 'False', error: null };
      case 'False':
        return { status: 'True', error: null };
      case 'Incomplete':
        return { status: 'Incomplete', error: null }; // propagate (B8)
      default:
        return { status: 'Error', error: c.error }; // propagate child error
    }
  }
  // group — empty → Incomplete (no vacuous truth); §10.1.
  if (node.children.length === 0) return { status: 'Incomplete', error: null };
  const outcomes = node.children.map((child) => evalNode(child, leaves));
  // Error dominates, first in child order (depth-first, since each child carries its subtree's error).
  const firstError = outcomes.find((o) => o.status === 'Error');
  if (firstError) return { status: 'Error', error: firstError.error };
  if (outcomes.some((o) => o.status === 'Incomplete')) return { status: 'Incomplete', error: null };
  const value =
    node.op === 'and' ? outcomes.every((o) => o.status === 'True') : outcomes.some((o) => o.status === 'True');
  return { status: value ? 'True' : 'False', error: null };
}

// ── Single comparator (spec §2 precedence) ──

export function evaluateComparator(cmp: ResolvedComparator): ComparatorResult {
  const id = cmp.id;

  // §2.1 — unknown operator id.
  if (!isKnownOperator(cmp.op)) {
    return fail(id, err('INVALID_LOGIC', `Unknown operator '${cmp.op}'.`, id, null));
  }

  const binary = isBinary(cmp.op);

  // §2.1 — arity/structure: a binary op must carry a present b slot.
  if (binary && (cmp.b === null || cmp.b.state === 'absent')) {
    return fail(id, err('INVALID_LOGIC', `Operator '${cmp.op}' requires a second operand.`, id, 'b'));
  }

  // Required operands, in reporting order. Unary ops ignore b entirely (§2, §5.4/§5.5).
  const required: [slot: 'a' | 'b', operand: ResolvedOperand][] = binary
    ? [['a', cmp.a], ['b', cmp.b as ResolvedOperand]]
    : [['a', cmp.a]];

  // §2.2 — unset → Incomplete (any required operand).
  if (required.some(([, o]) => o.state === 'unset')) {
    return incomplete(id);
  }

  // §2.3 — unresolved ref → RESOLUTION_FAILED (first by operand order).
  for (const [slot, operand] of required) {
    if (operand.state === 'unresolved') {
      return fail(id, err('RESOLUTION_FAILED', `Operand '${slot}' could not be resolved.`, id, slot));
    }
  }

  // §5 — apply the operator. Existence ops read the RAW value; everything else coerces (§3).
  switch (cmp.op) {
    case 'exists':
    case 'nexists':
    case 'empty':
    case 'nempty':
      return existence(id, cmp.op, cmp.a);
    case 'true':
    case 'false':
      return booleanOp(id, cmp.op, cmp.a);
    case 'eq':
    case 'ne':
      return equality(id, cmp.op, cmp.a, cmp.b as ResolvedOperand);
    case 'gt':
    case 'gte':
    case 'lt':
    case 'lte':
      return ordering(id, cmp.op, cmp.a, cmp.b as ResolvedOperand);
    case 'contains':
    case 'ncontains':
    case 'starts':
    case 'ends':
    case 'regex':
      return text(id, cmp.op, cmp.a, cmp.b as ResolvedOperand);
    case 'in':
    case 'nin':
      return membership(id, cmp.op, cmp.a, cmp.b as ResolvedOperand);
    default:
      return fail(id, err('INVALID_LOGIC', `Unhandled operator '${cmp.op}'.`, id, null));
  }
}

// ── §5.4 Existence — read the RAW resolved value (before coercion). ──

function existence(id: string, op: string, a: ResolvedOperand): ComparatorResult {
  const raw = normalize(a.raw);
  switch (op) {
    case 'exists':
      return ok(id, raw !== null);
    case 'nexists':
      return ok(id, raw === null);
    case 'empty':
      return ok(id, isEmpty(raw));
    case 'nempty':
      return ok(id, !isEmpty(raw));
    default:
      return fail(id, unhandled(op, id));
  }
}

function isEmpty(raw: unknown): boolean {
  if (raw === null) return true;
  if (typeof raw === 'string') return raw.trim().length === 0;
  if (Array.isArray(raw)) return raw.length === 0;
  if (typeof raw === 'object') return Object.keys(raw as object).length === 0;
  return false; // numbers, booleans are never "empty"
}

// ── §5.5 Boolean — read the operand COERCED to boolean. ──

function booleanOp(id: string, op: string, a: ResolvedOperand): ComparatorResult {
  const { value, error } = coerce(a, 'a', id);
  if (error) return fail(id, error);
  const target = op === 'true';
  if (typeof value !== 'boolean') return ok(id, false); // null effective → False
  return ok(id, value === target);
}

// ── §5.1 Comparison eq/ne ──

function equality(id: string, op: string, a: ResolvedOperand, b: ResolvedOperand): ComparatorResult {
  const ra = coerce(a, 'a', id);
  if (ra.error) return fail(id, ra.error);
  const rb = coerce(b, 'b', id);
  if (rb.error) return fail(id, rb.error);

  const equal = effectiveEquals(ra.value, rb.value);
  return ok(id, op === 'eq' ? equal : !equal);
}

function effectiveEquals(a: unknown, b: unknown): boolean {
  if (a === null && b === null) return true;
  if (a === null || b === null) return false;
  if (typeof a === 'number' && typeof b === 'number') return a === b; // exact, no epsilon (R11)
  if (typeof a === 'string' && typeof b === 'string') return a === b;
  if (typeof a === 'boolean' && typeof b === 'boolean') return a === b;
  return false; // different effective types → defined cross-type False (§5.1)
}

// ── §5.1 Ordering gt/gte/lt/lte ──

function ordering(id: string, op: string, a: ResolvedOperand, b: ResolvedOperand): ComparatorResult {
  const ra = coerce(a, 'a', id);
  if (ra.error) return fail(id, ra.error);
  const rb = coerce(b, 'b', id);
  if (rb.error) return fail(id, rb.error);

  // A null operand → ordering predicate unsatisfied → False (§5.1, §8).
  if (ra.value === null || rb.value === null) return ok(id, false);

  if (typeof ra.value === 'number' && typeof rb.value === 'number') {
    const da = ra.value;
    const db = rb.value;
    // Exact numeric comparison, no epsilon (R11): epsilon on ordering broke trichotomy (a value could
    // be both gt and lte at the boundary). gte/lte use the exact >=/<= so the four ops stay consistent.
    const value =
      op === 'gt'
        ? da > db
        : op === 'lt'
          ? da < db
          : op === 'gte'
            ? da >= db
            : da <= db; // lte
    return ok(id, value);
  }
  if (typeof ra.value === 'string' && typeof rb.value === 'string') {
    const c = compareOrdinal(ra.value, rb.value);
    const value =
      op === 'gt' ? c > 0 : op === 'lt' ? c < 0 : op === 'gte' ? c >= 0 : c <= 0;
    return ok(id, value);
  }

  // Differing, or same-but-non-orderable (e.g. both boolean), effective types → Error (§5.1).
  return fail(
    id,
    err('TYPE_MISMATCH', `Operator '${op}' cannot order operands of differing or non-orderable types.`, id, null),
  );
}

// ── §5.2 Text contains/ncontains/starts/ends/regex ──

function text(id: string, op: string, a: ResolvedOperand, b: ResolvedOperand): ComparatorResult {
  const ra = coerce(a, 'a', id);
  if (ra.error) return fail(id, ra.error);
  const rb = coerce(b, 'b', id);
  if (rb.error) return fail(id, rb.error);

  const sa = ra.value === null ? null : stringForm(ra.value);
  const sb = rb.value === null ? null : stringForm(rb.value);

  // Null operand: positive text ops → False; ncontains is the negation → True.
  if (sa === null || sb === null) {
    return ok(id, op === 'ncontains');
  }

  switch (op) {
    case 'contains':
      return ok(id, sa.includes(sb));
    case 'ncontains':
      return ok(id, !sa.includes(sb));
    case 'starts':
      return ok(id, sa.startsWith(sb));
    case 'ends':
      return ok(id, sa.endsWith(sb));
    case 'regex':
      return regexMatch(id, sa, sb);
    default:
      return fail(id, unhandled(op, id));
  }
}

function regexMatch(id: string, input: string, pattern: string): ComparatorResult {
  if (pattern.length > MAX_REGEX_LENGTH) {
    return fail(id, err('INVALID_LOGIC', `Regex pattern exceeds ${MAX_REGEX_LENGTH} characters.`, id, 'b'));
  }
  if (isLikelyCatastrophicRegex(pattern)) {
    // Not a logic error — the pattern may run fine on the server (under its ReDoS timeout). We just
    // refuse to execute it on the editor thread. Surfaced as a clear, operand-pinned message so the
    // author understands the preview deferred rather than the pattern being invalid (R8).
    return fail(id, err('INVALID_LOGIC',
      'Regex not previewed: pattern can backtrack catastrophically (ReDoS). It is evaluated on the server.',
      id, 'b'));
  }
  try {
    // Translate a leading inline-flag group `(?i)`/`(?im)`… to JS flags; the rest uses the JS engine.
    const { flags, body } = translateInlineFlags(pattern);
    const re = new RegExp(body, flags);
    return ok(id, re.test(input));
  } catch (e) {
    return fail(id, err('INVALID_LOGIC', `Invalid regex pattern: ${(e as Error).message}`, id, 'b'));
  }
}

// JS has no `(?i)` inline modifier; lift a leading one to the equivalent JS flag(s) (i/m/s only).
function translateInlineFlags(pattern: string): { flags: string; body: string } {
  const m = /^\(\?([a-z]+)\)/.exec(pattern);
  if (!m) return { flags: '', body: pattern };
  const inline = m[1];
  let flags = '';
  if (inline.includes('i')) flags += 'i';
  if (inline.includes('m')) flags += 'm';
  if (inline.includes('s')) flags += 's';
  return { flags, body: pattern.slice(m[0].length) };
}

// ── §5.3 Membership in/nin ──

function membership(id: string, op: string, a: ResolvedOperand, b: ResolvedOperand): ComparatorResult {
  const ra = coerce(a, 'a', id);
  if (ra.error) return fail(id, ra.error);
  const rb = coerce(b, 'b', id);
  if (rb.error) return fail(id, rb.error);

  if (ra.value === null) {
    return ok(id, op === 'nin');
  }

  const list = rb.value === null ? '' : stringForm(rb.value);
  const elements = list
    .split(',')
    .map((e) => e.trim())
    .filter((e) => e.length > 0);

  const member = elements.some((element) => elementEquals(ra.value, element));
  return ok(id, op === 'in' ? member : !member);
}

// Compare a list element (raw text) to A using the eq-rule for A's effective type (§5.3).
function elementEquals(effectiveA: unknown, element: string): boolean {
  if (typeof effectiveA === 'number') {
    const p = tryParseInvariantNumber(element);
    return p.ok && effectiveA === p.value; // exact, no epsilon (R11)
  }
  if (typeof effectiveA === 'string') {
    return effectiveA === element;
  }
  if (typeof effectiveA === 'boolean') {
    const p = tryParseBool(element);
    return p.ok && effectiveA === p.value;
  }
  return false;
}

// ── §3 Coercion to the declared type (per operand, non-null raw). ──

function coerce(operand: ResolvedOperand, slot: 'a' | 'b', id: string): { value: unknown; error: ConditionError | null } {
  const raw = normalize(operand.raw);
  if (raw === null) return { value: null, error: null }; // resolved null passes through (§3 last line)

  const failCoerce = (message: string) => ({
    value: null,
    error: err('COERCION_FAILED', message, id, slot),
  });

  switch (operand.type) {
    case 'number': {
      if (typeof raw === 'number' && Number.isFinite(raw)) return { value: raw, error: null };
      if (typeof raw === 'string') {
        const p = tryParseInvariantNumber(raw);
        if (p.ok) return { value: p.value, error: null };
        return failCoerce(`Operand '${slot}' value '${raw}' is not a number.`);
      }
      return failCoerce(`Operand '${slot}' cannot be coerced to number.`);
    }
    case 'string': {
      if (typeof raw === 'string') return { value: raw, error: null };
      if (typeof raw === 'number' && Number.isFinite(raw)) return { value: numberToString(raw), error: null };
      if (typeof raw === 'boolean') return { value: raw ? 'true' : 'false', error: null };
      return failCoerce(`Operand '${slot}' cannot be coerced to string.`);
    }
    case 'boolean': {
      if (typeof raw === 'boolean') return { value: raw, error: null };
      if (typeof raw === 'string') {
        const p = tryParseBool(raw);
        if (p.ok) return { value: p.value, error: null };
        return failCoerce(`Operand '${slot}' value '${raw}' is not a boolean.`);
      }
      return failCoerce(`Operand '${slot}' cannot be coerced to boolean.`);
    }
    default:
      return failCoerce(`Operand '${slot}' has an unknown declared type.`);
  }
}

// ── Helpers ──

/**
 * §3¹ — invariant-culture number parse (exported so the editor's draft→persisted coercion in
 * conditionModel.ts uses the SAME rule the runtime does, preventing a literal that the editor accepts
 * from failing at run time): leading/trailing whitespace, sign, decimal point ('.'), exponent allowed;
 * NO thousands separators; NaN/Infinity rejected. Mirrors .NET NumberStyles.Float.
 */
export function parseInvariantNumber(s: string): { ok: boolean; value: number } {
  return tryParseInvariantNumber(s);
}

/** Parse a boolean literal (case-insensitive `true`/`false`, trimmed). Exported alongside the above. */
export function parseBool(s: string): { ok: boolean; value: boolean } {
  return tryParseBool(s);
}

function tryParseInvariantNumber(s: string): { ok: boolean; value: number } {
  const t = s.trim();
  if (t.length === 0) return { ok: false, value: 0 };
  if (!/^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/.test(t)) return { ok: false, value: 0 };
  const v = Number(t);
  if (!Number.isFinite(v)) return { ok: false, value: 0 };
  return { ok: true, value: v };
}

function tryParseBool(s: string): { ok: boolean; value: boolean } {
  const t = s.trim().toLowerCase();
  if (t === 'true') return { ok: true, value: true };
  if (t === 'false') return { ok: true, value: false };
  return { ok: false, value: false };
}

// §3² — number→string uses the round-trippable invariant form. JS `String(n)` matches .NET "R" for
// the integers and finite decimals the editor deals with (scientific edge cases may diverge).
function numberToString(n: number): string {
  return String(n);
}

function stringForm(v: unknown): string {
  if (typeof v === 'string') return v;
  if (typeof v === 'boolean') return v ? 'true' : 'false';
  if (typeof v === 'number') return String(v);
  return String(v);
}

// CompareOrdinal — compare by UTF-16 code unit, matching .NET string.CompareOrdinal for BMP text.
function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

// Normalize a raw value into the native shapes the operators understand. JS values arrive native;
// `undefined` is treated as the legitimate null. (The backend additionally unwraps JsonElement.)
function normalize(raw: unknown): unknown {
  return raw === undefined ? null : raw;
}

function unhandled(op: string, id: string): ConditionError {
  return err('INVALID_LOGIC', `Unhandled operator '${op}'.`, id, null);
}
