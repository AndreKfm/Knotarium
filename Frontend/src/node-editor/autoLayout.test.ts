import { describe, it, expect } from 'vitest';
import {
  computeAutoLayout,
  computeNestedAutoLayout,
  alignNodes,
  distributeNodes,
  snapValueToGrid,
  snapPointToGrid,
  DEFAULT_NODE_WIDTH,
  type LayoutNodeInput,
  type AlignNodeInput,
} from './autoLayout';

const node = (id: string, extra: Partial<LayoutNodeInput> = {}): LayoutNodeInput => ({
  id,
  width: 200,
  height: 80,
  ...extra,
});

describe('computeNestedAutoLayout', () => {
  it('lays out a loop body inside its container and sizes the container to fit', () => {
    const nodes = [
      { id: 'trigger', type: 'start' },
      { id: 'loop', type: 'forLoop', width: 500, height: 280 },
      { id: 'a', type: 'fireAction', parentId: 'loop', width: 200, height: 80 },
      { id: 'b', type: 'setVariable', parentId: 'loop', width: 200, height: 80 },
    ];
    const edges = [
      { source: 'trigger', target: 'loop' },
      { source: 'a', target: 'b' },
    ];
    const out = computeNestedAutoLayout(nodes, edges, { direction: 'LR' });
    const by = new Map(out.map((p) => [p.id, p]));

    // Every node gets a position; the container is sized.
    expect(out.map((p) => p.id).sort()).toEqual(['a', 'b', 'loop', 'trigger']);
    const loop = by.get('loop')!;
    expect(loop.width).toBeGreaterThanOrEqual(500);
    expect(loop.height).toBeGreaterThanOrEqual(200);

    // Children are positioned INSIDE the container, below the header (relative coords, clear of chrome).
    for (const id of ['a', 'b']) {
      expect(by.get(id)!.x).toBeGreaterThanOrEqual(20);
      expect(by.get(id)!.y).toBeGreaterThanOrEqual(90);
    }
    // In LR order, b sits to the right of a.
    expect(by.get('b')!.x).toBeGreaterThan(by.get('a')!.x);
  });

  it('centers a short body within an oversized container instead of pinning it top-left', () => {
    const nodes = [
      { id: 'loop', type: 'forLoop', width: 500, height: 280 },
      { id: 'only', type: 'fireAction', parentId: 'loop', width: 200, height: 80 },
    ];
    const out = computeNestedAutoLayout(nodes, [], { direction: 'LR' });
    const by = new Map(out.map((p) => [p.id, p]));
    const loop = by.get('loop')!;
    const node = by.get('only')!;

    // Node centre should sit near the container centre horizontally (not hugging the left padding).
    const nodeCentreX = node.x + 200 / 2;
    expect(Math.abs(nodeCentreX - loop.width! / 2)).toBeLessThan(20);
    // And it must stay clear of the header vertically.
    expect(node.y).toBeGreaterThanOrEqual(90);
  });

  it('leaves children of non-loop parents (annotation groups) untouched', () => {
    const nodes = [
      { id: 'group', type: 'group' },
      { id: 'note', type: 'stickyNote', parentId: 'group' },
      { id: 'top', type: 'start' },
    ];
    const out = computeNestedAutoLayout(nodes, [], { direction: 'LR' });
    // The grouped child is not re-laid-out (no result), so the caller keeps its position.
    expect(out.some((p) => p.id === 'note')).toBe(false);
    expect(out.some((p) => p.id === 'group')).toBe(true);
  });
});

