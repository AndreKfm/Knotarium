// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';
import type { Edge } from '@xyflow/react';
import { decorateEdgesWithDiagnostics, groupDiagnosticsByEdge } from '../utils/edgeDiagnostics';
import type { CompilationDiagnostic } from '../types';

const edges: Edge[] = [
  { id: 'e1', source: 'a', target: 'b' },
  { id: 'e2', source: 'b', target: 'c' },
];

describe('edgeDiagnostics', () => {
  it('ignores diagnostics without an edgeId', () => {
    const grouped = groupDiagnosticsByEdge([
      { severity: 'Warning', code: 'X', message: 'no edge' },
      { severity: 'Warning', code: 'WARN_TYPE_MISMATCH', message: 'on e2', edgeId: 'e2' },
    ]);
    expect(grouped.has('e2')).toBe(true);
    expect(grouped.size).toBe(1);
  });

  it('returns edges by reference when there are no edge diagnostics', () => {
    const result = decorateEdgesWithDiagnostics(edges, []);
    expect(result).toBe(edges);
  });

  it('colours and labels only the offending edge', () => {
    const diagnostics: CompilationDiagnostic[] = [
      { severity: 'Warning', code: 'WARN_TYPE_MISMATCH', message: 'type mismatch on e2', edgeId: 'e2' },
    ];
    const [e1, e2] = decorateEdgesWithDiagnostics(edges, diagnostics);

    // Untouched edge keeps its identity.
    expect(e1).toBe(edges[0]);

    // Offending edge is amber (warning) with a concise label.
    expect(e2.style?.stroke).toBe('#f59e0b');
    expect(e2.label).toBe('⚠ type mismatch');
    expect((e2.data as { diagnostics: unknown[] }).diagnostics).toHaveLength(1);
  });

  it('escalates to the worst severity and counts multiple issues', () => {
    const diagnostics: CompilationDiagnostic[] = [
      { severity: 'Warning', code: 'WARN_MISSING_FIELD', message: 'missing', edgeId: 'e1' },
      { severity: 'Error', code: 'ERR_INVALID_SOCKET_MAPPING', message: 'bad socket', edgeId: 'e1' },
    ];
    const [e1] = decorateEdgesWithDiagnostics(edges, diagnostics);

    expect(e1.style?.stroke).toBe('#ef4444'); // red wins over amber
    expect(e1.label).toBe('⚠ 2 issues');
  });
});
