// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useState } from 'react';

/**
 * Width state for a right-docked panel, resizable by dragging its LEFT edge, persisted to localStorage.
 * Dragging left widens the panel (it grows toward the canvas). Returns the current width and an
 * onMouseDown handler to attach to a &lt;ResizeHandle/&gt;.
 */
export function useResizableWidth(storageKey: string, defaultWidth: number, min = 300, max = 900) {
  const [width, setWidth] = useState<number>(() => {
    try {
      const saved = Number(localStorage.getItem(storageKey));
      return Number.isFinite(saved) && saved >= min && saved <= max ? saved : defaultWidth;
    } catch {
      return defaultWidth;
    }
  });

  useEffect(() => {
    try { localStorage.setItem(storageKey, String(Math.round(width))); } catch { /* ignore */ }
  }, [storageKey, width]);

  const startResize = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = width;
    const onMove = (ev: MouseEvent) => {
      // Right-docked panel: moving the pointer left (smaller clientX) makes it wider.
      const next = Math.min(max, Math.max(min, startWidth + (startX - ev.clientX)));
      setWidth(next);
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
  }, [width, min, max]);

  return { width, startResize, setWidth };
}

/**
 * A thin grab strip on the left edge of a right-docked panel. The parent must be
 * <c>position: relative</c>. Double-click resets nothing here (caller can wire that if wanted).
 */
export function ResizeHandle({ onMouseDown, title }: { onMouseDown: (e: React.MouseEvent) => void; title?: string }) {
  const [hover, setHover] = useState(false);
  return (
    <div
      role="separator"
      aria-orientation="vertical"
      title={title ?? 'Drag to resize'}
      onMouseDown={onMouseDown}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        position: 'absolute',
        left: 0,
        top: 0,
        bottom: 0,
        width: '7px',
        transform: 'translateX(-50%)',
        cursor: 'col-resize',
        zIndex: 20,
        background: hover ? 'var(--color-accent, #6366f1)' : 'transparent',
        opacity: hover ? 0.5 : 1,
        transition: 'background 0.15s ease',
      }}
    />
  );
}
