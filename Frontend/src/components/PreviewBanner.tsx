import type { CSSProperties } from 'react';
import { Eye, RotateCcw, X, GitCompareArrows } from 'lucide-react';

export interface PreviewBannerProps {
  /** Version number being previewed (read-only). */
  versionNumber: number;
  /** True while the previewed version is the live/active one. */
  isActiveVersion?: boolean;
  onExit: () => void;
  onRestore?: () => void;
  onDiffAgainstDraft?: () => void;
}

/**
 * Sticky banner shown while the editor is in read-only PublishedPreview mode (plan §7.3). It reads as
 * a <em>mode</em>, not an alarm: a neutral surface bar with amber used only as accent (the contained
 * icon, the READ-ONLY tag, and Exit preview — the action the bar exists to offer). Editing / autosave /
 * publish are disabled by the canvas while this is up.
 */
export function PreviewBanner({
  versionNumber,
  isActiveVersion,
  onExit,
  onRestore,
  onDiffAgainstDraft,
}: PreviewBannerProps) {
  const subtitle = isActiveVersion
    ? 'This is the active version — exit to return to your draft.'
    : 'Restore it to keep editing, or exit to return to your draft.';

  return (
    <div
      role="status"
      aria-label={`Previewing version ${versionNumber}, read-only`}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: '14px',
        padding: '10px 24px',
        // Neutral surface, with a soft amber lead-in that fades across the left edge + a thin amber rail.
        background:
          'linear-gradient(90deg, rgba(245, 158, 11, 0.10), rgba(245, 158, 11, 0) 28%), var(--bg-surface-opaque, #101625)',
        borderBottom: '1px solid var(--border-color)',
        boxShadow: 'inset 2px 0 0 var(--color-warning, #f59e0b)',
      }}
    >
      {/* Contained amber icon */}
      <span
        style={{
          display: 'grid',
          placeItems: 'center',
          width: '32px',
          height: '32px',
          flex: '0 0 32px',
          borderRadius: '8px',
          background: 'var(--color-warning-glow, rgba(245, 158, 11, 0.2))',
          border: '1px solid rgba(245, 158, 11, 0.3)',
          color: 'var(--color-warning, #f59e0b)',
        }}
      >
        <Eye size={16} />
      </span>

      <div style={{ display: 'flex', flexDirection: 'column', gap: '1px' }}>
        <span style={{ fontSize: '0.86rem', fontWeight: 700, color: 'var(--text-primary, #e5e7eb)' }}>
          Previewing <span style={{ color: 'var(--color-warning, #f59e0b)' }}>version {versionNumber}</span>
        </span>
        <span style={{ fontSize: '0.72rem', color: 'var(--text-muted)' }}>{subtitle}</span>
      </div>

      {/* READ-ONLY tag (amber accent) */}
      <span
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '6px',
          marginLeft: '6px',
          padding: '3px 9px',
          borderRadius: '6px',
          background: 'var(--color-warning-glow, rgba(245, 158, 11, 0.2))',
          color: 'var(--color-warning, #f59e0b)',
          fontSize: '0.62rem',
          fontWeight: 800,
          letterSpacing: '0.06em',
        }}
      >
        <span style={{ width: '6px', height: '6px', borderRadius: '99px', background: 'var(--color-warning, #f59e0b)' }} />
        READ-ONLY
      </span>

      <span style={{ flex: 1 }} />

      {onDiffAgainstDraft && (
        <button
          type="button"
          onClick={onDiffAgainstDraft}
          style={secondaryBtnStyle}
          title="Compare this version with your working draft"
        >
          <GitCompareArrows size={14} />
          Diff vs draft
        </button>
      )}

      {onRestore && (
        <button
          type="button"
          onClick={onRestore}
          style={secondaryBtnStyle}
          title="Restore this version (fork-forward)"
        >
          <RotateCcw size={14} />
          Restore
        </button>
      )}

      <span style={{ width: '1px', height: '22px', background: 'var(--border-color)', margin: '0 4px' }} />

      {/* Exit is the sole amber-tinted action — leaving is what the bar exists to offer. */}
      <button
        type="button"
        onClick={onExit}
        aria-label="Exit preview"
        style={exitBtnStyle}
        title="Exit preview and return to your draft"
      >
        <X size={14} />
        Exit preview
      </button>
    </div>
  );
}

const secondaryBtnStyle: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: '6px',
  padding: '6px 12px',
  borderRadius: '8px',
  background: 'rgba(255, 255, 255, 0.04)',
  border: '1px solid var(--border-color)',
  color: 'var(--text-secondary)',
  fontSize: '0.78rem',
  fontWeight: 600,
  cursor: 'pointer',
  fontFamily: 'inherit',
};

const exitBtnStyle: CSSProperties = {
  ...secondaryBtnStyle,
  background: 'var(--color-warning-glow, rgba(245, 158, 11, 0.2))',
  border: '1px solid rgba(245, 158, 11, 0.5)',
  color: 'var(--color-warning, #f59e0b)',
  fontWeight: 700,
};
