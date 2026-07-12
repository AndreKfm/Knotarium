import { useEffect, useState } from 'react';
import { Lightbulb, ChevronRight, X } from 'lucide-react';
import { TIPS } from './tips';

const HIDDEN_KEY = 'kg-tips-hidden';
const INDEX_KEY = 'kg-tip-index';

function readHidden(): boolean {
  try { return localStorage.getItem(HIDDEN_KEY) === '1'; } catch { return false; }
}

function readIndex(): number {
  try {
    const n = Number(localStorage.getItem(INDEX_KEY));
    return Number.isInteger(n) && n >= 0 ? n : 0;
  } catch { return 0; }
}

/**
 * "Tip of the Day" — a subtle, dismissible card on the dashboard that surfaces one feature hint.
 * The tip rotates on each app open (persisted index); "Next" browses manually; the ✕ hides the card
 * for good (kg-tips-hidden). Data lives in tips.ts so the set is testable.
 */
export function TipOfTheDay() {
  const [hidden, setHidden] = useState(readHidden);
  const [index, setIndex] = useState(() => (TIPS.length ? readIndex() % TIPS.length : 0));

  // Advance the persisted index once per mount so the next visit opens on a different tip.
  useEffect(() => {
    if (!TIPS.length) return;
    try { localStorage.setItem(INDEX_KEY, String((readIndex() + 1) % TIPS.length)); } catch { /* ignore */ }
  }, []);

  if (hidden || TIPS.length === 0) return null;

  const tip = TIPS[index % TIPS.length];
  const next = () => setIndex((i) => (i + 1) % TIPS.length);
  const dismiss = () => {
    setHidden(true);
    try { localStorage.setItem(HIDDEN_KEY, '1'); } catch { /* ignore */ }
  };

  return (
    <div
      role="note"
      aria-label="Tip of the day"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: '14px',
        padding: '14px 16px',
        marginBottom: '24px',
        borderRadius: '12px',
        background: 'color-mix(in srgb, var(--color-accent) 8%, transparent)',
        border: '1px solid color-mix(in srgb, var(--color-accent) 30%, transparent)',
      }}
    >
      <div
        style={{
          flexShrink: 0,
          width: 34,
          height: 34,
          borderRadius: 9,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          background: 'color-mix(in srgb, var(--color-accent) 18%, transparent)',
          color: 'var(--color-accent)',
        }}
      >
        <Lightbulb size={18} />
      </div>

      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ fontSize: '0.72rem', fontWeight: 700, letterSpacing: '0.04em', textTransform: 'uppercase', color: 'var(--color-accent)', marginBottom: 2 }}>
          Tip
        </div>
        <div style={{ fontSize: '0.9rem', color: 'var(--text-primary)', lineHeight: 1.45 }}>
          {tip.text}
        </div>
      </div>

      <button
        onClick={next}
        aria-label="Next tip"
        title="Next tip"
        style={{
          flexShrink: 0,
          display: 'flex',
          alignItems: 'center',
          gap: '4px',
          padding: '7px 12px',
          borderRadius: '8px',
          background: 'rgba(255, 255, 255, 0.04)',
          border: '1px solid var(--border-color)',
          color: 'var(--text-secondary)',
          fontSize: '0.82rem',
          fontWeight: 600,
          cursor: 'pointer',
        }}
      >
        Next
        <ChevronRight size={15} />
      </button>

      <button
        onClick={dismiss}
        aria-label="Dismiss tips"
        title="Don't show tips"
        style={{
          flexShrink: 0,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          width: 30,
          height: 30,
          borderRadius: '8px',
          background: 'transparent',
          border: 'none',
          color: 'var(--text-secondary)',
          cursor: 'pointer',
        }}
      >
        <X size={16} />
      </button>
    </div>
  );
}
