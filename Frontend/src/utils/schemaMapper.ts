import type { Node as RFNode, Edge as RFEdge } from '@xyflow/react';
import type { WorkflowDefinition, NodeDefinition, EdgeDefinition } from '../types';
import { orderParentsBeforeChildren } from '../node-editor/loopContainment';

// Branch nodes expose semantic output ports (success/error, true/false, cases, …) and keep them.
const BRANCH_NODE_TYPES = new Set([
  'condition', 'httprequest', 'transform', 'merge', 'switch', 'scheduler', 'forloop', 'parallelforeach',
]);

function isBranchNodeType(nodeType: string | undefined): boolean {
  if (!nodeType) return false;
  const t = nodeType.toLowerCase();
  return t.startsWith('openapi.') || t === 'restcaller' || BRANCH_NODE_TYPES.has(t);
}

// Self-heal legacy edges: the generic single data-output port was renamed `success` -> `result`
// for non-branch nodes. Normalize stale `success` handles so a canvas loaded before the rename
// (or a workflow not yet touched by the backend startup migration) still compiles.
function canonicalizeOutput(nodeType: string | undefined, output: string): string {
  return output === 'success' && !isBranchNodeType(nodeType) ? 'result' : output;
}

// Handles are case-folded for the backend's canonical names (Success/True/…), EXCEPT device-block pin
// handles (`evt:<type>` / `act:<type>`), whose suffix is a case-sensitive signal-type id. Folding those
// (e.g. `act:CustomAction` → `act:customaction`) breaks the device-pin handle match and the edge is
// pruned on reload — so preserve their case verbatim.
function normalizeHandle(handle: string): string {
  return handle.startsWith('evt:') || handle.startsWith('act:') ? handle : handle.toLowerCase();
}

// Resizable containers (groups, loop/parallelForEach boxes) carry an explicit size. After a
// NodeResizer drag that size lives on the node's top-level width/height (xyflow v12 sets
// `node.width`/`node.height`, leaving `node.style` untouched); at creation/reload it's in
// `node.style`. Prefer the resizer-authoritative value, fall back to style. Non-container nodes
// have neither, so this stays undefined and nothing is persisted for them.
function containerWidth(node: RFNode): number | undefined {
  if (typeof node.width === 'number') return node.width;
  return node.style?.width ? Number(node.style.width) : undefined;
}

function containerHeight(node: RFNode): number | undefined {
  if (typeof node.height === 'number') return node.height;
  return node.style?.height ? Number(node.style.height) : undefined;
}

export const schemaMapper = {
  /**
   * Convert Backend WorkflowDefinition to React Flow format.
   */
  toReactFlow(definition: WorkflowDefinition): { nodes: RFNode[]; edges: RFEdge[] } {
    const nodeTypeById = new Map((definition.nodes || []).map((node) => [node.id.value, node.type]));
    const sourceHandleFor = (edge: EdgeDefinition): string => {
      const raw = (!edge.output || edge.output === 'default') ? 'result' : edge.output;
      return canonicalizeOutput(nodeTypeById.get(edge.from.value), raw);
    };

    const outgoingHandlesByNodeId = (definition.edges || []).reduce<Record<string, string[]>>((accumulator, edge) => {
      const sourceNodeId = edge.from.value;
      const sourceHandle = sourceHandleFor(edge);
      const existingHandles = accumulator[sourceNodeId] ?? [];

      if (!existingHandles.includes(sourceHandle)) {
        accumulator[sourceNodeId] = [...existingHandles, sourceHandle];
      }

      return accumulator;
    }, {});

    const nodes: RFNode[] = (definition.nodes || []).map((node, index) => {
      // Restore position from _metadata if available, else layout linearly
      const metadata = (node.properties?._metadata as { x?: number; y?: number; parentId?: string; width?: number; height?: number }) || {};
      const x = typeof metadata.x === 'number' ? metadata.x : 100 + index * 250;
      const y = typeof metadata.y === 'number' ? metadata.y : 150 + (index % 2) * 100;
      const parentId = metadata.parentId || undefined;
      const isContainer = node.type === 'forLoop' || node.type === 'parallelForEach';
      const width = typeof metadata.width === 'number' ? metadata.width : (isContainer ? 500 : undefined);
      const height = typeof metadata.height === 'number' ? metadata.height : (isContainer ? 280 : undefined);

      // Extract node-specific properties (omitting visual metadata)
      const cleanProperties = { ...(node.properties || {}) };
      delete cleanProperties._metadata;

      return {
        id: node.id.value,
        type: node.type, // Map directly to 'start' | 'condition' etc.
        position: { x, y },
        parentId,
        extent: parentId ? 'parent' : undefined,
        style: {
          ...(width ? { width } : {}),
          ...(height ? { height } : {}),
        },
        data: {
          properties: cleanProperties || {},
          ...(node.type === 'start' || node.type === 'scheduler'
            ? {
                triggerOnly: true,
                outputHandles: outgoingHandlesByNodeId[node.id.value] ?? [node.type === 'scheduler' ? 'triggeredAt' : 'result'],
              }
            : {}),
        },
      };
    });

    const edges: RFEdge[] = (definition.edges || []).map((edge) => ({
      id: edge.id,
      source: edge.from.value,
      sourceHandle: sourceHandleFor(edge),
      target: edge.to.value,
      targetHandle: (!edge.input || edge.input === 'default') ? 'in' : edge.input,
      animated: false,
    }));

    // React Flow silently drops a nested node's containment if the child appears before its parent in the
    // array — the child then pops OUT of its loop/group container. Guarantee parent-before-child order so a
    // saved graph always reloads with its groups intact, regardless of the persisted order.
    return { nodes: orderParentsBeforeChildren(nodes), edges };
  },

  /**
   * Convert React Flow format back to Backend WorkflowDefinition.
   */
  toBackend(
    id: string,
    name: string,
    rfNodes: RFNode[],
    rfEdges: RFEdge[]
  ): WorkflowDefinition {
    // Persist in parent-before-child order too, so the invariant holds on the next load (some edits can
    // reorder the array, which would otherwise break containment after save).
    const nodes: NodeDefinition[] = orderParentsBeforeChildren(rfNodes).map((node) => {
      // Bundle visual position in properties._metadata
      const properties = {
        ...((node.data?.properties as Record<string, unknown>) || {}),
        _metadata: {
          x: node.position.x,
          y: node.position.y,
          parentId: node.parentId || undefined,
          // NodeResizer (xyflow v12) writes a resize to the node's top-level
          // width/height, NOT node.style — so read that first and fall back to
          // the creation-time style size. Reading only style dropped every
          // group/loop-container resize on save.
          width: containerWidth(node),
          height: containerHeight(node),
        },
      };

      return {
        id: { value: node.id },
        type: node.type || 'log',
        properties,
      };
    });

    const nodeTypeById = new Map(rfNodes.map((node) => [node.id, node.type]));
    const edges: EdgeDefinition[] = rfEdges.map((edge) => {
      const rawOutput = (!edge.sourceHandle || edge.sourceHandle === 'default') ? 'result' : normalizeHandle(edge.sourceHandle);
      return {
        id: edge.id || `e-${edge.source}-${edge.target}-${Date.now()}`,
        from: { value: edge.source },
        output: canonicalizeOutput(nodeTypeById.get(edge.source), rawOutput),
        to: { value: edge.target },
        input: (!edge.targetHandle || edge.targetHandle === 'default') ? 'in' : normalizeHandle(edge.targetHandle),
      };
    });

    return {
      id: { value: id },
      name: name || 'Unnamed Canvas Workflow',
      nodes,
      edges,
    };
  },
};
