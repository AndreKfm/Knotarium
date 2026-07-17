// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import React, { useState, useRef, useEffect, useCallback } from 'react';
import { useVariableStore } from '../stores/useVariableStore';
import type { VariableRecord } from '../stores/useVariableStore';
import { VariableToken } from './VariableToken';
import { Plus, Trash2, Edit3, Check, X, HelpCircle, Layers } from 'lucide-react';
import { computeVirtualWindow, shouldVirtualize } from '../node-editor/listVirtualization';

interface VariablesPanelProps {
  workflowId: string;
  onRenameVariableRefs?: (oldName: string, newName: string) => void;
}

// Fixed row height (card + 10px inter-card gap) used to window long variable lists.
// Cards are constrained to this height so the windowing math stays exact (#15).
const VARIABLE_ROW_HEIGHT = 118;

// Memoized for the same reason as PropertiesPanel: it's a Canvas child rendered every drag frame, but its
// only prop (workflowId) is stable, so it should re-render solely from its own variable-store subscription.
const VariablesPanelImpl: React.FC<VariablesPanelProps> = ({ workflowId }) => {
  const variables = useVariableStore((state) => state.variables[workflowId] || []);
  const addVariable = useVariableStore((state) => state.addVariable);
  const removeVariable = useVariableStore((state) => state.removeVariable);
  const renameVariable = useVariableStore((state) => state.renameVariable);
  const conflictingName = useVariableStore((state) => state.conflictingName);
  const isDraggingOutput = useVariableStore((state) => state.isDraggingOutput);

  const [isDragOver, setIsDragOver] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState('');
  const [renameError, setRenameError] = useState<string | null>(null);

  const editInputRef = useRef<HTMLInputElement>(null);

  // Virtualization (#15): for long lists we render only the rows in view (plus
  // overscan) inside a scroll container, framed by top/bottom spacers. Track the
  // container's scroll offset and visible height to drive the windowing math.
  const listRef = useRef<HTMLDivElement>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(0);

  const measureViewport = useCallback(() => {
    const el = listRef.current;
    if (el) setViewportHeight(el.clientHeight);
  }, []);

  useEffect(() => {
    measureViewport();
    window.addEventListener('resize', measureViewport);
    return () => window.removeEventListener('resize', measureViewport);
  }, [measureViewport]);

  useEffect(() => {
    if (editingId && editInputRef.current) {
      editInputRef.current.focus();
      editInputRef.current.select();
    }
  }, [editingId]);

  const handleDragOver = (e: React.DragEvent<HTMLDivElement>) => {
    if (isDraggingOutput) {
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
      e.stopPropagation();
      setIsDragOver(true);
    }
  };

  const handleDragLeave = () => {
    setIsDragOver(false);
  };

  const handleDrop = (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragOver(false);

    try {
      const rawData = e.dataTransfer.getData('application/knotarium-node-output');
      if (!rawData) return;

      const output = JSON.parse(rawData);
      if (output && output.nodeId && output.outputHandle) {
        addVariable(workflowId, {
          name: output.proposedName,
          type: output.type,
          producer: output.nodeId,
          producerOutput: output.outputHandle,
          value: output.value,
        });
      }
    } catch (err) {
      console.error('Failed to parse dropped node output:', err);
    }
  };

  const handleStartRename = (v: VariableRecord) => {
    setEditingId(v.id);
    setEditName(v.name);
    setRenameError(null);
  };

  const handleSaveRename = (v: VariableRecord) => {
    const trimmed = editName.trim();
    if (!trimmed) {
      setRenameError('Name cannot be empty');
      return;
    }
    
    // Check for variable naming rules (alphanumeric and underscores)
    if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(trimmed)) {
      setRenameError('Invalid variable name');
      return;
    }

    const success = renameVariable(workflowId, v.id, trimmed);
    if (success) {
      setEditingId(null);
      setRenameError(null);
    } else {
      setRenameError('Name already exists');
    }
  };

  const handleCancelRename = () => {
    setEditingId(null);
    setRenameError(null);
  };

  const handleTokenDragStart = (e: React.DragEvent<HTMLDivElement>, v: VariableRecord) => {
    const tokenData = {
      variableId: v.id,
      variableName: v.name,
      type: v.type,
    };
    e.dataTransfer.setData('application/knotarium-variable-token', JSON.stringify(tokenData));
    e.dataTransfer.effectAllowed = 'copy';
    useVariableStore.getState().setDraggingToken(true, tokenData);
  };

  const handleTokenDragEnd = () => {
    useVariableStore.getState().setDraggingToken(false, null);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', width: '100%' }}>
      {/* CSS Styles for Shaking and Flashing conflict */}
      <style>{`
        @keyframes flash-error-border {
          0%, 100% { border-color: var(--border-color); background: rgba(16, 22, 37, 0.2); }
          50% { border-color: var(--color-error); background: rgba(239, 68, 68, 0.15); transform: translateX(-4px); }
          25%, 75% { transform: translateX(4px); }
        }
        .flash-conflict-card {
          animation: flash-error-border 0.4s ease-in-out 2 !important;
          border-color: var(--color-error) !important;
        }
        .variable-card {
          border: 1px solid var(--border-color);
          background: rgba(255, 255, 255, 0.02);
          border-radius: 8px;
          padding: 12px;
          display: flex;
          flex-direction: column;
          gap: 8px;
          transition: border-color 0.2s, background 0.2s;
        }
        .variable-card:hover {
          border-color: rgba(255, 255, 255, 0.12);
          background: rgba(255, 255, 255, 0.04);
        }
      `}</style>

      {/* Header Title & Legend */}
      <div style={{ padding: '16px 20px', borderBottom: '1px solid var(--border-color)' }}>
        <h3 style={{ fontSize: '0.9rem', fontWeight: 700, color: '#fff', textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: '10px', display: 'flex', alignItems: 'center', gap: '8px' }}>
          <Layers size={15} color="var(--color-accent)" />
          Workflow variables
        </h3>
        
        {/* Type suffix legend: the sigil after a name carries the type. */}
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '8px 12px', background: 'rgba(0,0,0,0.15)', padding: '8px 10px', borderRadius: '6px', border: '1px solid var(--border-color)' }}>
          <span style={{ fontSize: '0.68rem', color: 'var(--text-muted)', fontWeight: 700, width: '100%' }}>LEGEND:</span>
          {([
            { sigil: '""', label: 'string', color: '#10b981' },
            { sigil: '#', label: 'number', color: '#f59e0b' },
            { sigil: '?', label: 'boolean', color: '#ef4444' },
            { sigil: '{}', label: 'dictionary', color: '#6366f1' },
            { sigil: '[]', label: 'array', color: '#6366f1' },
          ] as const).map(({ sigil, label, color }) => (
            <div key={label} style={{ display: 'flex', alignItems: 'center', gap: '5px', fontSize: '0.68rem', color: 'var(--text-secondary)' }}>
              <span style={{ color, fontWeight: 700, fontFamily: 'monospace' }}>{sigil}</span>
              {label}
            </div>
          ))}
        </div>
      </div>

      {/* Drop Zone Target */}
      <div style={{ padding: '12px 20px' }}>
        <div
          onDragOver={handleDragOver}
          onDragLeave={handleDragLeave}
          onDrop={handleDrop}
          style={{
            border: isDragOver ? '2px dashed var(--color-accent)' : '1px dashed var(--border-color)',
            background: isDragOver ? 'rgba(99, 102, 241, 0.1)' : 'rgba(255, 255, 255, 0.02)',
            boxShadow: isDragOver ? '0 0 10px var(--color-accent-glow)' : 'none',
            padding: '16px',
            borderRadius: '10px',
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            justifyContent: 'center',
            textAlign: 'center',
            gap: '6px',
            transition: 'all 0.2s ease',
            cursor: 'default',
          }}
        >
          <Plus size={18} color={isDragOver ? 'var(--color-accent)' : 'var(--text-secondary)'} />
          <span style={{ fontSize: '0.75rem', fontWeight: 700, color: isDragOver ? '#fff' : 'var(--text-secondary)' }}>
            {isDragOver ? 'Drop Node Output Here' : 'Register New Variable'}
          </span>
          <span style={{ fontSize: '0.62rem', color: 'var(--text-muted)' }}>
            Drag output pill from canvas nodes
          </span>
        </div>
      </div>

      {/* Variables List */}
      <VariablesList
        variables={variables}
        listRef={listRef}
        onScroll={(e) => {
          setScrollTop(e.currentTarget.scrollTop);
          // Height can change with the panel; cheap to re-read on scroll too.
          setViewportHeight(e.currentTarget.clientHeight);
        }}
        scrollTop={scrollTop}
        viewportHeight={viewportHeight}
        renderCard={(v) => {
            const isConflicting = conflictingName && conflictingName.toLowerCase() === v.name.toLowerCase();
            const isEditing = editingId === v.id;

            return (
              <div
                key={v.id}
                className={`variable-card ${isConflicting ? 'flash-conflict-card' : ''}`}
              >
                {/* Title & Actions */}
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  {isEditing ? (
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '4px', flex: 1, marginRight: '8px' }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: '4px' }}>
                        <input
                          ref={editInputRef}
                          type="text"
                          value={editName}
                          onChange={(e) => setEditName(e.target.value)}
                          onKeyDown={(e) => {
                            if (e.key === 'Enter') handleSaveRename(v);
                            else if (e.key === 'Escape') handleCancelRename();
                          }}
                          style={{
                            background: 'rgba(0,0,0,0.4)',
                            border: '1px solid var(--border-color)',
                            borderRadius: '4px',
                            color: '#fff',
                            fontSize: '0.85rem',
                            padding: '2px 6px',
                            outline: 'none',
                            fontFamily: 'monospace',
                            flex: 1,
                          }}
                        />
                        <button
                          onClick={() => handleSaveRename(v)}
                          style={{ background: 'transparent', border: 'none', color: 'var(--color-success)', cursor: 'pointer', display: 'flex', alignItems: 'center' }}
                        >
                          <Check size={14} />
                        </button>
                        <button
                          onClick={handleCancelRename}
                          style={{ background: 'transparent', border: 'none', color: 'var(--color-error)', cursor: 'pointer', display: 'flex', alignItems: 'center' }}
                        >
                          <X size={14} />
                        </button>
                      </div>
                      {renameError && (
                        <span style={{ fontSize: '0.65rem', color: 'var(--color-error)' }}>{renameError}</span>
                      )}
                    </div>
                  ) : (
                    <div style={{ display: 'flex', alignItems: 'center', gap: '8px', overflow: 'hidden' }}>
                      <VariableToken
                        name={v.name}
                        type={v.type}
                        value={v.value}
                        status={v.status}
                        containerKind={v.containerKind}
                        draggable={true}
                        onDragStart={(e) => handleTokenDragStart(e, v)}
                        onDragEnd={handleTokenDragEnd}
                        onMouseEnter={() => useVariableStore.getState().setHoveredVariableId(v.id)}
                        onMouseLeave={() => useVariableStore.getState().setHoveredVariableId(null)}
                        onClick={(e) => { e.stopPropagation(); useVariableStore.getState().togglePinnedVariableId(v.id); }}
                      />
                    </div>
                  )}

                  {!isEditing && v.derived && (
                    <span title="Declared by a Set Variable(s) node" style={{ fontSize: '0.6rem', fontWeight: 700, textTransform: 'uppercase', color: 'var(--text-muted)', border: '1px solid var(--border-color)', borderRadius: '5px', padding: '2px 6px' }}>
                      declared
                    </span>
                  )}

                  {!isEditing && !v.derived && (
                    <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                      <button
                        onClick={() => handleStartRename(v)}
                        title="Rename Variable"
                        style={{
                          background: 'transparent',
                          border: 'none',
                          color: 'var(--text-muted)',
                          cursor: 'pointer',
                          display: 'flex',
                          alignItems: 'center',
                          padding: '4px',
                          borderRadius: '4px',
                          transition: 'color 0.2s, background 0.2s',
                        }}
                        onMouseOver={(e) => {
                          e.currentTarget.style.color = '#fff';
                          e.currentTarget.style.background = 'rgba(255,255,255,0.05)';
                        }}
                        onMouseOut={(e) => {
                          e.currentTarget.style.color = 'var(--text-muted)';
                          e.currentTarget.style.background = 'transparent';
                        }}
                      >
                        <Edit3 size={12} />
                      </button>
                      <button
                        onClick={() => removeVariable(workflowId, v.id)}
                        title="Delete Variable"
                        style={{
                          background: 'transparent',
                          border: 'none',
                          color: 'var(--text-muted)',
                          cursor: 'pointer',
                          display: 'flex',
                          alignItems: 'center',
                          padding: '4px',
                          borderRadius: '4px',
                          transition: 'color 0.2s, background 0.2s',
                        }}
                        onMouseOver={(e) => {
                          e.currentTarget.style.color = 'var(--color-error)';
                          e.currentTarget.style.background = 'rgba(239, 68, 68, 0.05)';
                        }}
                        onMouseOut={(e) => {
                          e.currentTarget.style.color = 'var(--text-muted)';
                          e.currentTarget.style.background = 'transparent';
                        }}
                      >
                        <Trash2 size={12} />
                      </button>
                    </div>
                  )}
                </div>

                {/* Details Footer */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: '2px', fontSize: '0.7rem', color: 'var(--text-muted)', borderTop: '1px solid rgba(255,255,255,0.03)', paddingTop: '6px', marginTop: '2px' }}>
                  <div>
                    Producer: <span style={{ color: 'var(--text-secondary)', fontFamily: 'monospace' }}>{v.producer}</span>
                  </div>
                  <div>
                    Consumers:{' '}
                    <span style={{ color: v.consumers.length > 0 ? 'var(--color-info)' : 'var(--text-muted)' }}>
                      {v.consumers.length > 0 ? v.consumers.join(', ') : 'none'}
                    </span>
                  </div>
                </div>
              </div>
            );
        }}
      />
    </div>
  );
};

