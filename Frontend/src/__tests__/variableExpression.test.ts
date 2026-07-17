// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { variableRefExpression } from '../utils/variableExpression';

describe('variableRefExpression', () => {
  it('uses $variables for a Set Variable-declared (derived) global', () => {
    expect(variableRefExpression({
      name: 'myDict',
      producer: 'setVariable-it1t30sl7',
      producerOutput: 'myDict',
      derived: true,
    })).toBe('{{ $variables.myDict }}');
  });

  it('uses $node.<id>.output.<field> for a promoted node output', () => {
    expect(variableRefExpression({
      name: 'http_response_body',
      producer: 'httpRequest-1',
      producerOutput: 'body',
      derived: false,
    })).toBe('{{ $node.httpRequest-1.output.body }}');
  });

  it('falls back to $variables when there is no producer output', () => {
    expect(variableRefExpression({ name: 'counter' })).toBe('{{ $variables.counter }}');
  });
});
