// The v2 nestable-logic tree for the editor (Phase 8) — pure model + transforms, mirroring the backend
// Backend/KnotGarden.Features/Nodes/Condition/{ConditionLogic,ConditionLogicParser}.cs and spec §10.
// The draft tree is what the (nested-box, N1-a) editor edits; coerceTreeToLogic produces the persisted
// v2 logic on Save; logicToTree hydrates either a v1 (flat) or v2 (tree) persisted blob on Open.
//
// Leaf data + semantics are unchanged from the flat model — a leaf IS a DraftComparator — so the
// operand/operator editing helpers (coerceOperand, persistedToDraft, newComparator) are reused.

import type { Combinator } from './conditionEval';
import {
  coerceOperand,
  newComparator,
  persistedToDraft,
  setOperator,
  structure,
  type ConditionLogic as ConditionLogicV1,
  type DraftComparator,
  type DraftIssue,
  type DraftOperand,
  type PersistedOperand,
} from './conditionModel';
import { isBinary, isKnownOperator } from './operators';

// B10 bounds — mirror ConditionLogicParser.cs (the backend parser is the authoritative gate).
export const MAX_TREE_DEPTH = 20;
export const MAX_TREE_NODES = 200;
export const MAX_GROUP_CHILDREN = 50;

// ── Draft tree (editor; textual, possibly incomplete) ──

/** A comparator leaf — the same draft comparator as the flat model, tagged as a tree node. */
export type DraftCmpNode = { kind: 'cmp' } & DraftComparator;
export interface DraftGroupNode {
  kind: 'group';
  id: string;
  op: Combinator;
  children: DraftNode[];
}
export interface DraftNotNode {
  kind: 'not';
  id: string;
  child: DraftNode;
}
export type DraftNode = DraftCmpNode | DraftGroupNode | DraftNotNode;

/** The whole editable tree. `root` is null in the editor's empty-first state (nothing built yet). */
export interface DraftTree {
  root: DraftNode | null;
}

// ── Persisted tree (mirror of backend ConditionLogic v2) ──

export type PersistedCmpNode = { kind: 'cmp'; id: string; op: string; a: PersistedOperand; b?: PersistedOperand };
export interface PersistedGroupNode {
  kind: 'group';
  id: string;
  op: Combinator;
  children: PersistedNode[];
}
export interface PersistedNotNode {
  kind: 'not';
  id: string;
  child: PersistedNode;
}
export type PersistedNode = PersistedCmpNode | PersistedGroupNode | PersistedNotNode;

export interface ConditionLogicTree {
  version: 2;
  root: PersistedNode;
}

// ── Construction / ids ──

export function emptyTree(): DraftTree {
  return { root: null };
}

export function newCmpNode(op = 'eq', id = 'c1'): DraftCmpNode {
  return { kind: 'cmp', ...newComparator(op, id) };
}

/** A fresh single-comparator tree (a bare comparator root). */
export function singleTree(): DraftTree {
  return { root: newCmpNode('eq', 'c1') };
}

function collectIds(node: DraftNode, acc: Set<string>): void {
  acc.add(node.id);
  if (node.kind === 'group') node.children.forEach((c) => collectIds(c, acc));
  else if (node.kind === 'not') collectIds(node.child, acc);
}

// Deterministic next id: a `<prefix><n>` whose number is higher than EVERY existing id's trailing
// number, so it is unique across the whole tree (B10) without renumbering. No Date/random.
function freshId(root: DraftNode | null, prefix: string): string {
  const used = new Set<string>();
  if (root) collectIds(root, used);
  let max = 0;
  for (const id of used) {
    const m = /(\d+)$/.exec(id);
    if (m) max = Math.max(max, Number.parseInt(m[1], 10));
  }
  return `${prefix}${max + 1}`;
}

// ── Draft → persisted coercion (on Save) ──

export interface TreeCoerceResult {
  /** The persisted v2 logic — non-null ONLY when the whole tree is valid (no issues). */
  logic: ConditionLogicTree | null;
  issues: DraftIssue[];
}

/**
 * Coerce an editor draft tree into persisted v2 ConditionLogic. Returns the logic only when the tree is
 * complete and valid; otherwise `logic` is null and `issues` lists every problem. Recursive, enforcing
 * the B10 bounds + tree-unique ids; leaf coercion reuses the same rules as the flat model.
 */
export function coerceTreeToLogic(tree: DraftTree): TreeCoerceResult {
  const issues: DraftIssue[] = [];
  if (!tree.root) {
    issues.push(structure(null, null, 'A condition needs at least one comparator.'));
    return { logic: null, issues };
  }

  const ctx = { seen: new Set<string>(), count: 0 };
  const root = coerceNode(tree.root, 1, ctx, issues);

  const logic: ConditionLogicTree | null =
    issues.length === 0 && root ? { version: 2, root } : null;
  return { logic, issues };
}

