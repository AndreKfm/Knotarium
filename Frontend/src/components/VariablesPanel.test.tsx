// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect, beforeEach } from 'vitest';
import { render, fireEvent, act } from '@testing-library/react';
import { VariablesPanel } from './VariablesPanel';
import { useVariableStore } from '../stores/useVariableStore';

const WF = 'wf-virtualize';

function seed(count: number) {
  act(() => {
    useVariableStore.getState().resetStore();
    for (let i = 0; i < count; i++) {
      useVariableStore.getState().addVariable(WF, {
        name: `var_${String(i).padStart(3, '0')}`,
        type: 'string',
        producer: `node-${i}`,
        producerOutput: 'result',
        value: 'x',
      });
    }
  });
}

describe('VariablesPanel virtualization (#15)', () => {
  beforeEach(() => {
    act(() => useVariableStore.getState().resetStore());
  });

  it('renders every card directly for a short list (no windowing)', () => {
    seed(5);
    const { container, queryByTestId } = render(<VariablesPanel workflowId={WF} />);
    expect(queryByTestId('variables-virtual-list')).toBeNull();
    expect(container.querySelectorAll('.variable-card')).toHaveLength(5);
  });

  it('windows a long list, rendering far fewer cards than exist', () => {
    seed(200);
    const { container, getByTestId } = render(<VariablesPanel workflowId={WF} />);
    getByTestId('variables-virtual-list'); // switched to the windowed path
    const rendered = container.querySelectorAll('.variable-card').length;
    expect(rendered).toBeGreaterThan(0);
    expect(rendered).toBeLessThan(200);
  });

  it('reveals later rows after scrolling and drops earlier ones', () => {
    seed(200);
    const { container, getByTestId } = render(<VariablesPanel workflowId={WF} />);
    const list = getByTestId('variables-virtual-list');

    const namesAtTop = Array.from(container.querySelectorAll('.variable-card'))
      .map((el) => el.textContent || '');
    expect(namesAtTop.some((t) => t.includes('var_000'))).toBe(true);

    act(() => {
      // jsdom doesn't lay out, so drive scrollTop explicitly; the handler reads it.
      Object.defineProperty(list, 'scrollTop', { value: 6000, configurable: true });
      fireEvent.scroll(list);
    });

    const namesAfter = Array.from(container.querySelectorAll('.variable-card'))
      .map((el) => el.textContent || '');
    // The first row is no longer rendered, and a deeper row now is.
    expect(namesAfter.some((t) => t.includes('var_000'))).toBe(false);
    expect(namesAfter.some((t) => t.includes('var_0'))).toBe(true);
  });
});
