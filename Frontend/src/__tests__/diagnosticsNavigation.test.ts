// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import type { CompilationDiagnostic } from '../types';
import {
  normalizeNodeId,
  severityRank,
  sortDiagnostics,
  diagnosticKey,
  mergeDiagnostics,
  countBySeverity,
  resolveDiagnosticFocus,
} from '../utils/diagnosticsNavigation';

// `nodeId` is typed as NodeId but the backend serialises it as a bare string;
// allow loose values so tests can exercise both forms.
const diag = (over: Partial<Record<keyof CompilationDiagnostic, unknown>> = {}): CompilationDiagnostic => ({
  severity: 'Warning',
  code: 'WARN_X',
  message: 'something',
  ...over,
}) as CompilationDiagnostic;

describe('normalizeNodeId', () => {
  it('passes through a bare string', () => {
    expect(normalizeNodeId('node-1')).toBe('node-1');
  });
  it('unwraps the { value } form', () => {
    expect(normalizeNodeId({ value: 'node-2' })).toBe('node-2');
  });
  it('returns undefined for empty / missing / wrong shapes', () => {
    expect(normalizeNodeId('')).toBeUndefined();
    expect(normalizeNodeId(undefined)).toBeUndefined();
    expect(normalizeNodeId(null)).toBeUndefined();
    expect(normalizeNodeId({ value: '' })).toBeUndefined();
    expect(normalizeNodeId({ value: 5 })).toBeUndefined();
  });
});

describe('severityRank / sortDiagnostics', () => {
  it('ranks Error > Warning > Info, unknown lowest', () => {
    expect(severityRank('Error')).toBeGreaterThan(severityRank('Warning'));
    expect(severityRank('Warning')).toBeGreaterThan(severityRank('Info'));
    expect(severityRank('Bogus')).toBeLessThan(severityRank('Info'));
  });

  it('sorts most-severe first and is stable within a severity', () => {
    const input = [
      diag({ severity: 'Info', code: 'I1' }),
      diag({ severity: 'Error', code: 'E1' }),
      diag({ severity: 'Warning', code: 'W1' }),
      diag({ severity: 'Error', code: 'E2' }),
    ];
    const out = sortDiagnostics(input);
    expect(out.map((d) => d.code)).toEqual(['E1', 'E2', 'W1', 'I1']);
    // input not mutated
    expect(input[0].code).toBe('I1');
  });
});

describe('mergeDiagnostics', () => {
  it('concatenates, de-duplicates by key, and orders by severity', () => {
    const blocking = [diag({ severity: 'Error', code: 'E1', message: 'boom' })];
    const edge = [
      diag({ severity: 'Error', code: 'E1', message: 'boom' }), // dupe of blocking
      diag({ severity: 'Warning', code: 'W1', edgeId: 'e1' }),
    ];
    const out = mergeDiagnostics(blocking, edge);
    expect(out).toHaveLength(2);
    expect(out.map((d) => d.code)).toEqual(['E1', 'W1']);
  });

  it('treats same code on different node/edge as distinct', () => {
    const a = diag({ code: 'C', nodeId: 'n1' });
    const b = diag({ code: 'C', nodeId: 'n2' });
    expect(diagnosticKey(a)).not.toBe(diagnosticKey(b));
    expect(mergeDiagnostics([a], [b])).toHaveLength(2);
  });
});

describe('countBySeverity', () => {
  it('tallies each severity', () => {
    const out = countBySeverity([
      diag({ severity: 'Error' }),
      diag({ severity: 'Error' }),
      diag({ severity: 'Warning' }),
      diag({ severity: 'Info' }),
    ]);
    expect(out).toEqual({ Error: 2, Warning: 1, Info: 1 });
  });
});

describe('resolveDiagnosticFocus', () => {
  const edges = [{ id: 'e1', source: 'a', target: 'b' }];

  it('frames the edge when edgeId matches a known edge', () => {
    expect(resolveDiagnosticFocus(diag({ edgeId: 'e1' }), edges)).toEqual({
      kind: 'edge',
      source: 'a',
      target: 'b',
    });
  });

  it('falls back to the node id when the edge is unknown', () => {
    expect(resolveDiagnosticFocus(diag({ edgeId: 'missing', nodeId: 'n9' }), edges)).toEqual({
      kind: 'node',
      nodeId: 'n9',
    });
  });

  it('resolves a node-only diagnostic (incl. the { value } form)', () => {
    expect(resolveDiagnosticFocus(diag({ nodeId: { value: 'n3' } }), edges)).toEqual({
      kind: 'node',
      nodeId: 'n3',
    });
  });

  it('returns null when nothing is locatable', () => {
    expect(resolveDiagnosticFocus(diag({}), edges)).toBeNull();
  });
});
