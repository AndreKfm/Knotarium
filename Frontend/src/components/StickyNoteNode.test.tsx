// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { StickyNoteNode } from './StickyNoteNode';
import { getStickyNoteText, getStickyNoteColorId } from '../node-editor/stickyNote';

const { setNodesSpy, state } = vi.hoisted(() => ({
  setNodesSpy: vi.fn(),
  state: { connectable: true },
}));

vi.mock('@xyflow/react', async () => {
  const actual = (await vi.importActual('@xyflow/react')) as Record<string, unknown>;
  return {
    ...actual,
    NodeResizer: () => null,
    useReactFlow: () => ({ setNodes: setNodesSpy }),
    useStore: (selector: (s: { nodesConnectable: boolean }) => unknown) =>
      selector({ nodesConnectable: state.connectable }),
  };
});

const props = (over: Partial<Parameters<typeof StickyNoteNode>[0]> = {}) =>
  ({ id: 'note-1', type: 'stickyNote', data: { properties: { text: 'hello', color: 'amber' } }, selected: false, ...over }) as Parameters<typeof StickyNoteNode>[0];

describe('StickyNoteNode (#13)', () => {
  beforeEach(() => { setNodesSpy.mockClear(); state.connectable = true; });

  it('renders an editable textarea with the note text', () => {
    render(<StickyNoteNode {...props()} />);
    const ta = screen.getByLabelText('Sticky note text') as HTMLTextAreaElement;
    expect(ta.value).toBe('hello');
  });

  it('editing the text commits via setNodes', () => {
    render(<StickyNoteNode {...props()} />);
    fireEvent.change(screen.getByLabelText('Sticky note text'), { target: { value: 'updated' } });
    expect(setNodesSpy).toHaveBeenCalledTimes(1);
    const updater = setNodesSpy.mock.calls[0][0] as (n: unknown[]) => unknown[];
    const out = updater([{ id: 'note-1', data: { properties: { text: 'hello', color: 'amber' } } }]) as Parameters<typeof getStickyNoteText>[0][];
    expect(getStickyNoteText(out[0])).toBe('updated');
    expect(getStickyNoteColorId(out[0])).toBe('amber'); // colour preserved
  });

  it('shows colour swatches when selected and recolours via setNodes', () => {
    render(<StickyNoteNode {...props({ selected: true })} />);
    const blue = screen.getByLabelText('Blue note');
    fireEvent.click(blue);
    const updater = setNodesSpy.mock.calls[0][0] as (n: unknown[]) => unknown[];
    const out = updater([{ id: 'note-1', data: { properties: { text: 'hello', color: 'amber' } } }]) as Parameters<typeof getStickyNoteColorId>[0][];
    expect(getStickyNoteColorId(out[0])).toBe('blue');
  });

  it('hides swatches when not selected', () => {
    render(<StickyNoteNode {...props({ selected: false })} />);
    expect(screen.queryByLabelText('Blue note')).toBeNull();
  });

  it('is read-only in the run view: text shown, no editor', () => {
    state.connectable = false;
    render(<StickyNoteNode {...props({ selected: true })} />);
    expect(screen.queryByLabelText('Sticky note text')).toBeNull();
    expect(screen.getByText('hello')).toBeInTheDocument();
    expect(screen.queryByLabelText('Blue note')).toBeNull();
  });
});
