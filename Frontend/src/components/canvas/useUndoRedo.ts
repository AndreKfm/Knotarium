import { useCallback, useRef } from 'react'
import type { Dispatch, RefObject, SetStateAction } from 'react'
import type { Edge, Node as RFNode } from '@xyflow/react'
import {
  createUndoHistory,
  record as recordHistory,
  applyUndo,
  applyRedo,
  type UndoHistory,
} from '../../node-editor/undoHistory'

/** A point-in-time clone of the graph, stored in history so later edits can't mutate it. */
export type CanvasSnapshot = { nodes: RFNode[]; edges: Edge[] }

export interface UseUndoRedoArgs {
  nodesRef: RefObject<RFNode[]>
  edgesRef: RefObject<Edge[]>
  setNodes: Dispatch<SetStateAction<RFNode[]>>
  setEdges: Dispatch<SetStateAction<Edge[]>>
  setSelectedNode: Dispatch<SetStateAction<RFNode | null>>
  setSelectedEdge: Dispatch<SetStateAction<Edge | null>>
}

export interface UndoRedo {
  /** Clone the current graph into a snapshot. */
  snapshotNow: () => CanvasSnapshot
  /** Record a pre-change snapshot before a structural edit (once per user gesture). */
  recordUndo: () => void
  doUndo: () => void
  doRedo: () => void
  /** Start a fresh history (e.g. when a different workflow is loaded). */
  resetHistory: () => void
  /** Push a pre-captured snapshot directly — for drag/pickup sites that snapshot before the gesture. */
  recordSnapshot: (snap: CanvasSnapshot) => void
}

/**
 * Undo/redo for the canvas. The live nodes/edges are the "present"; a ref-held history holds
 * snapshots to restore. Extracted from Canvas.tsx. Compound operations (e.g. edge-splice) call
 * recordUndo() once so they collapse into a single undo step.
 */
export function useUndoRedo(args: UseUndoRedoArgs): UndoRedo {
  const { nodesRef, edgesRef, setNodes, setEdges, setSelectedNode, setSelectedEdge } = args
  const historyRef = useRef<UndoHistory<CanvasSnapshot>>(createUndoHistory<CanvasSnapshot>())

  const snapshotNow = useCallback((): CanvasSnapshot => ({
    nodes: structuredClone(nodesRef.current),
    edges: structuredClone(edgesRef.current),
  }), [nodesRef, edgesRef])

  const recordUndo = useCallback(() => {
    historyRef.current = recordHistory(historyRef.current, snapshotNow())
  }, [snapshotNow])

  const applySnapshot = useCallback((s: CanvasSnapshot) => {
    setNodes(structuredClone(s.nodes))
    setEdges(structuredClone(s.edges))
    setSelectedNode(null)
    setSelectedEdge(null)
  }, [setNodes, setEdges, setSelectedNode, setSelectedEdge])

  const doUndo = useCallback(() => {
    const r = applyUndo(historyRef.current, snapshotNow())
    if (!r) return
    historyRef.current = r.history
    applySnapshot(r.restored)
  }, [snapshotNow, applySnapshot])

  const doRedo = useCallback(() => {
    const r = applyRedo(historyRef.current, snapshotNow())
    if (!r) return
    historyRef.current = r.history
    applySnapshot(r.restored)
  }, [snapshotNow, applySnapshot])

  const resetHistory = useCallback(() => {
    historyRef.current = createUndoHistory<CanvasSnapshot>()
  }, [])

  const recordSnapshot = useCallback((snap: CanvasSnapshot) => {
    historyRef.current = recordHistory(historyRef.current, snap)
  }, [])

  return { snapshotNow, recordUndo, doUndo, doRedo, resetHistory, recordSnapshot }
}
