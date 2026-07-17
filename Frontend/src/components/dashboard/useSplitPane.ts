// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useRef, useState } from 'react'
import type { Dispatch, SetStateAction } from 'react'

const SPLIT_STORAGE_KEY = 'kg-dashboard-split'
/** Minimum px width of either dashboard panel (also used by the grid template). */
export const SPLIT_MIN_PANEL_PX = 360
/** Default left-panel share of the row (also the double-click reset target). */
export const SPLIT_DEFAULT_FRAC = 0.48

export interface SplitPane {
  splitRowRef: React.RefObject<HTMLDivElement | null>
  /** Left panel's share of the row (0..1), persisted to localStorage. */
  splitFrac: number
  setSplitFrac: Dispatch<SetStateAction<number>>
  draggingSplit: boolean
  setDraggingSplit: Dispatch<SetStateAction<boolean>>
}

/**
 * Adjustable width of the two dashboard panels. `splitFrac` is the left panel's share of the row;
 * persisted to localStorage and clamped on drag so neither panel drops below SPLIT_MIN_PANEL_PX.
 * Extracted from Dashboard.tsx.
 */
export function useSplitPane(): SplitPane {
  const splitRowRef = useRef<HTMLDivElement | null>(null)
  const [splitFrac, setSplitFrac] = useState<number>(() => {
    const stored = Number(localStorage.getItem(SPLIT_STORAGE_KEY))
    return Number.isFinite(stored) && stored >= 0.15 && stored <= 0.85 ? stored : SPLIT_DEFAULT_FRAC
  })
  const [draggingSplit, setDraggingSplit] = useState(false)

  useEffect(() => {
    if (!draggingSplit) return
    const onMove = (e: MouseEvent) => {
      const row = splitRowRef.current
      if (!row) return
      const rect = row.getBoundingClientRect()
      if (rect.width <= 0) return
      const minFrac = SPLIT_MIN_PANEL_PX / rect.width
      const raw = (e.clientX - rect.left) / rect.width
      setSplitFrac(Math.min(1 - minFrac, Math.max(minFrac, raw)))
    }
    const stop = () => setDraggingSplit(false)
    window.addEventListener('mousemove', onMove)
    window.addEventListener('mouseup', stop)
    // Suppress text selection + hold the resize cursor for the whole drag.
    const prevSelect = document.body.style.userSelect
    const prevCursor = document.body.style.cursor
    document.body.style.userSelect = 'none'
    document.body.style.cursor = 'col-resize'
    return () => {
      window.removeEventListener('mousemove', onMove)
      window.removeEventListener('mouseup', stop)
      document.body.style.userSelect = prevSelect
      document.body.style.cursor = prevCursor
    }
  }, [draggingSplit])

  useEffect(() => {
    localStorage.setItem(SPLIT_STORAGE_KEY, String(splitFrac))
  }, [splitFrac])

  return { splitRowRef, splitFrac, setSplitFrac, draggingSplit, setDraggingSplit }
}
