// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Per-node binding of inbound external-signal fields. The inbound `signal` is ONE instance per run —
// seeded at the device-block pin (or Event/Action Trigger) that started the run — so its `params.<key>`
// fields belong to that specific action/event, not to the canvas as a whole. Registering them as global
// variables conflated distinct signal instances; instead we resolve, for a given consumer node, which
// action(s) can start a run that reaches it, and offer THAT action's fields scoped to the node.
//
// Kept free of React so the upstream trace is unit-testable with plain node/edge data.
import type { Node as RFNode, Edge as RFEdge } from '@xyflow/react';
import { ACTION_PIN_HANDLE_PREFIX, EVENT_PIN_HANDLE_PREFIX, readDeviceSurface } from './externalDevicePins';

export type SignalFieldType = 'string' | 'number' | 'boolean';

/** One action's field schema, fetched from the provider (reactor.actionFields). */
export interface SignalField {
  key: string;
  type: SignalFieldType;
}

/** action id -> its fields. Built once per graph from the provider; shared across nodes. */
export type ActionFieldsById = Record<string, SignalField[]>;

function readValue(raw: unknown): string | null {
  if (typeof raw === 'string') return raw || null;
  if (raw && typeof raw === 'object') {
    const v = (raw as { value?: unknown }).value;
    return typeof v === 'string' && v ? v : null;
  }
  return null;
}

/** Distinct action ids referenced anywhere in the graph (wired device action pins + Action Triggers),
 * so the caller can pre-fetch their field schemas once. */
export function referencedActionIds(nodes: RFNode[], edges: RFEdge[]): string[] {
  const ids = new Set<string>();
  for (const n of nodes) {
    const t = (n.type || '').toLowerCase();
    const props = (n.data?.properties as Record<string, unknown>) || {};
    if (t === 'externaldevice') {
      for (const pin of readDeviceSurface(props).actions) {
        const handle = `${ACTION_PIN_HANDLE_PREFIX}${pin.value}`;
        if (pin.value && edges.some((e) => e.source === n.id && e.sourceHandle === handle)) ids.add(pin.value);
      }
    } else if (t === 'actiontrigger') {
      const a = readValue(props.action);
      if (a) ids.add(a);
    }
  }
  return Array.from(ids).sort();
}

/** A wired device pin that can be simulated (fired) from the editor. */
export interface SimulatablePin {
  kind: 'action' | 'event';
  /** The signal type sent to the simulate endpoint — the action id, or an event's BASE type (the phase
   * suffix like `:started` is stripped, since the simulate seeds a fresh "started" event). */
  type: string;
  label: string;
}

/** Every WIRED device pin (action + event) across the graph's device blocks — the candidates the editor
 * can simulate. An unwired pin starts no run, so it's excluded. */
export function simulatablePins(nodes: RFNode[], edges: RFEdge[]): SimulatablePin[] {
  const pins: SimulatablePin[] = [];
  for (const n of nodes) {
    if ((n.type || '').toLowerCase() !== 'externaldevice') continue;
    const props = (n.data?.properties as Record<string, unknown>) || {};
    const surface = readDeviceSurface(props);
    for (const pin of surface.actions) {
      if (pin.value && edges.some((e) => e.source === n.id && e.sourceHandle === `${ACTION_PIN_HANDLE_PREFIX}${pin.value}`)) {
        pins.push({ kind: 'action', type: pin.value, label: pin.label });
      }
    }
    for (const pin of surface.events) {
      if (pin.value && edges.some((e) => e.source === n.id && e.sourceHandle === `${EVENT_PIN_HANDLE_PREFIX}${pin.value}`)) {
        pins.push({ kind: 'event', type: pin.value.split(':')[0], label: pin.label });
      }
    }
  }
  return pins;
}

/** Friendly camelCased global name for an action's payload — matches the backend `TypeAlias`, so logic
 * reads `customAction.String` rather than `signal.params.String`. (External-device action ids are
 * PascalCase identifiers; this just lower-cases the first char.) */
export function signalAlias(actionId: string): string {
  return actionId.length > 0 ? actionId[0].toLowerCase() + actionId.slice(1) : actionId;
}

/** Friendly label for an action id (the device pin's label if present, else the id itself). */
export function actionLabelById(nodes: RFNode[]): Record<string, string> {
  const labels: Record<string, string> = {};
  for (const n of nodes) {
    if ((n.type || '').toLowerCase() !== 'externaldevice') continue;
    const props = (n.data?.properties as Record<string, unknown>) || {};
    for (const pin of readDeviceSurface(props).actions) {
      if (pin.value && pin.label) labels[pin.value] = pin.label;
    }
  }
  return labels;
}

