import dagre from '@dagrejs/dagre';
import { isLoopContainerType } from './loopContainment';

/**
 * Pure layout helpers for the editor "tidy" button and multi-select
 * align/distribute. Kept free of React Flow so they unit-test with plain data.
 *
 * Auto-layout uses dagre over the **top-level** graph only (nodes without a
 * `parentId`); children inside loop/subflow containers keep their relative
 * positions, since dagre flattens nesting poorly. Container nodes participate
 * at their own measured size, so the body moves as a block.
 */

/** Fallbacks when a node hasn't been measured by React Flow yet. */
export const DEFAULT_NODE_WIDTH = 220;
export const DEFAULT_NODE_HEIGHT = 80;

export interface LayoutNodeInput {
  id: string;
  width?: number;
  height?: number;
  /** Set for nodes nested inside a container; excluded from top-level layout. */
  parentId?: string;
}

export interface LayoutEdgeInput {
  source: string;
  target: string;
}

export interface LayoutOptions {
  direction?: 'LR' | 'TB';
  /** Gap between nodes in the same rank (px). */
  nodeSep?: number;
  /** Gap between ranks (px). */
  rankSep?: number;
  defaultWidth?: number;
  defaultHeight?: number;
}

/** Top-left position for a node after layout. */
export interface LayoutPosition {
  id: string;
  x: number;
  y: number;
}

const sizeOf = (n: LayoutNodeInput, opts: Required<Pick<LayoutOptions, 'defaultWidth' | 'defaultHeight'>>) => ({
  width: n.width && n.width > 0 ? n.width : opts.defaultWidth,
  height: n.height && n.height > 0 ? n.height : opts.defaultHeight,
});

/**
 * Compute tidy positions for the top-level graph. Returns top-left positions
 * for every top-level node (those without a `parentId`); nested children are
 * left untouched (the caller keeps their existing positions). Edges that touch
 * a nested node are ignored for layout purposes.
 */
export function computeAutoLayout(
  nodes: readonly LayoutNodeInput[],
  edges: readonly LayoutEdgeInput[],
  options: LayoutOptions = {},
): LayoutPosition[] {
  const direction = options.direction ?? 'LR';
  const nodeSep = options.nodeSep ?? 60;
  const rankSep = options.rankSep ?? 120;
  const defaults = {
    defaultWidth: options.defaultWidth ?? DEFAULT_NODE_WIDTH,
    defaultHeight: options.defaultHeight ?? DEFAULT_NODE_HEIGHT,
  };

  const topLevel = nodes.filter((n) => !n.parentId);
  if (topLevel.length === 0) return [];
  const topLevelIds = new Set(topLevel.map((n) => n.id));

  const g = new dagre.graphlib.Graph();
  g.setGraph({ rankdir: direction, nodesep: nodeSep, ranksep: rankSep });
  g.setDefaultEdgeLabel(() => ({}));

  for (const n of topLevel) {
    const { width, height } = sizeOf(n, defaults);
    g.setNode(n.id, { width, height });
  }
  for (const e of edges) {
    if (topLevelIds.has(e.source) && topLevelIds.has(e.target) && e.source !== e.target) {
      g.setEdge(e.source, e.target);
    }
  }

  dagre.layout(g);

  return topLevel.map((n) => {
    const { width, height } = sizeOf(n, defaults);
    const pos = g.node(n.id);
    // dagre gives the node centre; React Flow positions are top-left.
    return { id: n.id, x: pos.x - width / 2, y: pos.y - height / 2 };
  });
}

// ── Nested (container-aware) layout ─────────────────────────────────────────
// Padding inside a loop/subflow container: room for the header + resizer chrome above the body, and a
// margin around it. Keeps auto-laid children clear of the container's title/pins.
const CONTAINER_PAD = { top: 104, left: 32, right: 32, bottom: 32 } as const;
const MIN_CONTAINER_WIDTH = 320;
const MIN_CONTAINER_HEIGHT = 200;

export interface NestedLayoutNode {
  id: string;
  /** Node type — container types (forLoop/parallelForEach) get sized to fit their body. */
  type?: string | null;
  /** Container membership; children lay out inside their parent, relative to it. */
  parentId?: string;
  width?: number;
  height?: number;
}

/** Layout result. `x`/`y` are top-left — absolute for top-level nodes, parent-relative for children.
 * `width`/`height` are set for container nodes (sized to fit their body). */
export interface NestedLayoutResult {
  id: string;
  x: number;
  y: number;
  width?: number;
  height?: number;
}

/**
 * Container-aware tidy: lays out the top-level graph with dagre AND each loop/subflow container's body
 * inside it (bottom-up, so a container is sized to fit its children before its own parent places it).
 * Unlike {@link computeAutoLayout} this positions nested children too — the fix for graphs (e.g. AI
 * output) whose container bodies would otherwise pile up at the origin. Returns a position for every node.
 */
