// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useRef, useState } from 'react';
import { Check, ChevronDown, GitCompare } from 'lucide-react';
import type { WorkflowVersionSummary } from '../types';

interface VersionRuntimeSelectProps {
  /** Versions in display order (newest first). */
  versions: WorkflowVersionSummary[];
  /** Currently committed (viewing) version id. */
  value: string;
  /** The live/active version id — marked with the Active badge. */
  activeVersionId?: string | null;
  disabled?: boolean;
  /** Delay before lingering on an item triggers a preview, so a quick scroll-past doesn't fire. */
  hoverPreviewDelayMs?: number;
  /** Commit a selection (parent also previews it). */
  onSelect: (versionId: string) => void;
  /**
   * Transiently preview a version while the user lingers on it, or `null` to revert to the committed
   * selection (e.g. when the menu closes without a commit).
   */
  onHoverPreview: (versionId: string | null) => void;
  /** Optional "Compare two versions" footer action (opens the diff/history surface). */
  onCompare?: () => void;
  /**
   * Make a version the live/active one directly, without running it or re-restoring. Omit to hide the
   * per-row "Set active" affordance (e.g. read-only contexts). The parent handles the API call + errors
   * (an old version may fail to compile on activation).
   */
  onActivate?: (versionId: string) => void;
  /** The version id currently being activated (shows a spinner + disables the row action). */
  activatingVersionId?: string | null;
}

/** Relative timestamp like "2m ago" / "Yesterday" / "3w ago" for a version's createdAt. */
function relativeTime(iso: string): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) {
    return '';
  }

  const seconds = Math.max(0, Math.floor((Date.now() - then) / 1000));
  if (seconds < 60) {
    return 'just now';
  }
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }
  const days = Math.floor(hours / 24);
  if (days === 1) {
    return 'Yesterday';
  }
  if (days < 7) {
    return `${days}d ago`;
  }
  if (days < 30) {
    return `${Math.floor(days / 7)}w ago`;
  }
  if (days < 365) {
    return `${Math.floor(days / 30)}mo ago`;
  }
  return `${Math.floor(days / 365)}y ago`;
}

/**
 * Themed runtime-version picker. A native &lt;select&gt; can't expose per-option hover or distinguish
 * the states that matter, so this renders the list itself: it previews a version when the user lingers
 * on it (mouse or keyboard), and marks which version is Active (runs), which is Latest, and which is
 * being viewed. Committing happens only on click / Enter — hovering never activates.
 */
