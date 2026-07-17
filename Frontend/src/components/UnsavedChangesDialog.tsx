// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { AlertTriangle } from 'lucide-react';

export interface UnsavedChangesDialogProps {
  /** True while the Save action is in flight — disables the buttons and shows a saving label. */
  saving?: boolean;
  onCancel: () => void;
  onDiscard: () => void;
  onSave: () => void;
}

/**
 * "Unsaved changes" confirmation shown when leaving the canvas with pending edits. Follows Knotarium's
 * dialog vocabulary (scrim + deep-shadowed surface, matching {@link RestoreVersionDialog}): an amber
 * warning chip in the header, and a clear button hierarchy — Cancel is a quiet ghost (pushed left),
 * Save & leave is the violet primary, and Discard & leave stays calm until hovered (so the destructive
 * action never shouts louder than the safe one). Hover states live in index.css (`.kg-dialog-*`).
 */
export function UnsavedChangesDialog({ saving = false, onCancel, onDiscard, onSave }: UnsavedChangesDialogProps) {
  return (
    <div
      className="kg-dialog-scrim"
      role="dialog"
      aria-modal="true"
      aria-label="Unsaved changes"
      onClick={saving ? undefined : onCancel}
    >
      <div className="kg-dialog-card" onClick={(e) => e.stopPropagation()}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '16px 20px', borderBottom: '1px solid #1d2737' }}>
          <span
            aria-hidden
            style={{
              display: 'grid', placeItems: 'center', width: 34, height: 34, flex: '0 0 34px',
              borderRadius: 9, background: 'rgba(245, 158, 11, 0.12)', border: '1px solid rgba(245, 158, 11, 0.35)',
            }}
          >
            <AlertTriangle size={18} color="var(--color-warning)" />
          </span>
          <strong style={{ fontSize: '1rem', color: 'var(--text-primary)' }}>Unsaved changes</strong>
        </div>

        <div style={{ padding: '18px 20px 20px', display: 'flex', flexDirection: 'column', gap: 20 }}>
          <p style={{ margin: 0, fontSize: '0.85rem', color: '#aab6c4', lineHeight: 1.55 }}>
            This workflow has changes that haven't been saved.{' '}
            <strong style={{ color: '#e6edf5', fontWeight: 600 }}>Save &amp; leave</strong> keeps them, or discard to
            leave them behind.
          </p>

          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <button type="button" className="kg-dialog-btn kg-dialog-ghost" onClick={onCancel} disabled={saving}>
              Cancel
            </button>
            <span style={{ flex: 1 }} />
            <button type="button" className="kg-dialog-btn kg-dialog-danger" onClick={onDiscard} disabled={saving}>
              Discard &amp; leave
            </button>
            <button type="button" className="kg-dialog-btn kg-dialog-primary" onClick={onSave} disabled={saving}>
              {saving ? 'Saving…' : 'Save & leave'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
