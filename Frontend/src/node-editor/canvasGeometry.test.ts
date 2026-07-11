import { describe, expect, it } from 'vitest';
import {
  getPortPositions,
  getFreePorts,
  findNearestCompatiblePort,
  distanceToSegment,
  findEdgeUnderPoint,
  collectDownstream,
  type InternalNodeLike,
  type EdgeLike,
  type PortPosition,
} from './canvasGeometry';

// A small node helper: positions a single input handle on the left edge and a
// single output handle on the right edge of a `width`×`height` box.
function node(
  id: string,
  x: number,
  y: number,
  opts: { width?: number; height?: number; source?: string[]; target?: string[] } = {},
): InternalNodeLike {
  const width = opts.width ?? 200;
  const height = opts.height ?? 100;
  const source = opts.source ?? ['result'];
  const target = opts.target ?? ['in'];
  const hs = 8; // handle size
  return {
    id,
    position: { x, y },
    internals: {
      positionAbsolute: { x, y },
      handleBounds: {
        source: source.map((sid, i) => ({
          id: sid,
          x: width - hs / 2,
          y: height / 2 - hs / 2 + i * 20,
          width: hs,
          height: hs,
        })),
        target: target.map((tid, i) => ({
          id: tid,
          x: -hs / 2,
          y: height / 2 - hs / 2 + i * 20,
          width: hs,
          height: hs,
        })),
      },
    },
  };
}

describe('getPortPositions', () => {
  it('resolves handle centres in absolute flow space', () => {
    const ports = getPortPositions(node('a', 100, 50));
    const out = ports.find((p) => p.kind === 'source')!;
    const inp = ports.find((p) => p.kind === 'target')!;
    // width 200, height 100 → source centre at right edge mid-height
    expect(out).toMatchObject({ nodeId: 'a', handleId: 'result', kind: 'source' });
    expect(out.x).toBeCloseTo(300); // 100 + 200
    expect(out.y).toBeCloseTo(100); // 50 + 50
    expect(inp).toMatchObject({ nodeId: 'a', handleId: 'in', kind: 'target' });
    expect(inp.x).toBeCloseTo(100); // left edge
    expect(inp.y).toBeCloseTo(100);
  });

  it('prefers positionAbsolute over relative position', () => {
    const n = node('a', 0, 0);
    n.position = { x: 5, y: 5 };
    n.internals!.positionAbsolute = { x: 500, y: 300 };
    const out = getPortPositions(n).find((p) => p.kind === 'source')!;
    expect(out.x).toBeCloseTo(700);
    expect(out.y).toBeCloseTo(350);
  });

  it('returns [] when handle bounds are unmeasured', () => {
    expect(getPortPositions({ id: 'x', position: { x: 0, y: 0 } })).toEqual([]);
    expect(getPortPositions({ id: 'x', internals: { handleBounds: null } })).toEqual([]);
  });

  it('falls back to default handle ids when id is null', () => {
    const n: InternalNodeLike = {
      id: 'a',
      internals: {
        positionAbsolute: { x: 0, y: 0 },
        handleBounds: {
          source: [{ id: null, x: 0, y: 0, width: 4, height: 4 }],
          target: [{ id: null, x: 0, y: 0, width: 4, height: 4 }],
        },
      },
    };
    const ports = getPortPositions(n);
    expect(ports.find((p) => p.kind === 'source')!.handleId).toBe('result');
    expect(ports.find((p) => p.kind === 'target')!.handleId).toBe('in');
  });
});

