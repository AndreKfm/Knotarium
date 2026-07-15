import { useState } from 'react'
import type { Dispatch, SetStateAction } from 'react'

export type DashboardStatusFilter = 'All' | 'Running' | 'Waiting' | 'Retrying' | 'Completed' | 'Failed' | 'Cancelled'

export interface DashboardFilters {
  /** Run-status filter for the Operations Timeline (feeds the executions loader). */
  statusFilter: DashboardStatusFilter
  setStatusFilter: Dispatch<SetStateAction<DashboardStatusFilter>>
  /** Free-text filter scoping the runs table (feeds the executions loader). */
  searchFilter: string
  setSearchFilter: Dispatch<SetStateAction<string>>
  /** Name filter for the Workflow Definitions list (client-side; distinct from searchFilter). */
  workflowSearch: string
  setWorkflowSearch: Dispatch<SetStateAction<string>>
}

/**
 * The dashboard's three filter states. Pure UI state — no async. statusFilter/searchFilter are fed
 * into the executions loader (they trigger reloads); workflowSearch filters the definitions list
 * client-side. Extracted from Dashboard.tsx.
 */
export function useDashboardFilters(): DashboardFilters {
  const [statusFilter, setStatusFilter] = useState<DashboardStatusFilter>('All')
  const [searchFilter, setSearchFilter] = useState('')
  const [workflowSearch, setWorkflowSearch] = useState('')
  return { statusFilter, setStatusFilter, searchFilter, setSearchFilter, workflowSearch, setWorkflowSearch }
}
