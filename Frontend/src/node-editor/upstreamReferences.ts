/**
 * Schema-driven expression references: for a selected node, enumerate the data its UPSTREAM nodes expose,
 * as ready-to-insert `{{ $node.<id>.output.<field> }}` expressions. Powers the properties-panel reference
 * picker so a user can discover and insert typed references instead of hand-writing node ids.
 *
 * Data source: each node type's output ports (from the manifest, via NodePackageMetadata.outputHandles)
 * plus a small set of known dynamic fields the runtime adds but the manifest doesn't declare (the loop's
 * index/item). Control/branch ports carry no referenceable payload and are filtered out. As manifests
 * start declaring structured output Fields, this list widens automatically.
 */

export interface ReferenceField {
  /** The output field/port name, e.g. "result", "index". */
  field: string;
  /** The full expression to insert, e.g. "{{ $node.loop-1.output.index }}". */
  expr: string;
}

export interface ReferenceGroup {
  nodeId: string;
  nodeType: string;
  /** Display label for the producing node (its display name). */
  label: string;
  fields: ReferenceField[];
}

interface RefNodeLike {
  id: string;
  type?: string;
}
interface RefEdgeLike {
  source: string;
  target: string;
}
interface NodeMetaLike {
  displayName: string;
  outputHandles: string[];
}

/** Control / branch ports that route flow but carry no referenceable data payload. */
const CONTROL_PORTS = new Set(['start', 'true', 'false']);

/** Dynamic output fields the runtime adds per node type but the manifest doesn't declare. */
const DYNAMIC_FIELDS: Record<string, string[]> = {
  forLoop: ['index', 'item'],
};

/** The referenceable data fields a node of the given type exposes (data ports + known dynamic fields). */
export function dataFieldsFor(nodeType: string, outputHandles: readonly string[]): string[] {
  if (DYNAMIC_FIELDS[nodeType]) return [...DYNAMIC_FIELDS[nodeType]];
  const seen = new Set<string>();
  const fields: string[] = [];
  for (const handle of outputHandles) {
    if (CONTROL_PORTS.has(handle) || seen.has(handle)) continue;
    seen.add(handle);
    fields.push(handle);
  }
  return fields;
}

/** All ancestor node ids of `selectedNodeId` (transitive upstream), excluding the node itself. */
export function upstreamNodeIds(
  selectedNodeId: string,
  edges: readonly RefEdgeLike[],
): string[] {
  const byTarget = new Map<string, string[]>();
  for (const e of edges) {
    (byTarget.get(e.target) ?? byTarget.set(e.target, []).get(e.target)!).push(e.source);
  }
  const result = new Set<string>();
  const queue = [selectedNodeId];
  const visited = new Set<string>([selectedNodeId]);
  while (queue.length > 0) {
    const current = queue.shift()!;
    for (const source of byTarget.get(current) ?? []) {
      result.add(source);
      if (!visited.has(source)) {
        visited.add(source);
        queue.push(source);
      }
    }
  }
  result.delete(selectedNodeId);
  return [...result];
}

/**
 * Reference groups for the properties-panel picker: one group per upstream node that exposes data, each
 * with its insertable `{{ $node.<id>.output.<field> }}` expressions. Ordered by node id for stability.
 */
export function upstreamReferenceGroups(
  selectedNodeId: string | null,
  nodes: readonly RefNodeLike[],
  edges: readonly RefEdgeLike[],
  metadata: Record<string, NodeMetaLike | undefined>,
): ReferenceGroup[] {
  if (!selectedNodeId) return [];
  const nodeById = new Map(nodes.map((n) => [n.id, n]));
  const ancestorIds = upstreamNodeIds(selectedNodeId, edges).sort();

  const groups: ReferenceGroup[] = [];
  for (const id of ancestorIds) {
    const node = nodeById.get(id);
    if (!node?.type) continue;
    const meta = metadata[node.type];
    const fields = dataFieldsFor(node.type, meta?.outputHandles ?? []);
    if (fields.length === 0) continue;
    groups.push({
      nodeId: id,
      nodeType: node.type,
      label: meta?.displayName || node.type,
      fields: fields.map((field) => ({ field, expr: `{{ $node.${id}.output.${field} }}` })),
    });
  }
  return groups;
}
