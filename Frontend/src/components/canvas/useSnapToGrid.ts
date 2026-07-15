import { useCallback, useEffect, useRef, useState } from 'react'
import { snapPointToGrid } from '../../node-editor/autoLayout'

/** Snap-to-grid step (px) — matches the Background dot gap so nodes land on dots. */
export const SNAP_GRID_SIZE = 24

export interface SnapToGrid {
  snapEnabled: boolean
  setSnapEnabled: React.Dispatch<React.SetStateAction<boolean>>
  /** Snap a point to the grid when snapping is on; pass it through unchanged otherwise. */
  snapIfEnabled: (p: { x: number; y: number }) => { x: number; y: number }
}

/**
 * Snap-to-grid toggle (grid matches the 24px Background dot gap). Extracted from Canvas.tsx.
 * React Flow snaps manual drags itself via the `snapToGrid` prop; `snapIfEnabled` is for the
 * programmatic placement paths (drop/paste/tidy) that bypass RF's own snapping.
 */
export function useSnapToGrid(): SnapToGrid {
  const [snapEnabled, setSnapEnabled] = useState(false)
  // Ref mirror so placement callbacks (drop/paste/tidy) can snap without being
  // re-created on every toggle.
  const snapEnabledRef = useRef(snapEnabled)
  useEffect(() => { snapEnabledRef.current = snapEnabled }, [snapEnabled])
  const snapIfEnabled = useCallback(
    (p: { x: number; y: number }) => (snapEnabledRef.current ? snapPointToGrid(p, SNAP_GRID_SIZE) : p),
    [],
  )
  return { snapEnabled, setSnapEnabled, snapIfEnabled }
}