interface VariablesListProps {
  variables: VariableRecord[];
  listRef: React.RefObject<HTMLDivElement | null>;
  onScroll: (e: React.UIEvent<HTMLDivElement>) => void;
  scrollTop: number;
  viewportHeight: number;
  renderCard: (v: VariableRecord) => React.ReactNode;
}

/**
 * The scrollable variable list. Renders every card directly for short lists, but
 * switches to fixed-height windowing once the store grows past the threshold so
 * the panel stays responsive with hundreds of variables (#15). The windowed path
 * frames the visible slice with top/bottom spacers that preserve the scrollbar.
 */
const VariablesList: React.FC<VariablesListProps> = ({
  variables, listRef, onScroll, scrollTop, viewportHeight, renderCard,
}) => {
  const containerStyle: React.CSSProperties = {
    flex: 1,
    overflowY: 'auto',
    padding: '0 20px 20px 20px',
  };

  if (variables.length === 0) {
    return (
      <div ref={listRef} style={{ ...containerStyle, display: 'flex', flexDirection: 'column' }}>
        <div style={{
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          padding: '30px 10px',
          textAlign: 'center',
          color: 'var(--text-muted)',
          border: '1px dashed rgba(255, 255, 255, 0.03)',
          borderRadius: '8px',
          background: 'rgba(0, 0, 0, 0.08)',
          gap: '8px',
        }}>
          <HelpCircle size={24} style={{ opacity: 0.4 }} />
          <span style={{ fontSize: '0.75rem', fontWeight: 600 }}>Store is Empty</span>
          <span style={{ fontSize: '0.68rem', opacity: 0.8 }}>
            No workflow variables registered. Promote a node output to get started.
          </span>
        </div>
      </div>
    );
  }

  if (!shouldVirtualize(variables.length)) {
    return (
      <div ref={listRef} style={{ ...containerStyle, display: 'flex', flexDirection: 'column', gap: '10px' }}>
        {variables.map(renderCard)}
      </div>
    );
  }

  const window = computeVirtualWindow({
    scrollTop,
    viewportHeight,
    rowHeight: VARIABLE_ROW_HEIGHT,
    itemCount: variables.length,
  });
  const visible = variables.slice(window.startIndex, window.endIndex);

  return (
    <div ref={listRef} onScroll={onScroll} style={containerStyle} data-testid="variables-virtual-list">
      <div style={{ height: window.paddingTop }} aria-hidden="true" />
      {visible.map((v) => (
        // Clip each row to a uniform height so the windowing offsets stay exact.
        <div key={v.id} style={{ height: VARIABLE_ROW_HEIGHT, paddingBottom: 10, boxSizing: 'border-box', overflow: 'hidden' }}>
          {renderCard(v)}
        </div>
      ))}
      <div style={{ height: window.paddingBottom }} aria-hidden="true" />
    </div>
  );
};

export const VariablesPanel = React.memo(VariablesPanelImpl);
