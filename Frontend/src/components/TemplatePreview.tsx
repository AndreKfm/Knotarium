import { useMemo } from 'react';
import { ReactFlow, ReactFlowProvider, Handle, Position, type Node, type Edge, type NodeProps } from '@xyflow/react';
import { computeAutoLayout, DEFAULT_NODE_WIDTH } from '../node-editor/autoLayout';
import type { NodeDefinition, EdgeDefinition } from '../types';

interface TemplatePreviewProps {
  nodes: NodeDefinition[];
  edges: EdgeDefinition[];
  /** Pixel height of the preview surface. */
  height?: number;
}

const PREVIEW_W = 168;
const PREVIEW_H = 52;

/** A compact, read-only node card — name + type, with anchor handles so edges connect. */
function PreviewNode({ data }: NodeProps) {
  const d = data as { label: string; nodeType: string };
  return (
    <div className="tpl-prev-node" title={`${d.label} · ${d.nodeType}`}>
      <Handle type="target" position={Position.Left} isConnectable={false} />
      <div className="tpl-prev-node-name">{d.label}</div>
      <div className="tpl-prev-node-type">{d.nodeType}</div>
      <Handle type="source" position={Position.Right} isConnectable={false} />
    </div>
  );
}

const nodeTypes = { preview: PreviewNode };

/** A non-secret display name for a node: a label/name property if present, else the node type. */
function nodeLabel(node: NodeDefinition): string {
  const props = node.properties ?? {};
  for (const key of ['label', 'name', 'title']) {
    const value = props[key];
    if (typeof value === 'string' && value.trim() !== '') return value;
  }
  return node.type;
}

/**
 * A read-only mini-canvas rendering a template's graph (dagre LR layout), for previewing before install or
 * insert. Non-interactive: no dragging, panning, zooming, selection, or controls. Renders the
 * already-substituted graph passed in (the caller fetches the payload with the current parameter values).
 */
export function TemplatePreview({ nodes, edges, height = 240 }: TemplatePreviewProps) {
  const { rfNodes, rfEdges } = useMemo(() => {
    const positions = computeAutoLayout(
      nodes.map((n) => ({ id: n.id.value, width: PREVIEW_W, height: PREVIEW_H })),
      edges.map((e) => ({ source: e.from.value, target: e.to.value })),
      { direction: 'LR', nodeSep: 24, rankSep: 56, defaultWidth: PREVIEW_W, defaultHeight: PREVIEW_H },
    );
    const positionById = new Map(positions.map((p) => [p.id, p]));

    const builtNodes: Node[] = nodes.map((n, index) => {
      const pos = positionById.get(n.id.value);
      return {
        id: n.id.value,
        type: 'preview',
        position: pos ? { x: pos.x, y: pos.y } : { x: index * (DEFAULT_NODE_WIDTH / 2), y: 0 },
        data: { label: nodeLabel(n), nodeType: n.type },
        draggable: false,
        selectable: false,
        connectable: false,
      };
    });

    // Strip handle ids — the preview node has generic anchor handles, so edges connect node-to-node.
    const builtEdges: Edge[] = edges.map((e) => ({
      id: e.id,
      source: e.from.value,
      target: e.to.value,
      type: 'smoothstep',
    }));

    return { rfNodes: builtNodes, rfEdges: builtEdges };
  }, [nodes, edges]);

  if (nodes.length === 0) {
    return <div className="tpl-prev-empty">This template has no nodes to preview.</div>;
  }

  return (
    <div className="tpl-prev" style={{ height }} aria-label="Template graph preview">
      <ReactFlowProvider>
        <ReactFlow
          nodes={rfNodes}
          edges={rfEdges}
          nodeTypes={nodeTypes}
          fitView
          fitViewOptions={{ padding: 0.15 }}
          nodesDraggable={false}
          nodesConnectable={false}
          elementsSelectable={false}
          panOnDrag={false}
          zoomOnScroll={false}
          zoomOnPinch={false}
          zoomOnDoubleClick={false}
          panOnScroll={false}
          preventScrolling={false}
          proOptions={{ hideAttribution: true }}
        />
      </ReactFlowProvider>
    </div>
  );
}
