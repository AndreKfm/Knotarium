// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Pure, framework-agnostic undo/redo stacks.
//
// The canvas itself is the live "present" state; this structure only holds the
// snapshots to restore. `record` pushes a pre-change snapshot onto `past` and
// clears `future` (a new action invalidates the redo branch). `applyUndo` /
// `applyRedo` swap the live `current` snapshot with the top of a stack and hand
// back the snapshot to apply. Everything is immutable and easy to unit-test.

export interface UndoHistory<T> {
  past: T[];
  future: T[];
  /** Maximum entries kept per stack (oldest dropped first). */
  limit: number;
}

export const DEFAULT_HISTORY_LIMIT = 100;

export function createUndoHistory<T>(limit: number = DEFAULT_HISTORY_LIMIT): UndoHistory<T> {
  return { past: [], future: [], limit };
}

export function canUndo<T>(h: UndoHistory<T>): boolean {
  return h.past.length > 0;
}

export function canRedo<T>(h: UndoHistory<T>): boolean {
  return h.future.length > 0;
}

/**
 * Push a pre-change `snapshot` onto the undo stack, clearing the redo branch.
 * When `isEqual` is supplied and the snapshot matches the most recent entry,
 * the call is a no-op (avoids redundant entries, e.g. a drag that didn't move).
 */
export function record<T>(
  h: UndoHistory<T>,
  snapshot: T,
  isEqual?: (a: T, b: T) => boolean,
): UndoHistory<T> {
  if (isEqual && h.past.length > 0 && isEqual(h.past[h.past.length - 1], snapshot)) {
    return h;
  }
  const past = [...h.past, snapshot];
  return {
    past: past.length > h.limit ? past.slice(past.length - h.limit) : past,
    future: [],
    limit: h.limit,
  };
}

/**
 * Move one step back: returns the snapshot to restore plus the updated history
 * (the live `current` is pushed onto the redo stack). Returns null when there's
 * nothing to undo.
 */
export function applyUndo<T>(h: UndoHistory<T>, current: T): { history: UndoHistory<T>; restored: T } | null {
  if (h.past.length === 0) return null;
  const restored = h.past[h.past.length - 1];
  const future = [...h.future, current];
  return {
    restored,
    history: {
      past: h.past.slice(0, -1),
      future: future.length > h.limit ? future.slice(future.length - h.limit) : future,
      limit: h.limit,
    },
  };
}

/** Move one step forward; mirror of {@link applyUndo}. Returns null when there's nothing to redo. */
export function applyRedo<T>(h: UndoHistory<T>, current: T): { history: UndoHistory<T>; restored: T } | null {
  if (h.future.length === 0) return null;
  const restored = h.future[h.future.length - 1];
  const past = [...h.past, current];
  return {
    restored,
    history: {
      past: past.length > h.limit ? past.slice(past.length - h.limit) : past,
      future: h.future.slice(0, -1),
      limit: h.limit,
    },
  };
}
