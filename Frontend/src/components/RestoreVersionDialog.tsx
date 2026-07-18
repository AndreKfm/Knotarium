// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useState } from 'react';
import { AlertTriangle, RotateCcw, X, CheckCircle2 } from 'lucide-react';
import { useScrimClose } from '../hooks/useScrimClose';
import type { RestoreVersionResult } from '../types';

export interface RestoreVersionDialogProps {
  /** Version number being restored (the source). */
  versionNumber: number;
  busy: boolean;
  /** Result of a completed restore (success view), or null while confirming. */
  result: RestoreVersionResult | null;
  /** Error message from a failed restore (e.g. 400 compile diagnostics / 409 concurrency). */
  error: string | null;
  onConfirm: (options: { activate: boolean }) => void;
  onClose: () => void;
}

/**
 * Restore confirmation dialog (plan §5 / §8.2). Makes the semantics explicit:
 * restore is FORK-FORWARD (creates a new version copied from the source) and
 * affects FUTURE executions only — it does not undo side effects of past runs.
 * Offers activate-now vs restore-inactive (the latter is the safe default), and
 * surfaces compile/concurrency failures or compatibility warnings.
 */
export function RestoreVersionDialog({
  versionNumber,
  busy,
  result,
  error,
  onConfirm,
  onClose,
}: RestoreVersionDialogProps) {
  const [activate, setActivate] = useState(false);
  const onScrimMouseDown = useScrimClose(onClose, !busy);

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label={`Restore version ${versionNumber}`}
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(2, 5, 10, 0.66)',
        display: 'grid',
        placeItems: 'center',
        zIndex: 1100,
      }}
      onMouseDown={onScrimMouseDown}
    >
      <div
        onClick={(event) => event.stopPropagation()}
        style={{
          width: 'min(460px, 92vw)',
          background: '#0c111b',
          border: '1px solid #283246',
          borderRadius: 14,
          boxShadow: '0 24px 60px rgba(0,0,0,0.5)',
          color: '#e6edf5',
          overflow: 'hidden',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '16px 20px', borderBottom: '1px solid #1d2737' }}>
          <RotateCcw size={18} color="#facc15" />
          <strong style={{ fontSize: '1rem', flex: 1 }}>
            {result ? 'Version restored' : `Restore version ${versionNumber}`}
          </strong>
          <button
            onClick={onClose}
            disabled={busy}
            aria-label="Close"
            style={{ background: 'transparent', border: 'none', color: '#8794a6', cursor: busy ? 'default' : 'pointer' }}
          >
            <X size={18} />
          </button>
        </div>

        {!result ? (
          <div style={{ padding: '18px 20px', display: 'flex', flexDirection: 'column', gap: 16 }}>
            <p style={{ margin: 0, fontSize: '0.85rem', color: '#aab6c4', lineHeight: 1.5 }}>
              Restore copies version <strong style={{ color: '#fde68a' }}>v{versionNumber}</strong> forward into a
              new version (fork-forward) — the history stays append-only and nothing is overwritten.
            </p>
            <div
              style={{
                border: '1px solid rgba(255, 184, 76, 0.35)',
                background: 'rgba(255, 184, 76, 0.08)',
                borderRadius: 10,
                padding: '10px 12px',
                display: 'flex',
                gap: 8,
                color: '#e0c79a',
                fontSize: '0.78rem',
                lineHeight: 1.5,
              }}
            >
              <AlertTriangle size={15} style={{ marginTop: 1, flex: '0 0 15px', color: '#ffce8a' }} />
              <span>
                Restoring affects <strong>future executions only</strong>. It does not undo side effects already
                caused by runs of the newer version.
              </span>
            </div>

            <label style={{ display: 'flex', alignItems: 'flex-start', gap: 8, fontSize: '0.8rem', color: '#cdd7e3', cursor: 'pointer' }}>
              <input
                type="checkbox"
                checked={activate}
                onChange={(event) => setActivate(event.target.checked)}
                disabled={busy}
                style={{ marginTop: 2 }}
              />
              <span>
                Activate now
                <span style={{ display: 'block', color: '#7d8a9b', fontSize: '0.72rem' }}>
                  Make the restored version live immediately. Requires a clean compile; otherwise the restore
                  still creates an inactive forward copy you can fix first.
                </span>
              </span>
            </label>

            {error && (
              <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start', color: '#ffb4b4', fontSize: '0.78rem' }}>
                <AlertTriangle size={15} style={{ marginTop: 1, flex: '0 0 15px' }} />
                <span>{error}</span>
              </div>
            )}

            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10 }}>
              <button
                onClick={onClose}
                disabled={busy}
                style={secondaryBtnStyle(busy)}
              >
                Cancel
              </button>
              <button
                onClick={() => onConfirm({ activate })}
                disabled={busy}
                style={primaryBtnStyle(busy)}
              >
                {busy ? 'Restoring…' : activate ? 'Restore & activate' : 'Restore (inactive)'}
              </button>
            </div>
          </div>
        ) : (
          <div style={{ padding: '18px 20px', display: 'flex', flexDirection: 'column', gap: 16 }}>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', color: '#86efac', fontSize: '0.86rem' }}>
              <CheckCircle2 size={16} />
              <span>
                Created version <strong>v{result.versionNumber}</strong>
                {result.activated ? ' — now active.' : ' — inactive forward copy.'}
              </span>
            </div>

            {result.warnings.length > 0 && (
              <div
                style={{
                  border: '1px solid rgba(255, 184, 76, 0.35)',
                  background: 'rgba(255, 184, 76, 0.08)',
                  borderRadius: 10,
                  padding: '12px 14px',
                }}
              >
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, color: '#ffce8a', fontSize: '0.8rem', fontWeight: 600, marginBottom: 8 }}>
                  <AlertTriangle size={15} />
                  Compatibility warnings
                </div>
                <ul style={{ margin: 0, paddingLeft: 18, color: '#e0c79a', fontSize: '0.78rem', lineHeight: 1.6 }}>
                  {result.warnings.map((warning, index) => (
                    <li key={index}>{warning}</li>
                  ))}
                </ul>
              </div>
            )}

            <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
              <button onClick={onClose} style={primaryBtnStyle(false)}>
                Done
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function secondaryBtnStyle(busy: boolean) {
  return {
    padding: '8px 14px',
    borderRadius: 8,
    background: 'transparent',
    border: '1px solid #283246',
    color: '#cdd7e3',
    cursor: busy ? 'default' : 'pointer',
    fontSize: '0.82rem',
  } as const;
}

function primaryBtnStyle(busy: boolean) {
  return {
    padding: '8px 16px',
    borderRadius: 8,
    background: busy ? '#1f3a73' : '#2563eb',
    border: '1px solid #2f6fed',
    color: '#fff',
    cursor: busy ? 'default' : 'pointer',
    fontSize: '0.82rem',
    fontWeight: 600,
  } as const;
}