function coerceNode(
  node: DraftNode,
  depth: number,
  ctx: { seen: Set<string>; count: number },
  issues: DraftIssue[],
): PersistedNode | undefined {
  if (depth > MAX_TREE_DEPTH) {
    issues.push(structure(node.id, null, `Logic nesting exceeds the max depth of ${MAX_TREE_DEPTH}.`));
    return undefined;
  }
  ctx.count += 1;
  if (ctx.count > MAX_TREE_NODES) {
    issues.push(structure(null, null, `Logic exceeds the max of ${MAX_TREE_NODES} nodes.`));
    return undefined;
  }
  if (ctx.seen.has(node.id)) {
    issues.push(structure(node.id, null, `Duplicate node id '${node.id}'.`));
  }
  ctx.seen.add(node.id);

  switch (node.kind) {
    case 'cmp':
      return coerceLeaf(node, issues);
    case 'group': {
      if (node.children.length < 1) {
        issues.push(structure(node.id, null, 'A group needs at least one child.'));
      }
      if (node.children.length > MAX_GROUP_CHILDREN) {
        issues.push(structure(node.id, null, `A group allows at most ${MAX_GROUP_CHILDREN} children.`));
      }
      const children = node.children
        .map((c) => coerceNode(c, depth + 1, ctx, issues))
        .filter((c): c is PersistedNode => c !== undefined);
      return { kind: 'group', id: node.id, op: node.op, children };
    }
    case 'not': {
      const child = coerceNode(node.child, depth + 1, ctx, issues);
      return child ? { kind: 'not', id: node.id, child } : undefined;
    }
  }
}

function coerceLeaf(node: DraftCmpNode, issues: DraftIssue[]): PersistedCmpNode | undefined {
  if (!isKnownOperator(node.op)) {
    issues.push(structure(node.id, null, `Unknown operator '${node.op}'.`));
    return undefined;
  }
  const a = coerceOperand(node.a, node.op, 'a', node.id, issues);
  let b: PersistedOperand | undefined;
  if (isBinary(node.op)) {
    if (node.b === null) {
      issues.push(structure(node.id, 'b', `Operator '${node.op}' needs a second operand.`));
    } else {
      b = coerceOperand(node.b, node.op, 'b', node.id, issues);
    }
  }
  if (!a) return undefined;
  return b ? { kind: 'cmp', id: node.id, op: node.op, a, b } : { kind: 'cmp', id: node.id, op: node.op, a };
}

// ── Persisted → draft hydration (on Open) — handles BOTH v1 (flat) and v2 (tree). ──

export function logicToTree(logic: ConditionLogicV1 | ConditionLogicTree): DraftTree {
  if (logic.version === 2) {
    return { root: persistedNodeToDraft(logic.root) };
  }
  // v1 (flat) → normalize to a tree exactly as the backend parser does (§10.4).
  const leaves: DraftNode[] = logic.cmps.map((c) => ({
    kind: 'cmp',
    id: c.id,
    op: c.op,
    a: persistedToDraft(c.a),
    b: c.b ? persistedToDraft(c.b) : null,
  }));
  const root: DraftNode =
    leaves.length === 1 ? leaves[0] : { kind: 'group', id: 'root', op: logic.comb, children: leaves };
  return { root };
}

/** Collect every `ref` operand expression across the persisted tree (for the last-run value fetch). */
export function collectTreeRefs(root: PersistedNode): string[] {
  const refs = new Set<string>();
  const walk = (node: PersistedNode): void => {
    if (node.kind === 'cmp') {
      for (const op of [node.a, node.b]) {
        if (op && op.kind === 'ref' && op.ref.trim()) refs.add(op.ref.trim());
      }
    } else if (node.kind === 'group') {
      node.children.forEach(walk);
    } else {
      walk(node.child);
    }
  };
  walk(root);
  return [...refs];
}

function persistedNodeToDraft(node: PersistedNode): DraftNode {
  switch (node.kind) {
    case 'cmp':
      return { kind: 'cmp', id: node.id, op: node.op, a: persistedToDraft(node.a), b: node.b ? persistedToDraft(node.b) : null };
    case 'group':
      return { kind: 'group', id: node.id, op: node.op, children: node.children.map(persistedNodeToDraft) };
    case 'not':
      return { kind: 'not', id: node.id, child: persistedNodeToDraft(node.child) };
  }
}

// ── Structural edits (pure; return a new tree). N1-a: no moveNode/free-wire. ──

// Replace the node with the given id (anywhere in the tree, incl. the root) via `fn`.
function replaceNode(root: DraftNode | null, id: string, fn: (node: DraftNode) => DraftNode): DraftNode | null {
  if (!root) return null;
  if (root.id === id) return fn(root);
  if (root.kind === 'group') {
    return { ...root, children: root.children.map((c) => replaceNode(c, id, fn) as DraftNode) };
  }
  if (root.kind === 'not') {
    return { ...root, child: replaceNode(root.child, id, fn) as DraftNode };
  }
  return root;
}

