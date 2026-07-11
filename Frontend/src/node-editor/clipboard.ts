import type { Node as RFNode, Edge } from '@xyflow/react';

// Pure subgraph cloning for copy/paste/duplicate.
//
// Given a set of selected nodes and the full edge list, produce fresh nodes
// (new ids, offset positions, cloned data) plus the internal edges (both
// endpoints in the selection) re-pointed at the clones. Edges that cross the
// selection boundary are dropped — there's nothing to attach their other end to.

export interface CloneOptions {
  /** Produces a unique id for a clone, given the original node's type and id. */
  newId: (type: string | undefined, oldId: string) => string;
  /** Flow-space offset applied to every cloned node's position. */
  offset: { x: number; y: number };
}

export interface ClonedSubgraph {
  nodes: RFNode[];
  edges: Edge[];
}

/**
 * Clone `selectedNodes` (+ their internal edges from `allEdges`) with new ids.
 * Parent links are preserved only when the parent is part of the selection;
 * otherwise the clone is detached to the top level (its position, previously
 * relative, is treated as-is plus the offset — a v1 simplification).
 * All cloned nodes come back `selected: true` so the paste lands selected.
 */
export function cloneSubgraph(
  selectedNodes: RFNode[],
  allEdges: Edge[],
  opts: CloneOptions,
): ClonedSubgraph {
  const idMap = new Map<string, string>();
  for (const n of selectedNodes) idMap.set(n.id, opts.newId(n.type, n.id));
  const selectedIds = new Set(selectedNodes.map((n) => n.id));

  const nodes: RFNode[] = selectedNodes.map((n) => {
    const clone = structuredClone(n) as RFNode;
    clone.id = idMap.get(n.id)!;
    clone.selected = true;
    clone.position = { x: n.position.x + opts.offset.x, y: n.position.y + opts.offset.y };
    if (n.parentId && idMap.has(n.parentId)) {
      clone.parentId = idMap.get(n.parentId);
    } else {
      delete clone.parentId;
      delete clone.extent;
    }
    return clone;
  });

  const edges: Edge[] = allEdges
    .filter((e) => selectedIds.has(e.source) && selectedIds.has(e.target))
    .map((e) => {
      const source = idMap.get(e.source)!;
      const target = idMap.get(e.target)!;
      const clone = structuredClone(e) as Edge;
      clone.id = `e-${source}-${e.sourceHandle ?? 'result'}-${target}-${e.targetHandle ?? 'in'}`;
      clone.source = source;
      clone.target = target;
      clone.selected = false;
      return clone;
    });

  return { nodes, edges };
}
