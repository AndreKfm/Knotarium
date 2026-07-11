import { describe, expect, it } from 'vitest';
import type { Node as RFNode, Edge } from '@xyflow/react';
import { cloneSubgraph } from './clipboard';

function n(id: string, x = 0, y = 0, extra: Partial<RFNode> = {}): RFNode {
  return { id, type: 'log', position: { x, y }, data: { properties: { v: id } }, ...extra };
}
function e(id: string, source: string, target: string): Edge {
  return { id, source, sourceHandle: 'result', target, targetHandle: 'in' };
}

// Deterministic id factory for assertions.
function counter() {
  let i = 0;
  return () => `clone-${i++}`;
}

describe('cloneSubgraph', () => {
  it('clones nodes with new ids, offset positions, and deep-cloned data', () => {
    const src = [n('a', 100, 100)];
    const { nodes } = cloneSubgraph(src, [], { newId: counter(), offset: { x: 40, y: 40 } });
    expect(nodes).toHaveLength(1);
    expect(nodes[0].id).toBe('clone-0');
    expect(nodes[0].position).toEqual({ x: 140, y: 140 });
    expect(nodes[0].selected).toBe(true);
    // Deep clone — mutating the clone doesn't touch the source.
    (nodes[0].data as { properties: { v: string } }).properties.v = 'mutated';
    expect((src[0].data as { properties: { v: string } }).properties.v).toBe('a');
  });

  it('keeps only internal edges and re-points them at the clones', () => {
    const nodes = [n('a'), n('b'), n('c')];
    const edges = [e('e-ab', 'a', 'b'), e('e-bc', 'b', 'c'), e('e-cx', 'c', 'outside')];
    // Select a and b only → only e-ab is internal.
    const map = new Map([['a', 'A'], ['b', 'B']]);
    const newId = (_t: string | undefined, old: string) => map.get(old)!;
    const res = cloneSubgraph([nodes[0], nodes[1]], edges, { newId, offset: { x: 0, y: 0 } });
    expect(res.edges).toHaveLength(1);
    expect(res.edges[0]).toMatchObject({ source: 'A', target: 'B', sourceHandle: 'result', targetHandle: 'in' });
    expect(res.edges[0].id).toBe('e-A-result-B-in');
  });

  it('remaps parentId when the parent is also copied', () => {
    const parent = n('p', 0, 0, { type: 'forLoop' });
    const child = n('c', 10, 10, { parentId: 'p', extent: 'parent' });
    const map = new Map([['p', 'P'], ['c', 'C']]);
    const res = cloneSubgraph([parent, child], [], { newId: (_t, old) => map.get(old)!, offset: { x: 5, y: 5 } });
    const clonedChild = res.nodes.find((x) => x.id === 'C')!;
    expect(clonedChild.parentId).toBe('P');
  });

  it('detaches a child whose parent is not in the selection', () => {
    const child = n('c', 10, 10, { parentId: 'p', extent: 'parent' });
    const res = cloneSubgraph([child], [], { newId: counter(), offset: { x: 5, y: 5 } });
    expect(res.nodes[0].parentId).toBeUndefined();
    expect(res.nodes[0].extent).toBeUndefined();
  });

  it('drops edges that cross the selection boundary', () => {
    const nodes = [n('a'), n('b')];
    const edges = [e('e-ax', 'a', 'x'), e('e-yb', 'y', 'b')];
    const res = cloneSubgraph(nodes, edges, { newId: counter(), offset: { x: 0, y: 0 } });
    expect(res.edges).toHaveLength(0);
  });

  it('returns an empty result for an empty selection', () => {
    expect(cloneSubgraph([], [e('e', 'a', 'b')], { newId: counter(), offset: { x: 0, y: 0 } })).toEqual({
      nodes: [],
      edges: [],
    });
  });
});
