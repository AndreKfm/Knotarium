// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { computeVirtualWindow, shouldVirtualize } from './listVirtualization';

describe('computeVirtualWindow', () => {
  const base = { rowHeight: 100, itemCount: 100, viewportHeight: 500, overscan: 0 };

  it('renders the visible slice from the top with no overscan', () => {
    const w = computeVirtualWindow({ ...base, scrollTop: 0 });
    expect(w.startIndex).toBe(0);
    // ceil(500/100)+1 = 6 rows visible.
    expect(w.endIndex).toBe(6);
    expect(w.paddingTop).toBe(0);
    expect(w.paddingBottom).toBe((100 - 6) * 100);
  });

  it('shifts the slice as the list scrolls and keeps total height constant', () => {
    const w = computeVirtualWindow({ ...base, scrollTop: 1000 });
    expect(w.startIndex).toBe(10);
    expect(w.endIndex).toBe(16);
    expect(w.paddingTop).toBe(1000);
    // padding + rendered rows always sum to the full content height.
    const rendered = (w.endIndex - w.startIndex) * base.rowHeight;
    expect(w.paddingTop + rendered + w.paddingBottom).toBe(base.itemCount * base.rowHeight);
  });

  it('applies overscan on both sides, clamped at the edges', () => {
    const top = computeVirtualWindow({ ...base, scrollTop: 0, overscan: 4 });
    expect(top.startIndex).toBe(0); // clamped, can't go negative
    expect(top.endIndex).toBe(10); // 6 + 4 overscan

    const mid = computeVirtualWindow({ ...base, scrollTop: 2000, overscan: 4 });
    expect(mid.startIndex).toBe(16); // 20 - 4
    expect(mid.endIndex).toBe(30); // 20 + 6 + 4
  });

  it('clamps the end index to itemCount when scrolled to the bottom', () => {
    const w = computeVirtualWindow({ ...base, scrollTop: 100 * 100, overscan: 4 });
    expect(w.endIndex).toBe(100);
    expect(w.paddingBottom).toBe(0);
  });

  it('never produces a negative slice for an over-scrolled or negative scrollTop', () => {
    const neg = computeVirtualWindow({ ...base, scrollTop: -500 });
    expect(neg.startIndex).toBe(0);
    expect(neg.paddingTop).toBe(0);
  });

  it('returns an empty window for an empty list or zero row height', () => {
    expect(computeVirtualWindow({ ...base, itemCount: 0, scrollTop: 0 })).toEqual({
      startIndex: 0, endIndex: 0, paddingTop: 0, paddingBottom: 0,
    });
    expect(computeVirtualWindow({ ...base, rowHeight: 0, scrollTop: 0 })).toEqual({
      startIndex: 0, endIndex: 0, paddingTop: 0, paddingBottom: 0,
    });
  });

  it('handles a viewport taller than the whole list', () => {
    const w = computeVirtualWindow({ rowHeight: 100, itemCount: 3, viewportHeight: 5000, scrollTop: 0, overscan: 4 });
    expect(w.startIndex).toBe(0);
    expect(w.endIndex).toBe(3);
    expect(w.paddingTop).toBe(0);
    expect(w.paddingBottom).toBe(0);
  });
});

describe('shouldVirtualize', () => {
  it('is false at or below the threshold and true above it', () => {
    expect(shouldVirtualize(40)).toBe(false);
    expect(shouldVirtualize(41)).toBe(true);
    expect(shouldVirtualize(0)).toBe(false);
  });

  it('respects a custom threshold', () => {
    expect(shouldVirtualize(10, 5)).toBe(true);
    expect(shouldVirtualize(5, 5)).toBe(false);
  });
});
