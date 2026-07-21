// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useRef } from 'react';
import { History, X, Eye, GitCompareArrows, ChevronRight } from 'lucide-react';
import type { WorkflowVersionSummary, WorkflowVersionOrigin } from '../types';

export interface VersionHistoryPanelProps {
  open: boolean;
  versions: WorkflowVersionSummary[];
  loading: boolean;
  error: string | null;
  activeVersionId?: string | null;
  /** The version currently shown in the read-only preview — its row is highlighted. */
  previewVersionId?: string | null;
  /** Delay before lingering on a row auto-previews it (matches the runtime dropdown). */
  hoverPreviewDelayMs?: number;
  onClose: () => void;
  /** Open a read-only preview of the clicked version (V2). Restore / Exit live in the preview banner. */
  onPreview?: (versionId: string) => void;
  /** Diff this specific version against the working draft directly — no need to preview it first. */
  onDiffVersion?: (versionId: string) => void;
  /** Diff the working draft against the active version (the most-wanted case, V5). */
  onDiffDraftVsActive?: () => void;
}

/** Human-readable absolute timestamp; falls back to the raw string if unparseable. */
function formatTimestamp(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

const ORIGIN_LABELS: Record<WorkflowVersionOrigin, string> = {
  Published: 'Published',
  Restored: 'Restored',
  Imported: 'Imported',
};

const ORIGIN_COLORS: Record<WorkflowVersionOrigin, string> = {
  Published: 'rgba(94, 234, 212, 0.16)',
  Restored: 'rgba(250, 204, 21, 0.16)',
  Imported: 'rgba(129, 140, 248, 0.16)',
};

/**
 * Collapsible right-edge drawer listing a workflow's published version history
 * (metadata only). Each row shows the version number, timestamp, author, label,
 * an ACTIVE badge and the origin. Clicking a row forwards the version id to
 * {@link VersionHistoryPanelProps.onPreview} (preview itself lands in a later
 * task). Mounted only while `open`, so it does not fetch in the background.
 */
export function VersionHistoryPanel({
  open,
  versions,
  loading,
  error,
  activeVersionId,
  previewVersionId,
  hoverPreviewDelayMs = 300,
  onClose,
  onPreview,
  onDiffVersion,
  onDiffDraftVsActive,
}: VersionHistoryPanelProps) {
  // Debounce timer for hover-to-preview, so a quick scroll-past doesn't fire a preview.
  const hoverTimer = useRef<number | null>(null);
  const clearHoverTimer = () => {
    if (hoverTimer.current != null) {
      window.clearTimeout(hoverTimer.current);
      hoverTimer.current = null;
    }
  };

  if (!open) {
    return null;
  }

  return (
    <aside
      role="complementary"
      aria-label="Version history"
      style={{
        position: 'absolute',
        top: 0,
        right: 0,
        bottom: 0,
        width: 'min(360px, 90%)',
        zIndex: 950,
        display: 'flex',
        flexDirection: 'column',
        background: 'var(--bg-surface-opaque, #101625)',
        borderLeft: '1px solid var(--border-color)',
        boxShadow: '-12px 0 40px rgba(0,0,0,0.45)',
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: '8px',
          padding: '14px 18px',
          borderBottom: '1px solid var(--border-color)',
        }}
      >
        <History size={16} color="var(--color-accent, #3b82f6)" />
        <strong style={{ flex: 1, fontSize: '0.95rem', color: 'var(--text-primary, #e5e7eb)' }}>
          Version history
        </strong>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close version history"
          style={{
            background: 'transparent',
            border: 'none',
            color: 'var(--text-secondary)',
            cursor: 'pointer',
            display: 'flex',
            alignItems: 'center',
          }}
        >
          <X size={16} />
        </button>
      </div>

      <div style={{ flex: 1, overflowY: 'auto' }}>
        {loading ? (
          <div style={{ padding: '16px 18px', color: 'var(--text-secondary)', fontSize: '0.85rem' }}>
            Loading versions…
          </div>
        ) : error ? (
          <div style={{ padding: '16px 18px', color: 'var(--color-error, #f87171)', fontSize: '0.85rem' }}>
            {error}
          </div>
        ) : versions.length === 0 ? (
          <div style={{ padding: '16px 18px', color: 'var(--text-secondary)', fontSize: '0.85rem' }}>
            No published versions yet.
          </div>
        ) : (
          versions.map((version) => {
            const isActive = version.isActive || version.id === activeVersionId;
            const isPreviewing = version.id === previewVersionId;
            const baseBackground = isPreviewing
              ? 'var(--color-accent-glow, rgba(99, 102, 241, 0.18))'
              : isActive
                ? 'rgba(94, 234, 212, 0.06)'
                : 'transparent';
            return (
              <div
                key={version.id}
                role="button"
                tabIndex={0}
                aria-current={isPreviewing ? 'true' : undefined}
                onClick={() => onPreview?.(version.id)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    onPreview?.(version.id);
                  }
                }}
                style={{
                  display: 'flex',
                  flexDirection: 'column',
                  gap: '4px',
                  padding: '12px 18px',
                  borderBottom: '1px solid var(--border-color)',
                  cursor: 'pointer',
                  background: baseBackground,
                  boxShadow: isPreviewing ? 'inset 3px 0 0 var(--color-accent, #6366f1)' : 'none',
                  transition: 'background .12s',
                }}
                onMouseEnter={(e) => {
                  e.currentTarget.style.background = isPreviewing ? baseBackground : 'rgba(255,255,255,0.04)';
                  // Lingering on a row auto-previews it after the debounce (matches the runtime dropdown).
                  if (onPreview) {
                    clearHoverTimer();
                    hoverTimer.current = window.setTimeout(() => onPreview(version.id), hoverPreviewDelayMs);
                  }
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.background = baseBackground;
                  clearHoverTimer();
                }}
              >
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                  <span style={{ fontWeight: 700, fontSize: '0.9rem', color: 'var(--text-primary, #e5e7eb)' }}>
                    v{version.versionNumber}
                  </span>
                  {isPreviewing && (
                    <span
                      title="Currently previewing"
                      style={{ display: 'inline-flex', alignItems: 'center', color: 'var(--color-accent, #6366f1)' }}
                    >
                      <Eye size={13} />
                    </span>
                  )}
                  {isActive && (
                    <span
                      style={{
                        fontSize: '0.65rem',
                        fontWeight: 700,
                        letterSpacing: '0.04em',
                        padding: '2px 6px',
                        borderRadius: '6px',
                        background: 'rgba(94, 234, 212, 0.16)',
                        color: '#5eead4',
                      }}
                    >
                      ACTIVE
                    </span>
                  )}
                  <span
                    style={{
                      fontSize: '0.65rem',
                      fontWeight: 600,
                      padding: '2px 6px',
                      borderRadius: '6px',
                      background: ORIGIN_COLORS[version.origin],
                      color: 'var(--text-secondary)',
                      marginLeft: 'auto',
                    }}
                  >
                    {ORIGIN_LABELS[version.origin]}
                  </span>
                </div>

                {version.label && (
                  <span style={{ fontSize: '0.82rem', color: 'var(--text-primary, #e5e7eb)' }}>
                    {version.label}
                  </span>
                )}

                <div style={{ fontSize: '0.72rem', color: 'var(--text-secondary)' }}>
                  {formatTimestamp(version.createdAt)}
                  {version.createdBy ? ` · ${version.createdBy}` : ''}
                </div>

                <div style={{ fontSize: '0.7rem', color: 'var(--text-secondary)', opacity: 0.8 }}>
                  {version.nodeCount} node{version.nodeCount === 1 ? '' : 's'}
                  {version.executionCount > 0
                    ? ` · ${version.executionCount} run${version.executionCount === 1 ? '' : 's'}`
                    : ''}
                </div>

                {/* Row click opens a read-only preview (Restore/Exit live in the banner); the Diff button
                    is a direct shortcut to compare this version against the working draft — no need to
                    preview it first. stopPropagation so Diff doesn't also open the preview. */}
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px', marginTop: '6px' }}>
                  <span style={{ display: 'flex', alignItems: 'center', gap: '4px', color: 'var(--text-secondary)', fontSize: '0.72rem', fontWeight: 600, opacity: isPreviewing ? 1 : 0.7 }}>
                    <Eye size={12} />
                    {isPreviewing ? 'Previewing' : 'Open'}
                    {!isPreviewing && <ChevronRight size={12} />}
                  </span>
                  {onDiffVersion && (
                    <button
                      type="button"
                      className="vhp-diff-btn"
                      title="Compare this version with your working draft"
                      aria-label={`Diff version ${version.versionNumber} against the working draft`}
                      onClick={(event) => {
                        event.stopPropagation();
                        onDiffVersion(version.id);
                      }}
                    >
                      <GitCompareArrows size={12} />
                      Diff
                    </button>
                  )}
                </div>
              </div>
            );
          })
        )}
      </div>

      {/* The most-wanted diff (plan §7.4): the live working draft against the
          currently-active version. First-class so it doesn't require selecting a row. */}
      {onDiffDraftVsActive && (
        <div style={{ padding: '12px 18px', borderTop: '1px solid var(--border-color)' }}>
          <button
            type="button"
            onClick={onDiffDraftVsActive}
            title="Compare your working draft with the active version"
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              width: '100%',
              justifyContent: 'center',
              padding: '8px 12px',
              borderRadius: '8px',
              background: 'rgba(255, 255, 255, 0.04)',
              border: '1px solid var(--border-color)',
              color: 'var(--text-primary, #e5e7eb)',
              fontSize: '0.8rem',
              cursor: 'pointer',
            }}
          >
            <GitCompareArrows size={14} />
            Diff draft vs active
          </button>
        </div>
      )}
    </aside>
  );
}
