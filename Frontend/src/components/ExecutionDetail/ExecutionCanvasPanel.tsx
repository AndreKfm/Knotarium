// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import {
  ReactFlow,
  Background,
  Controls,
  BackgroundVariant,
} from '@xyflow/react';
import type { ReactNode } from 'react';
import type {
  Node as RFNode,
  Edge as RFEdge,
  NodeTypes,
} from '@xyflow/react';
import { ArrowLeft, History, RefreshCw, ScanSearch } from 'lucide-react';
import { createStatusClassName, getStatusLabel } from './timelineUtils';
import type { VisualRunStatus } from './types';
import { GlobalReadEdge } from '../GlobalReadEdge';

const edgeTypes = {
  globalRead: GlobalReadEdge,
};

type ExecutionCanvasPanelProps = {
  executionId: string;
  workflowName?: string | null;
  executionVisualStatus: VisualRunStatus | null;
  loading: boolean;
  nodes: RFNode[];
  edges: RFEdge[];
  combinedNodeTypes: NodeTypes;
  onBack: () => void;
  onReplay: () => void;
  onNodeReplayRequest?: (nodeId: string) => void;
  lineage?: { sourceExecutionId: string; fromNodeId?: string };
  onOpenExecution?: (executionId: string) => void;
  stepThroughActive?: boolean;
  onToggleStepThrough?: () => void;
  highlightedNodeId?: string;
  dimmedNodeIds?: string[];
  inspectorSlot?: ReactNode;
};

export function ExecutionCanvasPanel({
  executionId,
  workflowName,
  executionVisualStatus,
  loading,
  nodes,
  edges,
  combinedNodeTypes,
  onBack,
  onReplay,
  onNodeReplayRequest,
  lineage,
  onOpenExecution,
  stepThroughActive,
  onToggleStepThrough,
  highlightedNodeId,
  dimmedNodeIds,
  inspectorSlot,
}: ExecutionCanvasPanelProps) {
  const dimmedSet = dimmedNodeIds && dimmedNodeIds.length > 0 ? new Set(dimmedNodeIds) : null;
  const renderedNodes = (highlightedNodeId || dimmedSet)
    ? nodes.map((node) => {
        if (node.id === highlightedNodeId) {
          // Current step: lit and fully opaque.
          return { ...node, style: { ...(node.style ?? {}), outline: '2px solid #8fd3ff', outlineOffset: 2, borderRadius: 12, opacity: 1 } };
        }
        if (dimmedSet?.has(node.id)) {
          // Not yet reached at this point in time ("future") — fade it back.
          return { ...node, style: { ...(node.style ?? {}), opacity: 0.32 } };
        }
        return node;
      })
    : nodes;
  return (
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', borderRight: '1px solid var(--border-color)' }}>
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          padding: '16px 24px',
          background: 'rgba(10, 13, 22, 0.8)',
          borderBottom: '1px solid var(--border-color)',
          zIndex: 5,
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
          <button
            onClick={onBack}
            style={{
              background: 'transparent',
              border: 'none',
              color: 'var(--text-secondary)',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              gap: '6px',
              fontSize: '0.85rem',
            }}
          >
            <ArrowLeft size={16} />
            Dashboard
          </button>
          <h2 style={{ fontSize: '1.1rem', fontWeight: 700, color: '#fff' }}>
            {workflowName || 'Live Runner Tracker'}
          </h2>
          {workflowName && (
            <span style={{ fontSize: '0.68rem', color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.06em' }}>
              Live run
            </span>
          )}
          <span style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>Run ID: {executionId.slice(0, 12)}...</span>
          {lineage && (
            <button
              onClick={() => onOpenExecution?.(lineage.sourceExecutionId)}
              title={`Replay of run ${lineage.sourceExecutionId}${lineage.fromNodeId ? ` from node ${lineage.fromNodeId}` : ''}`}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 6,
                padding: '3px 10px',
                borderRadius: 999,
                background: 'rgba(143, 211, 255, 0.1)',
                border: '1px solid rgba(143, 211, 255, 0.3)',
                color: '#8fd3ff',
                fontSize: '0.72rem',
                cursor: 'pointer',
              }}
            >
              <History size={12} />
              Replay{lineage.fromNodeId ? ` of node ${lineage.fromNodeId}` : ''} · source run {lineage.sourceExecutionId.slice(0, 8)}
            </button>
          )}
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
          {executionVisualStatus && (
            <span className={createStatusClassName(executionVisualStatus)}>
              {getStatusLabel(executionVisualStatus)}
            </span>
          )}
          {onToggleStepThrough && (
            <button
              onClick={onToggleStepThrough}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '6px',
                padding: '8px 12px',
                borderRadius: '8px',
                background: stepThroughActive ? 'rgba(143, 211, 255, 0.16)' : 'rgba(255,255,255,0.04)',
                border: stepThroughActive ? '1px solid rgba(143, 211, 255, 0.5)' : '1px solid var(--border-color)',
                color: stepThroughActive ? '#8fd3ff' : '#fff',
                fontSize: '0.8rem',
                cursor: 'pointer',
              }}
            >
              <ScanSearch size={12} />
              Step Through
            </button>
          )}
          <button
            onClick={onReplay}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '6px',
              padding: '8px 12px',
              borderRadius: '8px',
              background: 'rgba(255,255,255,0.04)',
              border: '1px solid var(--border-color)',
              color: '#fff',
              fontSize: '0.8rem',
              cursor: 'pointer',
            }}
          >
            <RefreshCw size={12} />
            Replay Logs
          </button>
        </div>
      </div>

      <div style={{ flex: 1, position: 'relative' }}>
        {loading ? (
          <div style={{ display: 'flex', height: '100%', alignItems: 'center', justifyContent: 'center' }}>
            <span style={{ color: 'var(--text-secondary)' }}>Polling workflow layouts...</span>
          </div>
        ) : (
          <ReactFlow
            proOptions={{ hideAttribution: true }}
            nodes={renderedNodes}
            edges={edges}
            nodeTypes={combinedNodeTypes}
            edgeTypes={edgeTypes}
            onNodesChange={undefined}
            onEdgesChange={undefined}
            nodesDraggable={true}
            nodesConnectable={false}
            onNodeContextMenu={onNodeReplayRequest ? (event, node) => {
              event.preventDefault();
              onNodeReplayRequest(node.id);
            } : undefined}
            onNodeClick={(_event, node) => {
              // The synthetic error-handler card carries its open action in data (RF swallows the
              // inner div's onClick, so handle it here).
              if (node.type === 'errorHandlerCard') {
                (node.data as { onOpen?: () => void })?.onOpen?.();
              }
            }}
            fitView
          >
            <Controls position="bottom-right" />
            <Background variant={BackgroundVariant.Dots} color="rgba(255,255,255,0.06)" size={1.5} gap={24} />
          </ReactFlow>
        )}
        {inspectorSlot}
      </div>
    </div>
  );
}