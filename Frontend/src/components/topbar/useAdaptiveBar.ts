// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useRef } from 'react';

/**
 * Measured (not breakpoint-driven) degradation of the top bar.
 *
 * The bar keeps every destination at every width; what it gives up, in a fixed
 * order, is labels. The ladder is:
 *
 *   1            hide the clock
 *   2 … 1+N      shed one nav label per step, in `shedOrder` (N sheddable items)
 *   2+N          ⌘K field → icon
 *   3+N          hide the wordmark + its separator
 *   4+N          arming pill → dot only (never removed — it is safety state)
 *   5+N          nav row becomes horizontally scrollable (the floor)
 *
 * With the standard twelve destinations minus the active one (which never
 * sheds), N = 11 and the ladder is exactly the 16 steps of the handoff.
 *
 * `paint(level)` renders the FULL state for a level — it is idempotent and
 * absolute, never a delta, because delta-based shedding drifts out of sync
 * with the level once a resize is interrupted mid-loop.
 *
 * Hysteresis: shrink as soon as the content overflows, but only grow back once
 * there is real headroom. Without it the bar oscillates when the window sits
 * exactly on a switch point. Headroom is read off the flexible spacer between
 * the nav row and the right cluster — .tb itself always reports
 * scrollWidth === clientWidth while the content fits, so it cannot tell "just
 * fits" from "fits with 200px to spare".
 */

/** Extra free space required before the bar steps back UP a level. */
const GROW_MARGIN = 24;

export interface AdaptiveBarHandles {
  /** The bar element (`.tb`). Both the measured box and the class target. */
  barRef: React.RefObject<HTMLElement | null>;
  /** The flex spacer used as the headroom gauge. */
  spacerRef: React.RefObject<HTMLDivElement | null>;
  /** The nav row; its `.ni` children carry `data-shed-rank`. */
  navRef: React.RefObject<HTMLElement | null>;
  /** Re-measure now (e.g. after the destination set or the active item changed). */
  relayout: () => void;
}

export function useAdaptiveBar(): AdaptiveBarHandles {
  const barRef = useRef<HTMLElement | null>(null);
  const spacerRef = useRef<HTMLDivElement | null>(null);
  const navRef = useRef<HTMLElement | null>(null);
  const levelRef = useRef(0);
  const busyRef = useRef(false);

  const relayout = useCallback(() => {
    const bar = barRef.current;
    const spacer = spacerRef.current;
    const nav = navRef.current;
    if (!bar || !spacer || !nav) return;
    // A paint never changes the bar's own box, but guard anyway so a
    // ResizeObserver callback can't re-enter the measuring loop.
    if (busyRef.current) return;
    busyRef.current = true;

    try {
      const items = Array.from(nav.querySelectorAll<HTMLElement>('.ni'));
      // Ranks are 1..N over the sheddable items; the active item has rank 0 and
      // is therefore never matched by `rank <= shed`.
      const sheddable = items.reduce((max, el) => Math.max(max, Number(el.dataset.shedRank ?? 0)), 0);
      const maxLevel = sheddable + 5;

      const paint = (level: number) => {
        const shed = Math.min(Math.max(level - 1, 0), sheddable);
        bar.classList.toggle('tb-l-clock', level >= 1);
        bar.classList.toggle('tb-l-cmd', level >= sheddable + 2);
        bar.classList.toggle('tb-l-brand', level >= sheddable + 3);
        bar.classList.toggle('tb-l-pill', level >= sheddable + 4);
        bar.classList.toggle('tb-l-scroll', level >= sheddable + 5);
        for (const el of items) {
          const rank = Number(el.dataset.shedRank ?? 0);
          el.classList.toggle('iconly', rank > 0 && rank <= shed);
        }
      };

      const fits = () => bar.scrollWidth <= bar.clientWidth + 1;
      const headroom = () => spacer.getBoundingClientRect().width;

      let level = Math.min(levelRef.current, maxLevel);
      paint(level);

      if (!fits()) {
        // Shrink until it fits, or until the scrollable floor takes over.
        while (level < maxLevel && !fits()) {
          level += 1;
          paint(level);
        }
      } else {
        // Grow only where there is room to spare, so a window parked on a
        // switch point settles instead of flickering.
        while (level > 0) {
          paint(level - 1);
          if (headroom() > GROW_MARGIN) {
            level -= 1;
          } else {
            paint(level);
            break;
          }
        }
      }

      levelRef.current = level;
    } finally {
      busyRef.current = false;
    }
  }, []);

  useEffect(() => {
    const bar = barRef.current;
    if (!bar) return;
    relayout();

    if (typeof ResizeObserver === 'undefined') return;

    // Coalesce into a frame: a paint resizes the observed content blocks, and
    // reacting synchronously inside the observer's own delivery pass is what
    // produces "ResizeObserver loop" console errors.
    let frame = 0;
    const schedule = () => {
      if (frame) return;
      frame = requestAnimationFrame(() => { frame = 0; relayout(); });
    };

    const observer = new ResizeObserver(schedule);
    // The BAR, not the window: a side panel changes the bar's width without
    // ever firing a window resize.
    observer.observe(bar);
    // …and the content blocks, because the bar's own box never changes when
    // late-arriving content widens them (the build stamp, the signed-in user,
    // "Runtime…" becoming "Disarmed"). Without this the bar keeps the level it
    // measured while that content was still missing, and then clips.
    const nav = navRef.current;
    const right = bar.querySelector('.tb-right');
    if (nav) observer.observe(nav);
    if (right) observer.observe(right);

    return () => {
      if (frame) cancelAnimationFrame(frame);
      observer.disconnect();
    };
  }, [relayout]);

  // Web fonts land after the first measure and change every label's width.
  useEffect(() => {
    const fonts = (document as Document & { fonts?: { ready: Promise<unknown> } }).fonts;
    if (!fonts?.ready) return;
    let cancelled = false;
    void fonts.ready.then(() => { if (!cancelled) relayout(); });
    return () => { cancelled = true; };
  }, [relayout]);

  return { barRef, spacerRef, navRef, relayout };
}
