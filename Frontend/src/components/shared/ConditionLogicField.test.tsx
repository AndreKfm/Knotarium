// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConditionLogicField } from './ConditionLogicField';
import { useConditionEditorOpenStore } from '../../stores/useConditionEditorOpenStore';
import { api } from '../../utils/api';
import type { ConditionLogic } from '../../node-editor/condition/conditionModel';
import type { ConditionLogicTree } from '../../node-editor/condition/conditionTree';

// Stub the full-screen tree editor; expose its inbound props + Save/Cancel. Save now yields v2 logic.
const SAVED: ConditionLogicTree = {
  version: 2,
  root: { kind: 'cmp', id: 'c1', op: 'eq', a: { kind: 'lit', type: 'string', value: 'x' }, b: { kind: 'lit', type: 'string', value: 'x' } },
};

vi.mock('../../node-editor/condition/ConditionTreeEditorView', () => ({
  ConditionTreeEditorView: ({ initialLogic, initialLegacy, lastRun, onSave, onCancel }: {
    initialLogic: unknown;
    initialLegacy: unknown;
    lastRun: unknown;
    onSave: (l: ConditionLogicTree) => void;
    onCancel: () => void;
  }) => (
    <div data-testid="cev">
      <span data-testid="cev-logic">{JSON.stringify(initialLogic)}</span>
      <span data-testid="cev-legacy">{JSON.stringify(initialLegacy)}</span>
      <span data-testid="cev-lastrun">{JSON.stringify(lastRun)}</span>
      <button type="button" onClick={() => onSave(SAVED)}>stub-save</button>
      <button type="button" onClick={onCancel}>stub-cancel</button>
    </div>
  ),
}));

afterEach(() => {
  vi.restoreAllMocks();
  useConditionEditorOpenStore.setState({ requestNodeId: null });
});

const logic: ConditionLogic = {
  version: 1,
  comb: 'or',
  cmps: [
    { id: 'c1', op: 'eq', a: { kind: 'ref', type: 'number', ref: '{{ $variables.n }}' }, b: { kind: 'lit', type: 'number', value: 3 } },
    { id: 'c2', op: 'gt', a: { kind: 'lit', type: 'number', value: 5 }, b: { kind: 'lit', type: 'number', value: 1 } },
  ],
};

