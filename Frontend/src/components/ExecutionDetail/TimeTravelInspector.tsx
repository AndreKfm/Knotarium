import { useEffect } from 'react';
import { ChevronLeft, ChevronRight, ScanSearch, X } from 'lucide-react';
import type { NodeState } from '../../types';
import type { JournalOverviewGroup } from './types';
import { createStatusClassName, getStatusLabel } from './timelineUtils';
import { StatePanel, parseVariables } from '../shared/NodeIoPanels';

export type InspectorStep = {
  key: string;
  nodeId: string;
  title: string;
  status: JournalOverviewGroup['status'];
  durationLabel: string;
};

type TimeTravelInspectorProps = {
  steps: InspectorStep[];
  nodeStates: NodeState[];
  index: number;
  onIndexChange: (index: number) => void;
  onClose: () => void;
};

export function TimeTravelInspector({ steps, nodeStates, index, onIndexChange, onClose }: TimeTravelInspectorProps) {
  const clampedIndex = Math.min(Math.max(index, 0), Math.max(steps.length - 1, 0));
  const step = steps[clampedIndex];

  useEffect(() => {
    const handleKey = (event: KeyboardEvent) => {
      if (event.key === 'ArrowLeft') {
        onIndexChange(Math.max(clampedIndex - 1, 0));
      } else if (event.key === 'ArrowRight') {
        onIndexChange(Math.min(clampedIndex + 1, steps.length - 1));
      } else if (event.key === 'Escape') {
        onClose();
      }
    };

    window.addEventListener('keydown', handleKey);
    return () => window.removeEventListener('keydown', handleKey);
  }, [clampedIndex, onClose, onIndexChange, steps.length]);

  if (!step) {
    return null;
  }

  const nodeState = nodeStates.find((state) => state.nodeId.value === step.nodeId);
  const variablesBefore = parseVariables(nodeState?.variablesBefore);

  return (
    <div
      data-testid="time-travel-inspector"
      style={{
        position: 'absolute',
        left: 16,
        right: 16,
        bottom: 16,
        // Fixed height keeps the header (and the ‹ › controls) pinned: stepping never
        // resizes the panel, so the buttons stay under the cursor.
        height: 248,
        display: 'flex',
        flexDirection: 'column',
        background: 'rgba(8, 12, 20, 0.96)',
        border: '1px solid #283246',
        borderRadius: 14,
        boxShadow: '0 18px 50px rgba(0,0,0,0.5)',
        color: '#e6edf5',
        zIndex: 6,
        overflow: 'hidden',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '12px 16px', borderBottom: '1px solid #1d2737', flex: '0 0 auto' }}>
        <ScanSearch size={18} color="#8fd3ff" />
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <button
            aria-label="Previous step"
            onClick={() => onIndexChange(Math.max(clampedIndex - 1, 0))}
            disabled={clampedIndex === 0}
            style={stepButtonStyle(clampedIndex === 0)}
          >
            <ChevronLeft size={16} />
          </button>
          <span style={{ fontSize: '0.78rem', color: '#aab6c4', fontFamily: 'monospace', minWidth: 86, textAlign: 'center' }}>
            Step {clampedIndex + 1} / {steps.length}
          </span>
          <button
            aria-label="Next step"
            onClick={() => onIndexChange(Math.min(clampedIndex + 1, steps.length - 1))}
            disabled={clampedIndex === steps.length - 1}
            style={stepButtonStyle(clampedIndex === steps.length - 1)}
          >
            <ChevronRight size={16} />
          </button>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0, flex: 1 }}>
          <strong style={{ fontSize: '0.92rem', color: '#f8fafc', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {step.title}
          </strong>
          <span className={createStatusClassName(step.status)}>{getStatusLabel(step.status)}</span>
          <span style={{ fontFamily: 'monospace', fontSize: '0.74rem', color: '#94a3b8' }}>{step.durationLabel}</span>
        </div>

        <button aria-label="Close inspector" onClick={onClose} style={{ background: 'transparent', border: 'none', color: '#8794a6', cursor: 'pointer' }}>
          <X size={18} />
        </button>
      </div>

      {/* Timeline scrubber: one tick per node, coloured by status. Click a tick to jump. */}
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          padding: '10px 16px',
          borderBottom: '1px solid #131c28',
          overflowX: 'auto',
          flex: '0 0 auto',
        }}
      >
        {steps.map((tick, tickIndex) => {
          const isCurrent = tickIndex === clampedIndex;
          return (
            <button
              key={tick.key}
              aria-label={`Go to step ${tickIndex + 1}: ${tick.title}`}
              aria-current={isCurrent ? 'step' : undefined}
              title={`${tickIndex + 1}. ${tick.title}`}
              onClick={() => onIndexChange(tickIndex)}
              style={{
                flex: '0 0 auto',
                width: 16,
                height: 16,
                padding: 0,
                borderRadius: '50%',
                cursor: 'pointer',
                background: 'transparent',
                border: 'none',
                display: 'grid',
                placeItems: 'center',
              }}
            >
              <span
                style={{
                  width: isCurrent ? 12 : 9,
                  height: isCurrent ? 12 : 9,
                  borderRadius: '50%',
                  background: statusDotColor(tick.status),
                  opacity: tickIndex <= clampedIndex ? 1 : 0.4,
                  boxShadow: isCurrent ? `0 0 0 3px rgba(143,211,255,0.35)` : undefined,
                  transition: 'width .12s, height .12s',
                }}
              />
            </button>
          );
        })}
      </div>

      <div style={{ display: 'flex', gap: 14, padding: '14px 16px', flex: 1, minHeight: 0 }}>
        <StatePanel title="Inputs" entries={Object.entries(nodeState?.inputs ?? {})} emptyLabel="No inputs" />
        <StatePanel
          title="Variables at this step"
          entries={variablesBefore ? Object.entries(variablesBefore) : []}
          emptyLabel={nodeState?.variablesBefore === undefined ? 'Not captured' : 'No variables set'}
        />
        <StatePanel title="Outputs" entries={Object.entries(nodeState?.outputs ?? {})} emptyLabel="No outputs" />
      </div>

      {nodeState?.errorMessage && (
        <div style={{ padding: '0 16px 12px 16px', color: '#ffb4b4', fontSize: '0.78rem', flex: '0 0 auto', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {nodeState.errorMessage}
        </div>
      )}
    </div>
  );
}

function statusDotColor(status: string): string {
  const value = status.toLowerCase();
  if (value.includes('fail') || value.includes('error')) return '#f87171';
  if (value.includes('run')) return '#22d3ee';
  if (value.includes('complet') || value.includes('success')) return '#34d399';
  if (value.includes('skip')) return '#94a3b8';
  return '#64748b';
}

function stepButtonStyle(disabled: boolean): React.CSSProperties {
  return {
    display: 'grid',
    placeItems: 'center',
    width: 28,
    height: 28,
    borderRadius: 8,
    background: disabled ? 'rgba(255,255,255,0.03)' : 'rgba(59, 158, 255, 0.14)',
    border: '1px solid #283246',
    color: disabled ? '#4b5566' : '#8fd3ff',
    cursor: disabled ? 'default' : 'pointer',
  };
}
