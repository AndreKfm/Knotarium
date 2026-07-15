import { useState } from 'react'
import type { Dispatch, SetStateAction } from 'react'
import type { ExecutionStatus } from '../../types'

export type DashboardStatusFilter = 'All' | 'Running' | 'Waiting' | 'Retrying' | 'Completed' | 'Failed' | 'Cancelled'

/** Map a runtime execution status to the dashboard's status-filter label. */
export function mapExecutionStatusLabel(status: ExecutionStatus): DashboardStatusFilter | 'Pending' {
  switch (status) {
    case 'Suspended':
      return 'Waiting'
    case 'WaitingForRetry':
      return 'Retrying'
    default:
      return status
  }
}

/** Map a UI status filter to the executions API `status` param (undefined = no filter). */
export function mapStatusFilterToApi(status: DashboardStatusFilter): string | undefined {
  switch (status) {
    case 'All':
      return undefined
    case 'Waiting':
      return 'Suspended'
    case 'Retrying':
      return 'Retrying'
    default:
      return status
  }
}

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
