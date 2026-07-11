import { describe, expect, it } from 'vitest';
import type { ConditionLogic as ConditionLogicV1 } from './conditionModel';
import {
  addComparator,
  addGroup,
  coerceTreeToLogic,
  emptyTree,
  logicToTree,
  newCmpNode,
  removeNode,
  setGroupOp,
  singleTree,
  unwrap,
  wrapInGroup,
  wrapInNot,
  type DraftCmpNode,
  type DraftGroupNode,
  type DraftNode,
  type DraftTree,
} from './conditionTree';

// A valid (fully-set) numeric eq leaf.
function litLeaf(id: string, a: string, b: string): DraftCmpNode {
  return {
    kind: 'cmp',
    id,
    op: 'eq',
    a: { kind: 'lit', type: 'number', text: a },
    b: { kind: 'lit', type: 'number', text: b },
  };
}

function group(id: string, op: 'and' | 'or', children: DraftNode[]): DraftGroupNode {
  return { kind: 'group', id, op, children };
}

describe('coerceTreeToLogic', () => {
  it('coerces a bare valid comparator to a v2 bare root', () => {
    const { logic, issues } = coerceTreeToLogic({ root: litLeaf('c1', '1', '1') });
    expect(issues).toEqual([]);
    expect(logic).toEqual({ version: 2, root: { kind: 'cmp', id: 'c1', op: 'eq', a: { kind: 'lit', type: 'number', value: 1 }, b: { kind: 'lit', type: 'number', value: 1 } } });
  });

  it('coerces a group containing a not', () => {
    const tree: DraftTree = { root: group('g1', 'or', [litLeaf('c1', '1', '1'), { kind: 'not', id: 'n1', child: litLeaf('c2', '1', '2') }]) };
    const { logic, issues } = coerceTreeToLogic(tree);
    expect(issues).toEqual([]);
    expect(logic!.root.kind).toBe('group');
    const root = logic!.root as { kind: 'group'; children: { kind: string }[] };
    expect(root.children.map((c) => c.kind)).toEqual(['cmp', 'not']);
  });

  it('reports an empty tree', () => {
    const { logic, issues } = coerceTreeToLogic(emptyTree());
    expect(logic).toBeNull();
    expect(issues[0].message).toMatch(/at least one comparator/);
  });

  it('reports an unset operand (fresh comparator)', () => {
    const { logic, issues } = coerceTreeToLogic({ root: newCmpNode('eq', 'c1') });
    expect(logic).toBeNull();
    expect(issues.some((i) => i.kind === 'unset')).toBe(true);
  });

  it('reports a duplicate id anywhere in the tree', () => {
    const tree: DraftTree = { root: group('x', 'and', [litLeaf('x', '1', '1')]) };
    expect(coerceTreeToLogic(tree).issues.some((i) => /Duplicate node id/.test(i.message))).toBe(true);
  });

  it('reports an empty group', () => {
    const tree: DraftTree = { root: group('g', 'and', []) };
    expect(coerceTreeToLogic(tree).issues.some((i) => /at least one child/.test(i.message))).toBe(true);
  });

  it('rejects nesting past the max depth', () => {
    let node: DraftNode = litLeaf('leaf', '1', '1');
    for (let i = 0; i < 25; i++) node = { kind: 'not', id: `n${i}`, child: node };
    expect(coerceTreeToLogic({ root: node }).issues.some((i) => /depth/.test(i.message))).toBe(true);
  });

  it('rejects a group with too many children', () => {
    const children = Array.from({ length: 51 }, (_, i) => litLeaf(`c${i}`, '1', '1'));
    expect(coerceTreeToLogic({ root: group('g', 'and', children) }).issues.some((i) => /at most 50 children/.test(i.message))).toBe(true);
  });
});

