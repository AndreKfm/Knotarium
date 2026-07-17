// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import type { Node as RFNode } from '@xyflow/react';
import {
  createStickyNoteNode,
  applyStickyNoteText,
  applyStickyNoteColor,
  getStickyNoteText,
  getStickyNoteColorId,
  getStickyNoteColor,
  isStickyNote,
  STICKY_NOTE_TYPE,
  STICKY_NOTE_DEFAULT_SIZE,
  DEFAULT_STICKY_NOTE_COLOR_ID,
  STICKY_NOTE_COLORS,
} from './stickyNote';

describe('createStickyNoteNode', () => {
  it('builds an inert, port-less note with default size, colour, and a back z-index', () => {
    const n = createStickyNoteNode({ id: 'note-1', position: { x: 10, y: 20 } });
    expect(n.type).toBe(STICKY_NOTE_TYPE);
    expect(n.position).toEqual({ x: 10, y: 20 });
    expect(n.style).toEqual(STICKY_NOTE_DEFAULT_SIZE);
    expect(n.zIndex).toBe(0);
    expect(getStickyNoteText(n)).toBe('');
    expect(getStickyNoteColorId(n)).toBe(DEFAULT_STICKY_NOTE_COLOR_ID);
    // No data outputHandles → no ports.
    expect((n.data as Record<string, unknown>).outputHandles).toBeUndefined();
  });

  it('honours provided text, colour, and explicit size', () => {
    const n = createStickyNoteNode({ id: 'n', position: { x: 0, y: 0 }, text: 'hi', colorId: 'blue', width: 300, height: 200 });
    expect(getStickyNoteText(n)).toBe('hi');
    expect(getStickyNoteColorId(n)).toBe('blue');
    expect(n.style).toEqual({ width: 300, height: 200 });
  });
});

describe('isStickyNote', () => {
  it('matches only the sticky-note type', () => {
    expect(isStickyNote('stickyNote')).toBe(true);
    expect(isStickyNote('group')).toBe(false);
    expect(isStickyNote(undefined)).toBe(false);
  });
});

describe('getStickyNoteColor', () => {
  it('resolves a known id and falls back to the default for unknown/empty', () => {
    expect(getStickyNoteColor('green').id).toBe('green');
    expect(getStickyNoteColor('does-not-exist')).toEqual(STICKY_NOTE_COLORS[0]);
    expect(getStickyNoteColor(null)).toEqual(STICKY_NOTE_COLORS[0]);
  });
});

describe('applyStickyNoteText / applyStickyNoteColor', () => {
  const nodes: RFNode[] = [
    createStickyNoteNode({ id: 'a', position: { x: 0, y: 0 }, text: 'one', colorId: 'amber' }),
    createStickyNoteNode({ id: 'b', position: { x: 0, y: 0 }, text: 'two', colorId: 'green' }),
  ];

  it('updates only the targeted note and preserves its other field', () => {
    const next = applyStickyNoteText(nodes, 'a', 'edited');
    expect(getStickyNoteText(next[0])).toBe('edited');
    expect(getStickyNoteColorId(next[0])).toBe('amber'); // colour preserved
    expect(getStickyNoteText(next[1])).toBe('two'); // sibling untouched
    expect(next[1]).toBe(nodes[1]); // identity preserved for unchanged node
  });

  it('updates colour without dropping text', () => {
    const next = applyStickyNoteColor(nodes, 'b', 'pink');
    expect(getStickyNoteColorId(next[1])).toBe('pink');
    expect(getStickyNoteText(next[1])).toBe('two');
  });
});