describe('getFreePorts', () => {
  it('excludes ports that already have an edge', () => {
    const a = node('a', 0, 0);
    const b = node('b', 400, 0);
    const edges: EdgeLike[] = [{ id: 'e1', source: 'a', sourceHandle: 'result', target: 'b', targetHandle: 'in' }];
    const free = getFreePorts([a, b], edges);
    // a.result wired, b.in wired → only a.in (target) and b.result (source) free
    const keys = free.map((p) => `${p.nodeId}.${p.handleId}.${p.kind}`).sort();
    expect(keys).toEqual(['a.in.target', 'b.result.source']);
  });

  it('keeps a multi-output source eligible on its unwired branches', () => {
    const cond = node('c', 0, 0, { source: ['true', 'false'] });
    const sink = node('s', 400, 0);
    const edges: EdgeLike[] = [{ id: 'e1', source: 'c', sourceHandle: 'true', target: 's', targetHandle: 'in' }];
    const free = getFreePorts([cond, sink], edges);
    expect(free.some((p) => p.nodeId === 'c' && p.handleId === 'false' && p.kind === 'source')).toBe(true);
    expect(free.some((p) => p.nodeId === 'c' && p.handleId === 'true')).toBe(false);
  });

  it('never offers a fan-in target (end) as free', () => {
    const loop = node('loop', 0, 0, { target: ['end'] });
    const free = getFreePorts([loop], []);
    expect(free.some((p) => p.handleId === 'end')).toBe(false);
  });

  it('honours a custom fan-in predicate (e.g. join nodes)', () => {
    const join = node('j', 0, 0, { target: ['in'] });
    const free = getFreePorts([join], [], (nodeId) => nodeId === 'j');
    expect(free.some((p) => p.nodeId === 'j' && p.kind === 'target')).toBe(false);
  });
});

describe('findNearestCompatiblePort', () => {
  it('matches a new source to the closest free target within threshold', () => {
    const newNode = node('new', 0, 0); // source at (200,50)
    const existing = node('ex', 240, 0); // target at (240,50) → distance 40
    const free = getFreePorts([existing], []);
    const match = findNearestCompatiblePort(getPortPositions(newNode), free, 60);
    expect(match).not.toBeNull();
    expect(match!.source).toEqual({ nodeId: 'new', handleId: 'result' });
    expect(match!.target).toEqual({ nodeId: 'ex', handleId: 'in' });
    expect(match!.newPortKind).toBe('source');
  });

  it('matches a new target to an existing free source (upstream orientation)', () => {
    const upstream = node('up', 0, 0); // source at (200,50)
    const newNode = node('new', 240, 0); // target at (240,50)
    const free = getFreePorts([upstream], []);
    const match = findNearestCompatiblePort(getPortPositions(newNode), free, 60);
    expect(match!.source).toEqual({ nodeId: 'up', handleId: 'result' });
    expect(match!.target).toEqual({ nodeId: 'new', handleId: 'in' });
    expect(match!.newPortKind).toBe('target');
  });

  it('returns null when nothing is within threshold', () => {
    const newNode = node('new', 0, 0);
    const existing = node('ex', 1000, 0);
    const free = getFreePorts([existing], []);
    expect(findNearestCompatiblePort(getPortPositions(newNode), free, 60)).toBeNull();
  });

  it('picks the closest of several candidates', () => {
    const newNode = node('new', 0, 0); // source at (200,50)
    const far = node('far', 250, 0); // dist 50
    const near = node('near', 230, 0); // dist 30
    const free = getFreePorts([far, near], []);
    const match = findNearestCompatiblePort(getPortPositions(newNode), free, 60);
    expect(match!.target.nodeId).toBe('near');
  });

  it('respects the isValid predicate (rejects self / invalid pairs)', () => {
    const newNode = node('new', 0, 0);
    const existing = node('ex', 240, 0);
    const free = getFreePorts([existing], []);
    const match = findNearestCompatiblePort(getPortPositions(newNode), free, 60, () => false);
    expect(match).toBeNull();
  });

  it('does not match two ports of the same node', () => {
    const n = node('n', 0, 0, { width: 30 }); // source ~(30,50), target ~(0,50) close
    const ports = getPortPositions(n);
    expect(findNearestCompatiblePort(ports, ports, 100)).toBeNull();
  });
});

