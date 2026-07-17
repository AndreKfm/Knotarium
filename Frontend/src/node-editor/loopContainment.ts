// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Infer loop-container membership from wiring, for graphs that carry no saved parentId — e.g. an
// AI-generated workflow (the model emits no coordinates/containment). Loop membership is otherwise
// authored spatially (drop a node into the container → parentId) and persisted in each node's _metadata;
// when that's absent the body nodes would render as top-level siblings of the loop instead of inside it.
//
// The editor wires a loop body as: container `start` output → body → back into the container `end` input
// (see Canvas auto-wire), with the post-loop continuation on a separate `Done`-style output. So a
// container's body is exactly the nodes reachable FORWARD from its `start` output that loop back into it.
// This mirrors the compiler's topological loop-back definition. Kept React-free so it unit-tests with
// plain node/edge data.

export const LOOP_CONTAINER_TYPES = new Set(['forLoop', 'parallelForEach']);
const BODY_START_HANDLE = 'start';

export function isLoopContainerType(type: string | null | undefined): boolean {
  return !!type && LOOP_CONTAINER_TYPES.has(type);
}

interface ContainmentNode {
  id: string;
  type?: string | null;
}
interface ContainmentEdge {
  source: string;
  sourceHandle?: string | null;
  target: string;
}

/**
 * Map each node id to the id of the INNERMOST loop container whose body contains it (nodes in no body
 * are absent → they stay top-level). Nested loops resolve to their own container because the smaller
 * (more-nested) body wins.
 */
export function inferLoopContainment(
  nodes: readonly ContainmentNode[],
  edges: readonly ContainmentEdge[],
): Map<string, string> {
  const containers = nodes.filter((n) => isLoopContainerType(n.type));
  const bodyByContainer = new Map<string, Set<string>>();

  for (const c of containers) {
    const seeds = edges
      .filter((e) => e.source === c.id && (e.sourceHandle ?? '') === BODY_START_HANDLE)
      .map((e) => e.target);

    const body = new Set<string>();
    const queue = [...seeds];
    while (queue.length > 0) {
      const cur = queue.pop()!;
      if (cur === c.id || body.has(cur)) continue;
      body.add(cur);
      for (const e of edges) {
        if (e.source !== cur) continue;
        if (e.target === c.id) continue; // loop-back into the container — a terminal edge, don't cross it
        queue.push(e.target);
      }
    }
    body.delete(c.id);
    bodyByContainer.set(c.id, body);
  }

  const parentByChild = new Map<string, string>();
  for (const node of nodes) {
    let best: string | undefined;
    let bestSize = Infinity;
    for (const [cid, body] of bodyByContainer) {
      if (cid === node.id) continue;
      if (body.has(node.id) && body.size < bestSize) {
        best = cid;
        bestSize = body.size;
      }
    }
    if (best) parentByChild.set(node.id, best);
  }
  return parentByChild;
}

/**
 * Reorder nodes so every parent precedes its children — React Flow drops a nested node that appears
 * before its parent in the array. Stable for unrelated nodes. Tolerates a dangling parentId (treated as
 * top-level).
 */
export function orderParentsBeforeChildren<T extends { id: string; parentId?: string }>(list: readonly T[]): T[] {
  const byId = new Map(list.map((node) => [node.id, node]));
  const seen = new Set<string>();
  const out: T[] = [];
  const visit = (node: T) => {
    if (seen.has(node.id)) return;
    const parent = node.parentId ? byId.get(node.parentId) : undefined;
    if (parent && !seen.has(parent.id)) visit(parent);
    seen.add(node.id);
    out.push(node);
  };
  for (const node of list) visit(node);
  return out;
}
