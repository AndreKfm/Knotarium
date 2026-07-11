import { useMemo, useState } from 'react';
import { GitCompareArrows, X, ChevronDown, ChevronRight } from 'lucide-react';
import type { VersionDiff, NodeDiff, FieldChange } from '../utils/versionDiff';

export interface VersionDiffViewProps {
  /** Human label for the left side (e.g. "active v3" or "v3"). */
  leftLabel: string;
  /** Human label for the right side (e.g. "working draft" or "v5"). */
  rightLabel: string;
  diff: VersionDiff;
  onClose: () => void;
}

const KIND_COLOR: Record<NodeDiff['kind'], string> = {
  added: '#86efac',
  removed: '#fca5a5',
  changed: '#fcd34d',
};

function valueText(value: unknown): string {
  if (value === undefined) return '∅';
  if (typeof value === 'string') return value;
  return JSON.stringify(value);
}

function FieldRow({ change }: { change: FieldChange }) {
  return (
    <div style={{ fontSize: '0.74rem', color: '#cbd5e1', display: 'flex', gap: 6, lineHeight: 1.5 }}>
      <code style={{ color: '#94a3b8', flex: '0 0 auto' }}>{change.path}</code>
      <span style={{ color: '#fca5a5', textDecoration: 'line-through' }}>{valueText(change.before)}</span>
      <span style={{ color: '#64748b' }}>→</span>
      <span style={{ color: '#86efac' }}>{valueText(change.after)}</span>
    </div>
  );
}

/**
 * Read-only side panel that renders a {@link VersionDiff} (plan §7.4). Behavioral
 * changes (added/removed nodes, type changes, config field changes, connection
 * changes) are shown by default; cosmetic layout-only changes are tucked behind a
 * collapsed disclosure so a node nudge doesn't drown out real edits. The diff
 * itself is computed by the pure `versionDiff` module — this component only renders.
 */
export function VersionDiffView({ leftLabel, rightLabel, diff, onClose }: VersionDiffViewProps) {
  const [showLayout, setShowLayout] = useState(false);

  const behavioralNodes = useMemo(() => diff.nodes.filter((n) => !n.layoutOnly), [diff.nodes]);
  const layoutNodes = useMemo(() => diff.nodes.filter((n) => n.layoutOnly), [diff.nodes]);
  const noChanges = diff.nodes.length === 0 && diff.edges.length === 0;

  return (
    <aside
      role="complementary"
      aria-label={`Diff ${leftLabel} versus ${rightLabel}`}
      style={{
        position: 'absolute',
        top: 0,
        right: 0,
        bottom: 0,
        width: 'min(420px, 92%)',
        zIndex: 960,
        display: 'flex',
        flexDirection: 'column',
        background: 'var(--bg-surface-opaque, #101625)',
        borderLeft: '1px solid var(--border-color)',
        boxShadow: '-12px 0 40px rgba(0,0,0,0.45)',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '14px 18px', borderBottom: '1px solid var(--border-color)' }}>
        <GitCompareArrows size={16} color="var(--color-accent, #3b82f6)" />
        <strong style={{ flex: 1, fontSize: '0.92rem', color: 'var(--text-primary, #e5e7eb)' }}>
          {leftLabel} <span style={{ color: 'var(--text-secondary)' }}>→</span> {rightLabel}
        </strong>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close diff"
          style={{ background: 'transparent', border: 'none', color: 'var(--text-secondary)', cursor: 'pointer', display: 'flex' }}
        >
          <X size={16} />
        </button>
      </div>

      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 0' }}>
        {noChanges ? (
          <div style={{ padding: '16px 18px', color: 'var(--text-secondary)', fontSize: '0.85rem' }}>
            No differences — these two are identical (ignoring cosmetic layout).
          </div>
        ) : (
          <>
            {behavioralNodes.map((node) => (
              <div key={node.nodeId} style={rowStyle}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <span style={{ ...kindBadge, color: KIND_COLOR[node.kind] }}>{node.kind}</span>
                  <code style={{ fontSize: '0.8rem', color: 'var(--text-primary, #e5e7eb)' }}>{node.nodeId}</code>
                  {(node.typeBefore || node.typeAfter) && (
                    <span style={{ fontSize: '0.72rem', color: 'var(--text-secondary)' }}>
                      {node.typeBefore && node.typeAfter && node.typeBefore !== node.typeAfter
                        ? `${node.typeBefore} → ${node.typeAfter}`
                        : node.typeBefore ?? node.typeAfter}
                    </span>
                  )}
                </div>
                {node.fieldChanges.length > 0 && (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 3, marginTop: 4, paddingLeft: 4 }}>
                    {node.fieldChanges.map((change) => (
                      <FieldRow key={change.path} change={change} />
                    ))}
                  </div>
                )}
              </div>
            ))}

            {diff.edges.map((edge) => (
              <div key={edge.key} style={rowStyle}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <span style={{ ...kindBadge, color: KIND_COLOR[edge.kind] }}>{edge.kind}</span>
                  <span style={{ fontSize: '0.76rem', color: 'var(--text-secondary)' }}>connection</span>
                </div>
                <div style={{ fontSize: '0.74rem', color: '#cbd5e1', marginTop: 3, paddingLeft: 4 }}>
                  <code>{edge.source}</code>
                  <span style={{ color: '#64748b' }}> :{edge.sourceHandle} → </span>
                  <code>{edge.target}</code>
                  <span style={{ color: '#64748b' }}> :{edge.targetHandle}</span>
                </div>
              </div>
            ))}

            {layoutNodes.length > 0 && (
              <div style={{ borderTop: '1px solid var(--border-color)', marginTop: 4 }}>
                <button
                  type="button"
                  onClick={() => setShowLayout((value) => !value)}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: 6,
                    width: '100%',
                    padding: '10px 18px',
                    background: 'transparent',
                    border: 'none',
                    color: 'var(--text-secondary)',
                    fontSize: '0.78rem',
                    cursor: 'pointer',
                  }}
                  aria-expanded={showLayout}
                >
                  {showLayout ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                  {layoutNodes.length} layout-only change{layoutNodes.length === 1 ? '' : 's'} (cosmetic)
                </button>
                {showLayout &&
                  layoutNodes.map((node) => (
                    <div key={node.nodeId} style={{ ...rowStyle, opacity: 0.8 }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                        <span style={{ ...kindBadge, color: 'var(--text-secondary)' }}>moved</span>
                        <code style={{ fontSize: '0.8rem', color: 'var(--text-secondary)' }}>{node.nodeId}</code>
                      </div>
                    </div>
                  ))}
              </div>
            )}
          </>
        )}
      </div>
    </aside>
  );
}

const rowStyle = {
  display: 'flex',
  flexDirection: 'column',
  gap: 2,
  padding: '10px 18px',
  borderBottom: '1px solid rgba(255,255,255,0.04)',
} as const;

const kindBadge = {
  fontSize: '0.64rem',
  fontWeight: 700,
  letterSpacing: '0.04em',
  textTransform: 'uppercase',
  padding: '2px 6px',
  borderRadius: 6,
  background: 'rgba(255,255,255,0.06)',
} as const;