describe('distanceToSegment', () => {
  it('is the perpendicular distance for a point beside the segment', () => {
    expect(distanceToSegment({ x: 5, y: 5 }, { x: 0, y: 0 }, { x: 10, y: 0 })).toBeCloseTo(5);
  });
  it('clamps to the nearest endpoint when the projection falls outside', () => {
    expect(distanceToSegment({ x: -3, y: 4 }, { x: 0, y: 0 }, { x: 10, y: 0 })).toBeCloseTo(5);
  });
  it('handles a degenerate zero-length segment', () => {
    expect(distanceToSegment({ x: 3, y: 4 }, { x: 0, y: 0 }, { x: 0, y: 0 })).toBeCloseTo(5);
  });
});

describe('findEdgeUnderPoint', () => {
  function scene() {
    const a = node('a', 0, 0); // source at (200,50)
    const b = node('b', 400, 0); // target at (400,50)
    const edges: EdgeLike[] = [{ id: 'e1', source: 'a', sourceHandle: 'result', target: 'b', targetHandle: 'in' }];
    const ports = [...getPortPositions(a), ...getPortPositions(b)];
    return { a, b, edges, ports };
  }

  it('hits an edge when the point is near its midline', () => {
    const { edges, ports } = scene();
    const hit = findEdgeUnderPoint(edges, ports, { x: 300, y: 55 }, 24);
    expect(hit).not.toBeNull();
    expect(hit!.edge.id).toBe('e1');
    expect(hit!.midpoint.x).toBeCloseTo(300); // (200+400)/2
    expect(hit!.midpoint.y).toBeCloseTo(50);
  });

  it('misses when the point is beyond tolerance', () => {
    const { edges, ports } = scene();
    expect(findEdgeUnderPoint(edges, ports, { x: 300, y: 200 }, 24)).toBeNull();
  });

  it('skips edges whose endpoints are not resolvable', () => {
    const { ports } = scene();
    const edges: EdgeLike[] = [{ id: 'ghost', source: 'zzz', target: 'qqq' }];
    expect(findEdgeUnderPoint(edges, ports, { x: 300, y: 50 }, 24)).toBeNull();
  });

  it('never reports a hit for a non-finite point (defensive guard)', () => {
    const { edges, ports } = scene();
    expect(findEdgeUnderPoint(edges, ports, { x: NaN, y: NaN }, 24)).toBeNull();
  });

  it('returns the closest of overlapping edges', () => {
    const a = node('a', 0, 0);
    const b = node('b', 400, 0);
    const c = node('c', 0, 100); // its source at (200,150)
    const d = node('d', 400, 100); // target (400,150)
    const ports: PortPosition[] = [a, b, c, d].flatMap(getPortPositions);
    const edges: EdgeLike[] = [
      { id: 'top', source: 'a', sourceHandle: 'result', target: 'b', targetHandle: 'in' }, // y≈50
      { id: 'bot', source: 'c', sourceHandle: 'result', target: 'd', targetHandle: 'in' }, // y≈150
    ];
    const hit = findEdgeUnderPoint(edges, ports, { x: 300, y: 60 }, 40);
    expect(hit!.edge.id).toBe('top');
  });
});

describe('collectDownstream', () => {
  it('collects the start node and everything reachable forward', () => {
    const edges: EdgeLike[] = [
      { id: '1', source: 'a', target: 'b' },
      { id: '2', source: 'b', target: 'c' },
      { id: '3', source: 'x', target: 'a' }, // upstream, excluded
    ];
    const set = collectDownstream('b', edges);
    expect([...set].sort()).toEqual(['b', 'c']);
  });

  it('terminates on cycles', () => {
    const edges: EdgeLike[] = [
      { id: '1', source: 'a', target: 'b' },
      { id: '2', source: 'b', target: 'a' },
    ];
    const set = collectDownstream('a', edges);
    expect([...set].sort()).toEqual(['a', 'b']);
  });
});
