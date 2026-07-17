// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Port derivation for the AI Router node. Its output branches come from the node's own
// 'categories' property (one branch per label) plus the always-present 'otherwise' fallback,
// so the manifest declares no outputs and the canvas derives handles here instead.
//
// The parsing rules MUST mirror AiRouterNodeTask.ParseCategories on the backend
// (split on , ; and newlines; trim; drop empties; case-insensitive dedupe keeping the first
// spelling) — the runtime routes via selectedPort string equality against these labels, so a
// drifting parse would draw handles that never fire.

export const AI_ROUTER_OTHERWISE_PORT = 'otherwise';

/** Parse the raw 'categories' property into the ordered, deduplicated label list. */
export function parseAiRouterCategories(raw: unknown): string[] {
  if (typeof raw !== 'string' || raw.trim().length === 0) {
    return [];
  }
  const seen = new Set<string>();
  const labels: string[] = [];
  for (const part of raw.split(/[,;\n\r]+/)) {
    const label = part.trim();
    const key = label.toLowerCase();
    if (label.length > 0 && !seen.has(key)) {
      seen.add(key);
      labels.push(label);
    }
  }
  return labels;
}

/** The node's output handles in render order: each category, then the fallback branch. */
export function aiRouterOutputHandles(properties: Record<string, unknown> | undefined): string[] {
  return [...parseAiRouterCategories(properties?.categories), AI_ROUTER_OTHERWISE_PORT];
}
