// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import React from 'react';
import { X, Pin } from 'lucide-react';
import { useVariableStore } from '../stores/useVariableStore';
import { formatVariableValue, variableKindLabel, variableTypeSuffix } from '../utils/variableDisplay';

export type VariableType = 'string' | 'number' | 'boolean' | 'object';

export interface VariableTokenProps {
  name: string;
  type: VariableType;
  value?: unknown;
  status?: 'awaiting run' | 'resolved';
  /** For keyed globals: head-container kind inferred from the path, so the glyph is correct pre-run. */
  containerKind?: 'object' | 'array';
  draggable?: boolean;
  onDragStart?: (e: React.DragEvent<HTMLDivElement>) => void;
  onDragEnd?: () => void;
  onRemove?: () => void;
  style?: React.CSSProperties;
  hideValue?: boolean;
  onMouseEnter?: () => void;
  onMouseLeave?: () => void;
  onClick?: (e: React.MouseEvent) => void;
}

export const getTypeStyles = (type: VariableType) => {
  switch (type) {
    case 'string':
      return {
        bg: 'rgba(16, 185, 129, 0.1)',
        border: '1px solid rgba(16, 185, 129, 0.35)',
        text: '#a7f3d0',
        color: '#10b981',
      };
    case 'number':
      return {
        bg: 'rgba(245, 158, 11, 0.1)',
        border: '1px solid rgba(245, 158, 11, 0.35)',
        text: '#fde68a',
        color: '#f59e0b',
      };
    case 'boolean':
      return {
        bg: 'rgba(239, 68, 68, 0.1)',
        border: '1px solid rgba(239, 68, 68, 0.35)',
        text: '#fecaca',
        color: '#ef4444',
      };
    case 'object':
      return {
        bg: 'rgba(99, 102, 241, 0.1)',
        border: '1px solid rgba(99, 102, 241, 0.35)',
        text: '#c7d2fe',
        color: '#6366f1',
      };
  }
};


export const formatProducerType = (producerId: string): string => {
  const type = producerId.split('-')[0];
  switch (type) {
    case 'httpRequest': return 'HTTP Request';
    case 'setVariable': return 'Set Variable';
    case 'condition': return 'Condition';
    case 'delay': return 'Delay';
    case 'log': return 'Log';
    case 'start': return 'Start';
    case 'end': return 'End';
    default:
      return type.charAt(0).toUpperCase() + type.slice(1);
  }
};

export const VariableToken: React.FC<VariableTokenProps> = ({
  name,
  type,
  value,
  status,
  containerKind,
  draggable = false,
  onDragStart,
  onDragEnd,
  onRemove,
  style,
  hideValue = false,
  onMouseEnter,
  onMouseLeave,
  onClick,
}) => {
  const activeWorkflowId = useVariableStore((state) => state.activeWorkflowId);
  const currentVars = useVariableStore((state) => activeWorkflowId ? (state.variables[activeWorkflowId] || []) : []);
  const pinnedVariableIds = useVariableStore((state) => state.pinnedVariableIds);

  const v = currentVars.find((candidate) => candidate.name === name);
  const isPinned = v ? pinnedVariableIds.includes(v.id) : false;

  const displayName = (() => {
    if (!v) return name;
    if (v.name !== `${v.producer}_${v.producerOutput}`) return v.name;
    const hasCollision = currentVars.filter(cv => cv.producerOutput === v.producerOutput).length > 1;
    if (hasCollision) {
      return `${formatProducerType(v.producer)} · ${v.producerOutput}`;
    }
    return v.producerOutput;
  })();

  const styles = getTypeStyles(type);
  const formattedValue = formatVariableValue(status, value);
  const kindLabel = variableKindLabel(type, containerKind, value);
  // Suffix notation: the type is carried by a sigil after the name ({} [] "" # ?),
  // no leading glyph and no type word.
  const typeSuffix = variableTypeSuffix(type, containerKind, value);

  const tooltipText = v
    ? `${v.producerOutput} · from ${formatProducerType(v.producer)} (ID: ${v.producer})\nKind: ${kindLabel}\nValue: ${formattedValue}`
    : `Global Variable: ${name} (${kindLabel})\nStatus: ${status || 'Awaiting run'}\nValue: ${formattedValue}`;

  return (
    <div
      draggable={draggable}
      onDragStart={onDragStart}
      onDragEnd={onDragEnd}
      onMouseEnter={(e) => {
        if (draggable) {
          e.currentTarget.style.transform = 'translateY(-1.5px)';
          e.currentTarget.style.boxShadow = '0 4px 8px rgba(0,0,0,0.3)';
        }
        onMouseEnter?.();
      }}
      onMouseLeave={(e) => {
        if (draggable) {
          e.currentTarget.style.transform = 'none';
          e.currentTarget.style.boxShadow = '0 2px 5px rgba(0,0,0,0.2)';
        }
        onMouseLeave?.();
      }}
      onClick={onClick}
      title={tooltipText}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: '6px',
        padding: '3px 8px',
        borderRadius: '6px',
        background: styles.bg,
        border: isPinned ? `1.5px solid ${styles.color}` : styles.border,
        color: styles.text,
        fontSize: '0.78rem',
        fontWeight: 600,
        fontFamily: 'monospace',
        cursor: draggable ? 'grab' : onClick ? 'pointer' : 'default',
        userSelect: 'none',
        boxShadow: isPinned ? `0 0 8px ${styles.color}` : '0 2px 5px rgba(0,0,0,0.2)',
        maxWidth: '100%',
        transition: 'all 0.15s ease',
        ...style,
      }}
    >
      {isPinned && (
        <span style={{ display: 'flex', alignItems: 'center', color: styles.color }}>
          <Pin size={10} style={{ transform: 'rotate(45deg)', fill: styles.color }} />
        </span>
      )}
      <span style={{
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
        maxWidth: '150px'
      }}>
        {displayName}
      </span>

      <span style={{ color: styles.color, fontWeight: 700, letterSpacing: '0.5px' }}>
        {typeSuffix}
      </span>

      {!hideValue && status === 'resolved' && (
        <span style={{
          color: 'rgba(255, 255, 255, 0.4)',
          fontWeight: 400,
          borderLeft: '1px solid rgba(255,255,255,0.1)',
          paddingLeft: '6px',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
          maxWidth: '120px'
        }}>
          {formattedValue}
        </span>
      )}

      {onRemove && (
        <button
          onClick={(e) => {
            e.stopPropagation();
            onRemove();
          }}
          style={{
            background: 'transparent',
            border: 'none',
            color: 'rgba(255,255,255,0.4)',
            cursor: 'pointer',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            padding: 0,
            marginLeft: '2px',
            borderRadius: '50%',
            width: '14px',
            height: '14px',
            transition: 'background 0.15s, color 0.15s',
          }}
          onMouseOver={(e) => {
            e.currentTarget.style.background = 'rgba(255,255,255,0.1)';
            e.currentTarget.style.color = '#fff';
          }}
          onMouseOut={(e) => {
            e.currentTarget.style.background = 'transparent';
            e.currentTarget.style.color = 'rgba(255,255,255,0.4)';
          }}
        >
          <X size={10} />
        </button>
      )}
    </div>
  );
};
