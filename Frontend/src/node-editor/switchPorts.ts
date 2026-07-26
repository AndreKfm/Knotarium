// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Port derivation for the Switch node. Its output branches come from the node's own
// 'cases' property (one branch per case label) plus the always-present 'default' fallback,
// so the manifest declares no per-case outputs and the canvas derives handles here instead.
//
// The parsing rules MUST mirror SwitchNodeTask on the backend (split on , ; CR and LF; trim;
// drop empties) — the runtime picks the port by matching the node's 'value' against a case
// label case-insensitively and emits that label as selectedPort, so a drifting parse would
// draw handles that never fire.

export const SWITCH_DEFAULT_PORT = 'default';

/** Parse the raw 'cases' property into the ordered, deduplicated case list. */
export function parseSwitchCases(raw: unknown): string[] {
  if (typeof raw !== 'string' || raw.trim().length === 0) {
    return [];
  }
  const seen = new Set<string>();
  const cases: string[] = [];
  for (const part of raw.split(/[,;\n\r]+/)) {
    const label = part.trim();
    // Dedupe case-insensitively, keeping the first spelling: the backend matches the value
    // case-insensitively and stops at the first hit, so two cases differing only in case
    // would render a second handle that can never be selected.
    const key = label.toLowerCase();
    if (label.length > 0 && !seen.has(key)) {
      seen.add(key);
      cases.push(label);
    }
  }
  return cases;
}

/** The node's output handles in render order: each case, then the fallback branch. */
export function switchOutputHandles(properties: Record<string, unknown> | undefined): string[] {
  // A case spelled 'default' resolves to the fallback port on the backend too, so it must not
  // produce a second handle sharing that id.
  const cases = parseSwitchCases(properties?.cases)
    .filter((label) => label.toLowerCase() !== SWITCH_DEFAULT_PORT);
  return [...cases, SWITCH_DEFAULT_PORT];
}
