// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ComparatorNode, GroupNode, NotNode } from './ConditionFlowNodes';
import { ConditionTreeContext, type ConditionTreeHandlers } from './conditionTreeContext';
import type { ComparatorNodeData, GroupNodeData, NotNodeData } from './conditionFlowTree';

// React Flow's Handle needs provider context; stub it (the node bodies are plain DOM otherwise).
vi.mock('@xyflow/react', () => ({
  Handle: () => null,
  Position: { Left: 'left', Right: 'right' },
}));

function ctx(overrides: Partial<ConditionTreeHandlers>): ConditionTreeHandlers {
  const noop = () => {};
  return {
    onPickOperator: noop,
    onChangeOperand: noop,
    onChangeSample: noop,
    onAddComparator: noop,
    onAddGroup: noop,
    onWrapGroup: noop,
    onWrapNot: noop,
    onSetGroupOp: noop,
    onRemove: noop,
    onUnwrap: noop,
    openOperatorFor: null,
    openInputFor: null,
    setOpenOperator: noop,
    setOpenInput: noop,
    variables: [],
    sampleValues: {},
    testMode: false,
    leafStatus: {},
    leafError: {},
    ...overrides,
  };
}

function renderWith(node: React.ReactNode, handlers: Partial<ConditionTreeHandlers>) {
  return render(<ConditionTreeContext.Provider value={ctx(handlers)}>{node}</ConditionTreeContext.Provider>);
}

describe('ConditionFlowNodes interactions', () => {
  it('GroupNode flips its combinator and adds children', () => {
    const onSetGroupOp = vi.fn();
    const onAddComparator = vi.fn();
    const onAddGroup = vi.fn();
    const data: GroupNodeData = { kind: 'group', id: 'g1', op: 'and', status: 'True', childCount: 2 };
    renderWith(<GroupNode data={data as never} id="group:g1" type="group" dragging={false} zIndex={0} selectable={false} deletable={false} selected={false} draggable={false} isConnectable={false} positionAbsoluteX={0} positionAbsoluteY={0} />, {
      onSetGroupOp,
      onAddComparator,
      onAddGroup,
    });
    // Operator is now a hero word that toggles; clicking it (or its "⇄ OR" caption) flips AND→OR.
    fireEvent.click(screen.getByRole('button', { name: /switch to OR/i }));
    expect(onSetGroupOp).toHaveBeenCalledWith('g1', 'or');
    fireEvent.click(screen.getByRole('button', { name: /Add condition to g1/i }));
    expect(onAddComparator).toHaveBeenCalledWith('g1');
    fireEvent.click(screen.getByRole('button', { name: /Add group to g1/i }));
    expect(onAddGroup).toHaveBeenCalledWith('g1');
  });

  it('ComparatorNode wraps and deletes via its tools', () => {
    const onWrapNot = vi.fn();
    const onRemove = vi.fn();
    const data: ComparatorNodeData = { kind: 'comparator', cmpId: 'c1', op: 'eq', symbol: '=', label: 'Equals', status: 'True', leftType: 'number', rightType: 'number' };
    renderWith(<ComparatorNode data={data as never} id="cmp:c1" type="comparator" dragging={false} zIndex={0} selectable={false} deletable={false} selected={false} draggable={false} isConnectable={false} positionAbsoluteX={0} positionAbsoluteY={0} />, {
      onWrapNot,
      onRemove,
    });
    fireEvent.click(screen.getByRole('button', { name: 'Negate c1' }));
    expect(onWrapNot).toHaveBeenCalledWith('c1');
    fireEvent.click(screen.getByRole('button', { name: 'Remove c1' }));
    expect(onRemove).toHaveBeenCalledWith('c1');
  });

  it('NotNode unwraps', () => {
    const onUnwrap = vi.fn();
    const data: NotNodeData = { kind: 'not', id: 'n1', status: 'False' };
    renderWith(<NotNode data={data as never} id="not:n1" type="not" dragging={false} zIndex={0} selectable={false} deletable={false} selected={false} draggable={false} isConnectable={false} positionAbsoluteX={0} positionAbsoluteY={0} />, {
      onUnwrap,
    });
    fireEvent.click(screen.getByRole('button', { name: 'Unwrap n1' }));
    expect(onUnwrap).toHaveBeenCalledWith('n1');
  });
});
