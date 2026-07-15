import { useCallback, useRef } from 'react'
import type { Dispatch, RefObject, SetStateAction } from 'react'
import type { Edge, Node as RFNode } from '@xyflow/react'
import { cloneSubgraph } from '../../node-editor/clipboard'
import { createNodeId } from '../../node-editor/nodeFactory'

export interface UseCanvasClipboardArgs {
  nodesRef: RefObject<RFNode[]>
  edgesRef: RefObject<Edge[]>
  recordUndo: () => void
  snapIfEnabled: (p: { x: number; y: number }) => { x: number; y: number }
  setNodes: Dispatch<SetStateAction<RFNode[]>>
  setEdges: Dispatch<SetStateAction<Edge[]>>
  setSelectedNode: Dispatch<SetStateAction<RFNode | null>>
  setSelectedEdge: Dispatch<SetStateAction<Edge | null>>
}

export interface CanvasClipboard {
  /** Copy the current selection to the clipboard; returns false if nothing was selected. */
  copySelection: () => boolean
  /** Paste the clipboard as a fresh clone (offset per repeat); returns false if the clipboard is empty. */
  pasteClipboard: () => boolean
  /** Duplicate the current selection in place (single offset); returns false if nothing was selected. */
  duplicateSelection: () => boolean
}

/**
 * Copy / paste / duplicate of the selected subgraph. Extracted from Canvas.tsx. Builds on
 * cloneSubgraph (id-remap + edge rewire) and records a single undo step per placement.
 */
export function useCanvasClipboard(args: UseCanvasClipboardArgs): CanvasClipboard {
  const { nodesRef, edgesRef, recordUndo, snapIfEnabled, setNodes, setEdges, setSelectedNode, setSelectedEdge } = args
  const clipboardRef = useRef<{ nodes: RFNode[]; edges: Edge[] } | null>(null)
  const pasteCountRef = useRef(0)

  // Append a freshly-cloned subgraph (new ids, offset) and leave it selected. Shared by
  // paste (from clipboard) and duplicate (direct from the current selection).
  const appendClones = useCallback(
    (sourceNodes: RFNode[], sourceEdges: Edge[], offset: number) => {
      if (sourceNodes.length === 0) return
      const { nodes: cloned, edges: cloneEdges } = cloneSubgraph(sourceNodes, sourceEdges, {
        newId: (type) => createNodeId(type ?? 'node'),
        offset: { x: offset, y: offset },
      })
      const clones = cloned.map((n) => ({ ...n, position: snapIfEnabled(n.position) }))
      recordUndo()
      setNodes((nds) => [...nds.map((n) => ({ ...n, selected: false })), ...clones])
      setEdges((eds) => [...eds, ...cloneEdges])
      setSelectedNode(clones[0] ?? null)
      setSelectedEdge(null)
    },
    [recordUndo, setNodes, setEdges, setSelectedNode, setSelectedEdge, snapIfEnabled],
  )

  const copySelection = useCallback(() => {
    const selected = nodesRef.current.filter((n) => n.selected)
    if (selected.length === 0) return false
    const ids = new Set(selected.map((n) => n.id))
    const internalEdges = edgesRef.current.filter((e) => ids.has(e.source) && ids.has(e.target))
    clipboardRef.current = { nodes: structuredClone(selected), edges: structuredClone(internalEdges) }
    pasteCountRef.current = 0
    return true
  }, [nodesRef, edgesRef])

  const pasteClipboard = useCallback(() => {
    const clip = clipboardRef.current
    if (!clip || clip.nodes.length === 0) return false
    pasteCountRef.current += 1
    appendClones(clip.nodes, clip.edges, 40 * pasteCountRef.current)
    return true
  }, [appendClones])

  const duplicateSelection = useCallback(() => {
    const selected = nodesRef.current.filter((n) => n.selected)
    if (selected.length === 0) return false
    const ids = new Set(selected.map((n) => n.id))
    const internalEdges = edgesRef.current.filter((e) => ids.has(e.source) && ids.has(e.target))
    appendClones(selected, internalEdges, 40)
    return true
  }, [nodesRef, edgesRef, appendClones])

  return { copySelection, pasteClipboard, duplicateSelection }
}