describe('logicToTree (hydration)', () => {
  it('hydrates a v1 single comparator to a bare root', () => {
    const v1: ConditionLogicV1 = { version: 1, comb: 'and', cmps: [{ id: 'c1', op: 'eq', a: { kind: 'lit', type: 'number', value: 5 }, b: { kind: 'lit', type: 'number', value: 5 } }] };
    expect(logicToTree(v1).root!.kind).toBe('cmp');
  });

  it('hydrates a v1 multi comparator to a root group', () => {
    const v1: ConditionLogicV1 = {
      version: 1,
      comb: 'or',
      cmps: [
        { id: 'c1', op: 'eq', a: { kind: 'lit', type: 'number', value: 1 }, b: { kind: 'lit', type: 'number', value: 1 } },
        { id: 'c2', op: 'eq', a: { kind: 'lit', type: 'number', value: 2 }, b: { kind: 'lit', type: 'number', value: 2 } },
      ],
    };
    const root = logicToTree(v1).root as DraftGroupNode;
    expect(root.kind).toBe('group');
    expect(root.op).toBe('or');
    expect(root.children).toHaveLength(2);
  });

  it('round-trips a nested tree: coerce → hydrate → coerce is stable', () => {
    const tree: DraftTree = { root: group('g1', 'and', [litLeaf('c1', '1', '1'), { kind: 'not', id: 'n1', child: litLeaf('c2', '2', '3') }]) };
    const first = coerceTreeToLogic(tree).logic!;
    const rehydrated = logicToTree(first);
    const second = coerceTreeToLogic(rehydrated).logic!;
    expect(second).toEqual(first);
  });
});

describe('structural edits', () => {
  it('addComparator on an empty tree creates a bare comparator', () => {
    expect(addComparator(emptyTree(), null).root!.kind).toBe('cmp');
  });

  it('addComparator appends a child to a group with a fresh unique id', () => {
    const tree: DraftTree = { root: group('g1', 'and', [litLeaf('c1', '1', '1')]) };
    const root = addComparator(tree, 'g1').root as DraftGroupNode;
    expect(root.children).toHaveLength(2);
    expect(root.children[1].id).toBe('c2');
  });

  it('addGroup adds a seeded child group', () => {
    const tree: DraftTree = { root: group('g1', 'and', [litLeaf('c1', '1', '1')]) };
    const root = addGroup(tree, 'g1', 'or').root as DraftGroupNode;
    expect(root.children).toHaveLength(2);
    expect(root.children[1].kind).toBe('group');
    expect((root.children[1] as DraftGroupNode).children).toHaveLength(1);
  });

  it('wrapInGroup wraps the root', () => {
    const tree = singleTree();
    const root = wrapInGroup(tree, tree.root!.id, 'or').root as DraftGroupNode;
    expect(root.kind).toBe('group');
    expect(root.op).toBe('or');
    expect(root.children[0].id).toBe('c1');
  });

  it('wrapInNot wraps a node', () => {
    const tree = singleTree();
    const root = wrapInNot(tree, 'c1').root!;
    expect(root.kind).toBe('not');
  });

  it('setGroupOp flips the combinator', () => {
    const tree: DraftTree = { root: group('g1', 'and', [litLeaf('c1', '1', '1')]) };
    expect((setGroupOp(tree, 'g1', 'or').root as DraftGroupNode).op).toBe('or');
  });

  it('removeNode filters a child out of its group', () => {
    const tree: DraftTree = { root: group('g1', 'and', [litLeaf('c1', '1', '1'), litLeaf('c2', '2', '2')]) };
    const root = removeNode(tree, 'c2').root as DraftGroupNode;
    expect(root.children.map((c) => c.id)).toEqual(['c1']);
  });

  it('removeNode cascades: a NOT losing its child is removed', () => {
    const tree: DraftTree = { root: group('g1', 'and', [litLeaf('c1', '1', '1'), { kind: 'not', id: 'n1', child: litLeaf('c2', '1', '1') }]) };
    const root = removeNode(tree, 'c2').root as DraftGroupNode;
    expect(root.children.map((c) => c.id)).toEqual(['c1']);
  });

  it('removeNode on the root empties the tree', () => {
    expect(removeNode(singleTree(), 'c1').root).toBeNull();
  });

  it('unwrap replaces a root NOT with its child', () => {
    const tree: DraftTree = { root: { kind: 'not', id: 'n1', child: litLeaf('c1', '1', '1') } };
    expect(unwrap(tree, 'n1').root!.id).toBe('c1');
  });

  it('unwrap splices a child group into its parent', () => {
    const tree: DraftTree = { root: group('g1', 'and', [litLeaf('c1', '1', '1'), group('g2', 'or', [litLeaf('c2', '2', '2'), litLeaf('c3', '3', '3')])]) };
    const root = unwrap(tree, 'g2').root as DraftGroupNode;
    expect(root.children.map((c) => c.id)).toEqual(['c1', 'c2', 'c3']);
  });
});
