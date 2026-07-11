import { describe, it, expect } from 'vitest';
import type { Node as RFNode, Edge as RFEdge } from '@xyflow/react';
import { schemaMapper } from './schemaMapper';

function node(id: string, type: string): RFNode {
  return { id, type, position: { x: 0, y: 0 }, data: {} } as RFNode;
}

function edge(id: string, source: string, sourceHandle: string, target: string, targetHandle: string): RFEdge {
  return { id, source, sourceHandle, target, targetHandle } as RFEdge;
}

describe('schemaMapper.toBackend handle casing', () => {
  it('preserves the case of device-pin handles (evt:/act:) so they match on reload', () => {
    const nodes = [node('dev', 'externalDevice'), node('log', 'log')];
    const edges = [
      edge('e1', 'dev', 'act:CustomAction', 'log', 'in'),
      edge('e2', 'dev', 'evt:VehicleRecognised', 'log', 'in'),
    ];

    const def = schemaMapper.toBackend('', 'wf', nodes, edges);

    expect(def.edges.find((e) => e.id === 'e1')?.output).toBe('act:CustomAction');
    expect(def.edges.find((e) => e.id === 'e2')?.output).toBe('evt:VehicleRecognised');
  });

  it('still folds case on ordinary branch handles', () => {
    const nodes = [node('c', 'condition'), node('log', 'log')];
    const def = schemaMapper.toBackend('', 'wf', nodes, [edge('e1', 'c', 'True', 'log', 'in')]);

    expect(def.edges[0].output).toBe('true');
  });
});
