import { MousePointerClick } from 'lucide-react';

/**
 * A non-interactive coach hint centered on an empty editor canvas — the "what do I do now?"
 * moment right after creating a first, blank workflow. Rendered only while the draft has no nodes;
 * `pointer-events: none` so it never blocks dropping the first node or panning.
 */
export function EmptyCanvasHint() {
  return (
    <div
      aria-hidden="true"
      style={{
        position: 'absolute',
        inset: 0,
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: '14px',
        pointerEvents: 'none',
        textAlign: 'center',
        padding: '24px',
      }}
    >
      <div
        style={{
          width: 52,
          height: 52,
          borderRadius: 14,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          background: 'color-mix(in srgb, var(--color-accent) 12%, transparent)',
          color: 'var(--color-accent)',
        }}
      >
        <MousePointerClick size={24} />
      </div>
      <div style={{ fontSize: '1.05rem', fontWeight: 700, color: 'var(--text-primary)' }}>
        Your canvas is empty
      </div>
      <div style={{ fontSize: '0.9rem', color: 'var(--text-secondary)', lineHeight: 1.5, maxWidth: 340 }}>
        Drag a node from the palette on the left to begin — or press{' '}
        <kbd style={{ padding: '1px 6px', borderRadius: 5, border: '1px solid var(--border-color)', background: 'rgba(255,255,255,0.05)', fontSize: '0.8rem' }}>
          Ctrl / ⌘ + F
        </kbd>{' '}
        to search for one.
      </div>
    </div>
  );
}
