// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { schemaMapper } from '../utils/schemaMapper';
import type { WorkflowDefinition } from '../types';
import type { Node as RFNode, Edge as RFEdge } from '@xyflow/react';

describe('schemaMapper', () => {
  it('should map backend definition to React Flow nodes and edges', () => {
    const backendWorkflow: WorkflowDefinition = {
      id: { value: 'wf-1' },
      name: 'Test Flow',
      nodes: [
        {
          id: { value: 'node-start' },
          type: 'start',
          properties: {
            label: 'Start Node',
            _metadata: { x: 120, y: 340 }
          }
        },
        {
          id: { value: 'node-end' },
          type: 'end',
          properties: {
            label: 'End Node'
            // No position metadata here to test fallback
          }
        }
      ],
      edges: [
        {
          id: 'edge-1',
          from: { value: 'node-start' },
          output: 'default',
          to: { value: 'node-end' },
          input: 'default'
        }
      ]
    };

    const { nodes, edges } = schemaMapper.toReactFlow(backendWorkflow);

    expect(nodes).toHaveLength(2);
    expect(edges).toHaveLength(1);

    // Node 1 position restored
    expect(nodes[0].id).toBe('node-start');
    expect(nodes[0].type).toBe('start');
    expect(nodes[0].position).toEqual({ x: 120, y: 340 });
    expect(nodes[0].data.properties).toEqual({ label: 'Start Node' }); // Metadata is omitted from properties data

    // Node 2 position fallback
    expect(nodes[1].id).toBe('node-end');
    expect(nodes[1].position.x).toBeTypeOf('number');
    expect(nodes[1].position.y).toBeTypeOf('number');

    // Edge mapping
    expect(edges[0].id).toBe('edge-1');
    expect(edges[0].source).toBe('node-start');
    expect(edges[0].sourceHandle).toBe('result');
    expect(edges[0].target).toBe('node-end');
    expect(edges[0].targetHandle).toBe('in');
  });

  it('should map React Flow elements back to backend definition format', () => {
    const rfNodes: RFNode[] = [
      {
        id: 'start-node',
        type: 'start',
        position: { x: 200, y: 400 },
        data: {
          properties: { label: 'Start' }
        }
      }
    ];

    const rfEdges: RFEdge[] = [
      {
        id: 'e-1',
        source: 'start-node',
        sourceHandle: 'success',
        target: 'end-node',
        targetHandle: 'in'
      }
    ];

    const wf = schemaMapper.toBackend('wf-uuid', 'Saved Canvas', rfNodes, rfEdges);

    expect(wf.id.value).toBe('wf-uuid');
    expect(wf.name).toBe('Saved Canvas');
    expect(wf.nodes).toHaveLength(1);
    expect(wf.edges).toHaveLength(1);

    // Node properties must contain _metadata position
    const savedNode = wf.nodes[0];
    expect(savedNode.id.value).toBe('start-node');
    expect(savedNode.type).toBe('start');
    expect(savedNode.properties.label).toBe('Start');
    expect(savedNode.properties._metadata).toEqual({ x: 200, y: 400 });

    // Edge
    const savedEdge = wf.edges[0];
    expect(savedEdge.id).toBe('e-1');
    expect(savedEdge.from.value).toBe('start-node');
    // Legacy 'success' handle on a non-branch node is normalized to the renamed 'result' port.
    expect(savedEdge.output).toBe('result');
    expect(savedEdge.to.value).toBe('end-node');
    expect(savedEdge.input).toBe('in');
  });

  it('normalizes legacy success handles on non-branch nodes but preserves branch ports', () => {
    const rfNodes: RFNode[] = [
      { id: 'start-1', type: 'start', position: { x: 0, y: 0 }, data: {} },
      { id: 'log-1', type: 'log', position: { x: 0, y: 0 }, data: {} },
      { id: 'http-1', type: 'httpRequest', position: { x: 0, y: 0 }, data: {} },
      { id: 'end-1', type: 'end', position: { x: 0, y: 0 }, data: {} },
    ];
    const rfEdges: RFEdge[] = [
      { id: 'e1', source: 'start-1', sourceHandle: 'success', target: 'log-1', targetHandle: 'in' },
      { id: 'e2', source: 'http-1', sourceHandle: 'success', target: 'end-1', targetHandle: 'in' },
      { id: 'e3', source: 'http-1', sourceHandle: 'error', target: 'log-1', targetHandle: 'in' },
    ];

    const wf = schemaMapper.toBackend('wf', 'Mixed', rfNodes, rfEdges);
    const byId = Object.fromEntries(wf.edges.map((e) => [e.id, e.output]));

    expect(byId.e1).toBe('result'); // start (non-branch) success -> result
    expect(byId.e2).toBe('success'); // httpRequest (branch) success preserved
    expect(byId.e3).toBe('error'); // httpRequest error preserved

    // Round-trip back to React Flow keeps branch handles intact.
    const { edges } = schemaMapper.toReactFlow(wf);
    const rt = Object.fromEntries(edges.map((e) => [e.id, e.sourceHandle]));
    expect(rt.e1).toBe('result');
    expect(rt.e2).toBe('success');
    expect(rt.e3).toBe('error');
  });

  it('persists a resized container size from the resizer-authoritative top-level width/height', () => {
    // NodeResizer (xyflow v12) writes a resize to node.width/node.height and leaves the stale
    // creation size on node.style. Serialization must read the top-level value, else every group
    // / loop-box resize is dropped and the container reverts to its creation size on reload.
    const rfNodes: RFNode[] = [
      {
        id: 'g1',
        type: 'group',
        position: { x: 50, y: 60 },
        width: 640,
        height: 420,
        style: { width: 400, height: 300 }, // stale creation size
        data: { properties: { label: 'My Group' } },
      },
    ];

    const wf = schemaMapper.toBackend('wf', 'WF', rfNodes, []);
    const meta = wf.nodes[0].properties._metadata as { width?: number; height?: number };
    expect(meta.width).toBe(640);
    expect(meta.height).toBe(420);

    // Round-trips back onto node.style so the group renders at the saved size on reload.
    const { nodes } = schemaMapper.toReactFlow(wf);
    expect(nodes[0].style?.width).toBe(640);
    expect(nodes[0].style?.height).toBe(420);
  });
});