export function computeNestedAutoLayout(
  nodes: readonly NestedLayoutNode[],
  edges: readonly LayoutEdgeInput[],
  options: LayoutOptions = {},
): NestedLayoutResult[] {
  const direction = options.direction ?? 'LR';
  const nodeSep = options.nodeSep ?? 60;
  const rankSep = options.rankSep ?? 120;
  const defaults = {
    defaultWidth: options.defaultWidth ?? DEFAULT_NODE_WIDTH,
    defaultHeight: options.defaultHeight ?? DEFAULT_NODE_HEIGHT,
  };

  const childrenByParent = new Map<string | undefined, NestedLayoutNode[]>();
  const passedSize = new Map<string, { width?: number; height?: number }>();
  for (const n of nodes) {
    const key = n.parentId ?? undefined;
    const list = childrenByParent.get(key);
    if (list) list.push(n); else childrenByParent.set(key, [n]);
    passedSize.set(n.id, { width: n.width, height: n.height });
  }

  const results: NestedLayoutResult[] = [];
  const sizeById = new Map<string, { width: number; height: number }>();

  // A container's final box: at least its current/passed size, big enough for the body + padding, and
  // never below the minimum.
  const containerBox = (containerId: string, contentW: number, contentH: number) => {
    const p = passedSize.get(containerId) ?? {};
    return {
      width: Math.max(p.width ?? 0, contentW + CONTAINER_PAD.left + CONTAINER_PAD.right, MIN_CONTAINER_WIDTH),
      height: Math.max(p.height ?? 0, contentH + CONTAINER_PAD.top + CONTAINER_PAD.bottom, MIN_CONTAINER_HEIGHT),
    };
  };

  // Lay out one sibling group (all nodes sharing a parentId). Recurses into container children first so
  // their fitted size is known. A container body is CENTERED within its container's content area (not
  // pinned top-left). Returns the group's outer size (the container box when parentId is a container).
  const layoutGroup = (parentId: string | undefined): { width: number; height: number } => {
    const group = childrenByParent.get(parentId) ?? [];
    if (group.length === 0) {
      // An empty container still needs a sensible size recorded for its own parent's layout.
      if (parentId) {
        const box = containerBox(parentId, 0, 0);
        sizeById.set(parentId, box);
        return box;
      }
      return { width: 0, height: 0 };
    }

    // Nested containers self-size (and record their box) before this group's dagre pass reads their size.
    for (const n of group) {
      if (isLoopContainerType(n.type)) layoutGroup(n.id);
    }

    const g = new dagre.graphlib.Graph();
    g.setGraph({ rankdir: direction, nodesep: nodeSep, ranksep: rankSep });
    g.setDefaultEdgeLabel(() => ({}));
    const ids = new Set(group.map((n) => n.id));
    const sizeFor = (n: NestedLayoutNode) => sizeById.get(n.id) ?? sizeOf(n, defaults);
    for (const n of group) {
      const s = sizeFor(n);
      g.setNode(n.id, { width: s.width, height: s.height });
    }
    for (const e of edges) {
      if (ids.has(e.source) && ids.has(e.target) && e.source !== e.target) g.setEdge(e.source, e.target);
    }
    dagre.layout(g);

    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    const placed = group.map((n) => {
      const s = sizeFor(n);
      const p = g.node(n.id);
      const x = p.x - s.width / 2;
      const y = p.y - s.height / 2;
      minX = Math.min(minX, x); minY = Math.min(minY, y);
      maxX = Math.max(maxX, x + s.width); maxY = Math.max(maxY, y + s.height);
      return { n, x, y, s };
    });
    const contentW = maxX - minX;
    const contentH = maxY - minY;

    const emit = (offsetX: number, offsetY: number) => {
      for (const { n, x, y, s } of placed) {
        results.push({
          id: n.id,
          x: x - minX + offsetX,
          y: y - minY + offsetY,
          ...(isLoopContainerType(n.type) ? { width: s.width, height: s.height } : {}),
        });
      }
    };

    if (parentId) {
      // Container body: size the box, then center the body inside the content area (below the header),
      // so a short chain sits in the middle of the loop rather than pinned to the top-left corner.
      const box = containerBox(parentId, contentW, contentH);
      sizeById.set(parentId, box);
      const slackX = Math.max(0, box.width - CONTAINER_PAD.left - CONTAINER_PAD.right - contentW);
      const slackY = Math.max(0, box.height - CONTAINER_PAD.top - CONTAINER_PAD.bottom - contentH);
      emit(CONTAINER_PAD.left + slackX / 2, CONTAINER_PAD.top + slackY / 2);
      return box;
    }

    emit(0, 0); // top-level: normalize to the origin
    return { width: contentW, height: contentH };
  };

  layoutGroup(undefined);
  return results;
}

