// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useState } from 'react'
import type { Dispatch, SetStateAction } from 'react'
import { api } from '../../utils/api'
import { getErrorMessage } from '../../utils/apiErrors'

type ArchivedEntry = { id: string; name: string }

export interface UseArchivedWorkflowsArgs {
  archived: ArchivedEntry[]
  setArchived: Dispatch<SetStateAction<ArchivedEntry[]>>
  /** Shared dashboard refresh — restore funnels through it to bring the workflow back into the list. */
  handleRefresh: () => Promise<void>
}

/**
 * Archived-workflow panel: the show/hide toggle and per-row in-flight ids, plus restore / permanent
 * delete / purge-all handlers. Extracted from Dashboard.tsx. The `archived` set itself is co-owned by
 * useDashboardData (loaded + refreshed there), so it is passed in along with its setter; restore
 * calls handleRefresh (it re-lists both workflows and archived), while the deletes mutate `archived`
 * optimistically.
 */
export function useArchivedWorkflows(args: UseArchivedWorkflowsArgs) {
  const { archived, setArchived, handleRefresh } = args

  const [showArchived, setShowArchived] = useState(false)
  const [restoringId, setRestoringId] = useState<string | null>(null)
  const [purgingId, setPurgingId] = useState<string | null>(null)
  const [purgingAll, setPurgingAll] = useState(false)

  const handleRestoreWorkflow = async (id: string) => {
    setRestoringId(id)
    try {
      await api.restoreWorkflow(id)
      await handleRefresh() // refreshes the list + the archived set
    } catch (err: unknown) {
      alert(`Failed to restore workflow: ${getErrorMessage(err, 'Unknown error')}`)
    } finally {
      setRestoringId(null)
    }
  }

  const handlePermanentlyDeleteWorkflow = async (id: string, name: string) => {
    if (!window.confirm(`Permanently delete “${name}”? This erases its entire version history and activation log and cannot be undone.`)) return
    setPurgingId(id)
    try {
      await api.permanentlyDeleteWorkflow(id)
      setArchived((prev) => prev.filter((w) => w.id !== id))
    } catch (err: unknown) {
      alert(`Failed to permanently delete workflow: ${getErrorMessage(err, 'Unknown error')}`)
    } finally {
      setPurgingId(null)
    }
  }

  const handlePurgeAllArchived = async () => {
    if (!window.confirm(`Permanently delete all ${archived.length} archived workflow${archived.length === 1 ? '' : 's'}? This erases their entire version history and activation log and cannot be undone.`)) return
    setPurgingAll(true)
    try {
      await api.purgeAllArchivedWorkflows()
      setArchived([])
    } catch (err: unknown) {
      alert(`Failed to delete archived workflows: ${getErrorMessage(err, 'Unknown error')}`)
    } finally {
      setPurgingAll(false)
    }
  }

  return {
    showArchived, setShowArchived, restoringId, purgingId, purgingAll,
    handleRestoreWorkflow, handlePermanentlyDeleteWorkflow, handlePurgeAllArchived,
  }
}
