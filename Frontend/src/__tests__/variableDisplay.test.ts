// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { formatVariableValue, isArrayValue, variableKindLabel, variableTypeSuffix } from '../utils/variableDisplay';

describe('formatVariableValue', () => {
  it('shows "Awaiting run" before a run', () => {
    expect(formatVariableValue('awaiting run', undefined)).toBe('Awaiting run');
    expect(formatVariableValue('awaiting run', { a: 1 })).toBe('Awaiting run');
  });

  it('never prints the literal "undefined" even when resolved', () => {
    expect(formatVariableValue('resolved', undefined)).toBe('Awaiting run');
  });

  it('stringifies a resolved object/array', () => {
    expect(formatVariableValue('resolved', { name: 'Alice' })).toBe('{"name":"Alice"}');
    expect(formatVariableValue('resolved', [1, 2])).toBe('[1,2]');
  });

  it('renders resolved scalars and null', () => {
    expect(formatVariableValue('resolved', 42)).toBe('42');
    expect(formatVariableValue('resolved', 'hi')).toBe('hi');
    expect(formatVariableValue('resolved', null)).toBe('null');
  });
});

describe('isArrayValue', () => {
  it('detects arrays only', () => {
    expect(isArrayValue([1, 2])).toBe(true);
    expect(isArrayValue({ a: 1 })).toBe(false);
    expect(isArrayValue('x')).toBe(false);
    expect(isArrayValue(undefined)).toBe(false);
  });
});

describe('variableKindLabel', () => {
  it('labels a keyed dictionary and array from containerKind (no run needed)', () => {
    expect(variableKindLabel('object', 'object', undefined)).toBe('dictionary');
    expect(variableKindLabel('object', 'array', undefined)).toBe('array');
  });

  it('detects an array from a resolved value when no containerKind', () => {
    expect(variableKindLabel('object', undefined, [1, 2])).toBe('array');
    expect(variableKindLabel('object', undefined, { a: 1 })).toBe('dictionary');
  });

  it('keeps primitive type names', () => {
    expect(variableKindLabel('string', undefined, 'x')).toBe('string');
    expect(variableKindLabel('number', undefined, 1)).toBe('number');
  });
});

describe('variableTypeSuffix', () => {
  it('maps containers from path-inferred kind', () => {
    expect(variableTypeSuffix('object', 'object', undefined)).toBe('{}');
    expect(variableTypeSuffix('object', 'array', undefined)).toBe('[]');
  });

  it('maps an array from a resolved value', () => {
    expect(variableTypeSuffix('object', undefined, [1])).toBe('[]');
    expect(variableTypeSuffix('object', undefined, { a: 1 })).toBe('{}');
  });

  it('maps scalars to one-char sigils', () => {
    expect(variableTypeSuffix('string', undefined, 'x')).toBe('""');
    expect(variableTypeSuffix('number', undefined, 1)).toBe('#');
    expect(variableTypeSuffix('boolean', undefined, true)).toBe('?');
  });
});
