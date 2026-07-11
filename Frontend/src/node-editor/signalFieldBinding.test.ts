import { describe, it, expect } from 'vitest';
import type { Node as RFNode, Edge as RFEdge } from '@xyflow/react';
import {
  referencedActionIds,
  originatingActionIds,
  signalFieldGroupsForNode,
  simulatablePins,
  type ActionFieldsById,
} from './signalFieldBinding';

const device = (id: string, actions: Array<{ value: string; label?: string }>): RFNode => ({
  id,
  type: 'externalDevice',
  position: { x: 0, y: 0 },
  data: { properties: { actionPins: { mode: 'multi', items: actions } } },
});

const node = (id: string, type: string, properties: Record<string, unknown> = {}): RFNode => ({
  id,
  type,
  position: { x: 0, y: 0 },
  data: { properties },
});

const edge = (id: string, source: string, target: string, sourceHandle?: string): RFEdge => ({
  id,
  source,
  target,
  sourceHandle,
  targetHandle: 'in',
});

describe('referencedActionIds', () => {
  it('includes only WIRED device action pins', () => {
    const nodes = [device('dev', [{ value: 'CustomAction' }, { value: 'CameraCycleStart' }]), node('log', 'log')];
    const edges = [edge('e1', 'dev', 'log', 'act:CustomAction')]; // CameraCycleStart pin is unwired
    expect(referencedActionIds(nodes, edges)).toEqual(['CustomAction']);
  });

  it('includes an Action Trigger\'s picked action', () => {
    const nodes = [node('at', 'actionTrigger', { action: 'TriggerAlarm' }), node('log', 'log')];
    expect(referencedActionIds(nodes, [])).toEqual(['TriggerAlarm']);
  });
});

describe('originatingActionIds', () => {
  it('resolves the pin a node is directly wired to', () => {
    const nodes = [device('dev', [{ value: 'CustomAction' }]), node('log', 'log')];
    const edges = [edge('e1', 'dev', 'log', 'act:CustomAction')];
    expect(originatingActionIds(nodes, edges, 'log')).toEqual(['CustomAction']);
  });

  it('traces through intermediate nodes (Condition) up to the pin', () => {
    const nodes = [device('dev', [{ value: 'CameraCycleStart' }]), node('cond', 'condition'), node('log', 'log')];
    const edges = [edge('e1', 'dev', 'cond', 'act:CameraCycleStart'), edge('e2', 'cond', 'log', undefined)];
    expect(originatingActionIds(nodes, edges, 'log')).toEqual(['CameraCycleStart']);
  });

  it('does not bind fields from an unrelated (different) pin', () => {
    const nodes = [device('dev', [{ value: 'CustomAction' }, { value: 'CameraCycleStart' }]), node('log', 'log')];
    const edges = [edge('e1', 'dev', 'log', 'act:CustomAction')];
    // CameraCycleStart pin exists but isn't on the path to `log`.
    expect(originatingActionIds(nodes, edges, 'log')).toEqual(['CustomAction']);
  });
});

describe('signalFieldGroupsForNode', () => {
  const fields: ActionFieldsById = {
    CustomAction: [{ key: 'Int', type: 'number' }, { key: 'String', type: 'string' }],
    CameraCycleStart: [{ key: 'Viewer', type: 'string' }],
  };

  it('returns the originating action\'s fields with a friendly label + action-named refPrefix', () => {
    const nodes = [device('dev', [{ value: 'CustomAction', label: 'Custom Action' }]), node('log', 'log')];
    const edges = [edge('e1', 'dev', 'log', 'act:CustomAction')];
    const groups = signalFieldGroupsForNode(nodes, edges, 'log', fields);
    expect(groups).toEqual([{ actionId: 'CustomAction', label: 'Custom Action', refPrefix: 'signal.customAction', fields: fields.CustomAction }]);
  });

  it('omits actions with no known fields', () => {
    const nodes = [device('dev', [{ value: 'UnknownAction' }]), node('log', 'log')];
    const edges = [edge('e1', 'dev', 'log', 'act:UnknownAction')];
    expect(signalFieldGroupsForNode(nodes, edges, 'log', fields)).toEqual([]);
  });

  it('adds a shared event group (signal.params) when an event pin reaches the node', () => {
    const eventFields = [{ key: 'EventString_A', type: 'string' as const }, { key: 'EventInt32_A', type: 'number' as const }];
    const dev: RFNode = {
      id: 'dev', type: 'externalDevice', position: { x: 0, y: 0 },
      data: { properties: { eventPins: { mode: 'multi', items: [{ value: '3:started', label: 'Event 001 ▸ Started' }] } } },
    };
    const edges = [edge('e1', 'dev', 'log', 'evt:3:started')];
    const groups = signalFieldGroupsForNode([dev, node('log', 'log')], edges, 'log', {}, eventFields);
    expect(groups).toEqual([{ actionId: '__event', label: 'Event 001 ▸ Started', refPrefix: 'signal.params', fields: eventFields }]);
  });
});

describe('simulatablePins', () => {
  const deviceWithEvents = (id: string, actions: Array<{ value: string; label?: string }>, events: Array<{ value: string; label?: string }>): RFNode => ({
    id,
    type: 'externalDevice',
    position: { x: 0, y: 0 },
    data: { properties: { actionPins: { mode: 'multi', items: actions }, eventPins: { mode: 'multi', items: events } } },
  });

  it('returns only wired pins, with the event phase stripped to the base type', () => {
    const nodes = [
      deviceWithEvents('dev',
        [{ value: 'CustomAction', label: 'Custom Action' }, { value: 'CameraCycleStart', label: 'Camera Cycle Start' }],
        [{ value: '3:started', label: 'Event 001 ▸ Started' }]),
      node('log', 'log'),
      node('cond', 'condition'),
    ];
    const edges = [
      edge('e1', 'dev', 'log', 'act:CustomAction'),     // wired action
      edge('e2', 'dev', 'cond', 'evt:3:started'),        // wired event
      // CameraCycleStart pin is unwired → excluded
    ];
    expect(simulatablePins(nodes, edges)).toEqual([
      { kind: 'action', type: 'CustomAction', label: 'Custom Action' },
      { kind: 'event', type: '3', label: 'Event 001 ▸ Started' },
    ]);
  });
});
