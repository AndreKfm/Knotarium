// Fan-in points that accept MULTIPLE incoming branches instead of the usual single input:
// a container's 'end' loopback (parallelForEach / forLoop body converging back) and the join
// node's input (wait-for-all). Every other input still replaces its existing wire.
export function acceptsMultipleIncoming(
  targetId: string | null | undefined,
  targetHandle: string | null | undefined,
  nodes: { id: string; type?: string }[],
): boolean {
  if ((targetHandle ?? '') === 'end') return true
  return nodes.find((n) => n.id === targetId)?.type === 'join'
}
