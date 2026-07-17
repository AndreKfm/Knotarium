// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import {
  diffVersions,
  canonicalize,
  edgeKey,
  type DiffablePayload,
} from '../utils/versionDiff';
import type { NodeDefinition, EdgeDefinition } from '../types';

function node(
  id: string,
  type: string,
  properties: Record<string, unknown> = {},
): NodeDefinition {
  return { id: { value: id }, type, properties };
}

function edge(from: string, output: string, to: string, input: string): EdgeDefinition {
  return { id: `e-${from}-${to}`, from: { value: from }, output, to: { value: to }, input };
}

function payload(nodes: NodeDefinition[], edges: EdgeDefinition[] = []): DiffablePayload {
  return { nodes, edges };
}

describe('canonicalize', () => {
  it('sorts keys, drops _metadata and empty defaults', () => {
    const result = canonicalize({ b: 1, a: 2, empty: '', list: [], _metadata: { x: 5 } });
    expect(result).toEqual({ a: 2, b: 1 });
  });

  it('masks credential-bearing values', () => {
    const result = canonicalize({ apiKey: 'sk-live-123', credentialId: 'cred-9', name: 'ok' }) as Record<string, unknown>;
    expect(result.apiKey).toBe('••••••••');
    expect(result.credentialId).toBe('••••••••');
    expect(result.name).toBe('ok');
  });
});

describe('edgeKey', () => {
  it('uses the source+output+target+input composite, ignoring edge id', () => {
    expect(edgeKey(edge('a', 'result', 'b', 'in'))).toBe('a result b in');
  });
});

describe('diffVersions', () => {
  it('reports an identical payload as no change', () => {
    const p = payload([node('a', 'log', { message: 'hi' })], [edge('a', 'result', 'b', 'in')]);
    const diff = diffVersions(p, p);
    expect(diff.nodes).toHaveLength(0);
    expect(diff.edges).toHaveLength(0);
    expect(diff.hasBehavioralChanges).toBe(false);
    expect(diff.hasLayoutChanges).toBe(false);
  });

  it('detects added and removed nodes', () => {
    const left = payload([node('a', 'log')]);
    const right = payload([node('b', 'httprequest')]);
    const diff = diffVersions(left, right);
    const byId = Object.fromEntries(diff.nodes.map((n) => [n.nodeId, n.kind]));
    expect(byId).toEqual({ a: 'removed', b: 'added' });
    expect(diff.hasBehavioralChanges).toBe(true);
  });

  it('reports a node type change as behavioral', () => {
    const diff = diffVersions(payload([node('a', 'log')]), payload([node('a', 'httprequest')]));
    expect(diff.nodes[0]).toMatchObject({ nodeId: 'a', kind: 'changed', typeBefore: 'log', typeAfter: 'httprequest' });
    expect(diff.nodes[0].layoutOnly).toBe(false);
    expect(diff.hasBehavioralChanges).toBe(true);
  });

  it('reports config field changes with masked credentials', () => {
    const left = payload([node('a', 'http', { url: 'http://old', apiKey: 'sk-1' })]);
    const right = payload([node('a', 'http', { url: 'http://new', apiKey: 'sk-2' })]);
    const diff = diffVersions(left, right);
    const paths = diff.nodes[0].fieldChanges.map((c) => c.path);
    expect(paths).toContain('url');
    const apiKeyChange = diff.nodes[0].fieldChanges.find((c) => c.path === 'apiKey');
    // Both sides masked → equal → not reported. Confirms secrets never leak into the diff.
    expect(apiKeyChange).toBeUndefined();
  });

  it('classifies a pure position move as layout-only', () => {
    const left = payload([node('a', 'log', { message: 'hi', _metadata: { x: 0, y: 0 } })]);
    const right = payload([node('a', 'log', { message: 'hi', _metadata: { x: 100, y: 50 } })]);
    const diff = diffVersions(left, right);
    expect(diff.nodes).toHaveLength(1);
    expect(diff.nodes[0].layoutOnly).toBe(true);
    expect(diff.hasBehavioralChanges).toBe(false);
    expect(diff.hasLayoutChanges).toBe(true);
  });

  it('ignores empty-default churn in properties', () => {
    const left = payload([node('a', 'log', { message: 'hi' })]);
    const right = payload([node('a', 'log', { message: 'hi', note: '', extras: {} })]);
    const diff = diffVersions(left, right);
    expect(diff.nodes).toHaveLength(0);
  });

  it('uses composite edge identity (added/removed, ignoring id churn)', () => {
    const left = payload([], [{ ...edge('a', 'result', 'b', 'in'), id: 'old-id' }]);
    const right = payload([], [{ ...edge('a', 'result', 'b', 'in'), id: 'new-id' }]);
    expect(diffVersions(left, right).edges).toHaveLength(0);

    const moved = payload([], [edge('a', 'result', 'c', 'in')]);
    const diff = diffVersions(left, moved);
    const kinds = diff.edges.map((e) => e.kind).sort();
    expect(kinds).toEqual(['added', 'removed']);
  });

  it('collapses duplicate parallel edges under one key', () => {
    const dup = payload([], [edge('a', 'result', 'b', 'in'), { ...edge('a', 'result', 'b', 'in'), id: 'dup' }]);
    const single = payload([], [edge('a', 'result', 'b', 'in')]);
    expect(diffVersions(dup, single).edges).toHaveLength(0);
  });
});
