import { useEffect, useState } from 'react'
import { api } from '../../utils/api'
import { replaceIfChanged } from '../../utils/stableState'
import { isApiError, getErrorMessage } from '../../utils/apiErrors'
import { mapStatusFilterToApi, type DashboardStatusFilter } from './useDashboardFilters'
import type { ExecutionInstance, WorkflowDefinition, WorkflowGroup } from '../../types'

export interface UseDashboardDataArgs {
  statusFilter: DashboardStatusFilter
  searchFilter: string
}

/**
 * The dashboard's data-loading spine: workflows, executions, stats runs, groups (+etag), the
 * archived set, and loading/error — loaded by three polling effects and refreshed as a unit by
 * handleRefresh (which every mutation cluster funnels through). Group mutations live here too since
 * they own groups/etag. Extracted from Dashboard.tsx. statusFilter/searchFilter come from
 * useDashboardFilters and drive the executions loader.
 */
export function useDashboardData(args: UseDashboardDataArgs) {
  const { statusFilter, searchFilter } = args

  const [workflows, setWorkflows] = useState<WorkflowDefinition[]>([])
  const [executions, setExecutions] = useState<ExecutionInstance[]>([])
  const [statsRuns, setStatsRuns] = useState<ExecutionInstance[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [archived, setArchived] = useState<{ id: string; name: string }[]>([])
  const [groups, setGroups] = useState<WorkflowGroup[]>([])
  const [etag, setEtag] = useState<string>('')

  useEffect(() => {
    let isCancelled = false

    async function loadWorkflowsAndGroups() {
      try {
        const [workflowList, groupsResult] = await Promise.all([
          api.getWorkflows(),
          api.getGroups(),
        ])
        if (!isCancelled) {
          setWorkflows(workflowList)
          setGroups(groupsResult.container.groups || [])
          setEtag(groupsResult.etag)
        }
      } catch (err: unknown) {
        if (!isCancelled) {
          setError(getErrorMessage(err, 'Failed to fetch baseline data from the server.'))
        }
      }
    }

    loadWorkflowsAndGroups()
    api.listArchivedWorkflows().then((a) => { if (!isCancelled) setArchived(a) }).catch(() => { /* non-fatal */ })

    return () => {
      isCancelled = true
    }
  }, [])

  useEffect(() => {
    let isCancelled = false

    async function loadExecutions(showLoading: boolean) {
      if (showLoading) {
        setLoading(true)
      }

      try {
        const executionList = await api.getExecutions({
          status: mapStatusFilterToApi(statusFilter),
          search: searchFilter.trim() || undefined,
        })

        if (!isCancelled) {
          setExecutions(replaceIfChanged(executionList))
          setError(null)
        }
      } catch (err: unknown) {
        if (!isCancelled) {
          setError(getErrorMessage(err, 'Failed to fetch executions from the server.'))
          setExecutions([])
        }
      } finally {
        if (!isCancelled) {
          setLoading(false)
        }
      }
    }

    loadExecutions(true)
    const timer = setInterval(() => {
      void loadExecutions(false)
    }, 4000)

    return () => {
      isCancelled = true
      clearInterval(timer)
    }
  }, [statusFilter, searchFilter])

  // Overview stat strip feed — the full, unfiltered run set, polled independently of the timeline filters.
  useEffect(() => {
    let isCancelled = false
    const loadStats = () => {
      api.getExecutions()
        .then((all) => { if (!isCancelled) setStatsRuns(replaceIfChanged(all)) })
        .catch(() => { /* non-fatal: the strip degrades to zeros until the next poll */ })
    }
    loadStats()
    const timer = setInterval(loadStats, 4000)
    return () => { isCancelled = true; clearInterval(timer) }
  }, [])

  const handleRefresh = async () => {
    setLoading(true)
    try {
      const [workflowList, executionList, statsList, groupsResult] = await Promise.all([
        api.getWorkflows(),
        api.getExecutions({
          status: mapStatusFilterToApi(statusFilter),
          search: searchFilter.trim() || undefined,
        }),
        api.getExecutions(),
        api.getGroups(),
      ])

      setWorkflows(workflowList)
      setExecutions(executionList)
      setStatsRuns(statsList)
      setGroups(groupsResult.container.groups || [])
      setEtag(groupsResult.etag)
      setError(null)
      api.listArchivedWorkflows().then(setArchived).catch(() => { /* non-fatal */ })
    } catch (err: unknown) {
      setError(getErrorMessage(err, 'Failed to fetch dashboard data from the server.'))
    } finally {
      setLoading(false)
    }
  }

  const handleSaveGroups = async (updatedGroups: WorkflowGroup[]) => {
    try {
      const newEtag = await api.saveGroups({ version: 1, groups: updatedGroups }, etag)
      setGroups(updatedGroups)
      setEtag(newEtag)
    } catch (err: unknown) {
      console.error('Failed to save group with optimistic lock:', err)
      if (isApiError(err) && err.status === 412) {
        alert('Action failed: another user has modified the workflow groups. Reloading latest changes...')
      } else {
        alert(`Failed to save groups: ${getErrorMessage(err, 'Unknown error')}`)
      }
      // Reload on failure to sync
      const rest = await api.getGroups()
      setGroups(rest.container.groups || [])
      setEtag(rest.etag)
    }
  }

  const handleCreateGroup = async (name: string, color: string): Promise<string> => {
    const newId = 'grp_' + Math.random().toString(36).substring(2, 11)
    const updated = [...groups, { id: newId, name, color }]
    await handleSaveGroups(updated)
    return newId
  }

  const handleRenameGroup = async (id: string, name: string) => {
    const updated = groups.map((g) => g.id === id ? { ...g, name } : g)
    await handleSaveGroups(updated)
  }

  const handleUpdateGroupColor = async (id: string, color: string) => {
    const updated = groups.map((g) => g.id === id ? { ...g, color } : g)
    await handleSaveGroups(updated)
  }

  const handleDeleteGroup = async (id: string) => {
    try {
      await api.deleteGroup(id)
      await handleRefresh()
    } catch (err: unknown) {
      alert(`Failed to delete group: ${getErrorMessage(err, 'Unknown error')}`)
    }
  }

  return {
    workflows, setWorkflows,
    executions, setExecutions,
    statsRuns, setStatsRuns,
    loading, setLoading,
    error, setError,
    archived, setArchived,
    groups, setGroups,
    etag, setEtag,
    handleRefresh,
    handleSaveGroups, handleCreateGroup, handleRenameGroup, handleUpdateGroupColor, handleDeleteGroup,
  }
}