describe('ConditionLogicField', () => {
  it('shows "Not configured" and opens/closes the editor', () => {
    render(<ConditionLogicField properties={{}} onChange={vi.fn()} />);
    expect(screen.getByText('Not configured')).toBeInTheDocument();
    expect(screen.queryByTestId('cev')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /Edit logic/i }));
    expect(screen.getByTestId('cev')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'stub-cancel' }));
    expect(screen.queryByTestId('cev')).toBeNull();
  });

  it('opens the editor when the summary box is clicked', () => {
    render(<ConditionLogicField properties={{ logic }} onChange={vi.fn()} />);
    expect(screen.queryByTestId('cev')).toBeNull();
    // The summary box is itself a button that opens the editor.
    fireEvent.click(screen.getByRole('button', { name: /Open the full-screen condition editor/i }));
    expect(screen.getByTestId('cev')).toBeInTheDocument();
  });

  it('opens the editor on a canvas request addressed to its node id (double-click bridge)', () => {
    render(<ConditionLogicField nodeId="cond-1" properties={{ logic }} onChange={vi.fn()} />);
    expect(screen.queryByTestId('cev')).toBeNull();

    // A request for a DIFFERENT node is ignored.
    act(() => useConditionEditorOpenStore.getState().requestOpen('other'));
    expect(screen.queryByTestId('cev')).toBeNull();

    // A request for THIS node opens the editor and is consumed (cleared).
    act(() => useConditionEditorOpenStore.getState().requestOpen('cond-1'));
    expect(screen.getByTestId('cev')).toBeInTheDocument();
    expect(useConditionEditorOpenStore.getState().requestNodeId).toBeNull();
  });

  it('renders a read-only summary of a configured logic graph', () => {
    render(<ConditionLogicField properties={{ logic }} onChange={vi.fn()} />);
    // Comparator operands + the OR combinator connector are shown without opening the editor.
    expect(screen.getByText('n')).toBeInTheDocument(); // ref short path for {{ $variables.n }}
    expect(screen.getAllByText('3').length).toBeGreaterThan(0); // literal operand
    expect(screen.getByText('OR')).toBeInTheDocument();
    expect(screen.queryByTestId('cev')).toBeNull();
  });

  it('renders a v2 (tree) logic as a one-line expression', () => {
    const v2: ConditionLogicTree = {
      version: 2,
      root: {
        kind: 'group',
        id: 'g1',
        op: 'and',
        children: [
          { kind: 'cmp', id: 'c1', op: 'gt', a: { kind: 'lit', type: 'number', value: 5 }, b: { kind: 'lit', type: 'number', value: 1 } },
          { kind: 'not', id: 'n1', child: { kind: 'cmp', id: 'c2', op: 'eq', a: { kind: 'lit', type: 'number', value: 2 }, b: { kind: 'lit', type: 'number', value: 3 } } },
        ],
      },
    };
    render(<ConditionLogicField properties={{ logic: v2 }} onChange={vi.fn()} />);
    expect(screen.getByText('5 > 1 AND NOT 2 = 3')).toBeInTheDocument();
  });

  it('fetches last-run values for the logic refs on open and passes them to the editor', async () => {
    const spy = vi.spyOn(api, 'getConditionLastRunValues').mockResolvedValue({
      runId: 'run-1',
      versionId: 'v1',
      createdAt: '2026-06-21T10:00:00Z',
      stale: false,
      values: { '{{ $variables.n }}': { found: true, value: 7, sensitive: false } },
    });

    render(<ConditionLogicField workflowId="wf-1" properties={{ logic }} onChange={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /Edit logic/i }));

    expect(spy).toHaveBeenCalledWith('wf-1', ['{{ $variables.n }}']);
    await waitFor(() => {
      const lastRun = JSON.parse(screen.getByTestId('cev-lastrun').textContent || 'null');
      expect(lastRun.values['{{ $variables.n }}']).toEqual({ found: true, value: 7, sensitive: false });
    });
  });

  it('does NOT strip legacy fields when the editor is cancelled (only Save migrates)', () => {
    const onChange = vi.fn();
    render(
      <ConditionLogicField properties={{ left: 'a', operator: 'Equal', right: 'b' }} onChange={onChange} />,
    );
    fireEvent.click(screen.getByRole('button', { name: /Edit logic/i }));
    fireEvent.click(screen.getByRole('button', { name: 'stub-cancel' }));
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.queryByTestId('cev')).toBeNull();
  });

  it('flags a legacy condition and seeds the editor from its left/operator/right', () => {
    render(<ConditionLogicField properties={{ left: '{{ $variables.n }}', operator: 'Equal', right: '3' }} onChange={vi.fn()} />);
    expect(screen.getByText(/Legacy condition/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Edit logic/i }));
    // No logic yet → the editor is seeded from the legacy fields, not initialLogic.
    expect(screen.getByTestId('cev-logic').textContent).toBe('null');
    expect(JSON.parse(screen.getByTestId('cev-legacy').textContent || '{}')).toEqual({
      left: '{{ $variables.n }}',
      operator: 'Equal',
      right: '3',
    });
  });

  it('on Save writes the logic and strips the legacy left/operator/right', () => {
    const onChange = vi.fn();
    render(
      <ConditionLogicField
        properties={{ left: 'a', operator: 'Equal', right: 'b', keepMe: 42 }}
        onChange={onChange}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: /Edit logic/i }));
    fireEvent.click(screen.getByRole('button', { name: 'stub-save' }));

    expect(onChange).toHaveBeenCalledTimes(1);
    const next = onChange.mock.calls[0][0] as Record<string, unknown>;
    expect(next).toEqual({ keepMe: 42, logic: SAVED });
    expect(next).not.toHaveProperty('left');
    expect(next).not.toHaveProperty('operator');
    expect(next).not.toHaveProperty('right');
    // Editor closes after Save.
    expect(screen.queryByTestId('cev')).toBeNull();
  });
});
