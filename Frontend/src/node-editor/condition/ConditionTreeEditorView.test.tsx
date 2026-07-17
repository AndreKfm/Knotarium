// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { ReactNode } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ConditionTreeEditorView } from './ConditionTreeEditorView';
import type { ConditionLogic } from './conditionModel';
import type { ConditionLogicTree } from './conditionTree';

// Stub @xyflow/react so the chrome (top bar, toolbar, output pill — siblings of <ReactFlow>) renders as
// plain DOM and the flow graph is exposed as JSON. (Per-node interactions are covered by the node tests.)
vi.mock('@xyflow/react', () => ({
  ReactFlowProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
  ReactFlow: ({ children, nodes, edges }: { children?: ReactNode; nodes?: unknown[]; edges?: unknown[] } & Record<string, unknown>) => (
    <div data-testid="react-flow">
      <pre data-testid="rf-nodes">{JSON.stringify(nodes ?? [])}</pre>
      <pre data-testid="rf-edges">{JSON.stringify(edges ?? [])}</pre>
      {children}
    </div>
  ),
  Background: () => null,
  BackgroundVariant: { Dots: 'dots' },
  Handle: () => null,
  Position: { Left: 'left', Right: 'right', Top: 'top', Bottom: 'bottom' },
  // FlowCanvas calls these; keep them inert so the measured-relayout effect no-ops in jsdom.
  useReactFlow: () => ({ getNodes: () => [], setViewport: () => {} }),
}));

const trueSingle: ConditionLogic = {
  version: 1,
  comb: 'and',
  cmps: [{ id: 'c1', op: 'eq', a: { kind: 'lit', type: 'number', value: 5 }, b: { kind: 'lit', type: 'number', value: 5 } }],
};

const nested: ConditionLogic = {
  version: 1,
  comb: 'or',
  cmps: [
    { id: 'c1', op: 'eq', a: { kind: 'lit', type: 'number', value: 5 }, b: { kind: 'lit', type: 'number', value: 5 } },
    { id: 'c2', op: 'eq', a: { kind: 'lit', type: 'number', value: 1 }, b: { kind: 'lit', type: 'number', value: 2 } },
  ],
};

const output = () => screen.getByLabelText('output').textContent ?? '';
const rfNodeIds = () => (JSON.parse(screen.getByTestId('rf-nodes').textContent || '[]') as Array<{ id: string }>).map((n) => n.id);

describe('ConditionTreeEditorView (flow)', () => {
  it('blocks Save and shows incomplete for an empty condition (placeholder node)', () => {
    render(<ConditionTreeEditorView onSave={vi.fn()} onCancel={vi.fn()} />);
    expect(screen.getByRole('button', { name: /Save & Publish/i })).toBeDisabled();
    expect(output()).toMatch(/incomplete/);
    expect(rfNodeIds()).toContain('placeholder');
  });

  it('opens a valid v1 condition, routes the live outcome, and saves it as v2', () => {
    const onSave = vi.fn();
    render(<ConditionTreeEditorView initialLogic={trueSingle} onSave={onSave} onCancel={vi.fn()} />);
    expect(output()).toMatch(/TRUE/);
    // Inputs + comparator + output are wired in the graph.
    expect(rfNodeIds()).toEqual(expect.arrayContaining(['in:c1:a', 'in:c1:b', 'cmp:c1', 'out']));
    const save = screen.getByRole('button', { name: /Save & Publish/i });
    expect(save).toBeEnabled();
    fireEvent.click(save);
    const saved = onSave.mock.calls[0][0] as ConditionLogicTree;
    expect(saved.version).toBe(2);
  });

  it('builds a group node for a multi-comparator condition', () => {
    render(<ConditionTreeEditorView initialLogic={nested} onSave={vi.fn()} onCancel={vi.fn()} />);
    expect(output()).toMatch(/TRUE/); // true OR false
    expect(rfNodeIds()).toEqual(expect.arrayContaining(['group:root', 'cmp:c1', 'cmp:c2', 'out']));
  });

  it('Add condition on an empty editor adds a comparator to the graph', () => {
    render(<ConditionTreeEditorView onSave={vi.fn()} onCancel={vi.fn()} />);
    expect(rfNodeIds()).not.toContain('cmp:c1');
    fireEvent.click(screen.getByRole('button', { name: /Add condition/i }));
    expect(rfNodeIds()).toContain('cmp:c1');
  });

  it('confirms before discarding unsaved edits on Back', () => {
    const onCancel = vi.fn();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    render(<ConditionTreeEditorView onSave={vi.fn()} onCancel={onCancel} />);
    fireEvent.click(screen.getByRole('button', { name: /Add condition/i })); // make it dirty
    fireEvent.click(screen.getByRole('button', { name: /Back/i }));
    expect(confirm).toHaveBeenCalled();
    expect(onCancel).not.toHaveBeenCalled();
    confirm.mockRestore();
  });
});
