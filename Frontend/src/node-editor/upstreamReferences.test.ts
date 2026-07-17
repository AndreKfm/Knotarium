// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { dataFieldsFor, upstreamNodeIds, upstreamReferenceGroups } from './upstreamReferences';

const META = {
  start: { displayName: 'Start', outputHandles: ['result'] },
  forLoop: { displayName: 'For Loop', outputHandles: ['start', 'success'] },
  httpRequest: { displayName: 'HTTP Request', outputHandles: ['success', 'error'] },
  condition: { displayName: 'Condition', outputHandles: ['true', 'false'] },
  log: { displayName: 'Log', outputHandles: ['result'] },
};

describe('dataFieldsFor', () => {
  it('uses known dynamic fields for the loop', () => {
    expect(dataFieldsFor('forLoop', ['start', 'success'])).toEqual(['index', 'item']);
  });
  it('keeps data ports and drops control/branch ports', () => {
    expect(dataFieldsFor('httpRequest', ['success', 'error'])).toEqual(['success', 'error']);
    expect(dataFieldsFor('condition', ['true', 'false'])).toEqual([]); // pure branch → no data
    expect(dataFieldsFor('log', ['result'])).toEqual(['result']);
  });
});

describe('upstreamNodeIds', () => {
  it('collects transitive ancestors, excluding the node itself', () => {
    const edges = [
      { source: 'a', target: 'b' },
      { source: 'b', target: 'c' },
      { source: 'c', target: 'd' },
    ];
    expect(upstreamNodeIds('d', edges).sort()).toEqual(['a', 'b', 'c']);
    expect(upstreamNodeIds('a', edges)).toEqual([]);
  });
});

describe('upstreamReferenceGroups', () => {
  const nodes = [
    { id: 's', type: 'start' },
    { id: 'loop-1', type: 'forLoop' },
    { id: 'log-1', type: 'log' },
  ];
  const edges = [
    { source: 's', target: 'loop-1' },
    { source: 'loop-1', target: 'log-1' },
  ];

  it('builds insertable {{ $node.<id>.output.<field> }} refs for upstream data', () => {
    const groups = upstreamReferenceGroups('log-1', nodes, edges, META);
    const byId = Object.fromEntries(groups.map((g) => [g.nodeId, g]));

    // Both upstream nodes surfaced (start via result, loop via index/item).
    expect(Object.keys(byId).sort()).toEqual(['loop-1', 's']);
    expect(byId['loop-1'].fields.map((f) => f.expr)).toEqual([
      '{{ $node.loop-1.output.index }}',
      '{{ $node.loop-1.output.item }}',
    ]);
    expect(byId['s'].fields[0].expr).toBe('{{ $node.s.output.result }}');
  });

  it('returns nothing when no node is selected', () => {
    expect(upstreamReferenceGroups(null, nodes, edges, META)).toEqual([]);
  });

  it('omits upstream nodes that expose no data (e.g. a pure branch)', () => {
    const branchNodes = [
      { id: 'c', type: 'condition' },
      { id: 'log-1', type: 'log' },
    ];
    const branchEdges = [{ source: 'c', target: 'log-1' }];
    expect(upstreamReferenceGroups('log-1', branchNodes, branchEdges, META)).toEqual([]);
  });
});
