// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';
import { buildExpressionCompletions, findOpenExpression } from './expressionCompletions';

describe('buildExpressionCompletions', () => {
  const vars = [
    { name: 'body', type: 'object', producer: 'http-1', producerOutput: 'body' },
    { name: 'statusCode', type: 'number', producer: 'http-1', producerOutput: 'statusCode' },
    { name: 'counter', type: 'number', derived: true },
  ];

  it('builds node-output and $variables expressions with type + source detail', () => {
    expect(buildExpressionCompletions(vars)).toEqual([
      { label: 'body', detail: 'object · from http-1', insertText: '{{ $node.http-1.output.body }}' },
      { label: 'statusCode', detail: 'number · from http-1', insertText: '{{ $node.http-1.output.statusCode }}' },
      { label: 'counter', detail: 'number · variable', insertText: '{{ $variables.counter }}' },
    ]);
  });

  it('filters case-insensitively by name', () => {
    expect(buildExpressionCompletions(vars, 'STATUS').map((c) => c.label)).toEqual(['statusCode']);
  });

  it('returns nothing when there are no variables', () => {
    expect(buildExpressionCompletions([])).toEqual([]);
  });

  it('collapses duplicate insert expressions', () => {
    const dupes = [
      { name: 'a', producer: 'n1', producerOutput: 'x' },
      { name: 'a', producer: 'n1', producerOutput: 'x' },
    ];
    expect(buildExpressionCompletions(dupes)).toHaveLength(1);
  });
});

describe('findOpenExpression', () => {
  it('detects an open fragment and returns its start + query', () => {
    const text = 'Order {{ stat';
    expect(findOpenExpression(text, text.length)).toEqual({ start: 6, query: ' stat' });
  });

  it('returns null when the fragment is already closed', () => {
    const text = '{{ body }} tail';
    expect(findOpenExpression(text, text.length)).toBeNull();
  });

  it('returns null when there is no {{ before the caret', () => {
    expect(findOpenExpression('plain text', 5)).toBeNull();
  });

  it('uses the caret, not end of string', () => {
    const text = '{{ a }} and {{ b';
    // caret right after the first closed fragment
    expect(findOpenExpression(text, 7)).toBeNull();
    // caret at end, inside the second open fragment
    expect(findOpenExpression(text, text.length)).toEqual({ start: 12, query: ' b' });
  });
});
