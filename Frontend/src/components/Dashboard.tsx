import { useEffect, useRef, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { api } from '../utils/api';
import type { ExecutionInstance, ExecutionStatus, WorkflowDefinition, WorkflowGroup, NotificationChannel, FailureAlertConfig, SystemActivityEntry } from '../types';
import { Eye, Plus, RefreshCw, Layers, Terminal, AlertTriangle, CheckCircle, Clock, Search, Globe, Trash2, Check, Minus, Ban, Archive, RotateCcw, Filter, Power, Activity } from 'lucide-react';

// Selection accent = the cyan the runs list already uses (Event tags, timeline) rather than the indigo
// app accent — selection is a frequent list action, so it should read as part of the list.
const RUN_SELECT_ACCENT = '#22d3ee';

// Resizable split between the Workflow Definitions panel (left) and the Operations Timeline (right).
// The left panel's fraction of the row is persisted; both sides are floored so neither can collapse.
const SPLIT_STORAGE_KEY = 'kg-dashboard-split';
const SPLIT_MIN_PANEL_PX = 360;
const SPLIT_DEFAULT_FRAC = 0.48;

/**
 * Styled run-selection checkbox — a rounded square that fills with the accent + a check when selected
 * (replaces the native browser checkbox). `indeterminate` shows a dash, for a partial "select all".
 */
function RunCheckbox({ checked, indeterminate = false, disabled = false }: { checked: boolean; indeterminate?: boolean; disabled?: boolean }) {
  const active = checked || indeterminate;
  return (
    <span
      style={{
        width: 20,
        height: 20,
        flexShrink: 0,
        borderRadius: 6,
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        border: `1.5px solid ${active ? RUN_SELECT_ACCENT : '#2c3a4d'}`,
        background: active ? 'linear-gradient(160deg, #3ee0f5, #22d3ee)' : 'rgba(255,255,255,0.02)',
        boxShadow: active ? '0 0 0 3px rgba(34,211,238,0.13)' : 'none',
        opacity: disabled ? 0.4 : 1,
        transition: 'background 0.15s, border-color 0.15s, box-shadow 0.15s',
      }}
    >
      {indeterminate ? <Minus size={13} color="#fff" strokeWidth={3.5} /> : checked ? <Check size={13} color="#fff" strokeWidth={3.5} /> : null}
    </span>
  );
}
import WorkflowDefinitions from './WorkflowDefinitions';
import { replaceIfChanged } from '../utils/stableState';

// One card of the overview stat strip — a labelled headline number with a category-tinted accent bar.
interface OverviewStat {
  accent: string;
  icon: ReactNode;
  label: string;
  value: ReactNode;
  meta: ReactNode;
}

function StatStrip({ stats }: { stats: OverviewStat[] }) {
  return (
    <div className="kg-stats">
      {stats.map((s) => (
        <div className="kg-stat" key={s.label} style={{ '--ac': s.accent } as CSSProperties}>
          <div className="kg-stat-lbl">
            <span className="kg-stat-ic">{s.icon}</span>
            {s.label}
          </div>
          <div className="kg-stat-val">{s.value}</div>
          <div className="kg-stat-meta">{s.meta}</div>
        </div>
      ))}
    </div>
  );
}

interface DashboardProps {
  onEditWorkflow: (id: string) => void;
  onViewExecution: (id: string) => void;
  onTriggeredExecution: (id: string) => void;
}

type DashboardStatusFilter = 'All' | 'Running' | 'Waiting' | 'Retrying' | 'Completed' | 'Failed' | 'Cancelled';

interface TimelineGroup {
  label: 'Today' | 'Yesterday' | 'Last 7 Days' | 'Older';
  runs: ExecutionInstance[];
}

const statusFilters: DashboardStatusFilter[] = ['All', 'Running', 'Waiting', 'Retrying', 'Completed', 'Failed', 'Cancelled'];

function mapExecutionStatusLabel(status: ExecutionStatus): DashboardStatusFilter | 'Pending' {
  switch (status) {
    case 'Suspended':
      return 'Waiting';
    case 'WaitingForRetry':
      return 'Retrying';
    default:
      return status;
  }
}

function mapStatusFilterToApi(status: DashboardStatusFilter): string | undefined {
  switch (status) {
    case 'All':
      return undefined;
    case 'Waiting':
      return 'Suspended';
    case 'Retrying':
      return 'Retrying';
    default:
      return status;
  }
}

function normalizeTriggerOrigin(origin?: string): 'manual' | 'webhook' | 'schedule' | 'deviceEvent' {
  switch ((origin ?? '').trim().toLowerCase()) {
    case 'webhook':
      return 'webhook';
    case 'schedule':
    case 'scheduler':
      return 'schedule';
    case 'deviceevent':
      return 'deviceEvent';
    default:
      return 'manual';
  }
}

function getTriggerOriginLabel(origin?: string): string {
  switch (normalizeTriggerOrigin(origin)) {
    case 'webhook':
      return 'Webhook';
    case 'schedule':
      return 'Schedule';
    case 'deviceEvent':
      return 'Event';
    default:
      return 'Manual';
  }
}

function getTimelineLabel(date: Date, now: Date): TimelineGroup['label'] {
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const target = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  const dayDifference = Math.floor((today.getTime() - target.getTime()) / 86_400_000);

  if (dayDifference <= 0) {
    return 'Today';
  }

  if (dayDifference == 1) {
    return 'Yesterday';
  }

  if (dayDifference <= 7) {
    return 'Last 7 Days';
  }

  return 'Older';
}

function groupRunsByDate(runs: ExecutionInstance[], now: Date = new Date()): TimelineGroup[] {
  const groupedRuns: TimelineGroup[] = [
    { label: 'Today', runs: [] },
    { label: 'Yesterday', runs: [] },
    { label: 'Last 7 Days', runs: [] },
    { label: 'Older', runs: [] },
  ];

  const groupMap = new Map(groupedRuns.map((group) => [group.label, group]));

  for (const run of runs) {
    const label = getTimelineLabel(new Date(run.createdAt), now);
    groupMap.get(label)?.runs.push(run);
  }

  return groupedRuns.filter((group) => group.runs.length > 0);
}

function getStatusIcon(status: ExecutionStatus) {
  switch (status) {
    case 'Completed':
      return <CheckCircle size={14} color="var(--color-success)" />;
    case 'Failed':
      return <AlertTriangle size={14} color="var(--color-error)" />;
    case 'Running':
      return <RefreshCw size={14} color="var(--color-warning)" className="animate-spin" style={{ animation: 'spin 2s linear infinite' }} />;
    case 'WaitingForRetry':
      return <RefreshCw size={14} color="var(--color-warning)" />;
    case 'Suspended':
      return <Clock size={14} color="var(--color-info)" />;
    case 'Cancelled':
      return <AlertTriangle size={14} color="var(--text-muted)" />;
    default:
      return <Clock size={14} color="var(--text-muted)" />;
  }
}

function getStatusStyle(status: ExecutionStatus) {
  switch (status) {
    case 'Completed':
      return { background: 'rgba(16, 185, 129, 0.12)', color: 'var(--color-success)', border: '1px solid rgba(16, 185, 129, 0.2)' };
    case 'Failed':
      return { background: 'rgba(239, 68, 68, 0.12)', color: 'var(--color-error)', border: '1px solid rgba(239, 68, 68, 0.2)' };
    case 'Running':
      return { background: 'rgba(245, 158, 11, 0.12)', color: 'var(--color-warning)', border: '1px solid rgba(245, 158, 11, 0.2)' };
    case 'WaitingForRetry':
      return { background: 'rgba(251, 191, 36, 0.14)', color: '#fbbf24', border: '1px solid rgba(251, 191, 36, 0.25)' };
    case 'Suspended':
      return { background: 'rgba(56, 189, 248, 0.12)', color: 'var(--color-info)', border: '1px solid rgba(56, 189, 248, 0.22)' };
    case 'Cancelled':
      return { background: 'rgba(148, 163, 184, 0.12)', color: 'var(--text-muted)', border: '1px solid rgba(148, 163, 184, 0.24)' };
    default:
      return { background: 'rgba(255, 255, 255, 0.05)', color: 'var(--text-secondary)', border: '1px solid var(--border-color)' };
  }
}

const isApiError = (error: unknown): error is { status?: number; message?: string; data?: unknown } => {
  return typeof error === 'object' && error !== null;
};

const getErrorMessage = (error: unknown, fallback: string) => {
  return isApiError(error) && typeof error.message === 'string' ? error.message : fallback;
};

export function Dashboard({ onEditWorkflow, onViewExecution, onTriggeredExecution }: DashboardProps) {
  const [workflows, setWorkflows] = useState<WorkflowDefinition[]>([]);
  const [executions, setExecutions] = useState<ExecutionInstance[]>([]);
  // Unfiltered run set powering the overview stat strip — kept separate from `executions` (which the
  // timeline narrows by status/search) so the headline health numbers stay stable while you filter below.
  const [statsRuns, setStatsRuns] = useState<ExecutionInstance[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [statusFilter, setStatusFilter] = useState<DashboardStatusFilter>('All');
  const [searchFilter, setSearchFilter] = useState('');
  // Name filter for the Workflow Definitions list (distinct from `searchFilter`, which scopes the runs table).
  const [workflowSearch, setWorkflowSearch] = useState('');
  // Selected run ids (Operations Timeline multi-select) + an in-progress flag while a delete runs.
  const [selectedRuns, setSelectedRuns] = useState<Set<string>>(new Set());
  const [deletingRuns, setDeletingRuns] = useState(false);
  const [bulkDeleting, setBulkDeleting] = useState(false);
  const [archived, setArchived] = useState<{ id: string; name: string }[]>([]);
  const [showArchived, setShowArchived] = useState(false);
  const [restoringId, setRestoringId] = useState<string | null>(null);
  const [purgingId, setPurgingId] = useState<string | null>(null);
  const [purgingAll, setPurgingAll] = useState(false);

  // Adjustable width of the two dashboard panels. `splitFrac` is the left panel's share of the row;
  // persisted to localStorage and clamped on drag so neither panel drops below SPLIT_MIN_PANEL_PX.
  const splitRowRef = useRef<HTMLDivElement | null>(null);
  const [splitFrac, setSplitFrac] = useState<number>(() => {
    const stored = Number(localStorage.getItem(SPLIT_STORAGE_KEY));
    return Number.isFinite(stored) && stored >= 0.15 && stored <= 0.85 ? stored : SPLIT_DEFAULT_FRAC;
  });
  const [draggingSplit, setDraggingSplit] = useState(false);

  useEffect(() => {
    if (!draggingSplit) return;
    const onMove = (e: MouseEvent) => {
      const row = splitRowRef.current;
      if (!row) return;
      const rect = row.getBoundingClientRect();
      if (rect.width <= 0) return;
      const minFrac = SPLIT_MIN_PANEL_PX / rect.width;
      const raw = (e.clientX - rect.left) / rect.width;
      setSplitFrac(Math.min(1 - minFrac, Math.max(minFrac, raw)));
    };
    const stop = () => setDraggingSplit(false);
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', stop);
    // Suppress text selection + hold the resize cursor for the whole drag.
    const prevSelect = document.body.style.userSelect;
    const prevCursor = document.body.style.cursor;
    document.body.style.userSelect = 'none';
    document.body.style.cursor = 'col-resize';
    return () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', stop);
      document.body.style.userSelect = prevSelect;
      document.body.style.cursor = prevCursor;
    };
  }, [draggingSplit]);

  useEffect(() => {
    localStorage.setItem(SPLIT_STORAGE_KEY, String(splitFrac));
  }, [splitFrac]);

  // Groups and optimistic ETag tracking
  const [groups, setGroups] = useState<WorkflowGroup[]>([]);
  const [etag, setEtag] = useState<string>('');

  // Notification channels (for per-workflow failure-alert routing)
  const [channels, setChannels] = useState<NotificationChannel[]>([]);

  // Auto-filtered signals (e.g. self-echoes an external-signal provider dropped before any run). These
  // never become executions, so they don't show up in /api/executions — we surface them here as their own
  // "skipped" entries. In-memory on the provider, so the list resets when the host restarts. Absent (no
  // provider / 404) → stays empty and the section simply doesn't render.
  const [filteredEchoes, setFilteredEchoes] = useState<SystemActivityEntry[]>([]);
  useEffect(() => {
    let cancelled = false;
    const load = () => {
      api.getExternalSystem()
        .then((sys) => { if (!cancelled) setFilteredEchoes(sys.diagnostics?.recentActivity ?? []); })
        .catch(() => { if (!cancelled) setFilteredEchoes([]); });
    };
    load();
    const timer = setInterval(load, 4000);
    return () => { cancelled = true; clearInterval(timer); };
  }, []);
  // Collapse the auto-filtered section (persisted) and clear its buffer on demand.
  const [echoesCollapsed, setEchoesCollapsed] = useState<boolean>(() => {
    try { return localStorage.getItem('kg-echoes-collapsed') === '1'; } catch { return false; }
  });
  const toggleEchoesCollapsed = () => setEchoesCollapsed((v) => {
    const next = !v;
    try { localStorage.setItem('kg-echoes-collapsed', next ? '1' : '0'); } catch { /* ignore */ }
    return next;
  });
  const [clearingEchoes, setClearingEchoes] = useState(false);
  const handleClearEchoes = async () => {
    setClearingEchoes(true);
    try {
      await api.clearExternalSystemDiagnostics();
      setFilteredEchoes([]);
    } catch { /* non-fatal: the buffer resets on host restart anyway */ }
    finally { setClearingEchoes(false); }
  };

  useEffect(() => {
    let cancelled = false;
    api.getNotificationChannels()
      .then((list) => { if (!cancelled) setChannels(list); })
      .catch(() => { /* non-fatal: alert routing UI degrades gracefully */ });
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    let isCancelled = false;

    async function loadWorkflowsAndGroups() {
      try {
        const [workflowList, groupsResult] = await Promise.all([
          api.getWorkflows(),
          api.getGroups()
        ]);
        if (!isCancelled) {
          setWorkflows(workflowList);
          setGroups(groupsResult.container.groups || []);
          setEtag(groupsResult.etag);
        }
      } catch (err: unknown) {
        if (!isCancelled) {
          setError(getErrorMessage(err, 'Failed to fetch baseline data from the server.'));
        }
      }
    }

    loadWorkflowsAndGroups();
    api.listArchivedWorkflows().then((a) => { if (!isCancelled) setArchived(a); }).catch(() => { /* non-fatal */ });

    return () => {
      isCancelled = true;
    };
  }, []);

  useEffect(() => {
    let isCancelled = false;

    async function loadExecutions(showLoading: boolean) {
      if (showLoading) {
        setLoading(true);
      }

      try {
        const executionList = await api.getExecutions({
          status: mapStatusFilterToApi(statusFilter),
          search: searchFilter.trim() || undefined,
        });

        if (!isCancelled) {
          setExecutions(replaceIfChanged(executionList));
          setError(null);
        }
      } catch (err: unknown) {
        if (!isCancelled) {
          setError(getErrorMessage(err, 'Failed to fetch executions from the server.'));
          setExecutions([]);
        }
      } finally {
        if (!isCancelled) {
          setLoading(false);
        }
      }
    }

    loadExecutions(true);
    const timer = setInterval(() => {
      void loadExecutions(false);
    }, 4000);

    return () => {
      isCancelled = true;
      clearInterval(timer);
    };
  }, [statusFilter, searchFilter]);

  // Overview stat strip feed — the full, unfiltered run set, polled independently of the timeline filters.
  useEffect(() => {
    let isCancelled = false;
    const loadStats = () => {
      api.getExecutions()
        .then((all) => { if (!isCancelled) setStatsRuns(replaceIfChanged(all)); })
        .catch(() => { /* non-fatal: the strip degrades to zeros until the next poll */ });
    };
    loadStats();
    const timer = setInterval(loadStats, 4000);
    return () => { isCancelled = true; clearInterval(timer); };
  }, []);

  const handleRefresh = async () => {
    setLoading(true);
    try {
      const [workflowList, executionList, statsList, groupsResult] = await Promise.all([
        api.getWorkflows(),
        api.getExecutions({
          status: mapStatusFilterToApi(statusFilter),
          search: searchFilter.trim() || undefined,
        }),
        api.getExecutions(),
        api.getGroups(),
      ]);

      setWorkflows(workflowList);
      setExecutions(executionList);
      setStatsRuns(statsList);
      setGroups(groupsResult.container.groups || []);
      setEtag(groupsResult.etag);
      setError(null);
      api.listArchivedWorkflows().then(setArchived).catch(() => { /* non-fatal */ });
    } catch (err: unknown) {
      setError(getErrorMessage(err, 'Failed to fetch dashboard data from the server.'));
    } finally {
      setLoading(false);
    }
  };

  const handleRestoreWorkflow = async (id: string) => {
    setRestoringId(id);
    try {
      await api.restoreWorkflow(id);
      await handleRefresh(); // refreshes the list + the archived set
    } catch (err: unknown) {
      alert(`Failed to restore workflow: ${getErrorMessage(err, 'Unknown error')}`);
    } finally {
      setRestoringId(null);
    }
  };

  const handlePermanentlyDeleteWorkflow = async (id: string, name: string) => {
    if (!window.confirm(`Permanently delete “${name}”? This erases its entire version history and activation log and cannot be undone.`)) return;
    setPurgingId(id);
    try {
      await api.permanentlyDeleteWorkflow(id);
      setArchived((prev) => prev.filter((w) => w.id !== id));
    } catch (err: unknown) {
      alert(`Failed to permanently delete workflow: ${getErrorMessage(err, 'Unknown error')}`);
    } finally {
      setPurgingId(null);
    }
  };

  const handlePurgeAllArchived = async () => {
    if (!window.confirm(`Permanently delete all ${archived.length} archived workflow${archived.length === 1 ? '' : 's'}? This erases their entire version history and activation log and cannot be undone.`)) return;
    setPurgingAll(true);
    try {
      await api.purgeAllArchivedWorkflows();
      setArchived([]);
    } catch (err: unknown) {
      alert(`Failed to delete archived workflows: ${getErrorMessage(err, 'Unknown error')}`);
    } finally {
      setPurgingAll(false);
    }
  };

  const handleTrigger = async (workflowId: string) => {
    try {
      const instance = await api.triggerWorkflow(workflowId);
      onTriggeredExecution(instance.id);
    } catch (err: unknown) {
      if (isApiError(err) && err.status === 409 && typeof err.message === 'string' && /active version/i.test(err.message)) {
        alert('Trigger failed: this workflow has no active runtime version. Open it, publish a version, and activate it before running.');
      } else {
        alert(`Trigger failed: ${getErrorMessage(err, 'Compilation errors')}`);
      }
    }
  };

  const handleSaveAsTemplate = async (workflowId: string) => {
    try {
      const saved = await api.saveTemplateToLibrary({ workflowId });
      alert(`Saved “${saved.manifest.name}” to your template library. Find it under Templates → Library.`);
    } catch (err: unknown) {
      if (isApiError(err) && err.status === 404) {
        alert('Save failed: this workflow has no published version yet. Open it, publish a version, then save it as a template.');
      } else {
        alert(`Save as template failed: ${getErrorMessage(err, 'Unknown error')}`);
      }
    }
  };

  const handleSaveGroups = async (updatedGroups: WorkflowGroup[]) => {
    try {
      const newEtag = await api.saveGroups({ version: 1, groups: updatedGroups }, etag);
      setGroups(updatedGroups);
      setEtag(newEtag);
    } catch (err: unknown) {
      console.error('Failed to save group with optimistic lock:', err);
      if (isApiError(err) && err.status === 412) {
        alert('Action failed: another user has modified the workflow groups. Reloading latest changes...');
      } else {
        alert(`Failed to save groups: ${getErrorMessage(err, 'Unknown error')}`);
      }
      // Reload on failure to sync
      const rest = await api.getGroups();
      setGroups(rest.container.groups || []);
      setEtag(rest.etag);
    }
  };

  const handleCreateGroup = async (name: string, color: string): Promise<string> => {
    const newId = 'grp_' + Math.random().toString(36).substring(2, 11);
    const updated = [...groups, { id: newId, name, color }];
    await handleSaveGroups(updated);
    return newId;
  };

  const handleRenameGroup = async (id: string, name: string) => {
    const updated = groups.map(g => g.id === id ? { ...g, name } : g);
    await handleSaveGroups(updated);
  };

  const handleUpdateGroupColor = async (id: string, color: string) => {
    const updated = groups.map(g => g.id === id ? { ...g, color } : g);
    await handleSaveGroups(updated);
  };

  const handleDeleteGroup = async (id: string) => {
    try {
      await api.deleteGroup(id);
      await handleRefresh();
    } catch (err: unknown) {
      alert(`Failed to delete group: ${getErrorMessage(err, 'Unknown error')}`);
    }
  };

  const handleToggleEnabled = async (id: string, enabled: boolean) => {
    try {
      const result = await api.setWorkflowEnabled(id, enabled);
      setWorkflows(prev => prev.map(w => (w.id.value === id ? { ...w, isEnabled: result.enabled } : w)));
      if (!enabled && result.cancelledExecutions > 0) {
        await handleRefresh();
      }
    } catch (err: unknown) {
      alert(`Failed to ${enabled ? 'activate' : 'deactivate'} workflow: ${getErrorMessage(err, 'Unknown error')}`);
    }
  };

  const handleDeleteWorkflow = async (id: string) => {
    try {
      await api.deleteWorkflow(id);
      await handleRefresh();
    } catch (err: unknown) {
      alert(`Failed to delete workflow: ${getErrorMessage(err, 'Unknown error')}`);
    }
  };

  // Bulk-delete the currently filtered/visible workflows (e.g. clean up a flood of imported ones). Archives
  // them — version history is kept — and is gated by a confirm showing the exact count.
  const handleBulkDeleteVisible = async (visible: WorkflowDefinition[]) => {
    const ids = visible.map((w) => (typeof w.id === 'string' ? w.id : w.id.value));
    if (ids.length === 0) return;
    if (!window.confirm(`Delete ${ids.length} workflow${ids.length === 1 ? '' : 's'}? They are archived (version history kept) and removed from the dashboard.`)) return;
    setBulkDeleting(true);
    try {
      await api.bulkDeleteWorkflows(ids);
      setWorkflowSearch('');
      await handleRefresh();
    } catch (err: unknown) {
      alert(`Failed to delete workflows: ${getErrorMessage(err, 'Unknown error')}`);
    } finally {
      setBulkDeleting(false);
    }
  };

  const handleDuplicateWorkflow = async (id: string) => {
    try {
      const copy = await api.duplicateWorkflow(id);
      await handleRefresh();
      setWorkflowSearch(copy.name); // surface the new "(copy)" so it's easy to find in a long list
    } catch (err: unknown) {
      alert(`Failed to duplicate workflow: ${getErrorMessage(err, 'Unknown error')}`);
    }
  };

  const toggleRunSelection = (id: string) => {
    setSelectedRuns((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };

  // Selectable runs = everything not in-flight (those can't be deleted). Drives the "Select all" control.
  const selectableRunIds = executions
    .filter((e) => { const l = mapExecutionStatusLabel(e.status); return l !== 'Running' && l !== 'Pending'; })
    .map((e) => e.id);
  const allRunsSelected = selectableRunIds.length > 0 && selectableRunIds.every((id) => selectedRuns.has(id));
  const someRunsSelected = selectedRuns.size > 0 && !allRunsSelected;
  const toggleSelectAllRuns = () => setSelectedRuns(allRunsSelected ? new Set() : new Set(selectableRunIds));

  const afterRunsDeleted = async () => {
    setSelectedRuns(new Set());
    await handleRefresh();
  };

  const handleCancelRun = async (id: string) => {
    if (!window.confirm('Stop this run? It will be marked Cancelled (then you can delete it).')) return;
    setDeletingRuns(true);
    try {
      await api.cancelExecution(id);
      await handleRefresh();
    } catch (err: unknown) {
      alert(`Failed to stop run: ${getErrorMessage(err, 'Unknown error')}`);
    } finally {
      setDeletingRuns(false);
    }
  };

  const handleDeleteRun = async (id: string) => {
    setDeletingRuns(true);
    try {
      await api.deleteExecution(id);
      await afterRunsDeleted();
    } catch (err: unknown) {
      alert(`Failed to delete run: ${getErrorMessage(err, 'Unknown error')}`);
    } finally {
      setDeletingRuns(false);
    }
  };

  const handleDeleteSelectedRuns = async () => {
    const ids = [...selectedRuns];
    if (ids.length === 0) return;
    if (!window.confirm(`Delete ${ids.length} selected run${ids.length === 1 ? '' : 's'}? This can't be undone.`)) return;
    setDeletingRuns(true);
    try {
      await api.bulkDeleteExecutions({ ids });
      await afterRunsDeleted();
    } catch (err: unknown) {
      alert(`Failed to delete runs: ${getErrorMessage(err, 'Unknown error')}`);
    } finally {
      setDeletingRuns(false);
    }
  };

  const handleDeleteAllRuns = async () => {
    const scope = statusFilter === 'All' ? 'all runs' : `all ${statusFilter} runs`;
    if (!window.confirm(`Delete ${scope}? In-progress runs are kept. This can't be undone.`)) return;
    setDeletingRuns(true);
    try {
      await api.bulkDeleteExecutions({ all: true, status: mapStatusFilterToApi(statusFilter) });
      await afterRunsDeleted();
    } catch (err: unknown) {
      alert(`Failed to delete runs: ${getErrorMessage(err, 'Unknown error')}`);
    } finally {
      setDeletingRuns(false);
    }
  };

  const handleRenameWorkflow = async (id: string, name: string) => {
    const target = workflows.find(w => w.id.value === id);
    if (target) {
      try {
        await api.updateWorkflow(id, { ...target, name });
        await handleRefresh();
      } catch (err: unknown) {
        alert(`Failed to rename workflow: ${getErrorMessage(err, 'Unknown error')}`);
      }
    }
  };

  const handleMoveWorkflow = async (id: string, arg: { group: string | null; beforeId: string | null }) => {
    const target = workflows.find(w => w.id.value === id);
    if (target) {
      try {
        const updatedMetadata = { ...target.metadata, group: arg.group };
        await api.updateWorkflow(id, { ...target, metadata: updatedMetadata });
        await handleRefresh();
      } catch (err: unknown) {
        alert(`Failed to move workflow: ${getErrorMessage(err, 'Unknown error')}`);
      }
    }
  };

  const handleSetFailureAlert = async (id: string, failureAlert: FailureAlertConfig) => {
    const target = workflows.find(w => w.id.value === id);
    if (target) {
      // Optimistic update so the chip reflects the change immediately.
      const updatedMetadata = { ...target.metadata, failureAlert };
      setWorkflows(prev => prev.map(w => (w.id.value === id ? { ...w, metadata: updatedMetadata } : w)));
      try {
        await api.updateWorkflow(id, { ...target, metadata: updatedMetadata });
      } catch (err: unknown) {
        alert(`Failed to update failure alerts: ${getErrorMessage(err, 'Unknown error')}`);
        await handleRefresh();
      }
    }
  };

  const executionGroups = groupRunsByDate(executions);

  // ── Overview strip: headline health numbers derived from the real data ──
  const groupedWorkflows = workflows.filter((w) => {
    const gId = w.metadata?.group ?? null;
    return gId !== null && groups.some((g) => g.id === gId);
  }).length;
  const ungroupedWorkflows = workflows.length - groupedWorkflows;
  const activeWorkflows = workflows.filter((w) => w.isEnabled !== false).length;
  const inactiveWorkflows = workflows.length - activeWorkflows;

  const sevenDaysAgo = Date.now() - 7 * 86_400_000;
  const recentRuns = statsRuns.filter((r) => new Date(r.createdAt).getTime() >= sevenDaysAgo);
  const manualRuns = recentRuns.filter((r) => normalizeTriggerOrigin(r.triggerOrigin) === 'manual').length;
  const eventRuns = recentRuns.length - manualRuns;
  const completedRuns = recentRuns.filter((r) => r.status === 'Completed').length;
  const terminalRuns = recentRuns.filter((r) => r.status === 'Completed' || r.status === 'Failed' || r.status === 'Cancelled').length;
  const successRate = terminalRuns > 0 ? Math.round((completedRuns / terminalRuns) * 100) : null;
  const deadLetterCount = statsRuns.filter((r) => r.status === 'Failed').length;

  const overviewStats: OverviewStat[] = [
    {
      accent: '#22d3ee',
      icon: <Layers size={14} />,
      label: 'Workflows',
      value: String(workflows.length),
      meta: <>{groupedWorkflows} grouped · {ungroupedWorkflows} ungrouped</>,
    },
    {
      accent: '#34d399',
      icon: <Power size={14} />,
      label: 'Active',
      value: String(activeWorkflows),
      meta: <><b>{inactiveWorkflows}</b> inactive</>,
    },
    {
      accent: '#7c6cf0',
      icon: <Activity size={14} />,
      label: 'Runs · 7d',
      value: String(recentRuns.length),
      meta: <>{eventRuns} event · {manualRuns} manual</>,
    },
    {
      accent: '#34d399',
      icon: <CheckCircle size={14} />,
      label: 'Success rate',
      value: successRate === null ? <>—</> : <>{successRate}<small>%</small></>,
      meta: terminalRuns > 0 ? <><b>{completedRuns}</b> of {terminalRuns} completed</> : <>no finished runs</>,
    },
    {
      accent: '#f0b429',
      icon: <AlertTriangle size={14} />,
      label: 'Dead Letter',
      value: String(deadLetterCount),
      meta: deadLetterCount > 0 ? <><b>{deadLetterCount}</b> to review</> : <>nothing to review</>,
    },
  ];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', width: '100%', overflowY: 'auto', padding: '32px 0' }}>
      {/* Cap + center the content so rows don't run edge-to-edge across an ultra-wide monitor. */}
      <div style={{ width: 'min(2040px, calc(100% - 64px))', margin: '0 auto', display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '32px' }}>
        <div>
          <h1 style={{ fontWeight: 800, fontSize: '2rem', letterSpacing: '-0.02em', background: 'linear-gradient(to right, #fff, #a5b4fc)', WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent' }}>
            Workflow Control Center
          </h1>
          <p style={{ color: 'var(--text-secondary)', marginTop: '4px', fontSize: '0.95rem' }}>
            Design, deploy, and monitor visual automation flows.
          </p>
        </div>
        <div style={{ display: 'flex', gap: '12px' }}>
          <button
            onClick={() => void handleRefresh()}
            aria-label="Refresh dashboard"
            style={{
              padding: '12px',
              borderRadius: '10px',
              background: 'rgba(255, 255, 255, 0.04)',
              border: '1px solid var(--border-color)',
              color: 'var(--text-primary)',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              transition: 'background 0.2s',
            }}
          >
            <RefreshCw size={18} />
          </button>
          <button
            onClick={() => onEditWorkflow('')}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '12px 20px',
              borderRadius: '10px',
              background: 'var(--color-accent)',
              border: 'none',
              color: '#fff',
              fontWeight: 700,
              fontSize: '0.9rem',
              cursor: 'pointer',
              boxShadow: '0 4px 14px var(--color-accent-glow)',
              transition: 'transform 0.2s, box-shadow 0.2s',
            }}
            onMouseOver={(e) => {
              e.currentTarget.style.transform = 'translateY(-1px)';
              e.currentTarget.style.boxShadow = '0 6px 20px var(--color-accent-glow)';
            }}
            onMouseOut={(e) => {
              e.currentTarget.style.transform = 'none';
              e.currentTarget.style.boxShadow = '0 4px 14px var(--color-accent-glow)';
            }}
          >
            <Plus size={18} />
            Create Workflow
          </button>
        </div>
      </div>

      {/* Overview strip — read instance health at a glance before diving into the lists below. */}
      <StatStrip stats={overviewStats} />

      {error && (
        <div style={{ padding: '16px', background: 'rgba(239, 68, 68, 0.1)', border: '1px solid var(--color-error)', borderRadius: '10px', color: 'var(--color-error)', display: 'flex', alignItems: 'center', gap: '10px', marginBottom: '24px' }}>
          <AlertTriangle size={18} />
          <span>{error}</span>
        </div>
      )}

      {loading && workflows.length === 0 ? (
        <div style={{ display: 'flex', flex: 1, alignItems: 'center', justifyContent: 'center', minHeight: '300px' }}>
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '16px' }}>
            <RefreshCw className="animate-spin" size={32} color="var(--color-accent)" style={{ animation: 'spin 2s linear infinite' }} />
            <span style={{ color: 'var(--text-secondary)', fontSize: '0.95rem' }}>Querying workspace data...</span>
          </div>
        </div>
      ) : (
        <div
          ref={splitRowRef}
          style={{
            display: 'grid',
            gridTemplateColumns: `minmax(${SPLIT_MIN_PANEL_PX}px, ${splitFrac}fr) 30px minmax(${SPLIT_MIN_PANEL_PX}px, ${1 - splitFrac}fr)`,
            alignItems: 'start',
          }}
        >
          <div className="kg-panel">
            {(() => {
              const wfQuery = workflowSearch.trim().toLowerCase();
              const visibleWorkflows = wfQuery
                ? workflows.filter((w) => (w.name || '').toLowerCase().includes(wfQuery))
                : workflows;
              return (
            <>
            <div className="kg-phead">
              <Layers size={18} color="var(--color-accent)" />
              <h2 style={{ fontSize: '1.05rem', fontWeight: 700 }}>Workflow Definitions ({wfQuery ? `${visibleWorkflows.length}/${workflows.length}` : workflows.length})</h2>
              {workflows.length > 0 && (
                <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: '10px' }}>
                  {archived.length > 0 && (
                    <button
                      onClick={() => setShowArchived((v) => !v)}
                      aria-label="Toggle archived workflows"
                      style={{ display: 'flex', alignItems: 'center', gap: '6px', padding: '6px 11px', borderRadius: '8px', background: showArchived ? 'rgba(34,211,238,0.12)' : 'rgba(0,0,0,0.25)', border: `1px solid ${showArchived ? 'rgba(34,211,238,0.4)' : 'var(--border-color)'}`, color: showArchived ? '#22d3ee' : 'var(--text-secondary)', fontSize: '0.82rem', fontWeight: 600, cursor: 'pointer' }}
                    >
                      <Archive size={13} /> Archived ({archived.length})
                    </button>
                  )}
                  <button
                    onClick={() => handleBulkDeleteVisible(visibleWorkflows)}
                    disabled={bulkDeleting || visibleWorkflows.length === 0}
                    aria-label="Delete shown workflows"
                    title={wfQuery ? `Delete the ${visibleWorkflows.length} filtered workflows` : `Delete all ${visibleWorkflows.length} workflows`}
                    style={{ display: 'flex', alignItems: 'center', gap: '6px', padding: '6px 11px', borderRadius: '8px', background: 'rgba(240,85,109,0.08)', border: '1px solid rgba(240,85,109,0.35)', color: '#f0556d', fontSize: '0.82rem', fontWeight: 600, cursor: bulkDeleting ? 'default' : 'pointer', opacity: bulkDeleting ? 0.6 : 1 }}
                  >
                    <Trash2 size={13} /> {bulkDeleting ? 'Deleting…' : `Delete ${visibleWorkflows.length}`}
                  </button>
                  <div style={{ position: 'relative', display: 'flex', alignItems: 'center' }}>
                    <Search size={14} style={{ position: 'absolute', left: 10, color: 'var(--text-muted)' }} />
                    <input
                      type="text"
                      value={workflowSearch}
                      onChange={(e) => setWorkflowSearch(e.target.value)}
                      placeholder="Filter by name"
                      aria-label="Filter workflows by name"
                      style={{ padding: '6px 10px 6px 30px', borderRadius: '8px', background: 'rgba(0,0,0,0.25)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.82rem', width: '180px' }}
                    />
                  </div>
                </div>
              )}
            </div>

            <div style={{ padding: '14px 16px' }}>
            {showArchived && archived.length > 0 && (
              <div style={{ marginBottom: '16px', border: '1px solid rgba(34,211,238,0.25)', borderRadius: '12px', background: 'rgba(34,211,238,0.04)', overflow: 'hidden' }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px', padding: '10px 14px', borderBottom: '1px solid rgba(34,211,238,0.15)' }}>
                  <Archive size={14} color="#22d3ee" />
                  <span style={{ fontWeight: 700, fontSize: '0.9rem' }}>Archived ({archived.length})</span>
                  <span style={{ color: 'var(--text-muted)', fontSize: '0.78rem' }}>Deleted workflows — restore brings them back from their latest version.</span>
                  <button
                    onClick={handlePurgeAllArchived}
                    disabled={purgingAll || !!purgingId || !!restoringId}
                    aria-label="Permanently delete all archived workflows"
                    title="Permanently delete every archived workflow — erases version history, cannot be undone"
                    style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: '6px', padding: '5px 11px', borderRadius: '8px', background: 'rgba(248,113,113,0.08)', border: '1px solid rgba(248,113,113,0.4)', color: '#f87171', fontSize: '0.8rem', fontWeight: 600, cursor: purgingAll ? 'default' : 'pointer', opacity: purgingAll || !!purgingId || !!restoringId ? 0.6 : 1 }}
                  >
                    <Trash2 size={12} /> {purgingAll ? 'Deleting…' : `Delete all (${archived.length})`}
                  </button>
                </div>
                <div>
                  {archived.map((w) => (
                    <div key={w.id} style={{ display: 'flex', alignItems: 'center', gap: '10px', padding: '9px 14px', borderTop: '1px solid rgba(255,255,255,0.04)' }}>
                      <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: 'var(--text-secondary)' }}>{w.name}</span>
                      <button
                        onClick={() => handleRestoreWorkflow(w.id)}
                        disabled={restoringId === w.id || purgingId === w.id || purgingAll}
                        aria-label={`Restore ${w.name}`}
                        style={{ display: 'flex', alignItems: 'center', gap: '6px', padding: '5px 11px', borderRadius: '8px', background: 'rgba(34,211,238,0.1)', border: '1px solid rgba(34,211,238,0.4)', color: '#22d3ee', fontSize: '0.8rem', fontWeight: 600, cursor: restoringId === w.id ? 'default' : 'pointer', opacity: restoringId === w.id || purgingId === w.id ? 0.6 : 1 }}
                      >
                        <RotateCcw size={12} /> {restoringId === w.id ? 'Restoring…' : 'Restore'}
                      </button>
                      <button
                        onClick={() => handlePermanentlyDeleteWorkflow(w.id, w.name)}
                        disabled={restoringId === w.id || purgingId === w.id || purgingAll}
                        aria-label={`Permanently delete ${w.name}`}
                        title="Permanently delete — erases version history, cannot be undone"
                        style={{ display: 'flex', alignItems: 'center', gap: '6px', padding: '5px 11px', borderRadius: '8px', background: 'rgba(248,113,113,0.08)', border: '1px solid rgba(248,113,113,0.4)', color: '#f87171', fontSize: '0.8rem', fontWeight: 600, cursor: purgingId === w.id ? 'default' : 'pointer', opacity: restoringId === w.id || purgingId === w.id ? 0.6 : 1 }}
                      >
                        <Trash2 size={12} /> {purgingId === w.id ? 'Deleting…' : 'Delete'}
                      </button>
                    </div>
                  ))}
                </div>
              </div>
            )}
            {workflows.length === 0 ? (
              <div style={{ padding: '48px', border: '2px dashed var(--border-color)', borderRadius: '12px', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px', textAlign: 'center' }}>
                <span style={{ color: 'var(--text-muted)' }}>No workflows configured yet.</span>
                <button
                  onClick={() => onEditWorkflow('')}
                  style={{ background: 'transparent', border: 'none', color: 'var(--color-accent)', fontWeight: 600, cursor: 'pointer', textDecoration: 'underline' }}
                >
                  Create your first canvas workflow now
                </button>
              </div>
            ) : visibleWorkflows.length === 0 ? (
              <div style={{ padding: '32px', color: 'var(--text-muted)', textAlign: 'center', fontSize: '0.88rem' }}>
                No workflows match “{workflowSearch.trim()}”.
              </div>
            ) : (
              <WorkflowDefinitions
                workflows={visibleWorkflows}
                isFiltering={!!wfQuery}
                groups={groups}
                channels={channels}
                onSetFailureAlert={handleSetFailureAlert}
                onRenameWorkflow={handleRenameWorkflow}
                onMoveWorkflow={handleMoveWorkflow}
                onCreateGroup={handleCreateGroup}
                onRenameGroup={handleRenameGroup}
                onUpdateGroupColor={handleUpdateGroupColor}
                onDeleteGroup={handleDeleteGroup}
                onDeleteWorkflow={handleDeleteWorkflow}
                onDuplicate={handleDuplicateWorkflow}
                onViewGraph={onEditWorkflow}
                onTriggerRun={handleTrigger}
                onToggleEnabled={handleToggleEnabled}
                onSaveAsTemplate={handleSaveAsTemplate}
              />
            )}
            </div>
            </>
              );
            })()}
          </div>

          <div
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize panels"
            title="Drag to resize panels — double-click to reset"
            className={'kg-split' + (draggingSplit ? ' kg-split-active' : '')}
            style={{ alignSelf: 'stretch' }}
            onMouseDown={(e) => { e.preventDefault(); setDraggingSplit(true); }}
            onDoubleClick={() => setSplitFrac(SPLIT_DEFAULT_FRAC)}
          >
            <span className="kg-split-grip" />
          </div>

          <div className="kg-panel">
            <div className="kg-phead">
                <Terminal size={18} color="var(--color-warning)" />
                <h2 style={{ fontSize: '1.05rem', fontWeight: 700 }}>
                  Operations Timeline ({executions.length})
                  {filteredEchoes.length > 0 && (
                    <span style={{ marginLeft: 8, fontSize: '0.78rem', fontWeight: 600, color: '#c69a5b' }} title="Signals auto-filtered before any run (e.g. self-echoes)">
                      · {filteredEchoes.length} auto-filtered
                    </span>
                  )}
                </h2>
                <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: '8px' }}>
                  {executions.length > 0 && (
                    <button
                      type="button"
                      disabled={deletingRuns}
                      onClick={handleDeleteAllRuns}
                      title={statusFilter === 'All' ? 'Delete all runs (in-progress kept)' : `Delete all ${statusFilter} runs`}
                      style={{ display: 'flex', alignItems: 'center', gap: '6px', padding: '6px 12px', borderRadius: '8px', background: 'transparent', border: '1px solid var(--border-color)', color: 'var(--text-secondary)', fontWeight: 600, fontSize: '0.8rem', cursor: deletingRuns ? 'default' : 'pointer' }}
                    >
                      <Trash2 size={14} /> {statusFilter === 'All' ? 'Delete all' : `Delete all ${statusFilter}`}
                    </button>
                  )}
                </div>
            </div>
            <div className="kg-psub">
              <div style={{ display: 'grid', gridTemplateColumns: '180px minmax(0, 1fr)', gap: '12px' }}>
                <label style={{ display: 'flex', flexDirection: 'column', gap: '6px', color: 'var(--text-secondary)', fontSize: '0.8rem', fontWeight: 600 }}>
                  Status
                  <select
                    aria-label="Execution status filter"
                    value={statusFilter}
                    onChange={(event) => setStatusFilter(event.target.value as DashboardStatusFilter)}
                    style={{
                      padding: '10px 12px',
                      borderRadius: '10px',
                      border: '1px solid var(--border-color)',
                      background: 'rgba(255, 255, 255, 0.05)',
                      color: '#fff',
                    }}
                  >
                    {statusFilters.map((status) => (
                      <option key={status} value={status}>
                        {status}
                      </option>
                    ))}
                  </select>
                </label>

                <label style={{ display: 'flex', flexDirection: 'column', gap: '6px', color: 'var(--text-secondary)', fontSize: '0.8rem', fontWeight: 600 }}>
                  Search runs
                  <div style={{ display: 'flex', alignItems: 'center', gap: '10px', padding: '0 12px', borderRadius: '10px', border: '1px solid var(--border-color)', background: 'rgba(255, 255, 255, 0.05)' }}>
                    <Search size={14} color="var(--text-muted)" />
                    <input
                      aria-label="Search runs"
                      value={searchFilter}
                      onChange={(event) => setSearchFilter(event.target.value)}
                      placeholder="Search by workflow or origin"
                      style={{
                        flex: 1,
                        padding: '10px 0',
                        border: 'none',
                        background: 'transparent',
                        color: '#fff',
                        outline: 'none',
                      }}
                    />
                  </div>
                </label>
              </div>
            </div>

            {executions.length === 0 && filteredEchoes.length === 0 ? (
              <div style={{ padding: '48px', border: '2px dashed var(--border-color)', borderRadius: '12px', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', color: 'var(--text-muted)', textAlign: 'center' }}>
                <span>No runs match the current filters. Trigger a workflow or adjust the dashboard filters.</span>
              </div>
            ) : (
              <div>
                {filteredEchoes.length > 0 && (
                  <section aria-label="Auto-filtered signals">
                    <div className="tl-grouphead">
                      <h3 style={{ fontSize: '0.78rem', fontWeight: 700, color: '#c69a5b', textTransform: 'uppercase', letterSpacing: '0.08em' }}>Auto-filtered (skipped)</h3>
                      <span style={{ fontSize: '0.72rem', color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.08em' }}>
                        {filteredEchoes.length} echo{filteredEchoes.length === 1 ? '' : 'es'}
                      </span>
                      <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 8 }}>
                        <button
                          type="button"
                          onClick={toggleEchoesCollapsed}
                          aria-expanded={!echoesCollapsed}
                          style={{ padding: '4px 10px', borderRadius: 8, background: 'transparent', border: '1px solid var(--border-color)', color: 'var(--text-secondary)', fontSize: '0.72rem', fontWeight: 600, cursor: 'pointer' }}
                        >
                          {echoesCollapsed ? 'Show' : 'Hide'}
                        </button>
                        <button
                          type="button"
                          onClick={handleClearEchoes}
                          disabled={clearingEchoes}
                          title="Clear the auto-filtered feed"
                          style={{ display: 'flex', alignItems: 'center', gap: 5, padding: '4px 10px', borderRadius: 8, background: 'rgba(198,154,91,0.08)', border: '1px solid rgba(198,154,91,0.28)', color: '#d8b483', fontSize: '0.72rem', fontWeight: 600, cursor: clearingEchoes ? 'default' : 'pointer', opacity: clearingEchoes ? 0.6 : 1 }}
                        >
                          <Trash2 size={12} /> {clearingEchoes ? 'Clearing…' : 'Clear'}
                        </button>
                      </div>
                    </div>
                    {!echoesCollapsed && filteredEchoes.map((echo, i) => (
                      <div key={`${echo.timestamp}-${i}`} className="tl-row" style={{ cursor: 'default' }}>
                        <div className="tl-check" style={{ cursor: 'default' }} title="Filtered before any run — no execution started">
                          <Filter size={14} color="#c69a5b" />
                        </div>
                        <div className="tl-main">
                          <div className="tl-nameline">
                            <span className="tl-name" title={echo.summary}>{echo.summary}</span>
                            <span className="tl-chip tl-chip-filtered"><Ban size={11} /> Filtered</span>
                          </div>
                          <div className="tl-meta">
                            <span>{new Date(echo.timestamp).toLocaleString()}</span>
                            {echo.detail && <span className="tl-id">{echo.detail}</span>}
                          </div>
                        </div>
                        <span className="tl-status" style={{ background: 'rgba(198,154,91,0.12)', color: '#d8b483', border: '1px solid rgba(198,154,91,0.25)' }}>
                          Skipped
                        </span>
                        <div className="tl-actions" />
                      </div>
                    ))}
                  </section>
                )}
                {selectableRunIds.length > 0 && (
                  <div className="tl-selbar">
                    <div
                      role="checkbox"
                      aria-checked={allRunsSelected ? true : someRunsSelected ? 'mixed' : false}
                      aria-label="Select all runs"
                      onClick={toggleSelectAllRuns}
                      style={{ display: 'flex', alignItems: 'center', gap: '10px', cursor: 'pointer' }}
                    >
                      <RunCheckbox checked={allRunsSelected} indeterminate={someRunsSelected} />
                      <span style={{ fontSize: '0.85rem', fontWeight: 600, color: 'var(--text-secondary)' }}>
                        {selectedRuns.size > 0 ? <><strong style={{ color: '#fff' }}>{selectedRuns.size}</strong> selected</> : 'Select all'}
                      </span>
                    </div>
                    {selectedRuns.size > 0 && (
                      <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                        <button
                          type="button"
                          onClick={() => setSelectedRuns(new Set())}
                          title="Clear selection"
                          style={{ padding: '6px 12px', borderRadius: '8px', background: 'transparent', border: '1px solid var(--border-color)', color: 'var(--text-secondary)', fontWeight: 600, fontSize: '0.8rem', cursor: 'pointer' }}
                        >
                          Clear
                        </button>
                        <button
                          type="button"
                          disabled={deletingRuns}
                          onClick={handleDeleteSelectedRuns}
                          title="Delete the selected runs"
                          style={{ display: 'flex', alignItems: 'center', gap: '6px', padding: '6px 12px', borderRadius: '8px', background: 'rgba(239,68,68,0.12)', border: '1px solid rgba(239,68,68,0.3)', color: '#fca5a5', fontWeight: 600, fontSize: '0.8rem', cursor: deletingRuns ? 'default' : 'pointer' }}
                        >
                          <Trash2 size={14} /> Delete {selectedRuns.size}
                        </button>
                      </div>
                    )}
                  </div>
                )}
                {executionGroups.map((group) => (
                  <section key={group.label} aria-label={group.label}>
                    <div className="tl-grouphead">
                      <h3 style={{ fontSize: '0.78rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: '0.08em' }}>{group.label}</h3>
                      <span style={{ fontSize: '0.72rem', color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.08em' }}>
                        {group.runs.length} run{group.runs.length === 1 ? '' : 's'}
                      </span>
                    </div>

                    {group.runs.map((execution) => {
                      const workflowName = execution.workflowName ?? workflows.find((workflow) => workflow.id.value === execution.workflowDefinitionId.value)?.name ?? 'Unknown Workflow';
                      const statusLabel = mapExecutionStatusLabel(execution.status);
                      const isSelected = selectedRuns.has(execution.id);
                      const inFlight = statusLabel === 'Running' || statusLabel === 'Pending';

                      return (
                        <div
                          key={execution.id}
                          className={'tl-row' + (isSelected ? ' tl-selected' : '')}
                          onClick={() => onViewExecution(execution.id)}
                        >
                          {/* col 1 — selection */}
                          <div
                            className="tl-check"
                            role="checkbox"
                            aria-checked={isSelected}
                            aria-disabled={inFlight}
                            aria-label={`Select run ${execution.id.slice(0, 8)}`}
                            title={inFlight ? "In-progress runs can't be deleted" : 'Select for deletion'}
                            onClick={(e) => { e.stopPropagation(); if (!inFlight) toggleRunSelection(execution.id); }}
                            style={{ cursor: inFlight ? 'not-allowed' : 'pointer' }}
                          >
                            <RunCheckbox checked={isSelected} disabled={inFlight} />
                          </div>

                          {/* col 2 — name + meta, all left-aligned */}
                          <div className="tl-main">
                            <div className="tl-nameline">
                              <span className="tl-name" title={workflowName}>{workflowName}</span>
                              {(() => {
                                const originLabel = getTriggerOriginLabel(execution.triggerOrigin);
                                const manual = originLabel === 'Manual';
                                return (
                                  <span className={'tl-chip ' + (manual ? 'tl-chip-manual' : 'tl-chip-event')}>
                                    <Globe size={11} />
                                    {originLabel}
                                  </span>
                                );
                              })()}
                            </div>
                            <div className="tl-meta">
                              <span>{new Date(execution.createdAt).toLocaleString()}</span>
                              <span className="tl-id">ID: {execution.id.slice(0, 8)}…</span>
                            </div>
                          </div>

                          {/* col 3 — status pill (pushed right by the 1fr main column) */}
                          <span className="tl-status" style={getStatusStyle(execution.status)}>
                            {getStatusIcon(execution.status)}
                            {statusLabel}
                          </span>

                          {/* col 4 — row actions, hidden until hover / selected */}
                          <div className="tl-actions">
                            <button
                              type="button"
                              className="tl-iconbtn tl-iconbtn-view"
                              title="View run"
                              aria-label="View run"
                              onClick={(e) => { e.stopPropagation(); onViewExecution(execution.id); }}
                            >
                              <Eye size={14} />
                            </button>
                            {inFlight ? (
                              <button
                                type="button"
                                className="tl-iconbtn tl-iconbtn-stop"
                                disabled={deletingRuns}
                                title="Stop this run (mark it Cancelled, then it can be deleted)"
                                aria-label="Stop run"
                                onClick={(e) => { e.stopPropagation(); void handleCancelRun(execution.id); }}
                              >
                                <Ban size={14} />
                              </button>
                            ) : (
                              <button
                                type="button"
                                className="tl-iconbtn tl-iconbtn-del"
                                disabled={deletingRuns}
                                title="Delete this run"
                                aria-label="Delete run"
                                onClick={(e) => { e.stopPropagation(); if (window.confirm('Delete this run? This can’t be undone.')) void handleDeleteRun(execution.id); }}
                              >
                                <Trash2 size={14} />
                              </button>
                            )}
                          </div>
                        </div>
                      );
                    })}
                  </section>
                ))}
              </div>
            )}
          </div>
        </div>
      )}
      </div>

      <style>{`
        @keyframes spin {
          from { transform: rotate(0deg); }
          to { transform: rotate(360deg); }
        }
        @keyframes kg-pulse { 0%,100% { opacity: 1; } 50% { opacity: .35; } }

        /* ---------- overview stat strip ---------- */
        .kg-stats { display: grid; grid-template-columns: repeat(5, 1fr); gap: 14px; margin-bottom: 24px; }
        @media (max-width: 1080px) { .kg-stats { grid-template-columns: repeat(2, 1fr); } }
        .kg-stat { position: relative; border: 1px solid #18212e; border-radius: 14px; padding: 15px 16px 14px;
          background: linear-gradient(180deg, #0d121b, #0b0f17); overflow: hidden; }
        .kg-stat::before { content: ""; position: absolute; left: 0; top: 0; bottom: 0; width: 3px; background: var(--ac, #22d3ee); opacity: .9; }
        .kg-stat-lbl { display: flex; align-items: center; gap: 8px; font-size: 11px; font-weight: 700; letter-spacing: .04em; color: #8b96a6; text-transform: uppercase; }
        .kg-stat-ic { width: 24px; height: 24px; border-radius: 7px; display: grid; place-items: center; flex: none;
          background: color-mix(in srgb, var(--ac) 14%, transparent); border: 1px solid color-mix(in srgb, var(--ac) 34%, transparent); color: var(--ac); }
        .kg-stat-val { margin-top: 11px; font-size: 30px; font-weight: 800; letter-spacing: -.03em; line-height: 1; color: #eef2f7; }
        .kg-stat-val small { font-size: 15px; font-weight: 700; color: #6b7888; letter-spacing: 0; }
        .kg-stat-meta { margin-top: 7px; font-size: 11.5px; color: #5a6675; }
        .kg-stat-meta b { color: var(--ac); font-weight: 700; }

        /* ---------- elevated panels ---------- */
        /* NOTE: intentionally NOT overflow:hidden — the definitions panel hosts absolutely-positioned
           group/alert/swatch popovers that must escape the card bounds. */
        .kg-panel { border: 1px solid #18212e; border-radius: 16px; background: linear-gradient(180deg, #0d121b, #0b0f17); box-shadow: 0 24px 60px -30px rgba(0,0,0,.8); display: flex; flex-direction: column; }

        /* ---------- draggable panel divider ---------- */
        .kg-split { position: relative; cursor: col-resize; display: flex; align-items: center; justify-content: center; min-height: 140px; touch-action: none; }
        .kg-split::before { content: ""; position: absolute; top: 0; bottom: 0; left: 50%; transform: translateX(-50%); width: 2px; background: #18212e; transition: background .15s, box-shadow .15s; }
        .kg-split:hover::before, .kg-split-active::before { background: #6f6cf0; box-shadow: 0 0 10px rgba(111,108,240,.6); }
        .kg-split-grip { position: relative; width: 4px; height: 36px; border-radius: 3px; background: #2a3547; transition: background .15s; }
        .kg-split:hover .kg-split-grip, .kg-split-active .kg-split-grip { background: #6f6cf0; }
        .kg-phead { display: flex; align-items: center; gap: 10px; padding: 16px 18px; border-bottom: 1px solid #161e2a; }
        .kg-psub { padding: 14px 18px; border-bottom: 1px solid #161e2a; }

        /* ---------- timeline ---------- */
        .tl-grouphead { display: flex; align-items: baseline; gap: 10px; padding: 12px 18px 6px; }
        .tl-selbar { display: flex; align-items: center; gap: 10px; padding: 10px 18px; border-bottom: 1px solid #161e2a; }
        /* name is a CAPPED track (not 1fr) so status sits a natural distance after it; the trailing 1fr
           is an empty spacer that soaks up the leftover width on the right — content clusters left. */
        .tl-row { display: grid; grid-template-columns: auto minmax(240px, 360px) auto auto 1fr; column-gap: 16px; align-items: center; min-height: 58px; padding: 0 18px 0 0; border-top: 1px solid #111824; cursor: pointer; position: relative; transition: background .12s; }
        .tl-row:hover { background: #0f1622; }
        .tl-row.tl-selected { background: rgba(34,211,238,.045); }
        .tl-row.tl-selected::before { content: ""; position: absolute; left: 0; top: 0; bottom: 0; width: 2.5px; background: #22d3ee; }
        /* Generous selection hit-zone: owns the row's left gutter + an equal band to the right of the box,
           full row height. Toggles the checkbox (stopPropagation) instead of opening the run. */
        .tl-check { display: flex; align-items: center; justify-content: center; align-self: stretch; padding: 0 18px; cursor: pointer; }
        .tl-main { min-width: 0; display: flex; flex-direction: column; gap: 4px; padding: 9px 0; }
        .tl-nameline { display: flex; align-items: center; gap: 8px; min-width: 0; }
        .tl-name { font-size: 14px; font-weight: 700; color: #e6edf3; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .tl-meta { display: flex; align-items: center; gap: 10px; font-size: 11.5px; color: #5a6675; font-family: ui-monospace, Menlo, monospace; }
        .tl-meta .tl-id { color: #4a5563; }
        .tl-actions { display: flex; align-items: center; gap: 8px; opacity: 0; transition: opacity .15s; }
        .tl-row:hover .tl-actions, .tl-row.tl-selected .tl-actions, .tl-row:focus-within .tl-actions { opacity: 1; }
        @media (hover: none) { .tl-actions { opacity: 1; } }

        /* origin chip — color reserved for meaning */
        .tl-chip { display: inline-flex; align-items: center; gap: 5px; padding: 2px 8px; border-radius: 999px; font-size: 11px; font-weight: 700; font-family: ui-monospace, Menlo, monospace; letter-spacing: .02em; flex: none; }
        .tl-chip-event { color: #8fe7f5; background: rgba(34,211,238,.08); border: 1px solid rgba(34,211,238,.18); }
        .tl-chip-manual { color: #c3b9ff; background: rgba(124,108,240,.1); border: 1px solid rgba(124,108,240,.22); }
        .tl-chip-filtered { color: #d8b483; background: rgba(198,154,91,.1); border: 1px solid rgba(198,154,91,.22); }

        /* status pill */
        .tl-status { display: inline-flex; align-items: center; gap: 6px; padding: 4px 11px; border-radius: 999px; font-size: 12px; font-weight: 700; flex: none; }
        .tl-iconbtn { padding: 7px; border-radius: 8px; border: 1px solid transparent; display: flex; align-items: center; cursor: pointer; background: transparent; }
        .tl-iconbtn-view { color: #6b7888; }
        .tl-iconbtn-view:hover { background: rgba(255,255,255,.05); color: #cdd6e2; }
        .tl-iconbtn-del { color: #d98a98; background: rgba(240,85,109,.07); border-color: rgba(240,85,109,.16); }
        .tl-iconbtn-del:hover { background: rgba(240,85,109,.16); color: #f0556d; }
        .tl-iconbtn-stop { color: #f0b429; background: rgba(240,176,41,.08); border-color: rgba(240,176,41,.22); }
        .tl-iconbtn-stop:hover { background: rgba(240,176,41,.18); }
      `}</style>
    </div>
  );
}