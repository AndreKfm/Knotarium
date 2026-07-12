import { useCallback, useEffect, useLayoutEffect, useState } from 'react';
import { X } from 'lucide-react';
import { TOUR_STEPS } from './tourSteps';

const CARD_WIDTH = 340;
const GAP = 14;

interface GuidedTourProps {
  /** Called when the tour is finished or skipped (parent persists "seen" and unmounts). */
  onClose: () => void;
}

interface Rect { top: number; left: number; width: number; height: number; }

/**
 * A lightweight guided product tour: spotlights one UI element per step (via its data-tour selector)
 * and explains it in a tooltip card, with Back / Next / Skip. Target-less steps (no selector) render
 * a centered card over a plain dim. No external dependency — positioning is recomputed on step change
 * and window resize. Closes on Escape.
 */
export function GuidedTour({ onClose }: GuidedTourProps) {
  const [index, setIndex] = useState(0);
  const [rect, setRect] = useState<Rect | null>(null);

  const step = TOUR_STEPS[index];
  const isFirst = index === 0;
  const isLast = index === TOUR_STEPS.length - 1;

  const measure = useCallback(() => {
    if (!step?.selector) { setRect(null); return; }
    const el = document.querySelector(step.selector);
    if (!el) { setRect(null); return; }
    el.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    const r = el.getBoundingClientRect();
    setRect({ top: r.top, left: r.left, width: r.width, height: r.height });
  }, [step]);

  useLayoutEffect(() => { measure(); }, [measure]);

  useEffect(() => {
    const onResize = () => measure();
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, [measure]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.stopPropagation(); onClose(); }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [onClose]);

  const next = () => (isLast ? onClose() : setIndex((i) => i + 1));
  const back = () => setIndex((i) => Math.max(0, i - 1));

  // Tooltip position: below a spotlighted target (nav lives at the top), clamped to the viewport;
  // centered when there is no target.
  const card: React.CSSProperties = rect
    ? {
        position: 'fixed',
        top: rect.top + rect.height + GAP,
        left: Math.min(Math.max(rect.left, 16), window.innerWidth - CARD_WIDTH - 16),
        width: CARD_WIDTH,
      }
    : {
        position: 'fixed',
        top: '50%',
        left: '50%',
        transform: 'translate(-50%, -50%)',
        width: CARD_WIDTH,
      };

  return (
    <div role="dialog" aria-label="Product tour" style={{ position: 'fixed', inset: 0, zIndex: 3000 }}>
      {/* Click-catcher so the dimmed app underneath can't be interacted with mid-tour. */}
      <div style={{ position: 'absolute', inset: 0, background: rect ? 'transparent' : 'rgba(0,0,0,0.55)' }} />

      {/* Spotlight: the target stays bright while a huge box-shadow dims everything around it. */}
      {rect && (
        <div
          style={{
            position: 'fixed',
            top: rect.top - 6,
            left: rect.left - 6,
            width: rect.width + 12,
            height: rect.height + 12,
            borderRadius: 10,
            boxShadow: '0 0 0 9999px rgba(0,0,0,0.55)',
            border: '2px solid var(--color-accent)',
            pointerEvents: 'none',
            transition: 'top 0.2s, left 0.2s, width 0.2s, height 0.2s',
          }}
        />
      )}

      <div
        style={{
          ...card,
          background: 'var(--bg-surface-opaque)',
          border: '1px solid var(--border-color)',
          borderRadius: 12,
          boxShadow: '0 20px 60px rgba(0,0,0,0.5)',
          padding: 18,
          display: 'flex',
          flexDirection: 'column',
          gap: 10,
        }}
      >
        <div style={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: 12 }}>
          <div style={{ fontSize: '1rem', fontWeight: 700, color: 'var(--text-primary)' }}>{step.title}</div>
          <button onClick={onClose} aria-label="Skip tour" style={{ background: 'transparent', border: 'none', color: 'var(--text-muted)', cursor: 'pointer', flexShrink: 0 }}>
            <X size={16} />
          </button>
        </div>

        <div style={{ fontSize: '0.88rem', color: 'var(--text-secondary)', lineHeight: 1.5 }}>{step.body}</div>

        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginTop: 4 }}>
          <span style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>{index + 1} / {TOUR_STEPS.length}</span>
          <div style={{ display: 'flex', gap: 8 }}>
            {!isFirst && (
              <button onClick={back} style={{ padding: '7px 14px', borderRadius: 8, background: 'rgba(255,255,255,0.04)', border: '1px solid var(--border-color)', color: 'var(--text-secondary)', fontSize: '0.83rem', fontWeight: 600, cursor: 'pointer' }}>
                Back
              </button>
            )}
            <button onClick={next} style={{ padding: '7px 16px', borderRadius: 8, background: 'var(--color-accent)', border: 'none', color: '#fff', fontSize: '0.83rem', fontWeight: 700, cursor: 'pointer' }}>
              {isLast ? 'Done' : 'Next'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