/**
 * The action id(s) whose inbound signal can reach `nodeId` — found by walking edges UPSTREAM from the
 * node until hitting a device-block action pin (handle `act:<id>`) or an Action Trigger. Intermediate
 * nodes (Condition, Set Variable, …) are traversed through. Usually one id; a union only when several
 * pins converge on the node. Event sources are intentionally ignored here (no static event schema yet).
 */
export function originatingActionIds(nodes: RFNode[], edges: RFEdge[], nodeId: string): string[] {
  const nodeById = new Map(nodes.map((n) => [n.id, n]));
  const result = new Set<string>();
  const visited = new Set<string>([nodeId]);
  const queue = [nodeId];

  while (queue.length > 0) {
    const current = queue.shift()!;
    for (const edge of edges) {
      if (edge.target !== current) continue;
      const source = nodeById.get(edge.source);
      if (!source) continue;
      const sourceType = (source.type || '').toLowerCase();

      if (sourceType === 'externaldevice' && edge.sourceHandle?.startsWith(ACTION_PIN_HANDLE_PREFIX)) {
        result.add(edge.sourceHandle.slice(ACTION_PIN_HANDLE_PREFIX.length));
        continue; // a pin is a terminal source — don't traverse past the device block
      }
      if (sourceType === 'actiontrigger') {
        const a = readValue((source.data?.properties as Record<string, unknown>)?.action);
        if (a) result.add(a);
        continue;
      }
      if (!visited.has(edge.source)) {
        visited.add(edge.source);
        queue.push(edge.source);
      }
    }
  }
  return Array.from(result).sort();
}

/** The event source label(s) whose inbound signal can reach `nodeId` — the device-block event pins
 * (handle `evt:<value>`) and Event Triggers found by the same upstream walk. Events all share one field
 * layout, so only the labels (for the group heading) are collected. */
export function originatingEventLabels(nodes: RFNode[], edges: RFEdge[], nodeId: string): string[] {
  const nodeById = new Map(nodes.map((n) => [n.id, n]));
  const result = new Set<string>();
  const visited = new Set<string>([nodeId]);
  const queue = [nodeId];

  while (queue.length > 0) {
    const current = queue.shift()!;
    for (const edge of edges) {
      if (edge.target !== current) continue;
      const source = nodeById.get(edge.source);
      if (!source) continue;
      const sourceType = (source.type || '').toLowerCase();

      if (sourceType === 'externaldevice' && edge.sourceHandle?.startsWith(EVENT_PIN_HANDLE_PREFIX)) {
        const value = edge.sourceHandle.slice(EVENT_PIN_HANDLE_PREFIX.length);
        const pin = readDeviceSurface((source.data?.properties as Record<string, unknown>) || {}).events.find((p) => p.value === value);
        result.add(pin?.label || value);
        continue;
      }
      if (sourceType === 'eventtrigger') {
        result.add(readValue((source.data?.properties as Record<string, unknown>)?.event) || 'Event');
        continue;
      }
      if (!visited.has(edge.source)) {
        visited.add(edge.source);
        queue.push(edge.source);
      }
    }
  }
  return Array.from(result).sort();
}

export interface SignalFieldGroup {
  /** Stable id: the action id, or '__event' for the shared event group. */
  actionId: string;
  label: string;
  /** The reference prefix before the field key — `signal.<action>` for actions (a payload alias), or
   * `signal.params` for events (no per-type alias). The full ref is `<refPrefix>.<field.key>`. */
  refPrefix: string;
  fields: SignalField[];
}

/** Resolve the scoped signal-field groups to show for `nodeId`: the originating action(s) (each with its
 * own fields) plus a shared event group (common layout) when an event source reaches the node. Empty when
 * the node isn't reachable from a schema-bearing signal source. */
export function signalFieldGroupsForNode(
  nodes: RFNode[],
  edges: RFEdge[],
  nodeId: string,
  fieldsById: ActionFieldsById,
  eventFields: SignalField[] = [],
): SignalFieldGroup[] {
  const labels = actionLabelById(nodes);
  const actionGroups = originatingActionIds(nodes, edges, nodeId)
    .map((actionId) => ({
      actionId,
      label: labels[actionId] || actionId,
      refPrefix: `signal.${signalAlias(actionId)}`,
      fields: fieldsById[actionId] || [],
    }))
    .filter((group) => group.fields.length > 0);

  const eventLabels = originatingEventLabels(nodes, edges, nodeId);
  // All events share one layout, so a single group (labelled by the reaching event(s)) under signal.params.
  const eventGroups: SignalFieldGroup[] = eventLabels.length > 0 && eventFields.length > 0
    ? [{ actionId: '__event', label: eventLabels.join(', '), refPrefix: 'signal.params', fields: eventFields }]
    : [];

  return [...actionGroups, ...eventGroups];
}
