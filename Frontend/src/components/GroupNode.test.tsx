// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import type { Node as RFNode } from '@xyflow/react';
import { GroupNode } from './GroupNode';
import { getGroupCollapsed, getGroupLabel } from '../node-editor/nodeGroup';

const { setNodesSpy, state } = vi.hoisted(() => ({
  setNodesSpy: vi.fn(),
  state: { connectable: true, nodeLookup: new Map<string, { parentId?: string }>() },
}));

vi.mock('@xyflow/react', async () => {
  const actual = (await vi.importActual('@xyflow/react')) as Record<string, unknown>;
  return {
    ...actual,
    NodeResizer: () => null,
    useReactFlow: () => ({ setNodes: setNodesSpy }),
    useStore: (selector: (s: { nodesConnectable: boolean; nodeLookup: Map<string, { parentId?: string }> }) => unknown) =>
      selector({ nodesConnectable: state.connectable, nodeLookup: state.nodeLookup }),
  };
});

/** Seed the mocked store with `n` children parented to group `g1`. */
function setChildren(n: number) {
  const m = new Map<string, { parentId?: string }>();
  for (let i = 0; i < n; i++) m.set(`c${i}`, { parentId: 'g1' });
  m.set('outsider', { parentId: 'other' });
  state.nodeLookup = m;
}

const props = (over: Partial<Parameters<typeof GroupNode>[0]> = {}) =>
  ({ id: 'g1', type: 'group', data: { properties: { label: 'Ingestion', collapsed: false } }, selected: false, ...over }) as Parameters<typeof GroupNode>[0];

// A grouped child + the group itself, so toggle/rename updaters have something to act on.
const groupAndChild: RFNode[] = [
  { id: 'g1', type: 'group', position: { x: 0, y: 0 }, style: { height: 200 }, data: { properties: { label: 'Ingestion', collapsed: false } } },
  { id: 'c1', type: 'log', position: { x: 10, y: 50 }, parentId: 'g1', data: {} },
];

describe('GroupNode (#14)', () => {
  beforeEach(() => { setNodesSpy.mockClear(); state.connectable = true; state.nodeLookup = new Map(); });

  it('shows the label and an expanded (collapse) toggle', () => {
    render(<GroupNode {...props()} />);
    expect(screen.getByText('Ingestion')).toBeInTheDocument();
    expect(screen.getByLabelText('Collapse group')).toBeInTheDocument();
  });

  it('collapsing hides children and flips the group flag via setNodes', () => {
    render(<GroupNode {...props()} />);
    fireEvent.click(screen.getByLabelText('Collapse group'));
    const updater = setNodesSpy.mock.calls[0][0] as (n: typeof groupAndChild) => typeof groupAndChild;
    const out = updater(groupAndChild);
    expect(getGroupCollapsed(out.find((n) => n.id === 'g1')!)).toBe(true);
    expect(out.find((n) => n.id === 'c1')!.hidden).toBe(true);
  });

  it('renders an expand toggle when already collapsed', () => {
    render(<GroupNode {...props({ data: { properties: { label: 'Ingestion', collapsed: true } } })} />);
    expect(screen.getByLabelText('Expand group')).toBeInTheDocument();
  });

  it('collapsed chip keeps the label renameable (pencil + double-click)', () => {
    render(<GroupNode {...props({ data: { properties: { label: 'Ingestion', collapsed: true } } })} />);
    // The chip is a pill, not a full header bar.
    const chip = screen.getByText('Ingestion').closest('.node-group-chip');
    expect(chip).not.toBeNull();
    // Rename still works from the collapsed chip.
    fireEvent.click(screen.getByLabelText('Rename group'));
    expect((screen.getByLabelText('Rename group') as HTMLInputElement).tagName).toBe('INPUT');
  });

  it('double-click renames the label, committing via setNodes', () => {
    render(<GroupNode {...props()} />);
    fireEvent.doubleClick(screen.getByText('Ingestion'));
    const input = screen.getByLabelText('Rename group');
    fireEvent.change(input, { target: { value: '  Parsing  ' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    const updater = setNodesSpy.mock.calls.at(-1)![0] as (n: typeof groupAndChild) => typeof groupAndChild;
    const out = updater(groupAndChild);
    expect(getGroupLabel(out.find((n) => n.id === 'g1')!)).toBe('Parsing');
  });

  it('the rename pencil opens the editor (surfacing the hidden double-click)', () => {
    render(<GroupNode {...props()} />);
    // Not renaming yet: the only "Rename group" element is the pencil button.
    fireEvent.click(screen.getByLabelText('Rename group'));
    const input = screen.getByLabelText('Rename group');
    expect((input as HTMLInputElement).tagName).toBe('INPUT');
    fireEvent.change(input, { target: { value: 'Parsing' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    const updater = setNodesSpy.mock.calls.at(-1)![0] as (n: typeof groupAndChild) => typeof groupAndChild;
    expect(getGroupLabel(updater(groupAndChild).find((n) => n.id === 'g1')!)).toBe('Parsing');
  });

  it('collapsed chip shows a live count of direct children only', () => {
    setChildren(5); // plus one node parented elsewhere, which must not count
    render(<GroupNode {...props({ data: { properties: { label: 'Ingestion', collapsed: true } } })} />);
    expect(screen.getByLabelText('5 nodes')).toBeInTheDocument();
    expect(screen.getByText('· 5')).toBeInTheDocument();
  });

  it('shows the colour swatch picker only when selected and recolours via setNodes', () => {
    const { rerender } = render(<GroupNode {...props({ selected: false })} />);
    expect(screen.queryByLabelText('Blue group')).toBeNull();

    rerender(<GroupNode {...props({ selected: true })} />);
    fireEvent.click(screen.getByLabelText('Blue group'));
    const updater = setNodesSpy.mock.calls.at(-1)![0] as (n: typeof groupAndChild) => typeof groupAndChild;
    const out = updater(groupAndChild);
    expect((out.find((n) => n.id === 'g1')!.data!.properties as { color: string }).color).toBe('blue');
  });

  it('read-only view disables rename but still allows collapse', () => {
    state.connectable = false;
    render(<GroupNode {...props()} />);
    fireEvent.doubleClick(screen.getByText('Ingestion'));
    expect(screen.queryByLabelText('Rename group')).toBeNull();
    // Collapse toggle remains usable.
    fireEvent.click(screen.getByLabelText('Collapse group'));
    expect(setNodesSpy).toHaveBeenCalled();
  });
});
