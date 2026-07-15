import { useEffect, useMemo, useState } from 'react'
import type { Edge, Node as RFNode } from '@xyflow/react'
import { api } from '../../utils/api'
import {
  referencedActionIds,
  signalFieldGroupsForNode,
  type ActionFieldsById,
  type SignalField,
} from '../../node-editor/signalFieldBinding'
import { useSignalFieldStore } from '../../stores/useSignalFieldStore'

const inferType = (description?: string): SignalField['type'] => {
  const d = (description || '').toLowerCase()
  if (d.startsWith('integer') || d.startsWith('number')) return 'number'
  if (d.startsWith('boolean')) return 'boolean'
  return 'string'
}

export interface UseSignalFieldsArgs {
  nodes: RFNode[]
  edges: Edge[]
  selectedNode: RFNode | null
}

export interface SignalFields {
  /** Static field schema (key + type) per referenced external action. */
  actionFieldsById: ActionFieldsById
}

/**
 * Inbound-signal field discovery for the canvas: the static field schema of every referenced
 * external action, the shared event fields, and the per-node scoped groups published to the signal
 * field store for the properties panel / condition reference pickers. Extracted from Canvas.tsx —
 * read-only over the graph; side effects go to the provider (fetch) and the Zustand store (publish).
 */
export function useSignalFields(args: UseSignalFieldsArgs): SignalFields {
  const { nodes, edges, selectedNode } = args

  // Distinct external-action ids referenced by the graph. Their static field schema names the keys
  // the inbound `signal.params` can carry.
  const referencedActions = useMemo(() => referencedActionIds(nodes, edges), [nodes, edges])
  const referencedActionsKey = useMemo(() => referencedActions.join('|'), [referencedActions])

  // Static field schema (key + type) per referenced action, fetched once from the provider. NOT
  // registered as canvas globals — surfaced per-node in the properties panel instead.
  const [actionFieldsById, setActionFieldsById] = useState<ActionFieldsById>({})
  useEffect(() => {
    let cancelled = false
    if (referencedActions.length === 0) {
      setActionFieldsById({})
      return
    }
    ;(async () => {
      const map: ActionFieldsById = {}
      await Promise.all(referencedActions.map(async (action) => {
        try {
          // integrationType is a routing segment the host ignores (loaders resolve by name); keep the
          // generic 'reactor' family so no specific provider is named on the public side.
          const result = await api.loadNodeOptions('reactor', 'reactor.actionFields', { dependsOn: { action } })
          map[action] = result.options
            .filter((opt) => opt.value)
            .map((opt) => ({ key: opt.value, type: inferType(opt.description) }))
        } catch {
          // Provider offline / loader absent → no static keys; the generic signal.params bag still works.
        }
      }))
      if (cancelled) return
      setActionFieldsById(map)
    })()
    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [referencedActionsKey])

  // Events all share one field layout, fetched once — but only when the graph has an inbound source.
  const hasInboundSignalSource = useMemo(
    () => nodes.some((n) => { const t = (n.type || '').toLowerCase(); return t === 'externaldevice' || t === 'eventtrigger' || t === 'actiontrigger' }),
    [nodes],
  )
  const [eventFields, setEventFields] = useState<SignalField[]>([])
  useEffect(() => {
    let cancelled = false
    if (!hasInboundSignalSource) { setEventFields([]); return }
    ;(async () => {
      try {
        const result = await api.loadNodeOptions('reactor', 'reactor.eventFields', {})
        if (cancelled) return
        setEventFields(result.options.filter((opt) => opt.value).map((opt) => ({ key: opt.value, type: inferType(opt.description) })))
      } catch {
        // Provider offline / loader absent → no static event keys; signal.params still works by hand.
      }
    })()
    return () => { cancelled = true }
  }, [hasInboundSignalSource])

  // Scoped signal fields for the currently-selected node, published to the per-node store so the
  // node's editors can read them without threading props through ManifestForm.
  const selectedNodeSignalGroups = useMemo(
    () => (selectedNode ? signalFieldGroupsForNode(nodes, edges, selectedNode.id, actionFieldsById, eventFields) : []),
    [selectedNode, nodes, edges, actionFieldsById, eventFields],
  )
  useEffect(() => {
    useSignalFieldStore.getState().setSignalFields(selectedNode?.id ?? null, selectedNodeSignalGroups)
  }, [selectedNode, selectedNodeSignalGroups])

  return { actionFieldsById }
}
