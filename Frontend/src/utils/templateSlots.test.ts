import { describe, it, expect } from 'vitest';
import { collectSlotNames, rewriteSlotsForInsert } from './templateSlots';
import type { Node as RFNode } from '@xyflow/react';

const node = (id: string, props: Record<string, unknown>): RFNode =>
  ({ id, type: 'log', position: { x: 0, y: 0 }, data: { properties: props } }) as RFNode;

describe('templateSlots', () => {
  it('collects slot keys from node properties (incl. nested)', () => {
    const nodes = [
      node('a', { apiKey: 'slot:weather-api' }),
      node('b', { auth: { token: 'slot:slack-oauth' }, note: 'plain' }),
    ];
    expect([...collectSlotNames(nodes)].sort()).toEqual(['slack-oauth', 'weather-api']);
  });

  it('renames incoming slots that collide with the open workflow', () => {
    const incoming = [node('x', { cred: 'slot:camera-api' })];
    const existing = new Set(['camera-api']); // already used by the open workflow

    const { nodes, renamed } = rewriteSlotsForInsert(incoming, existing);

    expect(renamed).toEqual([{ from: 'camera-api', to: 'camera-api-2' }]);
    expect((nodes[0].data as { properties: Record<string, unknown> }).properties.cred).toBe('slot:camera-api-2');
  });

  it('leaves non-colliding slots untouched', () => {
    const incoming = [node('x', { cred: 'slot:fresh-key' })];
    const { nodes, renamed } = rewriteSlotsForInsert(incoming, new Set(['other']));
    expect(renamed).toEqual([]);
    expect(nodes).toBe(incoming); // returned by reference when nothing changes
  });

  it('suffixes around already-taken renames', () => {
    const incoming = [node('x', { cred: 'slot:db' }), node('y', { cred: 'slot:db' })];
    // "db" and "db-2" already exist → incoming "db" becomes "db-3"
    const { nodes, renamed } = rewriteSlotsForInsert(incoming, new Set(['db', 'db-2']));
    expect(renamed).toEqual([{ from: 'db', to: 'db-3' }]);
    expect((nodes[0].data as { properties: Record<string, unknown> }).properties.cred).toBe('slot:db-3');
    expect((nodes[1].data as { properties: Record<string, unknown> }).properties.cred).toBe('slot:db-3');
  });
});
