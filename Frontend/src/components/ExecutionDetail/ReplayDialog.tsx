// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useState } from 'react';
import { AlertTriangle, History, X } from 'lucide-react';
import type { ReplayResult, WorkflowVersionSummary } from '../../types';

type ReplayDialogProps = {
  nodeId: string;
  originalVersionId?: string;
  versions: WorkflowVersionSummary[];
  busy: boolean;
  result: ReplayResult | null;
  error: string | null;
  onConfirm: (options: { targetVersionId?: string; mockSideEffects: boolean }) => void;
  onClose: () => void;
  onOpenRun: (executionId: string) => void;
};

const ORIGINAL_VALUE = '__original__';

export function ReplayDialog({
  nodeId,
  originalVersionId,
  versions,
  busy,
  result,
  error,
  onConfirm,
  onClose,
  onOpenRun,
}: ReplayDialogProps) {
  const [selectedVersion, setSelectedVersion] = useState<string>(ORIGINAL_VALUE);
  const [mockSideEffects, setMockSideEffects] = useState(false);

  const sortedVersions = [...versions].sort((left, right) => right.versionNumber - left.versionNumber);

  const handleConfirm = () => {
    onConfirm({
      targetVersionId: selectedVersion === ORIGINAL_VALUE ? undefined : selectedVersion,
      mockSideEffects,
    });
  };

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label="Replay workflow from node"
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(2, 5, 10, 0.66)',
        display: 'grid',
        placeItems: 'center',
        zIndex: 1000,
      }}
      onClick={busy ? undefined : onClose}
    >
      <div
        onClick={(event) => event.stopPropagation()}
        style={{
          width: 'min(440px, 92vw)',
          background: '#0c111b',
          border: '1px solid #283246',
          borderRadius: 14,
          boxShadow: '0 24px 60px rgba(0,0,0,0.5)',
          color: '#e6edf5',
          overflow: 'hidden',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '16px 20px', borderBottom: '1px solid #1d2737' }}>
          <History size={18} color="#8fd3ff" />
          <strong style={{ fontSize: '1rem', flex: 1 }}>
            {result ? 'Replay created' : 'Re-run from here'}
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
              Start a new run from node{' '}
              <code style={{ color: '#8fd3ff', background: 'rgba(143,211,255,0.08)', padding: '1px 6px', borderRadius: 5 }}>{nodeId}</code>{' '}
              using the inputs this run had at that point. Upstream nodes are reused; this node and everything
              downstream re-execute.
            </p>

            <label style={{ display: 'flex', flexDirection: 'column', gap: 6, fontSize: '0.78rem', color: '#aab6c4' }}>
              Target version
              <select
                value={selectedVersion}
                onChange={(event) => setSelectedVersion(event.target.value)}
                disabled={busy}
                style={{
                  background: '#111826',
                  color: '#e6edf5',
                  border: '1px solid #283246',
                  borderRadius: 8,
                  padding: '8px 10px',
                  fontSize: '0.82rem',
                }}
              >
                <option value={ORIGINAL_VALUE}>This run&apos;s version (original)</option>
                {sortedVersions.map((version) => (
                  <option key={version.id} value={version.id}>
                    v{version.versionNumber}
                    {version.id === originalVersionId ? ' (original)' : ''}
                  </option>
                ))}
              </select>
            </label>

            <label style={{ display: 'flex', alignItems: 'flex-start', gap: 8, fontSize: '0.8rem', color: '#cdd7e3', cursor: 'pointer' }}>
              <input
                type="checkbox"
                checked={mockSideEffects}
                onChange={(event) => setMockSideEffects(event.target.checked)}
                disabled={busy}
                style={{ marginTop: 2 }}
              />
              <span>
                Mock side effects
                <span style={{ display: 'block', color: '#7d8a9b', fontSize: '0.72rem' }}>
                  Non-idempotent nodes replay their original output instead of firing for real.
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
                style={{
                  padding: '8px 14px',
                  borderRadius: 8,
                  background: 'transparent',
                  border: '1px solid #283246',
                  color: '#cdd7e3',
                  cursor: busy ? 'default' : 'pointer',
                  fontSize: '0.82rem',
                }}
              >
                Cancel
              </button>
              <button
                onClick={handleConfirm}
                disabled={busy}
                style={{
                  padding: '8px 16px',
                  borderRadius: 8,
                  background: busy ? '#1f3a73' : '#2563eb',
                  border: '1px solid #2f6fed',
                  color: '#fff',
                  cursor: busy ? 'default' : 'pointer',
                  fontSize: '0.82rem',
                  fontWeight: 600,
                }}
              >
                {busy ? 'Starting…' : 'Re-run from here'}
              </button>
            </div>
          </div>
        ) : (
          <div style={{ padding: '18px 20px', display: 'flex', flexDirection: 'column', gap: 16 }}>
            <p style={{ margin: 0, fontSize: '0.85rem', color: '#aab6c4', lineHeight: 1.5 }}>
              A new replay run was created from node{' '}
              <code style={{ color: '#8fd3ff' }}>{nodeId}</code>.
            </p>

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
                  Non-idempotent side effects will re-run
                </div>
                <ul style={{ margin: 0, paddingLeft: 18, color: '#e0c79a', fontSize: '0.78rem', lineHeight: 1.6 }}>
                  {result.warnings.map((warning) => (
                    <li key={warning.nodeId}>
                      <code style={{ color: '#ffce8a' }}>{warning.nodeId}</code>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10 }}>
              <button
                onClick={onClose}
                style={{
                  padding: '8px 14px',
                  borderRadius: 8,
                  background: 'transparent',
                  border: '1px solid #283246',
                  color: '#cdd7e3',
                  cursor: 'pointer',
                  fontSize: '0.82rem',
                }}
              >
                Close
              </button>
              <button
                onClick={() => onOpenRun(result.newExecutionId)}
                style={{
                  padding: '8px 16px',
                  borderRadius: 8,
                  background: '#2563eb',
                  border: '1px solid #2f6fed',
                  color: '#fff',
                  cursor: 'pointer',
                  fontSize: '0.82rem',
                  fontWeight: 600,
                }}
              >
                Open replay run
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
