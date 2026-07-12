import { describe, expect, it } from 'vitest';
import { loadRepoJson } from '../../test/repoFixture';
import {
  aggregate,
  evaluateComparator,
  type Combinator,
  type ComparatorResult,
  type ConditionStatus,
  type OperandState,
  type OperandType,
  type ResolvedComparator,
  type ResolvedOperand,
} from './conditionEval';

// Conformance (B2): the FE evaluator is driven by the SAME truth table as the backend
// (test-fixtures/condition/condition-conformance.fixture.json). Mirrors ConditionEvaluatorTests so the editor's
// live preview status == the server's run-time status, enforced by construction.

interface FixtureOperand {
  type: OperandType;
  state?: OperandState;
  raw?: unknown;
}

interface FixtureCase {
  id: string;
  op: string;
  a: FixtureOperand;
  b?: FixtureOperand;
  expect: { status: ConditionStatus; code?: string; operand?: 'a' | 'b' | null };
}

interface FixtureAggregation {
  id: string;
  comb: Combinator;
  statuses: ConditionStatus[];
  expect: ConditionStatus;
}

interface ConformanceFixture {
  version: number;
  cases: FixtureCase[];
  aggregation: FixtureAggregation[];
}

// vitest runs with cwd = Frontend/; the shared fixture lives at the repo-root test-fixtures/condition/.
const fixture = loadRepoJson<ConformanceFixture>('../test-fixtures/condition/condition-conformance.fixture.json');

function toOperand(o: FixtureOperand): ResolvedOperand {
  const state: OperandState = o.state ?? 'value';
  // Preserve a legitimate null/false/0/"" raw; non-'value' states carry no raw.
  const raw = state === 'value' && Object.prototype.hasOwnProperty.call(o, 'raw') ? o.raw : null;
  return { type: o.type, state, raw };
}

function toComparator(c: FixtureCase): ResolvedComparator {
  return {
    id: c.id,
    op: c.op,
    a: toOperand(c.a),
    b: c.b ? toOperand(c.b) : null,
  };
}

describe('condition evaluator — per-comparator conformance', () => {
  it.each(fixture.cases.map((c) => [c.id, c] as const))('%s', (_id, c) => {
    const result = evaluateComparator(toComparator(c));
    expect(result.status).toBe(c.expect.status);

    if (c.expect.status === 'Error') {
      expect(result.error).not.toBeNull();
      expect(result.error!.code).toBe(c.expect.code);
      // operand defaults to null when omitted; the fixture uses explicit null for pair-level errors.
      expect(result.error!.operand).toBe(c.expect.operand ?? null);
      expect(result.error!.comparatorId).toBe(c.id);
    } else {
      expect(result.error).toBeNull();
    }
  });
});

describe('condition evaluator — aggregation conformance (§6)', () => {
  it.each(fixture.aggregation.map((a) => [a.id, a] as const))('%s', (_id, a) => {
    // Build comparator results carrying only the status the row specifies (error content irrelevant
    // to the aggregate status; Error rows just need a present error object).
    const results: ComparatorResult[] = a.statuses.map((status, i) => ({
      comparatorId: `c${i}`,
      status,
      error:
        status === 'Error'
          ? { code: 'INVALID_LOGIC', message: 'x', comparatorId: `c${i}`, operand: null }
          : null,
    }));
    const outcome = aggregate(a.comb, results);
    expect(outcome.status).toBe(a.expect);
  });
});

describe('regex preview ReDoS guard (R8)', () => {
  const rx = (input: string, pattern: string): ResolvedComparator => ({
    id: 'c1',
    op: 'regex',
    a: { type: 'string', state: 'value', raw: input },
    b: { type: 'string', state: 'value', raw: pattern },
  });

  it.each([
    ['(a+)+', '(a+)+$'],
    ['(a*)*', '(a*)*'],
    ['(.*)*', '(.*)*'],
    ['(\\d+)+', '(\\d+)+'],
    ['({n,} nested)', '(a{2,})+'],
  ])('refuses to execute a catastrophic pattern %s (no freeze)', (_label, pattern) => {
    // A 32-'a' input + trailing '!' is the canonical input that drives (a+)+$ into exponential
    // backtracking. If the guard were absent this test would hang; instead it returns immediately.
    const result = evaluateComparator(rx('a'.repeat(32) + '!', pattern));
    expect(result.status).toBe('Error');
    expect(result.error!.code).toBe('INVALID_LOGIC');
    expect(result.error!.operand).toBe('b');
    expect(result.error!.message).toMatch(/ReDoS|server/i);
  });

  it.each([
    ['plain unbounded', 'abc123', '[0-9]+', 'True'],
    ['single group + quantifier', 'abab', '(ab)+', 'True'],
    ['inner quantifier, group not quantified', 'a+b', '(a+)b', 'False'],
    ['bounded outer over inner unbounded', 'aa', '(a+){1,3}', 'True'],
  ] as const)('still evaluates a safe pattern %s', (_label, input, pattern, expected) => {
    const result = evaluateComparator(rx(input, pattern));
    expect(result.status).toBe(expected);
    expect(result.error).toBeNull();
  });
});
