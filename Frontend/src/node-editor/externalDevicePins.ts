// Pin model for the generic `externalDevice` node (firewall-clean — no provider names here).
//
// A device block is a pure INBOUND surface: ticked external signals become output (source) pins you
// react to — selected EVENTS and selected INCOMING ACTIONS both render as source pins on the right
// edge. The block has no input pins; sending a command to the device is the separate Fire Action node.
// The selections are authored with the multi-select resourceLocator fields `eventPins`/`actionPins`,
// which persist as `DynamicOptionMultiValue` ({ mode, items: [{ value, label }] }). The pins are
// derived from `node.Properties` exactly like subflow I/O — the manifest declares no static ports.

/** A single connectable pin derived from a selected signal. */
export interface DevicePin {
  /** The signal type (event type or action name). Stable id source. */
  value: string;
  /** Human label for the card; falls back to `value`. */
  label: string;
}

export const EVENT_PIN_HANDLE_PREFIX = 'evt:';
export const ACTION_PIN_HANDLE_PREFIX = 'act:';

/** React Flow handle id for an event-output pin. */
export function eventPinHandleId(value: string): string {
  return `${EVENT_PIN_HANDLE_PREFIX}${value}`;
}

/** React Flow handle id for an action-input pin. */
export function actionPinHandleId(value: string): string {
  return `${ACTION_PIN_HANDLE_PREFIX}${value}`;
}

/**
 * Normalize a persisted multi-select pin selection into a deduped list of pins.
 * Tolerates every shape the resourceLocator multi field (and its legacy forms) can write:
 *   - DynamicOptionMultiValue: { mode, items: [{ value, label? }] }
 *   - a bare array of strings or { value, label } objects
 *   - a single string / { value, label } object
 *   - null / undefined / '' → []
 */
export function readDevicePins(raw: unknown): DevicePin[] {
  const out: DevicePin[] = [];
  const seen = new Set<string>();
  const push = (value: unknown, label?: unknown) => {
    if (typeof value !== 'string') return;
    const v = value.trim();
    if (!v || seen.has(v)) return;
    seen.add(v);
    out.push({ value: v, label: typeof label === 'string' && label.trim() ? label : v });
  };

  if (raw == null || raw === '') return out;

  // { mode, items: [...] }
  if (typeof raw === 'object' && !Array.isArray(raw) && Array.isArray((raw as { items?: unknown }).items)) {
    for (const item of (raw as { items: unknown[] }).items) {
      if (typeof item === 'string') push(item);
      else if (item && typeof item === 'object') push((item as Record<string, unknown>).value, (item as Record<string, unknown>).label);
    }
    return out;
  }

  // bare array
  if (Array.isArray(raw)) {
    for (const item of raw) {
      if (typeof item === 'string') push(item);
      else if (item && typeof item === 'object') push((item as Record<string, unknown>).value, (item as Record<string, unknown>).label);
    }
    return out;
  }

  // single string / object
  if (typeof raw === 'string') push(raw);
  else if (typeof raw === 'object') push((raw as Record<string, unknown>).value, (raw as Record<string, unknown>).label);
  return out;
}

/** Convenience: read both pin lists off a node's properties bag. */
export function readDeviceSurface(properties: Record<string, unknown> | null | undefined): {
  events: DevicePin[];
  actions: DevicePin[];
} {
  const p = properties ?? {};
  return {
    events: readDevicePins(p.eventPins),
    actions: readDevicePins(p.actionPins),
  };
}

/**
 * The set of valid handle ids a device node currently exposes, for edge-pruning when the
 * authored pin selection changes (a removed pin's wires must be dropped, same as subflow I/O).
 */
export function deviceHandleIds(properties: Record<string, unknown> | null | undefined): {
  sourceHandles: Set<string>;
  targetHandles: Set<string>;
} {
  const { events, actions } = readDeviceSurface(properties);
  // The device block is a pure inbound surface: both events and incoming actions are SOURCE pins you
  // react to. It exposes no input pins (sending a command is the separate Fire Action node).
  return {
    sourceHandles: new Set([
      ...events.map((p) => eventPinHandleId(p.value)),
      ...actions.map((p) => actionPinHandleId(p.value)),
    ]),
    targetHandles: new Set<string>(),
  };
}
