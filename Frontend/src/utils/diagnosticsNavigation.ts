// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { CompilationDiagnostic } from '../types';

/**
 * Pure helpers backing the dockable diagnostics panel (Feature #9): normalising
 * the diagnostic node id, ordering/merging diagnostics, and resolving which
 * node or edge a diagnostic points at so the canvas can centre on it. Kept free
 * of React Flow so the rules are unit-testable with plain data.
 */

const SEVERITY_RANK: Record<string, number> = { Error: 2, Warning: 1, Info: 0 };

/** Minimal edge shape we need to resolve an edge-scoped diagnostic. */
export interface EdgeRef {
  id: string;
  source: string;
  target: string;
}

/** Where a diagnostic points — used by the canvas to compute a centre point. */
export type DiagnosticFocus =
  | { kind: 'node'; nodeId: string }
  | { kind: 'edge'; source: string; target: string };

/**
 * The backend serialises NodeId as a bare string; older callers wrap it as
 * `{ value }`. Normalise both (and undefined) to a plain id or undefined.
 */
export function normalizeNodeId(raw: unknown): string | undefined {
  if (typeof raw === 'string') {
    return raw.length > 0 ? raw : undefined;
  }
  const value = (raw as { value?: unknown } | null | undefined)?.value;
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

/** Higher = more severe. Unknown severities sort lowest. */
export function severityRank(severity: string): number {
  return SEVERITY_RANK[severity] ?? -1;
}

/** Stable sort, most severe first. Does not mutate the input. */
export function sortDiagnostics(diagnostics: readonly CompilationDiagnostic[]): CompilationDiagnostic[] {
  return diagnostics
    .map((d, index) => ({ d, index }))
    .sort((a, b) => {
      const rank = severityRank(b.d.severity) - severityRank(a.d.severity);
      return rank !== 0 ? rank : a.index - b.index;
    })
    .map((x) => x.d);
}

/** A key identifying a diagnostic for de-duplication across sources. */
export function diagnosticKey(d: CompilationDiagnostic): string {
  return `${d.severity}|${d.code}|${normalizeNodeId(d.nodeId) ?? ''}|${d.edgeId ?? ''}|${d.message}`;
}

/**
 * Merge the blocking publish/run diagnostics with the live edge-validation
 * diagnostics into one ordered, de-duplicated list (most severe first).
 */
export function mergeDiagnostics(
  blocking: readonly CompilationDiagnostic[],
  edge: readonly CompilationDiagnostic[],
): CompilationDiagnostic[] {
  const seen = new Set<string>();
  const merged: CompilationDiagnostic[] = [];
  for (const d of [...blocking, ...edge]) {
    const key = diagnosticKey(d);
    if (seen.has(key)) continue;
    seen.add(key);
    merged.push(d);
  }
  return sortDiagnostics(merged);
}

/** Count diagnostics per severity (for the panel header summary). */
export function countBySeverity(
  diagnostics: readonly CompilationDiagnostic[],
): { Error: number; Warning: number; Info: number } {
  const counts = { Error: 0, Warning: 0, Info: 0 };
  for (const d of diagnostics) {
    if (d.severity in counts) {
      counts[d.severity as keyof typeof counts] += 1;
    }
  }
  return counts;
}

/**
 * Resolve what a diagnostic points at so the canvas can centre on it. Prefers an
 * edge (when `edgeId` matches a known edge) so the offending wire is framed;
 * otherwise falls back to the node id. Returns null when nothing is locatable.
 */
export function resolveDiagnosticFocus(
  diagnostic: CompilationDiagnostic,
  edges: readonly EdgeRef[],
): DiagnosticFocus | null {
  if (diagnostic.edgeId) {
    const edge = edges.find((e) => e.id === diagnostic.edgeId);
    if (edge) {
      return { kind: 'edge', source: edge.source, target: edge.target };
    }
  }
  const nodeId = normalizeNodeId(diagnostic.nodeId);
  if (nodeId) {
    return { kind: 'node', nodeId };
  }
  return null;
}
