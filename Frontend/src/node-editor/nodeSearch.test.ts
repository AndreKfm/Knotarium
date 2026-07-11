import { describe, it, expect } from 'vitest';
import { fuzzyScore, nodeSearchLabel, searchNodes, type SearchableNode } from './nodeSearch';

const mk = (id: string, type: string, data?: SearchableNode['data']): SearchableNode => ({
  id,
  type,
  data,
});

describe('nodeSearchLabel', () => {
  it('uses displayName when present', () => {
    expect(nodeSearchLabel(mk('n1', 'http', { displayName: 'HTTP Request' }))).toBe('HTTP Request');
  });

  it('uses subflowName for subflow nodes', () => {
    expect(
      nodeSearchLabel(mk('n1', 'subflow', { displayName: 'Subflow', subflowName: 'Send Invoice' })),
    ).toBe('Send Invoice');
  });

  it('falls back to type then id', () => {
    expect(nodeSearchLabel(mk('n1', 'http'))).toBe('http');
    expect(nodeSearchLabel({ id: 'n9' })).toBe('n9');
  });

  it('ignores blank displayName', () => {
    expect(nodeSearchLabel(mk('n1', 'http', { displayName: '   ' }))).toBe('http');
  });
});

describe('fuzzyScore', () => {
  it('returns 0 for empty query (matches anything)', () => {
    expect(fuzzyScore('anything', '')).toBe(0);
  });

  it('returns null when chars are not a subsequence', () => {
    expect(fuzzyScore('abc', 'xyz')).toBeNull();
    expect(fuzzyScore('abc', 'acb')).toBeNull(); // order matters
  });

  it('matches a subsequence', () => {
    expect(fuzzyScore('HTTP Request', 'http')).not.toBeNull();
    expect(fuzzyScore('HTTP Request', 'req')).not.toBeNull();
  });

  it('is case-insensitive', () => {
    expect(fuzzyScore('HTTP Request', 'HTTP')).toEqual(fuzzyScore('http request', 'http'));
  });

  it('scores contiguous prefix higher than a scattered match', () => {
    const contiguous = fuzzyScore('HTTP Request', 'http')!;
    const scattered = fuzzyScore('Halt The Tidy Process', 'http')!;
    expect(contiguous).toBeGreaterThan(scattered);
  });

  it('rewards word-boundary matches', () => {
    const boundary = fuzzyScore('Send Request', 'r')!;
    const midword = fuzzyScore('Steer', 'r')!;
    expect(boundary).toBeGreaterThan(midword);
  });
});

describe('searchNodes', () => {
  const nodes = [
    mk('n1', 'http', { displayName: 'HTTP Request' }),
    mk('n2', 'condition', { displayName: 'Check Status' }),
    mk('n3', 'http', { displayName: 'Health Check' }),
    mk('n4', 'subflow', { displayName: 'Subflow', subflowName: 'Send Invoice' }),
  ];

  it('returns every node in original order for an empty query', () => {
    const r = searchNodes(nodes, '');
    expect(r.map((x) => x.node.id)).toEqual(['n1', 'n2', 'n3', 'n4']);
    expect(r.every((x) => x.score === 0)).toBe(true);
  });

  it('treats a whitespace-only query as empty', () => {
    expect(searchNodes(nodes, '   ').length).toBe(nodes.length);
  });

  it('filters to fuzzy matches only', () => {
    const r = searchNodes(nodes, 'check');
    expect(r.map((x) => x.node.id).sort()).toEqual(['n2', 'n3']);
  });

  it('ranks the exact-ish match first', () => {
    const r = searchNodes(nodes, 'http');
    expect(r[0].node.id).toBe('n1');
  });

  it('searches subflow nodes by their resolved name', () => {
    const r = searchNodes(nodes, 'invoice');
    expect(r.map((x) => x.node.id)).toEqual(['n4']);
  });

  it('returns nothing when no node matches', () => {
    expect(searchNodes(nodes, 'zzzzz')).toEqual([]);
  });

  it('breaks score ties by shorter label then id', () => {
    const tie = [
      mk('b', 'x', { displayName: 'Send' }),
      mk('a', 'x', { displayName: 'Sending Mail' }),
      mk('c', 'x', { displayName: 'Send' }),
    ];
    const r = searchNodes(tie, 'send');
    // Both "Send" labels (shorter) come before "Sending Mail"; id breaks the a/c... 'b' vs 'c'
    expect(r[0].label).toBe('Send');
    expect(r[r.length - 1].node.id).toBe('a');
  });
});
