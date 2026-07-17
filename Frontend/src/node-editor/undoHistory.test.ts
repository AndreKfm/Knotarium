// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';
import {
  createUndoHistory,
  record,
  applyUndo,
  applyRedo,
  canUndo,
  canRedo,
} from './undoHistory';

describe('undoHistory', () => {
  it('starts empty with nothing to undo or redo', () => {
    const h = createUndoHistory<number>();
    expect(canUndo(h)).toBe(false);
    expect(canRedo(h)).toBe(false);
  });

  it('records a pre-change snapshot and clears the redo branch', () => {
    let h = createUndoHistory<string>();
    h = record(h, 'a');
    expect(h.past).toEqual(['a']);
    expect(canUndo(h)).toBe(true);

    // After an undo there is a redo entry; a new record clears it.
    const u = applyUndo(h, 'b')!;
    expect(u.restored).toBe('a');
    expect(canRedo(u.history)).toBe(true);
    const h2 = record(u.history, 'c');
    expect(canRedo(h2)).toBe(false);
  });

  it('undo restores the last snapshot and stashes current for redo', () => {
    let h = createUndoHistory<string>();
    h = record(h, 's0'); // pre-change state before moving to s1
    const u = applyUndo(h, 's1')!;
    expect(u.restored).toBe('s0');
    expect(u.history.future).toEqual(['s1']);
    expect(u.history.past).toEqual([]);
  });

  it('redo re-applies the snapshot stashed by undo', () => {
    let h = createUndoHistory<string>();
    h = record(h, 's0');
    const u = applyUndo(h, 's1')!;
    const r = applyRedo(u.history, u.restored)!;
    expect(r.restored).toBe('s1');
    expect(r.history.past).toEqual(['s0']);
    expect(r.history.future).toEqual([]);
  });

  it('returns null when there is nothing to undo/redo', () => {
    const h = createUndoHistory<number>();
    expect(applyUndo(h, 1)).toBeNull();
    expect(applyRedo(h, 1)).toBeNull();
  });

  it('round-trips a multi-step edit history', () => {
    // Simulate edits v0 -> v1 -> v2 -> v3, recording the pre-state each time.
    let h = createUndoHistory<string>();
    h = record(h, 'v0');
    h = record(h, 'v1');
    h = record(h, 'v2');
    // current live state is v3
    let cur = 'v3';

    const u1 = applyUndo(h, cur)!; h = u1.history; cur = u1.restored; expect(cur).toBe('v2');
    const u2 = applyUndo(h, cur)!; h = u2.history; cur = u2.restored; expect(cur).toBe('v1');
    const r1 = applyRedo(h, cur)!; h = r1.history; cur = r1.restored; expect(cur).toBe('v2');
    const r2 = applyRedo(h, cur)!; h = r2.history; cur = r2.restored; expect(cur).toBe('v3');
    expect(canRedo(h)).toBe(false);
  });

  it('caps each stack at the configured limit, dropping the oldest', () => {
    let h = createUndoHistory<number>(3);
    for (let i = 0; i < 5; i++) h = record(h, i);
    expect(h.past).toEqual([2, 3, 4]); // oldest (0,1) dropped
  });

  it('skips a redundant record when equal to the last entry', () => {
    let h = createUndoHistory<{ x: number }>();
    const eq = (a: { x: number }, b: { x: number }) => a.x === b.x;
    h = record(h, { x: 1 }, eq);
    h = record(h, { x: 1 }, eq); // no-op
    expect(h.past).toHaveLength(1);
    h = record(h, { x: 2 }, eq);
    expect(h.past).toHaveLength(2);
  });
});
