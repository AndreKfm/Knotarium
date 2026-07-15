import { useState } from 'react'
import { api } from '../../utils/api'
import { getErrorMessage } from '../../utils/apiErrors'
import { mapExecutionStatusLabel, mapStatusFilterToApi, type DashboardStatusFilter } from './useDashboardFilters'
import type { ExecutionInstance } from '../../types'

export interface UseRunSelectionArgs {
  executions: ExecutionInstance[]
  statusFilter: DashboardStatusFilter
  /** Shared dashboard refresh — every delete/cancel funnels through it. */
  handleRefresh: () => Promise<void>
}

/**
 * Operations-Timeline run selection + deletion: multi-select set, select-all over the deletable
 * runs, and cancel / delete-one / delete-selected / delete-all handlers. Extracted from
 * Dashboard.tsx. Its state is never written by the loaders — it just consumes executions /
 * statusFilter / handleRefresh.
 */
export function useRunSelection(args: UseRunSelectionArgs) {
  const { executions, statusFilter, handleRefresh } = args

  const [selectedRuns, setSelectedRuns] = useState<Set<string>>(new Set())
  const [deletingRuns, setDeletingRuns] = useState(false)

  const toggleRunSelection = (id: string) => {
    setSelectedRuns((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id); else next.add(id)
      return next
    })
  }

  // Selectable runs = everything not in-flight (those can't be deleted). Drives the "Select all" control.
  const selectableRunIds = executions
    .filter((e) => { const l = mapExecutionStatusLabel(e.status); return l !== 'Running' && l !== 'Pending' })
    .map((e) => e.id)
  const allRunsSelected = selectableRunIds.length > 0 && selectableRunIds.every((id) => selectedRuns.has(id))
  const someRunsSelected = selectedRuns.size > 0 && !allRunsSelected
  const toggleSelectAllRuns = () => setSelectedRuns(allRunsSelected ? new Set() : new Set(selectableRunIds))
  const clearSelection = () => setSelectedRuns(new Set())

  const afterRunsDeleted = async () => {
    setSelectedRuns(new Set())
    await handleRefresh()
  }

  const handleCancelRun = async (id: string) => {
    if (!window.confirm('Stop this run? It will be marked Cancelled (then you can delete it).')) return
    setDeletingRuns(true)
    try {
      await api.cancelExecution(id)
      await handleRefresh()
    } catch (err: unknown) {
      alert(`Failed to stop run: ${getErrorMessage(err, 'Unknown error')}`)
    } finally {
      setDeletingRuns(false)
    }
  }

  const handleDeleteRun = async (id: string) => {
    setDeletingRuns(true)
    try {
      await api.deleteExecution(id)
      await afterRunsDeleted()
    } catch (err: unknown) {
      alert(`Failed to delete run: ${getErrorMessage(err, 'Unknown error')}`)
    } finally {
      setDeletingRuns(false)
    }
  }

  const handleDeleteSelectedRuns = async () => {
    const ids = [...selectedRuns]
    if (ids.length === 0) return
    if (!window.confirm(`Delete ${ids.length} selected run${ids.length === 1 ? '' : 's'}? This can't be undone.`)) return
    setDeletingRuns(true)
    try {
      await api.bulkDeleteExecutions({ ids })
      await afterRunsDeleted()
    } catch (err: unknown) {
      alert(`Failed to delete runs: ${getErrorMessage(err, 'Unknown error')}`)
    } finally {
      setDeletingRuns(false)
    }
  }

  const handleDeleteAllRuns = async () => {
    const scope = statusFilter === 'All' ? 'all runs' : `all ${statusFilter} runs`
    if (!window.confirm(`Delete ${scope}? In-progress runs are kept. This can't be undone.`)) return
    setDeletingRuns(true)
    try {
      await api.bulkDeleteExecutions({ all: true, status: mapStatusFilterToApi(statusFilter) })
      await afterRunsDeleted()
    } catch (err: unknown) {
      alert(`Failed to delete runs: ${getErrorMessage(err, 'Unknown error')}`)
    } finally {
      setDeletingRuns(false)
    }
  }

  return {
    selectedRuns, deletingRuns,
    toggleRunSelection, selectableRunIds, allRunsSelected, someRunsSelected, toggleSelectAllRuns, clearSelection,
    handleCancelRun, handleDeleteRun, handleDeleteSelectedRuns, handleDeleteAllRuns,
  }
}