/** Round a value to the nearest multiple of `size` (matches React Flow's snapGrid). */
export function snapValueToGrid(value: number, size: number): number {
  if (!(size > 0)) return value;
  return size * Math.round(value / size);
}

/** Snap a point's x/y to the grid. Used to align programmatic placement
 * (auto-layout, drop, paste) with the same grid manual drags snap to. */
export function snapPointToGrid<P extends { x: number; y: number }>(point: P, size: number): { x: number; y: number } {
  return { x: snapValueToGrid(point.x, size), y: snapValueToGrid(point.y, size) };
}

// ── Align / Distribute (multi-select) ──────────────────────────────────────

/** A positioned, sized node for align/distribute math. */
export interface AlignNodeInput {
  id: string;
  x: number;
  y: number;
  width?: number;
  height?: number;
}

export type AlignEdge = 'left' | 'right' | 'top' | 'bottom' | 'centerX' | 'centerY';
export type DistributeAxis = 'horizontal' | 'vertical';

const w = (n: AlignNodeInput) => (n.width && n.width > 0 ? n.width : DEFAULT_NODE_WIDTH);
const h = (n: AlignNodeInput) => (n.height && n.height > 0 ? n.height : DEFAULT_NODE_HEIGHT);

/**
 * Align nodes along an edge or centre line. Needs ≥2 nodes; returns new
 * top-left positions for every input (unchanged axis is preserved).
 */
export function alignNodes(nodes: readonly AlignNodeInput[], edge: AlignEdge): LayoutPosition[] {
  if (nodes.length < 2) return nodes.map((n) => ({ id: n.id, x: n.x, y: n.y }));

  const lefts = nodes.map((n) => n.x);
  const rights = nodes.map((n) => n.x + w(n));
  const tops = nodes.map((n) => n.y);
  const bottoms = nodes.map((n) => n.y + h(n));

  switch (edge) {
    case 'left': {
      const v = Math.min(...lefts);
      return nodes.map((n) => ({ id: n.id, x: v, y: n.y }));
    }
    case 'right': {
      const v = Math.max(...rights);
      return nodes.map((n) => ({ id: n.id, x: v - w(n), y: n.y }));
    }
    case 'top': {
      const v = Math.min(...tops);
      return nodes.map((n) => ({ id: n.id, x: n.x, y: v }));
    }
    case 'bottom': {
      const v = Math.max(...bottoms);
      return nodes.map((n) => ({ id: n.id, x: n.x, y: v - h(n) }));
    }
    case 'centerX': {
      const v = (Math.min(...lefts) + Math.max(...rights)) / 2;
      return nodes.map((n) => ({ id: n.id, x: v - w(n) / 2, y: n.y }));
    }
    case 'centerY': {
      const v = (Math.min(...tops) + Math.max(...bottoms)) / 2;
      return nodes.map((n) => ({ id: n.id, x: n.x, y: v - h(n) / 2 }));
    }
  }
}

/**
 * Distribute nodes so the **gaps** between consecutive nodes are equal along
 * the axis. The first and last nodes (by position) stay put; the rest are
 * spread evenly between them. Needs ≥3 nodes to have any effect.
 */
export function distributeNodes(nodes: readonly AlignNodeInput[], axis: DistributeAxis): LayoutPosition[] {
  const identity = () => nodes.map((n) => ({ id: n.id, x: n.x, y: n.y }));
  if (nodes.length < 3) return identity();

  const horizontal = axis === 'horizontal';
  const start = (n: AlignNodeInput) => (horizontal ? n.x : n.y);
  const size = (n: AlignNodeInput) => (horizontal ? w(n) : h(n));

  const sorted = [...nodes].sort((a, b) => start(a) - start(b));
  const first = sorted[0];
  const last = sorted[sorted.length - 1];

  const span = start(last) - (start(first) + size(first));
  const totalInner = sorted.slice(1, -1).reduce((sum, n) => sum + size(n), 0);
  const gap = (span - totalInner) / (sorted.length - 1);

  const posById = new Map<string, number>();
  let cursor = start(first) + size(first) + gap;
  for (let i = 1; i < sorted.length - 1; i++) {
    posById.set(sorted[i].id, cursor);
    cursor += size(sorted[i]) + gap;
  }

  return nodes.map((n) => {
    const moved = posById.get(n.id);
    if (moved === undefined) return { id: n.id, x: n.x, y: n.y };
    return horizontal ? { id: n.id, x: moved, y: n.y } : { id: n.id, x: n.x, y: moved };
  });
}
