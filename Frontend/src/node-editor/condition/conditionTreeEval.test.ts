import { describe, expect, it } from 'vitest';
import { loadRepoJson } from '../../test/repoFixture';
import {
  evaluateTree,
  type ComparatorResult,
  type ConditionStatus,
  type ResolvedComparator,
  type ResolvedLogicNode,
  type ResolvedOperand,
} from './conditionEval';

// Tree-aggregation conformance (Phase 8, spec §10): the FE recursive evaluator is driven by the SAME
// test-fixtures/condition/condition-tree.fixture.json the backend ConditionTreeEvaluatorTests load — so the editor's
// live preview folds a nested condition identically to the server. Fixture leaves carry a precomputed
// status (not operands); leaf operator semantics stay in condition-conformance.fixture.json.

type FixtureNode =
  | { kind: 'leaf'; id?: string; status: ConditionStatus }
  | { kind: 'group'; op: 'and' | 'or'; children: FixtureNode[] }
  | { kind: 'not'; child: FixtureNode };

interface TreeCase {
  id: string;
  tree: FixtureNode;
  expect: ConditionStatus;
  expectErrorFrom?: string;
}

interface TreeFixture {
  version: number;
  cases: TreeCase[];
}

const fixture = loadRepoJson<TreeFixture>('../test-fixtures/condition/condition-tree.fixture.json');

function num(v: number): ResolvedOperand {
  return { type: 'number', state: 'value', raw: v };
}

// Map a precomputed leaf status to a deterministic comparator (so evaluateComparator yields exactly
// that status); the Error leaf carries its id so the surfaced error's comparatorId can be asserted.
function leafFor(status: ConditionStatus, id: string): ResolvedComparator {
  switch (status) {
    case 'True':
      return { id, op: 'eq', a: num(1), b: num(1) };
    case 'False':
      return { id, op: 'eq', a: num(1), b: num(2) };
    case 'Incomplete':
      return { id, op: 'eq', a: { type: 'number', state: 'unset', raw: null }, b: num(1) };
    case 'Error':
      return { id, op: 'eq', a: { type: 'number', state: 'unresolved', raw: null }, b: num(1) };
  }
}

function build(node: FixtureNode): ResolvedLogicNode {
  switch (node.kind) {
    case 'leaf':
      return { kind: 'cmp', comparator: leafFor(node.status, node.id ?? 'leaf') };
    case 'group':
      return { kind: 'group', op: node.op, children: node.children.map(build) };
    case 'not':
      return { kind: 'not', child: build(node.child) };
  }
}

describe('condition tree evaluator — aggregation conformance (§10)', () => {
  it.each(fixture.cases.map((c) => [c.id, c] as const))('%s', (_id, c) => {
    const outcome = evaluateTree(build(c.tree));
    expect(outcome.status).toBe(c.expect);
    if (c.expectErrorFrom !== undefined) {
      expect(outcome.error).not.toBeNull();
      expect((outcome.error as NonNullable<typeof outcome.error>).comparatorId).toBe(c.expectErrorFrom);
    }
  });

  it('flattens leaf results in depth-first order for preview parity', () => {
    const tree: FixtureNode = {
      kind: 'group',
      op: 'and',
      children: [
        { kind: 'leaf', id: 'a', status: 'True' },
        { kind: 'not', child: { kind: 'leaf', id: 'b', status: 'False' } },
      ],
    };
    const outcome = evaluateTree(build(tree));
    expect(outcome.comparators.map((r: ComparatorResult) => r.comparatorId)).toEqual(['a', 'b']);
  });
});