describe('computeAutoLayout', () => {
  it('returns a position for every top-level node', () => {
    const nodes = [node('a'), node('b'), node('c')];
    const edges = [
      { source: 'a', target: 'b' },
      { source: 'b', target: 'c' },
    ];
    const out = computeAutoLayout(nodes, edges);
    expect(out.map((p) => p.id).sort()).toEqual(['a', 'b', 'c']);
    expect(out.every((p) => Number.isFinite(p.x) && Number.isFinite(p.y))).toBe(true);
  });

  it('lays a chain out left-to-right by default (x increases along edges)', () => {
    const nodes = [node('a'), node('b'), node('c')];
    const edges = [
      { source: 'a', target: 'b' },
      { source: 'b', target: 'c' },
    ];
    const byId = Object.fromEntries(computeAutoLayout(nodes, edges).map((p) => [p.id, p]));
    expect(byId.a.x).toBeLessThan(byId.b.x);
    expect(byId.b.x).toBeLessThan(byId.c.x);
  });

  it('lays out top-to-bottom when direction is TB (y increases along edges)', () => {
    const nodes = [node('a'), node('b')];
    const edges = [{ source: 'a', target: 'b' }];
    const byId = Object.fromEntries(
      computeAutoLayout(nodes, edges, { direction: 'TB' }).map((p) => [p.id, p]),
    );
    expect(byId.a.y).toBeLessThan(byId.b.y);
  });

  it('excludes nested children and edges touching them', () => {
    const nodes = [node('loop'), node('child', { parentId: 'loop' }), node('after')];
    const edges = [
      { source: 'loop', target: 'after' },
      { source: 'child', target: 'after' }, // touches a nested node -> ignored
    ];
    const out = computeAutoLayout(nodes, edges);
    expect(out.map((p) => p.id).sort()).toEqual(['after', 'loop']);
  });

  it('returns [] when there are no top-level nodes', () => {
    expect(computeAutoLayout([node('c', { parentId: 'x' })], [])).toEqual([]);
  });

  it('falls back to default size for unmeasured nodes without throwing', () => {
    const out = computeAutoLayout([{ id: 'a' }, { id: 'b' }], [{ source: 'a', target: 'b' }]);
    expect(out).toHaveLength(2);
  });

  it('ignores self-edges', () => {
    const out = computeAutoLayout([node('a')], [{ source: 'a', target: 'a' }]);
    expect(out).toHaveLength(1);
  });
});

describe('snapValueToGrid / snapPointToGrid', () => {
  it('rounds to the nearest multiple of the grid size', () => {
    expect(snapValueToGrid(10, 24)).toBe(0); // closer to 0
    expect(snapValueToGrid(13, 24)).toBe(24); // closer to 24
    expect(snapValueToGrid(30, 24)).toBe(24); // closer to 24
    expect(snapValueToGrid(40, 24)).toBe(48); // closer to 48
    expect(snapValueToGrid(36, 24)).toBe(48); // tie rounds up (matches React Flow)
    expect(snapValueToGrid(-13, 24)).toBe(-24);
  });

  it('leaves exact multiples untouched', () => {
    expect(snapValueToGrid(48, 24)).toBe(48);
  });

  it('is a no-op for a non-positive grid size', () => {
    expect(snapValueToGrid(13, 0)).toBe(13);
    expect(snapValueToGrid(13, -5)).toBe(13);
  });

  it('snaps both axes of a point', () => {
    expect(snapPointToGrid({ x: 13, y: 30 }, 24)).toEqual({ x: 24, y: 24 });
  });
});

