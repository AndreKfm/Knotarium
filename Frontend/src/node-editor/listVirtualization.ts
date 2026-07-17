// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

/**
 * Pure windowing math for virtualizing a long, vertically-scrolling list of
 * fixed-height rows. Kept free of React / DOM so the slice + spacer arithmetic
 * can be unit-tested with plain numbers.
 *
 * The consumer (VariablesPanel) renders only `items[startIndex .. endIndex)`
 * inside a scroll container, padding the top and bottom with empty spacers so
 * the scrollbar still reflects the full list height and the visible rows sit at
 * the right offset. `overscan` rows are rendered just outside the viewport on
 * each side so fast scrolling doesn't flash blank gaps.
 */

/** The slice of rows to render plus the spacer heights that frame it. */
export interface VirtualWindow {
  /** First row index to render (inclusive). */
  startIndex: number;
  /** One past the last row index to render (exclusive) — safe for Array.slice. */
  endIndex: number;
  /** Pixel height of the spacer above the rendered slice. */
  paddingTop: number;
  /** Pixel height of the spacer below the rendered slice. */
  paddingBottom: number;
}

export interface VirtualWindowInput {
  /** Current scroll offset of the container, in pixels. */
  scrollTop: number;
  /** Visible height of the scroll container, in pixels. */
  viewportHeight: number;
  /** Fixed height of a single row, in pixels (must be > 0). */
  rowHeight: number;
  /** Total number of items in the list. */
  itemCount: number;
  /** Extra rows rendered beyond the viewport on each side. Default 4. */
  overscan?: number;
}

/**
 * Compute which rows are visible (plus overscan) and the spacer heights that
 * keep them positioned within the full scroll height. Clamps to valid bounds so
 * a stale/over-scrolled `scrollTop` or a zero-height viewport never produces a
 * negative or out-of-range slice.
 */
export function computeVirtualWindow(input: VirtualWindowInput): VirtualWindow {
  const overscan = Math.max(0, input.overscan ?? 4);
  const rowHeight = input.rowHeight;
  const itemCount = Math.max(0, Math.floor(input.itemCount));

  if (itemCount === 0 || rowHeight <= 0) {
    return { startIndex: 0, endIndex: 0, paddingTop: 0, paddingBottom: 0 };
  }

  const scrollTop = Math.max(0, input.scrollTop);
  const viewportHeight = Math.max(0, input.viewportHeight);

  const firstVisible = Math.floor(scrollTop / rowHeight);
  // +1 covers a partially-visible row at the bottom edge; the ceil covers the top.
  const visibleCount = Math.ceil(viewportHeight / rowHeight) + 1;

  const startIndex = Math.max(0, firstVisible - overscan);
  const endIndex = Math.min(itemCount, firstVisible + visibleCount + overscan);

  return {
    startIndex,
    endIndex,
    paddingTop: startIndex * rowHeight,
    paddingBottom: Math.max(0, itemCount - endIndex) * rowHeight,
  };
}

/**
 * Whether a list is long enough to be worth virtualizing. Below the threshold
 * the windowing overhead (scroll listener, fixed row heights) isn't worth it, so
 * the caller should render every row normally and let it size to its content.
 */
export function shouldVirtualize(itemCount: number, threshold = 40): boolean {
  return itemCount > threshold;
}
