// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

/**
 * Pure helpers for inline node renaming (double-click the card label to edit
 * the name in place). Kept free of React / React Flow so the trimming, commit,
 * and "is this type renamable" rules can be unit-tested with plain data.
 *
 * The name lives on `data.displayName` — the same field CustomNodes/nodeSearch
 * read for the header label. Subflow cards show the *resolved* child-workflow
 * name (`data.subflowName`), so renaming them in place would be ignored; those
 * are excluded.
 */

/** Minimal shape we need to rename a node. */
export interface RenamableNode {
  id: string;
  type?: string;
  data?: {
    displayName?: unknown;
    [key: string]: unknown;
  };
}

/**
 * Whether a node type supports inline rename. Subflow labels are derived from
 * the referenced workflow, so an in-place rename wouldn't stick — exclude them.
 */
export function canRenameNode(node: Pick<RenamableNode, 'type'>): boolean {
  return node.type !== 'subflow';
}

/**
 * Normalise a draft name into the value to persist. Returns the trimmed string,
 * or `null` when it's empty/whitespace-only (meaning: keep the existing name).
 */
export function commitNodeName(draft: string): string | null {
  const trimmed = draft.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/**
 * Return a copy of `nodes` with `data.displayName` set to `name` on the node
 * matching `id`. Untouched nodes are returned by reference. A `null`/empty name
 * is a no-op (the array is returned unchanged).
 */
export function applyNodeRename<N extends RenamableNode>(
  nodes: readonly N[],
  id: string,
  name: string | null,
): N[] {
  const next = name == null ? null : commitNodeName(name);
  if (next == null) {
    return nodes as N[];
  }
  return nodes.map((node) =>
    node.id === id
      ? { ...node, data: { ...(node.data ?? {}), displayName: next } }
      : node,
  );
}