describe('alignNodes', () => {
  const nodes: AlignNodeInput[] = [
    { id: 'a', x: 0, y: 0, width: 100, height: 40 },
    { id: 'b', x: 50, y: 100, width: 100, height: 60 },
    { id: 'c', x: 200, y: 300, width: 80, height: 40 },
  ];
  const byId = (out: { id: string; x: number; y: number }[]) =>
    Object.fromEntries(out.map((p) => [p.id, p]));

  it('aligns left edges to the minimum x', () => {
    const r = byId(alignNodes(nodes, 'left'));
    expect(r.a.x).toBe(0);
    expect(r.b.x).toBe(0);
    expect(r.c.x).toBe(0);
    expect(r.b.y).toBe(100); // other axis preserved
  });

  it('aligns right edges to the maximum right', () => {
    const r = byId(alignNodes(nodes, 'right'));
    const maxRight = 200 + 80; // c
    expect(r.a.x).toBe(maxRight - 100);
    expect(r.c.x).toBe(maxRight - 80);
  });

  it('aligns top edges to the minimum y', () => {
    const r = byId(alignNodes(nodes, 'top'));
    expect(r.a.y).toBe(0);
    expect(r.b.y).toBe(0);
    expect(r.c.y).toBe(0);
  });

  it('aligns horizontal centres', () => {
    const r = byId(alignNodes(nodes, 'centerX'));
    const center = (0 + 280) / 2; // min left .. max right
    expect(r.a.x).toBe(center - 50);
    expect(r.c.x).toBe(center - 40);
  });

  it('is a no-op for a single node', () => {
    const single = [{ id: 'a', x: 5, y: 7 }];
    expect(alignNodes(single, 'left')).toEqual([{ id: 'a', x: 5, y: 7 }]);
  });

  it('defaults the width when unset', () => {
    const r = byId(alignNodes([{ id: 'a', x: 0, y: 0 }, { id: 'b', x: 500, y: 0 }], 'right'));
    expect(r.a.x).toBe(500); // both width default -> max right is 500+W, a.x = that - W = 500
    expect(r.b.x).toBe(500);
    expect(DEFAULT_NODE_WIDTH).toBeGreaterThan(0);
  });
});

describe('distributeNodes', () => {
  const byId = (out: { id: string; x: number; y: number }[]) =>
    Object.fromEntries(out.map((p) => [p.id, p]));

  it('equalises horizontal gaps, keeping first and last fixed', () => {
    // three 100-wide nodes between x=0 and x=400 -> one node should land centred
    const nodes: AlignNodeInput[] = [
      { id: 'a', x: 0, y: 0, width: 100, height: 40 },
      { id: 'b', x: 130, y: 0, width: 100, height: 40 },
      { id: 'c', x: 400, y: 0, width: 100, height: 40 },
    ];
    const r = byId(distributeNodes(nodes, 'horizontal'));
    expect(r.a.x).toBe(0); // first fixed
    expect(r.c.x).toBe(400); // last fixed
    // span between a.right(100) and c.left(400) is 300; one inner node width 100 -> gaps (300-100)/2 = 100
    expect(r.b.x).toBe(200);
  });

  it('equalises vertical gaps along y', () => {
    const nodes: AlignNodeInput[] = [
      { id: 'a', x: 0, y: 0, width: 100, height: 50 },
      { id: 'b', x: 0, y: 70, width: 100, height: 50 },
      { id: 'c', x: 0, y: 400, width: 100, height: 50 },
    ];
    const r = byId(distributeNodes(nodes, 'vertical'));
    expect(r.a.y).toBe(0);
    expect(r.c.y).toBe(400);
    // span between a.bottom(50) and c.top(400) = 350; inner height 50 -> gap (350-50)/2 = 150 -> b.y = 50+150 = 200
    expect(r.b.y).toBe(200);
  });

  it('sorts by position before distributing (input order irrelevant)', () => {
    const nodes: AlignNodeInput[] = [
      { id: 'c', x: 400, y: 0, width: 100, height: 40 },
      { id: 'a', x: 0, y: 0, width: 100, height: 40 },
      { id: 'b', x: 130, y: 0, width: 100, height: 40 },
    ];
    const r = byId(distributeNodes(nodes, 'horizontal'));
    expect(r.b.x).toBe(200);
  });

  it('is a no-op with fewer than 3 nodes', () => {
    const two: AlignNodeInput[] = [
      { id: 'a', x: 0, y: 0 },
      { id: 'b', x: 50, y: 0 },
    ];
    expect(distributeNodes(two, 'horizontal')).toEqual([
      { id: 'a', x: 0, y: 0 },
      { id: 'b', x: 50, y: 0 },
    ]);
  });
});
