// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react'
import { api } from '../../utils/api'
import type { SystemActivityEntry } from '../../types'

export interface SystemEchoes {
  /** Recent auto-filtered external-system activity (self-echo "skipped" entries). */
  filteredEchoes: SystemActivityEntry[]
  echoesCollapsed: boolean
  toggleEchoesCollapsed: () => void
  clearingEchoes: boolean
  handleClearEchoes: () => Promise<void>
}

/**
 * The dashboard's auto-filtered external-system activity ("echoes"): polled from the provider every
 * 4s, a persisted collapse toggle, and an on-demand buffer clear. Extracted from Dashboard.tsx.
 * Absent provider (404) → stays empty and the section simply doesn't render.
 */
export function useSystemEchoes(): SystemEchoes {
  const [filteredEchoes, setFilteredEchoes] = useState<SystemActivityEntry[]>([])
  useEffect(() => {
    let cancelled = false
    const load = () => {
      api.getExternalSystem()
        .then((sys) => { if (!cancelled) setFilteredEchoes(sys.diagnostics?.recentActivity ?? []) })
        .catch(() => { if (!cancelled) setFilteredEchoes([]) })
    }
    load()
    const timer = setInterval(load, 4000)
    return () => { cancelled = true; clearInterval(timer) }
  }, [])

  const [echoesCollapsed, setEchoesCollapsed] = useState<boolean>(() => {
    try { return localStorage.getItem('kg-echoes-collapsed') === '1' } catch { return false }
  })
  const toggleEchoesCollapsed = () => setEchoesCollapsed((v) => {
    const next = !v
    try { localStorage.setItem('kg-echoes-collapsed', next ? '1' : '0') } catch { /* ignore */ }
    return next
  })

  const [clearingEchoes, setClearingEchoes] = useState(false)
  const handleClearEchoes = async () => {
    setClearingEchoes(true)
    try {
      await api.clearExternalSystemDiagnostics()
      setFilteredEchoes([])
    } catch { /* non-fatal: the buffer resets on host restart anyway */ }
    finally { setClearingEchoes(false) }
  }

  return { filteredEchoes, echoesCollapsed, toggleEchoesCollapsed, clearingEchoes, handleClearEchoes }
}
