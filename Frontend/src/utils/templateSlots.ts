import type { Node as RFNode } from '@xyflow/react';

// Slot-token handling for INSERTING a template into an open workflow. The open workflow may already
// carry its own `slot:<key>` placeholders; an inserted template with the same key but a different
// intended credential would otherwise be conflated (binding once hits both). So before inserting we
// rename the incoming template's colliding slot keys (camera-api → camera-api-2), keeping the two
// namespaces distinct. Pure string rewriting over the already-plain node properties.

const SLOT_PREFIX = 'slot:';

function collectFromValue(value: unknown, into: Set<string>): void {
  if (typeof value === 'string') {
    if (value.startsWith(SLOT_PREFIX)) into.add(value.slice(SLOT_PREFIX.length));
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) collectFromValue(item, into);
    return;
  }
  if (value && typeof value === 'object') {
    for (const v of Object.values(value as Record<string, unknown>)) collectFromValue(v, into);
  }
}

/** Every slot key referenced as a `slot:<key>` placeholder across the given nodes' properties. */
export function collectSlotNames(nodes: RFNode[]): Set<string> {
  const names = new Set<string>();
  for (const node of nodes) {
    const props = (node.data as { properties?: unknown } | undefined)?.properties;
    collectFromValue(props, names);
  }
  return names;
}

function nextFreeName(base: string, used: Set<string>): string {
  for (let n = 2; ; n++) {
    const candidate = `${base}-${n}`;
    if (!used.has(candidate)) return candidate;
  }
}

function rewriteValue(value: unknown, map: Map<string, string>): unknown {
  if (typeof value === 'string') {
    if (value.startsWith(SLOT_PREFIX)) {
      const key = value.slice(SLOT_PREFIX.length);
      const renamed = map.get(key);
      if (renamed) return SLOT_PREFIX + renamed;
    }
    return value;
  }
  if (Array.isArray(value)) {
    return value.map((item) => rewriteValue(item, map));
  }
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) out[k] = rewriteValue(v, map);
    return out;
  }
  return value;
}

export interface SlotRewriteResult {
  nodes: RFNode[];
  renamed: Array<{ from: string; to: string }>;
}

/**
 * Renames the incoming template's slot keys that collide with `existing` (the open workflow's slot
 * keys), so the two never bind together by accident. Non-colliding keys are untouched.
 */
export function rewriteSlotsForInsert(incoming: RFNode[], existing: Set<string>): SlotRewriteResult {
  const incomingNames = [...collectSlotNames(incoming)].sort();
  const used = new Set(existing);
  const map = new Map<string, string>();

  for (const name of incomingNames) {
    if (used.has(name)) {
      const renamed = nextFreeName(name, used);
      map.set(name, renamed);
      used.add(renamed);
    } else {
      used.add(name);
    }
  }

  if (map.size === 0) {
    return { nodes: incoming, renamed: [] };
  }

  const nodes = incoming.map((node) => ({
    ...node,
    data: { ...(node.data as object), properties: rewriteValue((node.data as { properties?: unknown }).properties, map) },
  })) as RFNode[];

  return { nodes, renamed: [...map].map(([from, to]) => ({ from, to })) };
}