/** Append a fresh comparator to the group with `groupId`. If the tree is empty, it becomes a bare cmp. */
export function addComparator(tree: DraftTree, groupId: string | null, op = 'eq'): DraftTree {
  if (!tree.root) return { root: newCmpNode(op, freshId(null, 'c')) };
  const leaf = newCmpNode(op, freshId(tree.root, 'c'));
  if (groupId === null) return tree;
  return { root: replaceNode(tree.root, groupId, (n) => (n.kind === 'group' ? { ...n, children: [...n.children, leaf] } : n)) };
}

/** Add a fresh group (seeded with one comparator) to the group with `groupId`. */
export function addGroup(tree: DraftTree, groupId: string, op: Combinator = 'and'): DraftTree {
  if (!tree.root) return tree;
  const group: DraftGroupNode = { kind: 'group', id: freshId(tree.root, 'g'), op, children: [newCmpNode('eq', freshId(tree.root, 'c'))] };
  return { root: replaceNode(tree.root, groupId, (n) => (n.kind === 'group' ? { ...n, children: [...n.children, group] } : n)) };
}

/** Wrap the node (incl. the root) in a new AND/OR group. */
export function wrapInGroup(tree: DraftTree, nodeId: string, op: Combinator = 'and'): DraftTree {
  if (!tree.root) return tree;
  return { root: replaceNode(tree.root, nodeId, (n) => ({ kind: 'group', id: freshId(tree.root, 'g'), op, children: [n] })) };
}

/** Wrap the node (incl. the root) in a NOT. */
export function wrapInNot(tree: DraftTree, nodeId: string): DraftTree {
  if (!tree.root) return tree;
  return { root: replaceNode(tree.root, nodeId, (n) => ({ kind: 'not', id: freshId(tree.root, 'n'), child: n })) };
}

/** Change a comparator leaf's operator (re-flowing the B operand: dropped for unary, seeded for binary). */
export function setLeafOperator(tree: DraftTree, nodeId: string, op: string): DraftTree {
  if (!tree.root) return tree;
  return { root: replaceNode(tree.root, nodeId, (n) => (n.kind === 'cmp' ? { kind: 'cmp', ...setOperator(n, op) } : n)) };
}

/** Replace a comparator leaf's A or B operand. */
export function setLeafOperand(tree: DraftTree, nodeId: string, slot: 'a' | 'b', operand: DraftOperand): DraftTree {
  if (!tree.root) return tree;
  return {
    root: replaceNode(tree.root, nodeId, (n) =>
      n.kind === 'cmp' ? (slot === 'a' ? { ...n, a: operand } : { ...n, b: operand }) : n,
    ),
  };
}

/** Change a group's combinator. */
export function setGroupOp(tree: DraftTree, groupId: string, op: Combinator): DraftTree {
  if (!tree.root) return tree;
  return { root: replaceNode(tree.root, groupId, (n) => (n.kind === 'group' ? { ...n, op } : n)) };
}

/** Remove a node (and its subtree). A NOT that loses its child is removed too; the root → empty. */
export function removeNode(tree: DraftTree, nodeId: string): DraftTree {
  return { root: removeFromTree(tree.root, nodeId) };
}

function removeFromTree(node: DraftNode | null, id: string): DraftNode | null {
  if (!node || node.id === id) return null;
  if (node.kind === 'group') {
    const children = node.children
      .map((c) => removeFromTree(c, id))
      .filter((c): c is DraftNode => c !== null);
    return { ...node, children };
  }
  if (node.kind === 'not') {
    const child = removeFromTree(node.child, id);
    return child ? { ...node, child } : null; // a NOT with no child is meaningless → removed
  }
  return node;
}

/**
 * Unwrap a NOT (replace with its child) or a GROUP (splice its children into the parent). Unwrapping the
 * root group only succeeds when it has exactly one child (no parent to absorb several).
 */
export function unwrap(tree: DraftTree, nodeId: string): DraftTree {
  if (!tree.root) return tree;
  // Root special-cases.
  if (tree.root.id === nodeId) {
    if (tree.root.kind === 'not') return { root: tree.root.child };
    if (tree.root.kind === 'group' && tree.root.children.length === 1) return { root: tree.root.children[0] };
    return tree; // can't lift several children to the root
  }
  return { root: unwrapInTree(tree.root, nodeId) };
}

function unwrapInTree(node: DraftNode, id: string): DraftNode {
  if (node.kind === 'group') {
    const children: DraftNode[] = [];
    for (const c of node.children) {
      if (c.id === id && c.kind === 'group') children.push(...c.children);
      else if (c.id === id && c.kind === 'not') children.push(c.child);
      else children.push(unwrapInTree(c, id));
    }
    return { ...node, children };
  }
  if (node.kind === 'not') {
    // The not's single child can be unwrapped only if it stays a single node (group with 1 child / a not).
    const child = node.child;
    if (child.id === id && child.kind === 'not') return { ...node, child: child.child };
    if (child.id === id && child.kind === 'group' && child.children.length === 1) return { ...node, child: child.children[0] };
    return { ...node, child: unwrapInTree(child, id) };
  }
  return node;
}
