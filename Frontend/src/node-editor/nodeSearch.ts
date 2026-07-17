// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

/**
 * Pure helpers for the node search / jump palette (Ctrl+F / Cmd+K).
 *
 * Kept free of React Flow so they can be unit-tested with plain node data.
 * The palette feeds these the canvas nodes and a query string, and renders the
 * ranked result list; picking a result centres the canvas on that node.
 */

/** Minimal shape we need off an RFNode to search + label it. */
export interface SearchableNode {
  id: string;
  type?: string;
  data?: {
    displayName?: unknown;
    subflowName?: unknown;
    [key: string]: unknown;
  };
}

export interface NodeSearchResult<N extends SearchableNode = SearchableNode> {
  node: N;
  /** The text that matched / is shown in the list (node title). */
  label: string;
  /** Higher = better match. Only meaningful for a non-empty query. */
  score: number;
}

/**
 * The human-readable title shown on a node card — what the user expects to
 * search by. Mirrors CustomNodes: subflow uses its resolved name, everything
 * else uses `data.displayName`, falling back to the node type then id.
 */
export function nodeSearchLabel(node: SearchableNode): string {
  const subflowName = node.data?.subflowName;
  if (node.type === 'subflow' && typeof subflowName === 'string' && subflowName.trim()) {
    return subflowName;
  }
  const displayName = node.data?.displayName;
  if (typeof displayName === 'string' && displayName.trim()) {
    return displayName;
  }
  return node.type || node.id;
}

/**
 * Case-insensitive subsequence ("fuzzy") match. Returns a score when every
 * query character appears in order within `text`, otherwise null.
 *
 * Scoring rewards: contiguous runs, matches at a word boundary, and matches
 * near the start. This keeps "http" ranking the literal "HTTP Request" above
 * an incidental "h…t…t…p" spread across "Halt The Process".
 */
export function fuzzyScore(text: string, query: string): number | null {
  if (!query) return 0;
  const t = text.toLowerCase();
  const q = query.toLowerCase();

  let score = 0;
  let ti = 0;
  let prevMatchIdx = -2;
  for (let qi = 0; qi < q.length; qi++) {
    const ch = q[qi];
    const found = t.indexOf(ch, ti);
    if (found === -1) return null;

    // Base point for the matched char.
    score += 1;
    // Contiguous with the previous matched char: big bonus.
    if (found === prevMatchIdx + 1) score += 5;
    // At a word boundary (start, or after space / separator): bonus.
    const before = found === 0 ? ' ' : t[found - 1];
    if (before === ' ' || before === '-' || before === '_' || before === '/') score += 3;
    // Earlier matches edge out later ones.
    score -= found * 0.01;

    prevMatchIdx = found;
    ti = found + 1;
  }
  return score;
}

/**
 * Rank nodes against a query. An empty / whitespace query returns every node
 * in its original order (so the palette opens showing the full list).
 * Otherwise only nodes whose label fuzzily matches are returned, best first.
 * Ties break by shorter label, then by id for stability.
 */
export function searchNodes<N extends SearchableNode>(
  nodes: readonly N[],
  query: string,
): NodeSearchResult<N>[] {
  const trimmed = query.trim();
  if (!trimmed) {
    return nodes.map((node) => ({ node, label: nodeSearchLabel(node), score: 0 }));
  }

  const results: NodeSearchResult<N>[] = [];
  for (const node of nodes) {
    const label = nodeSearchLabel(node);
    const score = fuzzyScore(label, trimmed);
    if (score !== null) {
      results.push({ node, label, score });
    }
  }

  results.sort((a, b) => {
    if (b.score !== a.score) return b.score - a.score;
    if (a.label.length !== b.label.length) return a.label.length - b.label.length;
    return a.node.id.localeCompare(b.node.id);
  });
  return results;
}
