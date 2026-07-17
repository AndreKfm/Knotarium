// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { InputEditor, type RefOption } from './InputEditor';
import type { DraftOperand } from './conditionModel';

const VARS: RefOption[] = [
  { id: 'v1', label: 'recordCount', type: 'number', ref: '{{ $variables.recordCount }}' },
  { id: 'v2', label: 'body.plan', type: 'string', ref: '{{ $node.http.output.body }}' },
];

function setup(operand: DraftOperand, sampleValue?: unknown, isList = false) {
  const onChangeOperand = vi.fn();
  const onChangeSample = vi.fn();
  const onClose = vi.fn();
  render(
    <InputEditor
      operand={operand}
      variables={VARS}
      sampleValue={sampleValue}
      isList={isList}
      onChangeOperand={onChangeOperand}
      onChangeSample={onChangeSample}
      onClose={onClose}
    />,
  );
  return { onChangeOperand, onChangeSample, onClose };
}

const lit = (type: 'string' | 'number' | 'boolean', text: string): DraftOperand => ({ kind: 'lit', type, text });
const ref = (type: 'string' | 'number' | 'boolean', r: string): DraftOperand => ({ kind: 'ref', type, ref: r });

describe('InputEditor — Literal tab', () => {
  it('opens on the Literal tab for a literal operand and edits the value', () => {
    const { onChangeOperand } = setup(lit('string', 'hi'));
    expect(screen.getByRole('tab', { name: 'Literal' })).toHaveAttribute('aria-selected', 'true');
    fireEvent.change(screen.getByLabelText('Literal value'), { target: { value: 'bye' } });
    expect(onChangeOperand).toHaveBeenCalledWith({ kind: 'lit', type: 'string', text: 'bye' });
  });

  it('switches the literal type via the segmented control', () => {
    const { onChangeOperand } = setup(lit('string', '5'));
    fireEvent.click(screen.getByRole('button', { name: 'number' }));
    expect(onChangeOperand).toHaveBeenCalledWith({ kind: 'lit', type: 'number', text: '5' });
  });

  it('renders a true/false toggle for boolean literals', () => {
    const { onChangeOperand } = setup(lit('boolean', 'true'));
    fireEvent.click(screen.getByRole('button', { name: 'false' }));
    expect(onChangeOperand).toHaveBeenCalledWith({ kind: 'lit', type: 'boolean', text: 'false' });
  });
});

describe('InputEditor — list mode (Is one of)', () => {
  it('shows a comma-separated list input with no type picker and writes string text', () => {
    const { onChangeOperand } = setup(lit('string', '3, 5'), undefined, true);
    // The string/number/boolean type segments are hidden for a list operand.
    expect(screen.queryByRole('button', { name: 'number' })).not.toBeInTheDocument();
    const input = screen.getByLabelText('List values') as HTMLInputElement;
    expect(input.value).toBe('3, 5');
    fireEvent.change(input, { target: { value: '3, 5, 4' } });
    expect(onChangeOperand).toHaveBeenCalledWith({ kind: 'lit', type: 'string', text: '3, 5, 4' });
  });
});

describe('InputEditor — Reference tab', () => {
  it('opens on the Reference tab for a ref operand and lists upstream variables', () => {
    setup(ref('number', '{{ $variables.recordCount }}'));
    expect(screen.getByRole('tab', { name: 'Reference' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('recordCount')).toBeInTheDocument();
    expect(screen.getByText('body.plan')).toBeInTheDocument();
  });

  it('picks a reference, persisting its type and expression', () => {
    const { onChangeOperand } = setup(lit('string', ''));
    fireEvent.click(screen.getByRole('tab', { name: 'Reference' }));
    fireEvent.click(screen.getByText('recordCount'));
    expect(onChangeOperand).toHaveBeenCalledWith({ kind: 'ref', type: 'number', ref: '{{ $variables.recordCount }}' });
  });

  it('edits the manual sample value for a chosen reference', () => {
    const { onChangeSample } = setup(ref('number', '{{ $variables.recordCount }}'), 7);
    const sample = screen.getByLabelText('Sample value') as HTMLInputElement;
    expect(sample.value).toBe('7');
    fireEvent.change(sample, { target: { value: '42' } });
    expect(onChangeSample).toHaveBeenCalledWith('42');
  });
});

describe('InputEditor — dismissal', () => {
  it('closes on a mousedown outside the popover', () => {
    const { onClose } = setup(lit('string', 'hi'));
    fireEvent.mouseDown(document.body);
    expect(onClose).toHaveBeenCalled();
  });

  it('does not close on a mousedown inside the popover', () => {
    const { onClose } = setup(lit('string', 'hi'));
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Reference' }));
    expect(onClose).not.toHaveBeenCalled();
  });

  it('does not close when an input-card trigger is clicked (it toggles/switches instead)', () => {
    const trigger = document.createElement('button');
    trigger.className = 'cne-input';
    document.body.appendChild(trigger);
    const { onClose } = setup(lit('string', 'hi'));
    fireEvent.mouseDown(trigger);
    expect(onClose).not.toHaveBeenCalled();
    trigger.remove();
  });
});
