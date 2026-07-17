// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { CompilationDiagnostic } from '../types';
import {
  sortDiagnostics,
  countBySeverity,
  normalizeNodeId,
} from '../utils/diagnosticsNavigation';

export interface DiagnosticsPanelProps {
  diagnostics: CompilationDiagnostic[];
  collapsed: boolean;
  onToggleCollapse: () => void;
  /** Centre the canvas on the node / edge a diagnostic points at. */
  onFocus: (diagnostic: CompilationDiagnostic) => void;
}

const SEVERITY_COLOR: Record<string, string> = {
  Error: '#ef4444',
  Warning: '#f59e0b',
  Info: '#38bdf8',
};

/**
 * Dockable, non-blocking diagnostics panel (Feature #9). Replaces the old
 * always-on error overlay: it collapses to a compact summary bar and each row
 * is clickable to centre the canvas on the offending node / edge.
 *
 * Renders nothing when there are no diagnostics.
 */
export function DiagnosticsPanel({
  diagnostics,
  collapsed,
  onToggleCollapse,
  onFocus,
}: DiagnosticsPanelProps) {
  if (diagnostics.length === 0) {
    return null;
  }

  const counts = countBySeverity(diagnostics);
  const sorted = sortDiagnostics(diagnostics);
  const headerColor = counts.Error > 0 ? SEVERITY_COLOR.Error
    : counts.Warning > 0 ? SEVERITY_COLOR.Warning
    : SEVERITY_COLOR.Info;

  return (
    <div
      role="region"
      aria-label="Diagnostics"
      style={{
        position: 'absolute',
        bottom: '24px',
        left: '24px',
        width: 'min(440px, calc(100% - 280px))',
        maxHeight: collapsed ? undefined : '240px',
        display: 'flex',
        flexDirection: 'column',
        background: 'rgba(16, 22, 37, 0.92)',
        backdropFilter: 'blur(10px)',
        border: `1px solid ${headerColor}`,
        borderRadius: '10px',
        overflow: 'hidden',
        zIndex: 4,
        boxShadow: '0 12px 40px rgba(0,0,0,0.4)',
      }}
    >
      <button
        type="button"
        aria-label={collapsed ? 'Expand diagnostics' : 'Collapse diagnostics'}
        aria-expanded={!collapsed}
        onClick={onToggleCollapse}
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: '10px',
          padding: '10px 14px',
          border: 'none',
          background: 'transparent',
          color: headerColor,
          fontWeight: 700,
          fontSize: '0.82rem',
          cursor: 'pointer',
          textAlign: 'left',
        }}
      >
        <span style={{ transform: collapsed ? 'rotate(-90deg)' : 'none', transition: 'transform 120ms' }}>▾</span>
        <span>Diagnostics</span>
        <span style={{ display: 'flex', gap: '8px', marginLeft: 'auto', fontSize: '0.74rem' }}>
          {counts.Error > 0 && <SeverityCount color={SEVERITY_COLOR.Error} label="errors" n={counts.Error} />}
          {counts.Warning > 0 && <SeverityCount color={SEVERITY_COLOR.Warning} label="warnings" n={counts.Warning} />}
          {counts.Info > 0 && <SeverityCount color={SEVERITY_COLOR.Info} label="info" n={counts.Info} />}
        </span>
      </button>

      {!collapsed && (
        <div style={{ overflowY: 'auto', padding: '4px 8px 8px', display: 'flex', flexDirection: 'column', gap: '4px' }}>
          {sorted.map((d, index) => {
            const color = SEVERITY_COLOR[d.severity] ?? SEVERITY_COLOR.Info;
            const nodeIdStr = normalizeNodeId(d.nodeId);
            const where = d.edgeId ? 'edge' : nodeIdStr ? `node ${nodeIdStr}` : null;
            return (
              <button
                key={`${d.code}-${index}`}
                type="button"
                onClick={() => onFocus(d)}
                title="Click to locate on the canvas"
                style={{
                  display: 'flex',
                  alignItems: 'flex-start',
                  gap: '8px',
                  width: '100%',
                  textAlign: 'left',
                  padding: '7px 9px',
                  border: '1px solid transparent',
                  borderLeft: `3px solid ${color}`,
                  borderRadius: '6px',
                  background: 'rgba(255,255,255,0.03)',
                  color: 'rgba(255,255,255,0.9)',
                  fontSize: '0.78rem',
                  cursor: 'pointer',
                }}
              >
                <span style={{ flex: 1 }}>
                  <strong style={{ color }}>[{d.code}]</strong> {d.message}
                  {where && <span style={{ color: 'var(--text-secondary, #9ca3af)' }}> · {where}</span>}
                </span>
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}

function SeverityCount({ color, label, n }: { color: string; label: string; n: number }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: '4px', color }}>
      <span style={{ width: '7px', height: '7px', borderRadius: '50%', background: color }} />
      {n} {label}
    </span>
  );
}
