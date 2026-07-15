import { useCallback, useEffect, useMemo, useState } from 'react'
import type { Dispatch, RefObject, SetStateAction } from 'react'
import type { Edge, InternalNode, Node as RFNode, SetCenter } from '@xyflow/react'
import { api } from '../../utils/api'
import { DEFAULT_NODE_WIDTH } from '../../node-editor/canvasGeometry'
import { mergeDiagnostics, resolveDiagnosticFocus, countBySeverity } from '../../utils/diagnosticsNavigation'
import type { CompilationDiagnostic } from '../../types'

export interface UseDiagnosticsArgs {
  currentId: string
  /** Meaningful-shape signature (ignores selection/position churn) that gates the live validate pass. */
  currentSignature: string
  nodes: RFNode[]
  edges: Edge[]
  nodesRef: RefObject<RFNode[]>
  edgesRef: RefObject<Edge[]>
  getInternalNode: (id: string) => InternalNode<RFNode> | undefined
  setCenter: SetCenter
  setNodes: Dispatch<SetStateAction<RFNode[]>>
  setEdges: Dispatch<SetStateAction<Edge[]>>
  setSelectedNode: Dispatch<SetStateAction<RFNode | null>>
  setSelectedEdge: Dispatch<SetStateAction<Edge | null>>
}

export interface Diagnostics {
  /** Blocking publish/run failure overlay diagnostics. */
  diagnostics: CompilationDiagnostic[]
  setDiagnostics: Dispatch<SetStateAction<CompilationDiagnostic[]>>
  /** Non-blocking edge type-mismatch warnings, fetched live and used to colour offending edges. */
  edgeDiagnostics: CompilationDiagnostic[]
  diagnosticsCollapsed: boolean
  setDiagnosticsCollapsed: Dispatch<SetStateAction<boolean>>
  /** Merged blocking + edge diagnostics for the dockable panel. */
  panelDiagnostics: CompilationDiagnostic[]
  /** Error-severity count, surfaced on the Save & Publish button. */
  blockingErrorCount: number
  /** Centre the canvas on the node/edge a diagnostic points at, and select it. */
  focusDiagnostic: (diagnostic: CompilationDiagnostic) => void
}

/**
 * Compilation/validation diagnostics for the canvas: the live debounced edge-validate pass, the
 * blocking failure list written by save/run, the merged panel view, and click-to-locate focus.
 * Extracted from Canvas.tsx. Reads the graph and drives selection/viewport but never mutates
 * node/edge structure.
 */
export function useDiagnostics(args: UseDiagnosticsArgs): Diagnostics {
  const {
    currentId, currentSignature, nodes, edges, nodesRef, edgesRef,
    getInternalNode, setCenter, setNodes, setEdges, setSelectedNode, setSelectedEdge,
  } = args

  const [diagnostics, setDiagnostics] = useState<CompilationDiagnostic[]>([])
  const [edgeDiagnostics, setEdgeDiagnostics] = useState<CompilationDiagnostic[]>([])
  const [diagnosticsCollapsed, setDiagnosticsCollapsed] = useState(false)

  // Live, debounced compile pass so the editor can mark type-mismatch edges as you wire the graph.
  // Keyed off currentSignature (which ignores selection/position churn), so it only re-runs when
  // the graph's meaningful shape changes.
  useEffect(() => {
    if (!currentId || edges.length === 0) {
      setEdgeDiagnostics([])
      return
    }

    let cancelled = false
    const handle = setTimeout(() => {
      api.validateWorkflow(currentId, nodes, edges)
        .then((result) => { if (!cancelled) setEdgeDiagnostics(result) })
        .catch(() => { if (!cancelled) setEdgeDiagnostics([]) })
    }, 500)

    return () => { cancelled = true; clearTimeout(handle) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentId, currentSignature])

  const panelDiagnostics = useMemo(
    () => mergeDiagnostics(diagnostics, edgeDiagnostics),
    [diagnostics, edgeDiagnostics],
  )
  const blockingErrorCount = useMemo(() => countBySeverity(panelDiagnostics).Error, [panelDiagnostics])

  const focusDiagnostic = useCallback(
    (diagnostic: CompilationDiagnostic) => {
      const focus = resolveDiagnosticFocus(diagnostic, edgesRef.current)
      if (!focus) return
      const centerOf = (nodeId: string): { x: number; y: number } | null => {
        const internal = getInternalNode(nodeId)
        if (!internal) return null
        const w = internal.measured?.width ?? DEFAULT_NODE_WIDTH
        const h = internal.measured?.height ?? 80
        const pos = internal.internals?.positionAbsolute ?? internal.position
        return { x: pos.x + w / 2, y: pos.y + h / 2 }
      }
      if (focus.kind === 'node') {
        const c = centerOf(focus.nodeId)
        if (!c) return
        setCenter(c.x, c.y, { zoom: 1.2, duration: 400 })
        setNodes((nds) => nds.map((n) => ({ ...n, selected: n.id === focus.nodeId })))
        setSelectedNode(nodesRef.current.find((n) => n.id === focus.nodeId) ?? null)
        setSelectedEdge(null)
      } else {
        const pts = [centerOf(focus.source), centerOf(focus.target)].filter(
          (p): p is { x: number; y: number } => p !== null,
        )
        if (pts.length === 0) return
        const x = pts.reduce((s, p) => s + p.x, 0) / pts.length
        const y = pts.reduce((s, p) => s + p.y, 0) / pts.length
        setCenter(x, y, { zoom: 1.2, duration: 400 })
        setEdges((eds) =>
          eds.map((e) => ({ ...e, selected: e.source === focus.source && e.target === focus.target })),
        )
        setSelectedNode(null)
      }
    },
    [getInternalNode, setCenter, setNodes, setEdges, setSelectedNode, setSelectedEdge, edgesRef, nodesRef],
  )

  return {
    diagnostics, setDiagnostics,
    edgeDiagnostics,
    diagnosticsCollapsed, setDiagnosticsCollapsed,
    panelDiagnostics, blockingErrorCount, focusDiagnostic,
  }
}
