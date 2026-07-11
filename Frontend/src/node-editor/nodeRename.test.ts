import { describe, it, expect } from 'vitest';
import { canRenameNode, commitNodeName, applyNodeRename } from './nodeRename';

describe('canRenameNode', () => {
  it('allows generic node types', () => {
    expect(canRenameNode({ type: 'httpRequest' })).toBe(true);
    expect(canRenameNode({ type: 'inlineCode' })).toBe(true);
    expect(canRenameNode({ type: 'start' })).toBe(true);
    expect(canRenameNode({ type: undefined })).toBe(true);
  });

  it('excludes subflow (its label is the derived child-workflow name)', () => {
    expect(canRenameNode({ type: 'subflow' })).toBe(false);
  });
});

describe('commitNodeName', () => {
  it('trims surrounding whitespace', () => {
    expect(commitNodeName('  Fetch user  ')).toBe('Fetch user');
  });

  it('returns null for empty or whitespace-only drafts', () => {
    expect(commitNodeName('')).toBeNull();
    expect(commitNodeName('   ')).toBeNull();
    expect(commitNodeName('\t\n')).toBeNull();
  });

  it('keeps inner whitespace', () => {
    expect(commitNodeName('My  cool   node')).toBe('My  cool   node');
  });
});

describe('applyNodeRename', () => {
  const nodes = [
    { id: 'a', type: 'httpRequest', data: { displayName: 'Old A', foo: 1 } },
    { id: 'b', type: 'inlineCode', data: { displayName: 'B' } },
  ];

  it('sets displayName on the matching node only', () => {
    const next = applyNodeRename(nodes, 'a', 'New A');
    expect(next[0].data).toEqual({ displayName: 'New A', foo: 1 });
    // Other node untouched (and returned by reference).
    expect(next[1]).toBe(nodes[1]);
  });

  it('trims the new name before applying', () => {
    const next = applyNodeRename(nodes, 'a', '  Spaced  ');
    expect(next[0].data?.displayName).toBe('Spaced');
  });

  it('is a no-op for an empty name (returns the array unchanged)', () => {
    expect(applyNodeRename(nodes, 'a', '   ')).toBe(nodes);
    expect(applyNodeRename(nodes, 'a', null)).toBe(nodes);
  });

  it('handles a node with no data object', () => {
    const bare: Array<{ id: string; type: string; data?: { displayName?: unknown } }> = [
      { id: 'x', type: 'noop' },
    ];
    const next = applyNodeRename(bare, 'x', 'Named');
    expect(next[0].data).toEqual({ displayName: 'Named' });
  });

  it('returns a no-op map when id is not found (no node changed)', () => {
    const next = applyNodeRename(nodes, 'missing', 'Whatever');
    expect(next[0]).toBe(nodes[0]);
    expect(next[1]).toBe(nodes[1]);
  });
});
