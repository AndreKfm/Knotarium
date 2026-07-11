import type { CSSProperties } from 'react';
import { Check, Clock, FileText, GitBranch, Globe, OctagonX, Play, RefreshCw, RotateCcw, ShieldCheck, SkipForward, Square, Variable, Zap } from 'lucide-react';
import type { ExecutionInstance, ExecutionJournal, ExecutionStatus, NodeStatus, WorkflowDefinition } from '../../types';
import type { JournalOverviewGroup, KnownExecutionStatus, VisualNodeStatus, VisualRunStatus } from './types';

export function normalizeStatusValue(status: unknown, fallback: KnownExecutionStatus = 'Pending'): KnownExecutionStatus {
  if (typeof status === 'string' && status.trim().length > 0) {
    return status as KnownExecutionStatus;
  }

  if (status && typeof status === 'object') {
    const candidate = 'value' in status
      ? (status as { value?: unknown }).value
      : 'status' in status
        ? (status as { status?: unknown }).status
        : undefined;

    if (typeof candidate === 'string' && candidate.trim().length > 0) {
      return candidate as KnownExecutionStatus;
    }
  }

  return fallback;
}

function humanizeToken(value: string): string {
  if (!value) {
    return 'Node';
  }

  return value
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/[-_]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/^./, (character) => character.toUpperCase());
}

export function formatDuration(durationMs: number): string {
  if (!Number.isFinite(durationMs) || durationMs <= 0) {
    return '0ms';
  }

  if (durationMs < 1000) {
    return `${Math.round(durationMs)}ms`;
  }

  if (durationMs < 60_000) {
    const seconds = durationMs / 1000;
    const display = seconds < 10 ? seconds.toFixed(1) : Math.round(seconds).toString();
    return `${display.replace(/\.0$/, '')}s`;
  }

  if (durationMs < 3_600_000) {
    const minutes = Math.floor(durationMs / 60_000);
    const seconds = Math.floor((durationMs % 60_000) / 1000);
    return seconds > 0 ? `${minutes}m ${seconds}s` : `${minutes}m`;
  }

  const hours = Math.floor(durationMs / 3_600_000);
  const minutes = Math.floor((durationMs % 3_600_000) / 60_000);
  return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
}

export function getDurationBetween(startTimestamp: string | undefined, endTimestamp: string | undefined): number {
  if (!startTimestamp || !endTimestamp) {
    return 0;
  }

  const start = new Date(startTimestamp).getTime();
  const end = new Date(endTimestamp).getTime();

  if (!Number.isFinite(start) || !Number.isFinite(end)) {
    return 0;
  }

  return Math.max(0, end - start);
}

function formatOffsetLabel(timestamp: string, baselineTimestamp: string | undefined): string {
  return `+${formatDuration(getDurationBetween(baselineTimestamp, timestamp))}`;
}

function mapEventTypeToStatus(eventType: string): KnownExecutionStatus {
  if (eventType.includes('Failed') || eventType.includes('Error')) {
    return 'Failed';
  }

  if (eventType === 'ManualDecisionRecorded') {
    return 'RequiresManualDecision';
  }

  if (eventType.includes('Retry')) {
    return 'Retrying';
  }

  if (eventType.includes('Suspended') || eventType.includes('Waiting')) {
    return 'Waiting';
  }

  if (eventType.includes('Completed') || eventType.includes('Resumed')) {
    return 'Completed';
  }

  if (eventType.includes('Cancelled')) {
    return 'Cancelled';
  }

  if (eventType.includes('Started') || eventType.includes('Attempting')) {
    return 'Running';
  }

  return 'Pending';
}

export type TriggerOriginKind = 'manual' | 'webhook' | 'schedule' | 'deviceEvent';