export function VersionRuntimeSelect({
  versions,
  value,
  activeVersionId,
  disabled = false,
  hoverPreviewDelayMs = 300,
  onSelect,
  onHoverPreview,
  onCompare,
  onActivate,
  activatingVersionId,
}: VersionRuntimeSelectProps) {
  const [open, setOpen] = useState(false);
  const [highlight, setHighlight] = useState(-1);
  const rootRef = useRef<HTMLDivElement>(null);
  const hoverTimer = useRef<number | null>(null);

  const selected = versions.find((version) => version.id === value);

  const clearHoverTimer = () => {
    if (hoverTimer.current != null) {
      window.clearTimeout(hoverTimer.current);
      hoverTimer.current = null;
    }
  };

  const close = (revert: boolean) => {
    clearHoverTimer();
    setOpen(false);
    setHighlight(-1);
    if (revert) {
      onHoverPreview(null);
    }
  };

  useEffect(() => {
    if (!open) {
      return;
    }

    const onDocMouseDown = (event: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(event.target as Node)) {
        close(true);
      }
    };

    document.addEventListener('mousedown', onDocMouseDown);
    return () => document.removeEventListener('mousedown', onDocMouseDown);
  }, [open]);

  const previewAt = (index: number) => {
    const version = versions[index];
    if (version) {
      onHoverPreview(version.id);
    }
  };

  const handleItemEnter = (index: number) => {
    setHighlight(index);
    clearHoverTimer();
    hoverTimer.current = window.setTimeout(() => previewAt(index), hoverPreviewDelayMs);
  };

  const commit = (versionId: string) => {
    clearHoverTimer();
    setOpen(false);
    setHighlight(-1);
    onSelect(versionId);
  };

  const handleKeyDown = (event: React.KeyboardEvent) => {
    if (disabled) {
      return;
    }

    if (!open) {
      if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        setOpen(true);
        setHighlight(Math.max(0, versions.findIndex((version) => version.id === value)));
      }
      return;
    }

    switch (event.key) {
      case 'ArrowDown': {
        event.preventDefault();
        const next = Math.min(versions.length - 1, highlight + 1);
        setHighlight(next);
        previewAt(next);
        break;
      }
      case 'ArrowUp': {
        event.preventDefault();
        const prev = Math.max(0, highlight - 1);
        setHighlight(prev);
        previewAt(prev);
        break;
      }
      case 'Enter': {
        event.preventDefault();
        if (versions[highlight]) {
          commit(versions[highlight].id);
        }
        break;
      }
      case 'Escape': {
        event.preventDefault();
        close(true);
        break;
      }
      default:
        break;
    }
  };

  return (
    <div ref={rootRef} className={`vrs${open ? ' vrs-open' : ''}`} onKeyDown={handleKeyDown}>
      <button
        type="button"
        className="vrs-trigger"
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label="Runtime version"
        onClick={() => (open ? close(true) : setOpen(true))}
      >
        <span className="vrs-num">{selected ? `v${selected.versionNumber}` : '—'}</span>
        {selected && (
          <span className="vrs-meta">{selected.id === activeVersionId ? 'ACTIVE' : 'VIEWING'}</span>
        )}
        <span className="vrs-chev"><ChevronDown size={14} /></span>
      </button>

      {open && versions.length > 0 && (
        <div className="vrs-menu">
          <div className="vrs-head">
            <span className="vrs-head-t">Select version</span>
            <span className="vrs-head-c">{versions.length} saved</span>
          </div>
          {/* Names the two distinct actions so the row-click isn't mistaken for "switch to this version".
              Clicking previews (read-only, reversible); Set active is the commit that changes what runs. */}
          <p className="vrs-hint">
            Click a version to <strong>preview</strong> it{onActivate ? <> · <strong>Set active</strong> makes it the live runtime version</> : null}
          </p>

          <div className="vrs-list" role="listbox" aria-label="Runtime version" onMouseLeave={clearHoverTimer}>
            {versions.map((version, index) => {
              const isViewing = version.id === value;
              const isActive = version.id === activeVersionId;
              const isLatest = index === 0;
              const hasRuns = version.executionCount > 0;
              return (
                <div
                  key={version.id}
                  role="option"
                  aria-selected={isViewing}
                  aria-label={`Version ${version.versionNumber}`}
                  className={`vrs-row${isViewing ? ' vrs-viewing' : ''}${index === highlight ? ' vrs-hl' : ''}`}
                  onMouseEnter={() => handleItemEnter(index)}
                  onClick={() => commit(version.id)}
                  title={isActive ? 'Preview the live version' : 'Preview this version (read-only)'}
                >
                  <span className="vrs-tick"><Check size={14} /></span>
                  <span
                    className={`vrs-dot ${hasRuns ? 'ok' : 'none'}`}
                    title={hasRuns ? `${version.executionCount} run(s)` : 'No runs yet'}
                  />
                  <span className="vrs-vn">v{version.versionNumber}</span>
                  <span className="vrs-stamp">{relativeTime(version.createdAt)}</span>
                  <span className="vrs-spacer" />
                  {/* Direct activation: the row click only *views* a version; this makes it the live
                      one without a run or a re-restore. stopPropagation so it doesn't also commit-view. */}
                  {!isActive && onActivate && (
                    <button
                      type="button"
                      className="vrs-activate"
                      disabled={activatingVersionId != null}
                      aria-label={`Set version ${version.versionNumber} active`}
                      title="Make this the live/active version"
                      onClick={(event) => {
                        event.stopPropagation();
                        onActivate(version.id);
                      }}
                    >
                      {activatingVersionId === version.id ? 'Activating…' : 'Set active'}
                    </button>
                  )}
                  {isActive ? (
                    <span className="vrs-badge active">Active</span>
                  ) : isLatest ? (
                    <span className="vrs-badge latest">Latest</span>
                  ) : null}
                </div>
              );
            })}
          </div>

          {onCompare && (
            <div className="vrs-foot">
              <button
                type="button"
                onClick={() => {
                  close(false);
                  onCompare();
                }}
              >
                <GitCompare size={14} /> Compare two versions
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
