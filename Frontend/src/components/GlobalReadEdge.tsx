// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useState } from 'react';
import { BaseEdge, EdgeLabelRenderer, getBezierPath } from '@xyflow/react';
import type { EdgeProps } from '@xyflow/react';
import { VariableToken, getTypeStyles } from './VariableToken';
import type { VariableType } from './VariableToken';
import { useVariableStore } from '../stores/useVariableStore';

interface GlobalReadEdgeData {
  variableName: string;
  variableType: VariableType;
  variableValue: unknown;
  variableStatus: 'awaiting run' | 'resolved';
  variableContainerKind?: 'object' | 'array';
  variableId: string;
  producerId: string;
  consumerId: string;
  densityMode: 'reveal' | 'dots' | 'boxes';
  isHovered: boolean;
  isPinned: boolean;
}

export function GlobalReadEdge({
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  style = {},
  markerEnd,
  selected,
  data,
}: EdgeProps) {
  const [isHoveredLocal, setIsHoveredLocal] = useState(false);
  const [edgePath, labelX, labelY] = getBezierPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
  });

  if (!data) return null;
  const edgeData = data as unknown as GlobalReadEdgeData;
  const typeStyles = getTypeStyles(edgeData.variableType);

  const isHovered = edgeData.isHovered || isHoveredLocal;
  const isPinned = edgeData.isPinned;

  const isVisible =
    edgeData.densityMode === 'boxes' ||
    edgeData.densityMode === 'dots' ||
    isHovered ||
    isPinned ||
    selected;

  if (!isVisible) return null;

  const shouldExpand =
    edgeData.densityMode === 'boxes' ||
    isHovered ||
    isPinned ||
    selected;

  const strokeDasharray = isPinned ? 'none' : '6,6';
  const strokeWidth = isPinned ? 3 : selected || isHovered ? 2.5 : 1.8;
  const opacity = isPinned ? 1 : selected || isHovered ? 0.9 : 0.45;

  return (
    <>
      <BaseEdge
        path={edgePath}
        markerEnd={markerEnd}
        style={{
          ...style,
          stroke: typeStyles.color,
          strokeWidth,
          strokeDasharray,
          opacity,
          transition: 'stroke-width 0.2s, opacity 0.2s',
          filter: (isHovered || isPinned) ? `drop-shadow(0 0 4px ${typeStyles.color}88)` : 'none',
        }}
      />
      <EdgeLabelRenderer>
        <div
          style={{
            position: 'absolute',
            transform: `translate(-50%, -50%) translate(${labelX}px,${labelY}px)`,
            pointerEvents: 'all',
            cursor: 'pointer',
            zIndex: shouldExpand ? 10 : 2,
            transition: 'all 0.2s cubic-bezier(0.4, 0, 0.2, 1)',
          }}
          onMouseEnter={() => {
            setIsHoveredLocal(true);
            useVariableStore.getState().setHoveredVariableId(edgeData.variableId);
          }}
          onMouseLeave={() => {
            setIsHoveredLocal(false);
            useVariableStore.getState().setHoveredVariableId(null);
          }}
        >
          {shouldExpand ? (
            <div
              style={{
                transform: 'scale(1.05)',
                transition: 'transform 0.15s ease',
              }}
            >
              <VariableToken
                name={edgeData.variableName}
                type={edgeData.variableType}
                value={edgeData.variableValue}
                status={edgeData.variableStatus}
                containerKind={edgeData.variableContainerKind}
                hideValue={false}
                onClick={(e) => {
                  e.stopPropagation();
                  useVariableStore.getState().togglePinnedVariableId(edgeData.variableId);
                }}
              />
            </div>
          ) : (
            <div
              onClick={(e) => {
                e.stopPropagation();
                useVariableStore.getState().togglePinnedVariableId(edgeData.variableId);
              }}
              style={{
                width: '10px',
                height: '10px',
                background: typeStyles.color,
                border: '1.5px solid #0b0f17',
                boxShadow: `0 0 0 1.5px ${typeStyles.color}`,
                transform: 'rotate(45deg)',
                cursor: 'pointer',
                transition: 'all 0.15s ease',
              }}
              title={`Global variable read: ${edgeData.variableName} (${edgeData.variableType})`}
            />
          )}
        </div>
      </EdgeLabelRenderer>
    </>
  );
}
