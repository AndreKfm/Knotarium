// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { MarkerType } from '@xyflow/react';
import type { Edge } from '@xyflow/react';
import type { CompilationDiagnostic } from '../types';

const SEVERITY_RANK: Record<string, number> = { Info: 0, Warning: 1, Error: 2 };
const SEVERITY_COLOR: Record<string, string> = {
  Info: '#38bdf8',
  Warning: '#f59e0b',
  Error: '#ef4444',
};

// Short, human-readable tag per diagnostic code, shown on the edge label.
function conciseLabel(diagnostic: CompilationDiagnostic): string {
  switch (diagnostic.code) {
    case 'WARN_TYPE_MISMATCH':
      return 'type mismatch';
    case 'WARN_FIELD_TYPE_MISMATCH':
      return 'field type';
    case 'WARN_MISSING_FIELD':
      return 'missing field';
    default:
      return diagnostic.severity.toLowerCase();
  }
}

/**
 * Groups diagnostics by their edgeId. Diagnostics without an edgeId are ignored.
 */
export function groupDiagnosticsByEdge(
  diagnostics: CompilationDiagnostic[],
): Map<string, CompilationDiagnostic[]> {
  const byEdge = new Map<string, CompilationDiagnostic[]>();
  for (const diagnostic of diagnostics) {
    if (!diagnostic.edgeId) {
      continue;
    }
    const list = byEdge.get(diagnostic.edgeId) ?? [];
    list.push(diagnostic);
    byEdge.set(diagnostic.edgeId, list);
  }
  return byEdge;
}

/**
 * Returns a copy of the edges where any edge carrying a diagnostic (matched by id) is coloured
 * and labelled by its worst severity. Untouched edges are returned by reference.
 */
export function decorateEdgesWithDiagnostics(
  edges: Edge[],
  diagnostics: CompilationDiagnostic[],
): Edge[] {
  const byEdge = groupDiagnosticsByEdge(diagnostics);
  if (byEdge.size === 0) {
    return edges;
  }

  return edges.map((edge) => {
    const edgeDiagnostics = byEdge.get(edge.id);
    if (!edgeDiagnostics || edgeDiagnostics.length === 0) {
      return edge;
    }

    const worst = edgeDiagnostics.reduce((current, candidate) =>
      (SEVERITY_RANK[candidate.severity] ?? 0) > (SEVERITY_RANK[current.severity] ?? 0) ? candidate : current);
    const color = SEVERITY_COLOR[worst.severity] ?? SEVERITY_COLOR.Warning;
    const label = edgeDiagnostics.length > 1
      ? `⚠ ${edgeDiagnostics.length} issues`
      : `⚠ ${conciseLabel(worst)}`;

    return {
      ...edge,
      style: { ...(edge.style ?? {}), stroke: color, strokeWidth: 2.5 },
      markerEnd: { type: MarkerType.ArrowClosed, color },
      label,
      labelStyle: { fill: color, fontSize: 11, fontWeight: 600 },
      labelShowBg: true,
      labelBgStyle: { fill: '#0b1220', fillOpacity: 0.92 },
      labelBgPadding: [6, 3] as [number, number],
      labelBgBorderRadius: 6,
      className: `edge-diagnostic edge-diagnostic-${worst.severity.toLowerCase()}`,
      data: {
        ...(edge.data ?? {}),
        diagnostics: edgeDiagnostics,
        diagnosticTooltip: edgeDiagnostics.map((d) => d.message).join('\n'),
      },
    };
  });
}