export function normalizeTriggerOrigin(origin?: string): TriggerOriginKind {
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

/** Global-variable keys the backend stamps on a device-event run (see ExternalSignalRunEnqueuer). */
const DEVICE_EVENT_SOURCE_KEY = '__deviceEventSourceNode';
const DEVICE_EVENT_PIN_KEY = '__deviceEventFiredPin';

/**
 * Device-event provenance for a run: the origin device block (whose pin fired) and a label for that pin.
 * A device block hosts many pins but a single event fires exactly one and seeds only its downstream branch;
 * every other node never runs. This lets the timeline show the origin as "Triggered · &lt;pin&gt;" (always
 * visible) and, once the run is terminal, the untouched branches as "Skipped" — instead of phantom "Pending".
 * Returns null for non-device-event runs.
 */
export function getDeviceEventProvenance(
  execution: ExecutionInstance | null,
): { sourceNodeId?: string; firedPin?: string } | null {
  if (!execution || normalizeTriggerOrigin(execution.triggerOrigin) !== 'deviceEvent') {
    return null;
  }
  const globals = execution.globalVariables ?? {};
  const sourceNodeId = typeof globals[DEVICE_EVENT_SOURCE_KEY] === 'string' ? (globals[DEVICE_EVENT_SOURCE_KEY] as string) : undefined;
  const firedPin = typeof globals[DEVICE_EVENT_PIN_KEY] === 'string' ? (globals[DEVICE_EVENT_PIN_KEY] as string) : undefined;
  return { sourceNodeId, firedPin };
}

// ExecutionStatus enum ordinals that mean "no further nodes will run": Cancelled(3), Completed(4),
// Failed(5), Discarded(7). Kept in sync with Backend/Knotarium.Core/Domain/ExecutionStatus.cs.
const TERMINAL_STATUS_ORDINALS = new Set([3, 4, 5, 7]);

/**
 * Whether an execution has reached a terminal state (no further nodes will run). Handles the numeric enum
 * form too: the execution *detail* endpoint serializes status as a number (e.g. 4 = Completed), which
 * normalizeStatusValue folds to 'Pending' — so a naive string compare would treat a finished run as still
 * pending. Check the numeric form explicitly before falling back to the string/object form.
 */
export function isTerminalExecutionStatus(status: unknown): boolean {
  const raw = status && typeof status === 'object' && 'value' in status ? (status as { value?: unknown }).value : status;
  if (typeof raw === 'number') {
    return TERMINAL_STATUS_ORDINALS.has(raw);
  }
  if (typeof raw === 'string' && /^\d+$/.test(raw.trim())) {
    return TERMINAL_STATUS_ORDINALS.has(Number(raw));
  }
  const normalized = normalizeStatusValue(status);
  // (Discarded is only reachable via the numeric ordinal above; it isn't in the string status union.)
  return normalized === 'Completed' || normalized === 'Failed' || normalized === 'Cancelled';
}

export function formatClockTime(timestamp: string | undefined): string | null {
  if (!timestamp) {
    return null;
  }

  const parsed = new Date(timestamp);
  if (Number.isNaN(parsed.getTime())) {
    return null;
  }

  return parsed.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

export function isSkippedData(data: Record<string, unknown> | undefined): boolean {
  if (!data) {
    return false;
  }

  if (data.skipped === true) {
    return true;
  }

  return typeof data.manualDecision === 'string' && data.manualDecision.trim().toLowerCase() === 'skip';
}

function getJournalEntryStatus(item: ExecutionJournal): KnownExecutionStatus {
  if (isSkippedData(item.data)) {
    return 'Skipped';
  }

  return mapEventTypeToStatus(item.eventType);
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function stripNodeIdFromMessage(message: string, nodeId: string | undefined): string {
  if (!nodeId) {
    return message;
  }

  return message
    .replace(new RegExp("(['\"`])" + escapeRegExp(nodeId) + "\\1", 'g'), '')
    .replace(new RegExp(`node\\s+${escapeRegExp(nodeId)}`, 'ig'), 'node')
    .replace(new RegExp(escapeRegExp(nodeId), 'g'), '')
    .replace(/\s{2,}/g, ' ')
    .replace(/\s+([.,])/g, '$1')
    .trim();
}

function resolveJournalNodeId(item: ExecutionJournal, nodeMap: Map<string, WorkflowDefinition['nodes'][number]>): string | undefined {
  const explicitNodeId = item.nodeId?.value;
  if (explicitNodeId) {
    return explicitNodeId;
  }

  const quotedNodeIdMatch = item.message.match(/'([^']+)'/);
  if (quotedNodeIdMatch && nodeMap.has(quotedNodeIdMatch[1])) {
    return quotedNodeIdMatch[1];
  }

  return undefined;
}

// Nodes inlined from a subflow carry a prefixed id like `subflow-abc/log-xyz` (nested subflows chain
// further: `subflow-a/subflow-b/log-xyz`). These nodes don't exist in the parent definition, so a
// plain lookup misses them. Recover the inner node's type from its generated id (`<type>-<random>`).
function getSubflowChildType(nodeId: string | undefined): string | null {
  if (!nodeId || !nodeId.includes('/')) {
    return null;
  }
  const innerId = nodeId.slice(nodeId.lastIndexOf('/') + 1);
  const dashIndex = innerId.indexOf('-');
  const inferredType = dashIndex > 0 ? innerId.slice(0, dashIndex) : innerId;
  return inferredType.length > 0 ? inferredType : null;
}

function isSubflowChildNodeId(nodeId: string | undefined): boolean {
  return typeof nodeId === 'string' && nodeId.includes('/');
}

function getNodeDisplayName(
  node: WorkflowDefinition['nodes'][number] | undefined,
  metadataMap: Record<string, { displayName: string }>,
  nodeId?: string,
): string {
  if (!node) {
    // Inlined subflow node: name it after the inner node's type (Log, Start, End, …) rather than
    // a generic "Node", so the timeline reads meaningfully inside a subflow.
    const innerType = getSubflowChildType(nodeId);
    if (innerType) {
      return metadataMap[innerType]?.displayName || humanizeToken(innerType);
    }
    return 'Node';
  }

  if (typeof node.properties?.label === 'string' && node.properties.label.trim().length > 0) {
    return node.properties.label.trim();
  }

  return metadataMap[node.type]?.displayName || humanizeToken(node.type);
}

export function formatOutputLabel(key: string): string {
  return humanizeToken(key).toUpperCase();
}

export function getCollapsedGroupSummary(group: JournalOverviewGroup): string {
  if (group.nodeType === 'end') {
    return 'End execution';
  }

  if (group.hint) {
    return group.hint;
  }

  if (group.nodeType === 'log') {
    const message = group.latestPayload?.message;
    if (typeof message === 'string' && message.trim().length > 0) {
      return `"${message.trim()}"`;
    }
  }

  if (group.latestPayload) {
    const firstEntry = Object.entries(group.latestPayload).find(([, value]) => value !== undefined && value !== null);
    if (firstEntry) {
      const [, value] = firstEntry;
      if (typeof value === 'string') {
        const trimmed = value.trim();
        return trimmed.length > 0 ? trimmed : 'View details';
      }

      if (typeof value === 'number' || typeof value === 'boolean') {
        return String(value);
      }
    }
  }

  return group.entries[group.entries.length - 1]?.message || 'View details';
}

function isIsoTimestampString(value: unknown): value is string {
  if (typeof value !== 'string') {
    return false;
  }

  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(value.trim())) {
    return false;
  }

  return Number.isFinite(new Date(value).getTime());
}

function shouldRenderPayloadAsTimestamp(key: string, value: unknown): value is string {
  if (!isIsoTimestampString(value)) {
    return false;
  }

  return /(?:^|_|-)(?:at|time|timestamp|date)$/i.test(key) || /(?:At|Time|Timestamp|Date)$/i.test(key);
}

export function getTimelineSummaryStatus(
  executionStatus: KnownExecutionStatus | null,
  groups: JournalOverviewGroup[],
): KnownExecutionStatus | null {
  const normalizedExecutionStatus = normalizeStatusValue(executionStatus ?? 'Pending');

  if (normalizedExecutionStatus !== 'Pending' || groups.length === 0) {
    return executionStatus;
  }

  const normalizedGroupStatuses = groups.map((group) => normalizeStatusValue(group.status));

  if (normalizedGroupStatuses.some((status) => status === 'Failed')) {
    return 'Failed';
  }

  if (normalizedGroupStatuses.some((status) => status === 'Running')) {
    return 'Running';
  }

  if (normalizedGroupStatuses.some((status) => status === 'Waiting' || status === 'Retrying' || status === 'RequiresManualDecision')) {
    return normalizedGroupStatuses.find((status) => status === 'RequiresManualDecision' || status === 'Retrying' || status === 'Waiting') ?? executionStatus;
  }

  if (normalizedGroupStatuses.every((status) => status === 'Completed' || status === 'Skipped' || status === 'Triggered')) {
    return 'Completed';
  }

  return executionStatus;
}

export function getTimelineHeaderStatusLabel(status: KnownExecutionStatus | null): string {
  switch (normalizeStatusValue(status ?? 'Pending')) {
    case 'Completed':
      return 'Success';
    case 'Running':
      return 'Running';
    case 'Failed':
      return 'Failed';
    case 'Waiting':
      return 'Waiting';
    case 'Retrying':
      return 'Retrying';
    case 'Cancelled':
      return 'Cancelled';
    default:
      return 'Pending';
  }
}

export function getStatusChrome(status: unknown): { accent: string; border: string; background: string; text: string } {
  switch (normalizeStatusValue(status)) {
    case 'Completed':
      return {
        accent: '#34d399',
        border: 'rgba(52, 211, 153, 0.28)',
        background: 'rgba(52, 211, 153, 0.12)',
        text: '#6ee7b7',
      };
    case 'Failed':
      return {
        accent: '#f87171',
        border: 'rgba(248, 113, 113, 0.28)',
        background: 'rgba(248, 113, 113, 0.12)',
        text: '#fecaca',
      };
    case 'Running':
      return {
        accent: '#38bdf8',
        border: 'rgba(56, 189, 248, 0.28)',
        background: 'rgba(56, 189, 248, 0.12)',
        text: '#bae6fd',
      };
    case 'Waiting':
      return {
        accent: '#22d3ee',
        border: 'rgba(34, 211, 238, 0.28)',
        background: 'rgba(34, 211, 238, 0.12)',
        text: '#a5f3fc',
      };
    case 'Retrying':
      return {
        accent: '#60a5fa',
        border: 'rgba(96, 165, 250, 0.28)',
        background: 'rgba(96, 165, 250, 0.12)',
        text: '#bfdbfe',
      };
    case 'RequiresManualDecision':
      return {
        accent: '#fbbf24',
        border: 'rgba(251, 191, 36, 0.28)',
        background: 'rgba(251, 191, 36, 0.12)',
        text: '#fde68a',
      };
    case 'Cancelled':
      return {
        accent: '#94a3b8',
        border: 'rgba(148, 163, 184, 0.28)',
        background: 'rgba(148, 163, 184, 0.12)',
        text: '#cbd5e1',
      };
    case 'Skipped':
      return {
        accent: '#94a3b8',
        border: 'rgba(148, 163, 184, 0.2)',
        background: 'rgba(148, 163, 184, 0.08)',
        text: '#cbd5e1',
      };
    case 'Triggered':
      // Emerald, matching the device-event trigger pill — the origin that fired this run.
      return {
        accent: '#34d399',
        border: 'rgba(52, 211, 153, 0.3)',
        background: 'rgba(52, 211, 153, 0.12)',
        text: '#a7f3d0',
      };
    default:
      return {
        accent: '#7c8a9c',
        border: 'rgba(124, 138, 156, 0.24)',
        background: 'rgba(124, 138, 156, 0.12)',
        text: '#cbd5e1',
      };
  }
}

export function getEventTagLabel(eventType: string): string {
  if (eventType.includes('Started')) {
    return 'STARTED';
  }

  if (eventType.includes('Completed') || eventType.includes('Resumed')) {
    return 'DONE';
  }

  if (eventType.includes('Failed') || eventType.includes('Error')) {
    return 'FAILED';
  }

  if (eventType.includes('Suspended') || eventType.includes('Waiting')) {
    return 'WAITING';
  }

  if (eventType === 'ManualDecisionRecorded') {
    return 'MANUAL';
  }

  if (eventType.includes('Retry')) {
    return 'RETRY';
  }

  return humanizeToken(eventType).toUpperCase();
}

export function renderJournalNodeIcon(nodeType: string | undefined, status: KnownExecutionStatus, size = 16) {
  const normalizedStatus = normalizeStatusValue(status);
  const chrome = getStatusChrome(normalizedStatus);
  const iconStyle = normalizedStatus === 'Running'
    ? { animation: 'execution-timeline-spin 1s linear infinite' }
    : undefined;

  switch (normalizedStatus) {
    case 'Completed':
      return <Check size={size} color={chrome.accent} />;
    case 'Failed':
      return <OctagonX size={size} color={chrome.accent} />;
    case 'Running':
      return <RefreshCw size={size} color={chrome.accent} style={iconStyle} />;
    case 'Waiting':
      return <Clock size={size} color={chrome.accent} />;
    case 'Retrying':
      return <RotateCcw size={size} color={chrome.accent} />;
    case 'RequiresManualDecision':
      return <ShieldCheck size={size} color={chrome.accent} />;
    case 'Skipped':
      return <SkipForward size={size} color={chrome.accent} />;
    case 'Triggered':
      return <Zap size={size} color={chrome.accent} />;
    case 'Cancelled':
      return <Square size={size} color={chrome.accent} />;
    case 'Pending':
      return <span style={{ fontSize: size - 1, color: chrome.accent, lineHeight: 1 }}>○</span>;
    default:
      switch (nodeType) {
        case 'start':
          return <Play size={size} fill="#34d399" color="#34d399" />;
        case 'condition':
          return <GitBranch size={size} color="#fbbf24" />;
        case 'setVariable':
          return <Variable size={size} color="#38bdf8" />;
        case 'httpRequest':
          return <Globe size={size} color="#a78bfa" />;
        case 'delay':
        case 'scheduler':
          return <Clock size={size} color="#22d3ee" />;
        case 'log':
          return <FileText size={size} color="#0ea5e9" />;
        case 'end':
          return <Square size={size} fill="#fb7185" color="#fb7185" />;
        default:
          return <FileText size={size} color={chrome.accent} />;
      }
  }
}

function getNodeTimelineStatus(
  nodeId: string,
  execution: ExecutionInstance | null,
  latestJournalStatus: KnownExecutionStatus | undefined,
): KnownExecutionStatus {
  if (latestJournalStatus) {
    return latestJournalStatus;
  }

  const state = execution?.nodeStates?.find((nodeState) => nodeState.nodeId.value === nodeId);
  if (state) {
    if (isSkippedData(state.outputs)) {
      return 'Skipped';
    }

    return mapNodeStatus(state.status, execution?.status ?? 'Pending');
  }

  // Device-event run: the origin block is the trigger source (never a work node), and any node with no
  // state once the run is terminal belongs to a pin/branch this event didn't fire. Surface those as
  // "Triggered" / "Skipped" rather than a phantom "Pending".
  const device = getDeviceEventProvenance(execution);
  if (device) {
    if (device.sourceNodeId && nodeId === device.sourceNodeId) {
      return 'Triggered';
    }
    if (isTerminalExecutionStatus(execution?.status)) {
      return 'Skipped';
    }
  }

  return 'Pending';
}

export function getConnectorStyle(status: KnownExecutionStatus, nextStatus: KnownExecutionStatus | undefined): CSSProperties {
  const normalizedStatus = normalizeStatusValue(status);
  const normalizedNextStatus = nextStatus ? normalizeStatusValue(nextStatus) : undefined;

  if (normalizedStatus === 'Running') {
    return {
      width: '2px',
      flex: 1,
      marginTop: '6px',
      marginBottom: '6px',
      borderRadius: '999px',
      backgroundImage: 'repeating-linear-gradient(to bottom, rgba(34, 211, 238, 0.95) 0 8px, rgba(34, 211, 238, 0.15) 8px 16px)',
      backgroundSize: '100% 24px',
      animation: 'execution-timeline-dash 0.9s linear infinite',
    };
  }

  if (normalizedStatus === 'Failed' || normalizedStatus === 'Skipped' || normalizedNextStatus === 'Skipped') {
    return {
      width: '2px',
      flex: 1,
      marginTop: '6px',
      marginBottom: '6px',
      borderRadius: '999px',
      backgroundImage: 'repeating-linear-gradient(to bottom, rgba(148, 163, 184, 0.6) 0 6px, transparent 6px 12px)',
    };
  }

  if (normalizedStatus === 'Completed' && normalizedNextStatus === 'Completed') {
    return {
      width: '2px',
      flex: 1,
      marginTop: '6px',
      marginBottom: '6px',
      borderRadius: '999px',
      background: 'linear-gradient(rgba(52, 211, 153, 0.95), rgba(52, 211, 153, 0.35))',
    };
  }

  return {
    width: '2px',
    flex: 1,
    marginTop: '6px',
    marginBottom: '6px',
    borderRadius: '999px',
    background: 'rgba(100, 116, 139, 0.35)',
  };
}

export function hasDataPayload(data: Record<string, unknown> | undefined): boolean {
  return !!data && Object.keys(data).length > 0;
}

export function isSimplePayload(data: Record<string, unknown> | undefined): boolean {
  if (!hasDataPayload(data)) {
    return false;
  }

  const entries = Object.entries(data ?? {});
  return entries.length <= 2 && entries.every(([, value]) => typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean' || value === null);
}

function getNodeHint(node: WorkflowDefinition['nodes'][number] | undefined): string | null {
  if (!node) {
    return null;
  }

  const properties = node.properties || {};

  switch (node.type) {
    case 'scheduler': {
      const cronExpression = typeof properties.cronExpression === 'string' ? properties.cronExpression : null;
      const timeZoneId = typeof properties.timeZoneId === 'string' ? properties.timeZoneId : null;

      if (!cronExpression) {
        return null;
      }

      return timeZoneId ? `Schedule ${cronExpression} · ${timeZoneId}` : `Schedule ${cronExpression}`;
    }
    case 'log':
      return typeof properties.message === 'string' && properties.message.trim().length > 0
        ? `"${properties.message.trim()}"`
        : null;
    case 'httpRequest': {
      const method = typeof properties.method === 'string' ? properties.method : 'GET';
      const url = typeof properties.url === 'string' ? properties.url : null;
      return url ? `${method.toUpperCase()} ${url}` : method.toUpperCase();
    }
    case 'setVariable': {
      const variableName = typeof properties.variableName === 'string' ? properties.variableName : 'variable';
      return `Set ${variableName}`;
    }
    case 'delay': {
      if (typeof properties.delayMs === 'number') {
        return `Wait ${properties.delayMs}ms`;
      }

      return typeof properties.duration === 'string' ? `Wait ${properties.duration}` : null;
    }
    default:
      return null;
  }
}

export function getGroupFooterMessage(status: KnownExecutionStatus | null): string {
  switch (normalizeStatusValue(status ?? 'Pending')) {
    case 'Completed':
      return 'Workflow run completed successfully.';
    case 'Failed':
      return 'Workflow run failed. Review the highlighted node entries above.';
    case 'Retrying':
      return 'Workflow is waiting for the next retry attempt.';
    case 'Waiting':
      return 'Workflow is paused and waiting for an external resume signal.';
    case 'Running':
      return 'Workflow is still running. New node updates will stream into this timeline.';
    case 'Cancelled':
      return 'Workflow run was cancelled before reaching the end node.';
    default:
      return 'Workflow timeline will update as execution journal events arrive.';
  }
}

export function buildJournalOverview(
  journal: ExecutionJournal[],
  workflow: WorkflowDefinition | null,
  metadataMap: Record<string, { displayName: string }>,
  execution: ExecutionInstance | null,
): JournalOverviewGroup[] {
  if (journal.length === 0 && !workflow?.nodes?.length) {
    return [];
  }

  const sortedJournal = [...journal].sort((left, right) => new Date(left.timestamp).getTime() - new Date(right.timestamp).getTime());
  const baselineTimestamp = sortedJournal[0]?.timestamp || execution?.createdAt;
  const nodeMap = new Map((workflow?.nodes || []).map((node) => [node.id.value, node]));
  const nodeOrder = new Map((workflow?.nodes || []).map((node, index) => [node.id.value, index]));

  // Track how many times each node has started so we can create a separate group per iteration.
  const nodeIterationCount = new Map<string, number>();
  // groupedEntries key is `nodeId:iterationIndex` to support multiple runs of the same node.
  const groupedEntries = new Map<string, JournalOverviewGroup & { firstTimestamp?: string; lastTimestamp?: string; order: number }>();
  // Track the active group key per nodeId so we know which group to append to.
  const activeGroupKey = new Map<string, string>();

  for (const item of sortedJournal) {
    const nodeId = resolveJournalNodeId(item, nodeMap);
    if (!nodeId) {
      continue;
    }

    const node = nodeMap.get(nodeId);
    const resolvedStatus = getJournalEntryStatus(item);

    // A new NodeExecutionStarted means a fresh iteration — open a new group.
    if (item.eventType === 'NodeExecutionStarted') {
      const iteration = (nodeIterationCount.get(nodeId) ?? 0) + 1;
      nodeIterationCount.set(nodeId, iteration);
      const groupKey = iteration === 1 ? nodeId : `${nodeId}:${iteration}`;
      const iterationLabel = iteration > 1 ? ` (×${iteration})` : '';
      activeGroupKey.set(nodeId, groupKey);
      groupedEntries.set(groupKey, {
        key: groupKey,
        nodeId,
        nodeType: node?.type ?? getSubflowChildType(nodeId) ?? undefined,
        title: `${getNodeDisplayName(node, metadataMap, nodeId)}${iterationLabel}`,
        subtitle: nodeId,
        isSubflowChild: isSubflowChildNodeId(nodeId),
        hint: getNodeHint(node),
        status: resolvedStatus,
        durationLabel: '+0ms',
        entries: [],
        isWorkflow: false,
        latestPayload: undefined,
        firstTimestamp: item.timestamp,
        lastTimestamp: item.timestamp,
        order: nodeOrder.get(nodeId) ?? Number.MAX_SAFE_INTEGER,
      });
    }

    // Append to the active group for this node (or create one if no Started event was seen).
    if (!activeGroupKey.has(nodeId)) {
      activeGroupKey.set(nodeId, nodeId);
    }
    const currentKey = activeGroupKey.get(nodeId)!;
    if (!groupedEntries.has(currentKey)) {
      groupedEntries.set(currentKey, {
        key: currentKey,
        nodeId,
        nodeType: node?.type ?? getSubflowChildType(nodeId) ?? undefined,
        title: getNodeDisplayName(node, metadataMap, nodeId),
        subtitle: nodeId,
        isSubflowChild: isSubflowChildNodeId(nodeId),
        hint: getNodeHint(node),
        status: resolvedStatus,
        durationLabel: '+0ms',
        entries: [],
        isWorkflow: false,
        latestPayload: undefined,
        firstTimestamp: item.timestamp,
        lastTimestamp: item.timestamp,
        order: nodeOrder.get(nodeId) ?? Number.MAX_SAFE_INTEGER,
      });
    }

    const group = groupedEntries.get(currentKey)!;
    group.entries.push({
      id: item.id,
      eventType: item.eventType,
      message: stripNodeIdFromMessage(item.message, nodeId),
      offsetLabel: formatOffsetLabel(item.timestamp, baselineTimestamp),
      status: resolvedStatus,
      data: item.data,
    });
    group.lastTimestamp = item.timestamp;
    group.status = resolvedStatus;
    if (hasDataPayload(item.data)) {
      group.latestPayload = item.data;
    }
  }

  // Device-event provenance: the origin block gets a "Triggered · <pin>" hint so the fired pin is visible.
  const deviceProvenance = getDeviceEventProvenance(execution);

  for (const node of workflow?.nodes || []) {
    // For nodes that ran multiple iterations the first group key is just nodeId; subsequent ones are nodeId:N.
    // We only need to patch up the *first* group (or create a placeholder if no journal entries at all).
    const existingGroup = groupedEntries.get(node.id.value);
    const latestJournalStatus = existingGroup?.entries[existingGroup.entries.length - 1]?.status;
    const timelineStatus = getNodeTimelineStatus(node.id.value, execution, latestJournalStatus);
    const isTriggerOrigin = Boolean(deviceProvenance?.sourceNodeId && deviceProvenance.sourceNodeId === node.id.value);
    const nodeHint = isTriggerOrigin && deviceProvenance?.firedPin ? `Triggered · ${deviceProvenance.firedPin}` : getNodeHint(node);
    // The trigger origin is t0 of the run — it fired before any node ran. Anchor it to the run start so it
    // sorts to the top of the timeline (ahead of the first downstream node) rather than sinking below by
    // virtue of having no journal timestamp of its own.
    const originTimestamp = isTriggerOrigin ? (execution?.createdAt ?? baselineTimestamp) : undefined;

    if (existingGroup) {
      existingGroup.status = timelineStatus;
      existingGroup.hint = nodeHint;
      if (originTimestamp) {
        existingGroup.firstTimestamp = originTimestamp;
      }
      if (!existingGroup.latestPayload) {
        const nodeState = execution?.nodeStates?.find((state) => state.nodeId.value === node.id.value);
        if (hasDataPayload(nodeState?.outputs)) {
          existingGroup.latestPayload = nodeState?.outputs;
        }
      }

      continue;
    }

    const nodeState = execution?.nodeStates?.find((state) => state.nodeId.value === node.id.value);

    groupedEntries.set(node.id.value, {
      key: node.id.value,
      nodeId: node.id.value,
      nodeType: node.type,
      title: getNodeDisplayName(node, metadataMap),
      subtitle: node.id.value,
      hint: nodeHint,
      status: timelineStatus,
      durationLabel: '+0ms',
      entries: [],
      isWorkflow: false,
      latestPayload: hasDataPayload(nodeState?.outputs) ? nodeState?.outputs : undefined,
      firstTimestamp: originTimestamp,
      lastTimestamp: undefined,
      order: isTriggerOrigin ? -1 : (nodeOrder.get(node.id.value) ?? Number.MAX_SAFE_INTEGER),
    });
  }

  return [...groupedEntries.values()]
    .sort((left, right) => {
      const leftTime = left.firstTimestamp ? new Date(left.firstTimestamp).getTime() : Number.MAX_SAFE_INTEGER;
      const rightTime = right.firstTimestamp ? new Date(right.firstTimestamp).getTime() : Number.MAX_SAFE_INTEGER;

      if (leftTime !== rightTime) {
        return leftTime - rightTime;
      }

      return left.order - right.order;
    })
    .map((group) => ({
      key: group.key,
      nodeId: group.nodeId,
      nodeType: group.nodeType,
      title: group.title,
      subtitle: group.subtitle,
      hint: group.hint,
      status: group.status,
      durationLabel: group.lastTimestamp ? formatOffsetLabel(group.lastTimestamp, baselineTimestamp) : '+0ms',
      entries: group.entries,
      isWorkflow: group.isWorkflow,
      isSubflowChild: group.isSubflowChild,
      latestPayload: group.latestPayload,
    } satisfies JournalOverviewGroup));
}

function tryFormatJsonString(value: string): string | null {
  const trimmed = value.trim();
  if ((!trimmed.startsWith('{') || !trimmed.endsWith('}')) && (!trimmed.startsWith('[') || !trimmed.endsWith(']'))) {
    return null;
  }

  try {
    return JSON.stringify(JSON.parse(trimmed), null, 2);
  } catch {
    return null;
  }
}

export function formatPayloadValue(value: unknown): string {
  if (typeof value === 'string') {
    return tryFormatJsonString(value) ?? value;
  }

  if (value === null || value === undefined) {
    return String(value);
  }

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

export function getPayloadValueStyle(key: string, value: unknown): CSSProperties {
  return {
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
    wordBreak: shouldRenderPayloadAsTimestamp(key, value) ? 'break-all' : 'break-word',
    overflowX: 'auto',
    fontSize: shouldRenderPayloadAsTimestamp(key, value) ? '0.82rem' : undefined,
  };
}

export function getStatusFromJournal(nodeId: string, journal: ExecutionJournal[]): string {
  const nodeEvents = journal.filter((item) => item.nodeId?.value === nodeId);
  if (nodeEvents.length === 0) {
    return 'Pending';
  }

  const sortedEvents = [...nodeEvents].sort((a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime());
  const latestEvent = sortedEvents[sortedEvents.length - 1];

  if (latestEvent.eventType === 'NodeExecutionStarted') {
    return 'Running';
  }
  if (latestEvent.eventType === 'NodeExecutionCompleted' || latestEvent.eventType === 'NodeResumed') {
    return 'Completed';
  }
  if (latestEvent.eventType === 'NodeExecutionFailed') {
    return 'Failed';
  }
  if (latestEvent.eventType === 'WorkflowSuspended') {
    return 'Waiting';
  }
  return 'Pending';
}

const STATUS_RANKS: Record<string, number> = {
  Pending: 0,
  Waiting: 1,
  Running: 2,
  Retrying: 2,
  RequiresManualDecision: 3,
  Completed: 4,
  Failed: 4,
  Cancelled: 4,
  Skipped: 4,
  Triggered: 4,
};

export function isProgressiveTransition(currentStatus: string, newStatus: string): boolean {
  const currentRank = STATUS_RANKS[currentStatus] ?? 0;
  const newRank = STATUS_RANKS[newStatus] ?? 0;
  return newRank >= currentRank;
}

// ExecutionStatus enum ordinal → visual run status. The detail endpoint serializes status as a number,
// which normalizeStatusValue folds to 'Pending' — so a completed run (4) would otherwise show "Pending"
// in the header pill. Kept in sync with Backend/Knotarium.Core/Domain/ExecutionStatus.cs.
const EXECUTION_STATUS_BY_ORDINAL: Record<number, VisualRunStatus> = {
  0: 'Pending',
  1: 'Running',
  2: 'Waiting', // Suspended
  3: 'Cancelled',
  4: 'Completed',
  5: 'Failed',
  6: 'Retrying', // WaitingForRetry
  7: 'Failed', // Discarded (a failed run triaged away)
};

export function mapExecutionStatus(status: ExecutionStatus): VisualRunStatus {
  const raw = status && typeof status === 'object' && 'value' in status ? (status as { value?: unknown }).value : status;
  if (typeof raw === 'number' && raw in EXECUTION_STATUS_BY_ORDINAL) {
    return EXECUTION_STATUS_BY_ORDINAL[raw];
  }
  if (typeof raw === 'string' && /^\d+$/.test(raw.trim()) && Number(raw) in EXECUTION_STATUS_BY_ORDINAL) {
    return EXECUTION_STATUS_BY_ORDINAL[Number(raw)];
  }
  switch (normalizeStatusValue(status)) {
    case 'Suspended':
      return 'Waiting';
    case 'WaitingForRetry':
      return 'Retrying';
    case 'Cancelled':
      return 'Cancelled';
    default:
      return normalizeStatusValue(status, 'Pending') as VisualRunStatus;
  }
}

export function mapNodeStatus(status: NodeStatus, executionStatus: ExecutionStatus): VisualNodeStatus {
  const normalizedStatus = normalizeStatusValue(status);
  const normalizedExecutionStatus = normalizeStatusValue(executionStatus);

  if (normalizedStatus === 'RequiresManualDecision') {
    return 'RequiresManualDecision';
  }

  if (normalizedExecutionStatus === 'WaitingForRetry' && normalizedStatus === 'Failed') {
    return 'Retrying';
  }

  return normalizedStatus as VisualNodeStatus;
}

export function createStatusClassName(status: unknown): string {
  const normalizedStatus = normalizeStatusValue(status);
  return `status-badge status-badge-${normalizedStatus.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase()}`;
}

export function getStatusLabel(status: unknown): string {
  const normalizedStatus = normalizeStatusValue(status);
  if (normalizedStatus === 'RequiresManualDecision') {
    return 'Manual Decision';
  }

  if (normalizedStatus === 'Skipped') {
    return 'Skipped';
  }

  return normalizedStatus;
}

export function getLatestPendingAttemptId(nodeId: string, journal: ExecutionJournal[]): string | undefined {
  const attempts = journal
    .filter((item) => item.nodeId?.value === nodeId && item.eventType === 'AttemptingExternalEffect')
    .slice()
    .reverse();

  for (const attempt of attempts) {
    const attemptId = attempt.data?.AttemptId ?? attempt.data?.attemptId;
    if (typeof attemptId !== 'string' || !attemptId) {
      continue;
    }

    const hasCompletion = journal.some((entry) => {
      if (entry.nodeId?.value !== nodeId) {
        return false;
      }

      if (entry.eventType !== 'NodeExecutionCompleted' && entry.eventType !== 'NodeExecutionFailed') {
        return false;
      }

      return (entry.data?.AttemptId ?? entry.data?.attemptId) === attemptId;
    });

    if (!hasCompletion) {
      return attemptId;
    }
  }

  return undefined;
}