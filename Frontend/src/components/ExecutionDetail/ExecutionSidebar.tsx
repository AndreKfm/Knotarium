import { Fragment, type RefObject, useMemo, useState } from 'react';
import { ChevronDown, Clock3, Copy, Eye, EyeOff, FolderLock, OctagonX, Play, RotateCcw, ShieldAlert, ShieldCheck, SkipForward } from 'lucide-react';
import type { ExecutionInstance, WorkflowScheduleSummary } from '../../types';
import type { JournalOverviewGroup, KnownExecutionStatus, VisualRunStatus } from './types';
import { usePendingFileAccessGrantStore } from '../../stores/usePendingFileAccessGrantStore';
import { useResizableWidth, ResizeHandle } from '../shared/useResizablePanel';

// A File Access policy denial (guard-blocked path) — as opposed to a free-space denial or an ordinary IO
// error — carries "File access denied" and names the attempted path in single quotes. Returns the parent
// directory to grant plus a short "what to fix" title, or null when this failure isn't a grantable denial.
function fileAccessRemediation(group: JournalOverviewGroup): { grantPath: string; title: string } | null {
  const fromPayload = typeof group.latestPayload?.error === 'string' ? group.latestPayload.error : '';
  const fromEntry = group.entries.map((e) => e.message).find((m) => m.includes('File access denied')) ?? '';
  const err = fromPayload.includes('File access denied') ? fromPayload : fromEntry;
  if (!err.includes('File access denied')) return null;
  const match = err.match(/'([^']+)'/);
  if (!match) return null;
  const attempted = match[1];
  const idx = Math.max(attempted.lastIndexOf('\\'), attempted.lastIndexOf('/'));
  const grantPath = idx <= 0
    ? attempted
    : (/^[a-zA-Z]:$/.test(attempted.slice(0, idx)) ? attempted.slice(0, idx) + '\\' : attempted.slice(0, idx));

  let title = 'Path is not permitted';
  if (/outside every permitted directory/.test(err)) title = 'Path is outside permitted directories';
  else if (/does not permit writing/.test(err)) title = 'Path is granted read-only';
  else if (/does not permit reading/.test(err)) title = 'Path is not granted for reading';
  else if (/nothing is granted|denied by default/.test(err)) title = 'No paths are permitted yet';
  return { grantPath, title };
}
import {
  createStatusClassName,
  formatOutputLabel,
  formatPayloadValue,
  getCollapsedGroupSummary,
  getConnectorStyle,
  getEventTagLabel,
  getGroupFooterMessage,
  getPayloadValueStyle,
  getStatusChrome,
  getStatusLabel,
  getTimelineHeaderStatusLabel,
  hasDataPayload,
  isSimplePayload,
  normalizeStatusValue,
  renderJournalNodeIcon,
} from './timelineUtils';

type ManualActionNode = {
  id: string;
  nodeId: { value: string };
  errorMessage?: string | null;
};

type ExecutionSidebarProps = {
  execution: ExecutionInstance | null;
  workflowSchedules: WorkflowScheduleSummary[];
  actionNodes: ManualActionNode[];
  manualDecisionNodeId: string | null;
  triggeringScheduleNodeId: string | null;
  journal: Array<{ timestamp: string }>;
  journalOverview: JournalOverviewGroup[];
  timelineSummaryStatus: KnownExecutionStatus | null;
  executionVisualStatus: VisualRunStatus | null;
  nodeTimelineCount: number;
  timelineDurationLabel: string;
  triggerOrigin: 'manual' | 'webhook' | 'schedule' | 'deviceEvent';
  triggerPillLabel: string;
  triggerDescription: string;
  expandedGroupKeys: Set<string>;
  consoleEndRef: RefObject<HTMLDivElement | null>;
  onFireSchedule: (scheduleNodeId: string) => void;
  onManualDecision: (nodeId: string, decision: 'Retry' | 'Skip' | 'Fail') => void;
  onToggleGroupExpansion: (groupKey: string) => void;
  /** Node id of the step currently selected in the time-travel inspector (cross-link highlight). */
  activeStepNodeId?: string;
  /** When provided (step mode), clicking a timeline row selects that node as the current step. */
  onSelectStepNode?: (nodeId: string) => void;
  /** The error-handler run spawned by this failed run, if any (drives the handler affordances). */
  handlerRun?: { id: string; status: string } | null;
  /** Opens the handler-run drawer. */
  onOpenHandler?: () => void;
  /** Navigate to Settings → File Access (the CTA pre-fills the denied path via the pending-grant store). */
  onGrantFileAccess?: () => void;
};

export function ExecutionSidebar({
  execution,
  workflowSchedules,
  actionNodes,
  manualDecisionNodeId,
  triggeringScheduleNodeId,
  journal,
  journalOverview,
  timelineSummaryStatus,
  executionVisualStatus,
  nodeTimelineCount,
  timelineDurationLabel,
  triggerOrigin,
  triggerPillLabel,
  triggerDescription,
  expandedGroupKeys,
  consoleEndRef,
  onFireSchedule,
  onManualDecision,
  onToggleGroupExpansion,
  activeStepNodeId,
  onSelectStepNode,
  handlerRun,
  onOpenHandler,
  onGrantFileAccess,
}: ExecutionSidebarProps) {
  const showHandlerAffordance = timelineSummaryStatus === 'Failed' && !!handlerRun && !!onOpenHandler;
  const requestFileAccessGrant = usePendingFileAccessGrantStore((state) => state.requestGrant);
  const [copiedGrantPath, setCopiedGrantPath] = useState<string | null>(null);

  // A device-event trigger fans out to every wired pin but only one fires; the rest render as "Skipped"
  // noise. Hide those by default (opt-in reveal). Scoped to device-event runs so a meaningful skip
  // elsewhere — an operator "Skip" decision, a Condition's untaken branch — stays visible as before.
  const [showSkipped, setShowSkipped] = useState(false);
  const { width: panelWidth, startResize: startPanelResize } = useResizableWidth('execution-sidebar-width', 420, 320, 900);
  const canHideSkipped = triggerOrigin === 'deviceEvent';
  const skippedCount = useMemo(
    () => (canHideSkipped ? journalOverview.filter((group) => normalizeStatusValue(group.status) === 'Skipped').length : 0),
    [journalOverview, canHideSkipped],
  );
  const visibleGroups = useMemo(
    () => (!canHideSkipped || showSkipped
      ? journalOverview
      : journalOverview.filter((group) => normalizeStatusValue(group.status) !== 'Skipped')),
    [journalOverview, showSkipped, canHideSkipped],
  );
  // Trigger-pill colours per origin: scheduled = cyan, device event = emerald (matches the device
  // block's event lane), manual/webhook = violet.
  const triggerPalette = triggerOrigin === 'schedule'
    ? {
        bg: 'linear-gradient(135deg, rgba(34, 211, 238, 0.14), rgba(14, 116, 144, 0.08))',
        border: 'rgba(34, 211, 238, 0.2)',
        pillBg: 'rgba(34, 211, 238, 0.18)',
        pillBorder: 'rgba(34, 211, 238, 0.28)',
        pillColor: '#a5f3fc',
      }
    : triggerOrigin === 'deviceEvent'
      ? {
          bg: 'linear-gradient(135deg, rgba(16, 185, 129, 0.16), rgba(6, 95, 70, 0.08))',
          border: 'rgba(16, 185, 129, 0.22)',
          pillBg: 'rgba(16, 185, 129, 0.18)',
          pillBorder: 'rgba(16, 185, 129, 0.3)',
          pillColor: '#6ee7b7',
        }
      : {
          bg: 'linear-gradient(135deg, rgba(168, 85, 247, 0.16), rgba(91, 33, 182, 0.08))',
          border: 'rgba(168, 85, 247, 0.22)',
          pillBg: 'rgba(168, 85, 247, 0.18)',
          pillBorder: 'rgba(168, 85, 247, 0.28)',
          pillColor: '#e9d5ff',
        };
  return (
    <div style={{ width: `${panelWidth}px`, flex: 'none', background: '#0b1220', display: 'flex', flexDirection: 'column', height: '100%', borderLeft: '1px solid #182231', position: 'relative', zIndex: 2 }}>
      <ResizeHandle onMouseDown={startPanelResize} title="Drag to resize the panel" />
      <div style={{ padding: '24px', borderBottom: '1px solid var(--border-color)', maxHeight: '200px', overflowY: 'auto' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '12px' }}>
          <ShieldCheck size={16} color="var(--color-info)" />
          <h3 style={{ fontSize: '0.9rem', fontWeight: 700, color: '#fff', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Evaluated Globals</h3>
        </div>
        {execution && Object.keys(execution.globalVariables || {}).length === 0 ? (
          <div style={{ fontSize: '0.8rem', color: 'var(--text-muted)', fontStyle: 'italic' }}>
            No state variables mutated yet.
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            {execution && Object.entries(execution.globalVariables).map(([key, val]) => (
              <div key={key} style={{ display: 'flex', justifyContent: 'space-between', padding: '6px 10px', borderRadius: '6px', background: 'rgba(0,0,0,0.2)', fontFamily: 'monospace', fontSize: '0.8rem' }}>
                <span style={{ color: 'var(--color-info)' }}>{key}</span>
                <span style={{ color: '#fff' }}>{JSON.stringify(val)}</span>
              </div>
            ))}
          </div>
        )}
      </div>

      <div style={{ padding: '24px', borderBottom: '1px solid var(--border-color)', maxHeight: '240px', overflowY: 'auto' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '12px' }}>
          <Clock3 size={16} color="var(--color-info)" />
          <h3 style={{ fontSize: '0.9rem', fontWeight: 700, color: '#fff', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Schedule Controls</h3>
        </div>
        {workflowSchedules.length === 0 ? (
          <div style={{ fontSize: '0.8rem', color: 'var(--text-muted)', fontStyle: 'italic' }}>
            No scheduler nodes are configured for this workflow.
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
            {workflowSchedules.map((schedule) => (
              <div
                key={schedule.nodeId}
                style={{
                  border: '1px solid var(--border-color)',
                  borderRadius: '10px',
                  background: 'rgba(4, 7, 17, 0.65)',
                  padding: '12px',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: '8px',
                }}
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px' }}>
                  <span style={{ color: '#fff', fontWeight: 700, fontSize: '0.8rem' }}>{schedule.nodeId}</span>
                  <span className={createStatusClassName(schedule.isActive ? 'Waiting' : 'Cancelled')}>
                    {schedule.isActive ? 'Active' : 'Inactive'}
                  </span>
                </div>
                <div style={{ fontSize: '0.76rem', color: '#f3f4f6' }}>
                  Next fire: {new Date(schedule.nextFireAtUtc).toLocaleString()}
                </div>
                <div style={{ fontSize: '0.72rem', color: 'var(--text-secondary)' }}>
                  {schedule.cronExpression} in {schedule.timeZoneId}
                </div>
                <button
                  type="button"
                  onClick={() => onFireSchedule(schedule.nodeId)}
                  disabled={triggeringScheduleNodeId === schedule.nodeId}
                  style={{
                    alignSelf: 'flex-start',
                    padding: '8px 10px',
                    borderRadius: '8px',
                    border: '1px solid rgba(16, 185, 129, 0.24)',
                    background: 'rgba(6, 78, 59, 0.45)',
                    color: '#bbf7d0',
                    cursor: 'pointer',
                    display: 'inline-flex',
                    alignItems: 'center',
                    gap: '6px',
                    fontSize: '0.74rem',
                    fontWeight: 700,
                  }}
                >
                  <Play size={13} />
                  {triggeringScheduleNodeId === schedule.nodeId ? 'Firing...' : 'Fire now'}
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      {actionNodes.length > 0 && (
        <div style={{ padding: '24px', borderBottom: '1px solid var(--border-color)', maxHeight: '220px', overflowY: 'auto' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '12px' }}>
            <ShieldCheck size={16} color="#fbbf24" />
            <h3 style={{ fontSize: '0.9rem', fontWeight: 700, color: '#fff', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Manual Actions</h3>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '10px', marginBottom: '14px' }}>
            {actionNodes.map((state) => (
              <div
                key={`manual-${state.id}`}
                style={{
                  border: '1px solid rgba(239, 68, 68, 0.24)',
                  borderRadius: '12px',
                  background: 'rgba(127, 29, 29, 0.18)',
                  padding: '12px',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: '10px',
                }}
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px' }}>
                  <span style={{ color: '#fff', fontWeight: 700, fontSize: '0.82rem' }}>{state.nodeId.value}</span>
                  <span className={createStatusClassName('RequiresManualDecision')}>Manual Decision</span>
                </div>
                <div style={{ fontSize: '0.76rem', color: 'rgba(254, 226, 226, 0.92)' }}>
                  {state.errorMessage || 'Operator intervention required to continue this execution.'}
                </div>
                <div style={{ display: 'flex', gap: '8px', flexWrap: 'wrap' }}>
                  <button
                    type="button"
                    onClick={() => onManualDecision(state.nodeId.value, 'Retry')}
                    disabled={manualDecisionNodeId === state.nodeId.value}
                    style={{
                      padding: '8px 10px',
                      borderRadius: '8px',
                      border: '1px solid rgba(14, 165, 233, 0.24)',
                      background: 'rgba(8, 47, 73, 0.45)',
                      color: '#bae6fd',
                      cursor: 'pointer',
                      display: 'inline-flex',
                      alignItems: 'center',
                      gap: '6px',
                      fontSize: '0.74rem',
                      fontWeight: 700,
                    }}
                  >
                    <RotateCcw size={13} />
                    Retry Node
                  </button>
                  <button
                    type="button"
                    onClick={() => onManualDecision(state.nodeId.value, 'Skip')}
                    disabled={manualDecisionNodeId === state.nodeId.value}
                    style={{
                      padding: '8px 10px',
                      borderRadius: '8px',
                      border: '1px solid rgba(245, 158, 11, 0.24)',
                      background: 'rgba(120, 53, 15, 0.4)',
                      color: '#fde68a',
                      cursor: 'pointer',
                      display: 'inline-flex',
                      alignItems: 'center',
                      gap: '6px',
                      fontSize: '0.74rem',
                      fontWeight: 700,
                    }}
                  >
                    <SkipForward size={13} />
                    Skip Node
                  </button>
                  <button
                    type="button"
                    onClick={() => onManualDecision(state.nodeId.value, 'Fail')}
                    disabled={manualDecisionNodeId === state.nodeId.value}
                    style={{
                      padding: '8px 10px',
                      borderRadius: '8px',
                      border: '1px solid rgba(239, 68, 68, 0.24)',
                      background: 'rgba(127, 29, 29, 0.38)',
                      color: '#fecaca',
                      cursor: 'pointer',
                      display: 'inline-flex',
                      alignItems: 'center',
                      gap: '6px',
                      fontSize: '0.74rem',
                      fontWeight: 700,
                    }}
                  >
                    <OctagonX size={13} />
                    Fail Run
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      <div style={{ flex: 1, padding: '24px', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        <div
          data-testid="execution-log-overview"
          style={{
            flex: 1,
            background: '#060a10',
            borderRadius: '16px',
            border: '1px solid #182231',
            padding: '18px',
            overflowY: 'auto',
            display: 'flex',
            flexDirection: 'column',
            gap: '14px',
          }}
        >
          <div style={{ display: 'flex', alignItems: 'flex-start', gap: '12px', paddingBottom: '14px', borderBottom: '1px solid #131c28' }}>
            <div
              style={{
                width: '36px',
                height: '36px',
                borderRadius: '12px',
                display: 'grid',
                placeItems: 'center',
                background: 'rgba(34, 211, 238, 0.1)',
                border: '1px solid rgba(34, 211, 238, 0.24)',
                color: '#67e8f9',
                fontFamily: 'monospace',
                fontWeight: 700,
              }}
            >
              &gt;_
            </div>
            <div style={{ minWidth: 0 }}>
              <div style={{ fontSize: '0.95rem', fontWeight: 800, color: '#f8fafc', letterSpacing: '0.08em', textTransform: 'uppercase' }}>
                Execution Timeline
              </div>
              <div style={{ marginTop: '4px', fontSize: '0.78rem', color: 'var(--text-secondary)' }}>
                {nodeTimelineCount} nodes • {timelineDurationLabel}
              </div>
            </div>
            {execution && (
              <span style={{ marginLeft: 'auto' }} className={createStatusClassName(timelineSummaryStatus || executionVisualStatus || 'Pending')}>
                {getTimelineHeaderStatusLabel(timelineSummaryStatus || executionVisualStatus || 'Pending')}
              </span>
            )}
          </div>

          {journal.length > 0 && (
            <div
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '10px',
                padding: '12px 14px',
                borderRadius: '14px',
                background: triggerPalette.bg,
                border: `1px solid ${triggerPalette.border}`,
              }}
            >
              <span
                style={{
                  display: 'inline-flex',
                  alignItems: 'center',
                  gap: '6px',
                  padding: '4px 10px',
                  borderRadius: '999px',
                  background: triggerPalette.pillBg,
                  border: `1px solid ${triggerPalette.pillBorder}`,
                  color: triggerPalette.pillColor,
                  fontSize: '0.72rem',
                  fontWeight: 800,
                  letterSpacing: '0.06em',
                }}
              >
                {triggerPillLabel}
              </span>
              <div style={{ minWidth: 0, color: '#e2e8f0', fontSize: '0.82rem' }}>
                {triggerDescription}
              </div>
            </div>
          )}

          {journalOverview.length === 0 ? (
            <span style={{ color: 'var(--text-muted)', fontStyle: 'italic' }}>Awaiting first execution trigger...</span>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column' }}>
              {visibleGroups.map((group, index) => {
                const groupChrome = getStatusChrome(group.status);
                const showInlinePayload = isSimplePayload(group.latestPayload);
                const nextGroupStatus = visibleGroups[index + 1]?.status;
                const isSkipped = normalizeStatusValue(group.status) === 'Skipped';
                const isRunning = normalizeStatusValue(group.status) === 'Running';
                const isExpanded = expandedGroupKeys.has(group.key);
                const collapsedSummary = getCollapsedGroupSummary(group);
                const isExpandable = group.entries.length > 0 || hasDataPayload(group.latestPayload);
                const isActiveStep = Boolean(activeStepNodeId && group.nodeId === activeStepNodeId);
                const canSelectStep = Boolean(onSelectStepNode && group.nodeId);
                const isRowInteractive = isExpandable || canSelectStep;
                const activateRow = () => {
                  // In step mode, clicking a timeline row drives the same stepIndex the canvas
                  // ring and inspector use, so all three surfaces move together.
                  if (canSelectStep) {
                    onSelectStepNode!(group.nodeId as string);
                  }
                  if (isExpandable) {
                    onToggleGroupExpansion(group.key);
                  }
                };

                return (
                  <div key={group.key} data-testid={`journal-group-${group.key}`} style={{ display: 'flex', gap: '14px' }}>
                    <div style={{ width: '38px', display: 'flex', flexDirection: 'column', alignItems: 'center', flex: '0 0 38px' }}>
                      <div
                        style={{
                          width: '38px',
                          height: '38px',
                          borderRadius: '12px',
                          display: 'grid',
                          placeItems: 'center',
                          fontSize: '0.74rem',
                          fontWeight: 800,
                          letterSpacing: '0.05em',
                          color: groupChrome.text,
                          background: groupChrome.background,
                          border: `1px solid ${groupChrome.border}`,
                          boxShadow: `0 0 16px ${groupChrome.background}`,
                          animation: isRunning ? 'execution-timeline-pulse 1.4s ease-in-out infinite' : undefined,
                        }}
                      >
                        {renderJournalNodeIcon(group.nodeType, group.status)}
                      </div>
                      {index < visibleGroups.length - 1 && (
                        <div style={getConnectorStyle(group.status, nextGroupStatus)} />
                      )}
                    </div>

                    <div style={{ flex: 1, minWidth: 0, paddingBottom: index < visibleGroups.length - 1 ? '18px' : 0 }}>
                      <div
                        data-active-step={isActiveStep ? 'true' : undefined}
                        style={{
                          background: isActiveStep ? 'rgba(143, 211, 255, 0.08)' : '#0b1220',
                          border: isActiveStep ? '1px solid rgba(143, 211, 255, 0.55)' : '1px solid #182231',
                          borderRadius: '14px',
                          overflow: 'hidden',
                          opacity: isSkipped ? 0.74 : 1,
                          boxShadow: isActiveStep ? '0 0 0 1px rgba(143, 211, 255, 0.25)' : undefined,
                          transition: 'border-color .15s, background .15s',
                        }}
                      >
                        <div
                          className="node-row"
                          style={{
                            padding: isExpanded ? '12px 16px' : '10px 16px',
                            display: 'flex',
                            flexDirection: 'column',
                            alignItems: 'stretch',
                            gap: '8px',
                            cursor: isRowInteractive ? 'pointer' : 'default',
                          }}
                          role={isRowInteractive ? 'button' : undefined}
                          tabIndex={isRowInteractive ? 0 : undefined}
                          data-testid={`journal-toggle-${group.key}`}
                          onClick={isRowInteractive ? activateRow : undefined}
                          onKeyDown={(event) => {
                            if (!isRowInteractive) {
                              return;
                            }

                            if (event.key === 'Enter' || event.key === ' ') {
                              event.preventDefault();
                              activateRow();
                            }
                          }}
                        >
                          <div style={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: '12px', minWidth: 0 }}>
                            <div style={{ display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap', minWidth: 0, flex: 1 }}>
                              <span style={{ fontSize: '0.96rem', fontWeight: 700, color: '#f8fafc' }}>{group.title}</span>
                              {group.isSubflowChild && (
                                <span
                                  style={{
                                    display: 'inline-flex',
                                    alignItems: 'center',
                                    padding: '2px 8px',
                                    borderRadius: '999px',
                                    fontSize: '0.62rem',
                                    letterSpacing: '0.08em',
                                    fontWeight: 800,
                                    color: '#c7d2fe',
                                    background: 'rgba(99, 102, 241, 0.14)',
                                    border: '1px solid rgba(99, 102, 241, 0.28)',
                                  }}
                                >
                                  SUBFLOW
                                </span>
                              )}
                              {group.nodeType === 'scheduler' && (
                                <span
                                  style={{
                                    display: 'inline-flex',
                                    alignItems: 'center',
                                    padding: '2px 8px',
                                    borderRadius: '999px',
                                    fontSize: '0.62rem',
                                    letterSpacing: '0.08em',
                                    fontWeight: 800,
                                    color: triggerOrigin === 'schedule' ? '#a5f3fc' : '#e9d5ff',
                                    background: triggerOrigin === 'schedule' ? 'rgba(34, 211, 238, 0.12)' : 'rgba(168, 85, 247, 0.12)',
                                    border: triggerOrigin === 'schedule' ? '1px solid rgba(34, 211, 238, 0.22)' : '1px solid rgba(168, 85, 247, 0.22)',
                                  }}
                                >
                                  {triggerPillLabel}
                                </span>
                              )}
                            </div>

                            {isExpandable && (
                              <span aria-hidden="true" className={`chev-btn${isExpanded ? ' open' : ''}`}>
                                <ChevronDown className={`chev${isExpanded ? ' open' : ''}`} size={18} strokeWidth={2.25} />
                              </span>
                            )}
                          </div>

                          <div style={{ display: 'flex', alignItems: 'center', gap: '10px', minWidth: 0 }}>
                            <span className={createStatusClassName(group.status)}>{getStatusLabel(group.status)}</span>
                            <span style={{ fontFamily: 'monospace', fontSize: '0.76rem', color: '#94a3b8', flex: '0 0 auto' }}>{group.durationLabel}</span>
                            {!isExpanded && (
                              <span style={{ color: '#dbe4ee', fontSize: '0.8rem', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, flex: 1 }}>
                                {collapsedSummary}
                              </span>
                            )}
                          </div>

                          <div>
                            <span style={{ fontSize: '0.72rem', color: '#64748b', fontFamily: 'monospace' }}>{group.subtitle}</span>
                          </div>
                        </div>

                        {isExpanded && (
                          <div style={{ padding: '0 16px 16px 16px', display: 'flex', flexDirection: 'column', gap: '12px' }}>
                            {/* Affordance C — link the handler run right where the failure is read in the timeline. */}
                            {showHandlerAffordance && normalizeStatusValue(group.status) === 'Failed' && (
                              <button
                                onClick={onOpenHandler}
                                style={{
                                  display: 'flex', alignItems: 'center', gap: '8px', width: '100%',
                                  padding: '8px 12px', borderRadius: '10px', cursor: 'pointer', textAlign: 'left',
                                  color: '#f5a623',
                                  background: 'linear-gradient(90deg, rgba(245,166,35,0.14), rgba(245,166,35,0.03))',
                                  border: '1px solid rgba(245,166,35,0.3)', fontSize: '0.78rem', fontWeight: 700,
                                }}
                              >
                                ⚡ Error handler caught this — view run →
                              </button>
                            )}
                            {group.hint && (
                              <div style={{ fontSize: '0.8rem', color: '#cbd5e1', fontStyle: 'italic' }}>{group.hint}</div>
                            )}
                            <div
                              style={{
                                display: 'grid',
                                gridTemplateColumns: '56px minmax(0, 1fr)',
                                columnGap: '12px',
                                rowGap: '10px',
                                alignItems: 'start',
                              }}
                            >
                              {group.entries.map((entry) => {
                                const entryChrome = getStatusChrome(entry.status);
                                const showEntryPayload = hasDataPayload(entry.data) && !showInlinePayload;

                                return (
                                  <Fragment key={entry.id}>
                                    <span style={{ minWidth: '44px', paddingTop: '2px', color: '#64748b', fontFamily: 'monospace', fontSize: '0.74rem' }}>
                                      {entry.offsetLabel}
                                    </span>
                                    <div style={{ minWidth: 0, flex: 1 }}>
                                      <div style={{ display: 'grid', gridTemplateColumns: '8px auto minmax(0, 1fr)', alignItems: 'baseline', columnGap: '8px', minWidth: 0 }}>
                                        <span
                                          style={{
                                            width: '8px',
                                            height: '8px',
                                            borderRadius: '999px',
                                            background: entryChrome.accent,
                                            boxShadow: `0 0 10px ${entryChrome.accent}`,
                                            flex: '0 0 8px',
                                            marginTop: '4px',
                                          }}
                                        />
                                        <span
                                          style={{
                                            display: 'inline-flex',
                                            alignItems: 'center',
                                            padding: '2px 7px',
                                            borderRadius: '999px',
                                            fontSize: '0.62rem',
                                            letterSpacing: '0.08em',
                                            fontWeight: 800,
                                            color: entryChrome.text,
                                            background: entryChrome.background,
                                            border: `1px solid ${entryChrome.border}`,
                                          }}
                                        >
                                          {getEventTagLabel(entry.eventType)}
                                        </span>
                                        <span style={{ color: '#dbe4ee', fontSize: '0.82rem', lineHeight: 1.5, minWidth: 0 }}>{entry.message}</span>
                                      </div>
                                      {showEntryPayload && (
                                        <details style={{ marginTop: '8px' }}>
                                          <summary style={{ cursor: 'pointer', color: '#7dd3fc', fontSize: '0.76rem' }}>
                                            View payload
                                          </summary>
                                          <div style={{ display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '8px' }}>
                                            {Object.entries(entry.data).map(([key, value]) => {
                                              const formattedValue = formatPayloadValue(value);
                                              return (
                                                <div key={key}>
                                                  <div style={{ fontSize: '0.7rem', color: '#94a3b8', marginBottom: '4px' }}>{key}</div>
                                                  <pre
                                                    style={{
                                                      margin: 0,
                                                      background: '#030712',
                                                      border: '1px solid rgba(148, 163, 184, 0.12)',
                                                      borderRadius: '10px',
                                                      padding: '10px',
                                                      maxHeight: '140px',
                                                      ...getPayloadValueStyle(key, value),
                                                      overflow: 'auto',
                                                      color: '#e2e8f0',
                                                      fontSize: '0.75rem',
                                                    }}
                                                  >
                                                    {formattedValue}
                                                  </pre>
                                                </div>
                                              );
                                            })}
                                          </div>
                                        </details>
                                      )}
                                    </div>
                                  </Fragment>
                                );
                              })}
                            </div>

                            {/* AI Agent: render the per-iteration tool-call trail from the node's `steps`
                                output as a nested list — each call's status, name, and its child run id. */}
                            {group.nodeType === 'aiAgent' && (() => {
                              const payload = group.latestPayload as Record<string, unknown> | null;
                              const steps = payload && Array.isArray(payload.steps) ? (payload.steps as unknown[]) : null;
                              if (!steps || steps.length === 0) return null;
                              return (
                                <div style={{ background: '#09111d', border: '1px solid rgba(244, 114, 182, 0.24)', borderRadius: '12px', padding: '12px' }}>
                                  <div style={{ fontSize: '0.68rem', color: '#f472b6', textTransform: 'uppercase', letterSpacing: '0.08em', marginBottom: '8px', fontWeight: 700 }}>
                                    Agent steps{typeof payload?.iterations === 'number' ? ` · ${payload.iterations} iteration(s)` : ''}
                                  </div>
                                  <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                                    {steps.map((raw, si) => {
                                      const step = raw as { iteration?: number; toolCalls?: unknown[] };
                                      const calls = Array.isArray(step.toolCalls) ? step.toolCalls : [];
                                      return (
                                        <div key={si} style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
                                          <span style={{ fontSize: '0.7rem', color: '#94a3b8' }}>Turn {step.iteration ?? si + 1}</span>
                                          {calls.map((c, ci) => {
                                            const call = c as { tool?: string; ok?: boolean; childExecutionId?: string; error?: string };
                                            return (
                                              <div key={ci} style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.78rem', fontFamily: 'monospace', color: '#f8fafc' }}>
                                                <span style={{ color: call.ok === false ? '#f87171' : call.ok === true ? '#34d399' : '#94a3b8' }}>{call.ok === false ? '✗' : call.ok === true ? '✓' : '•'}</span>
                                                <span>{call.tool ?? 'tool'}</span>
                                                {call.error && <span style={{ color: '#f87171' }}>— {call.error}</span>}
                                                {call.childExecutionId && call.childExecutionId !== '00000000-0000-0000-0000-000000000000' && (
                                                  <span style={{ color: '#64748b', fontSize: '0.7rem' }} title={`child run ${call.childExecutionId}`}>
                                                    run {call.childExecutionId.slice(0, 8)}
                                                  </span>
                                                )}
                                              </div>
                                            );
                                          })}
                                        </div>
                                      );
                                    })}
                                  </div>
                                </div>
                              );
                            })()}

                            {showInlinePayload && group.latestPayload && (
                              <div
                                style={{
                                  background: '#09111d',
                                  border: '1px solid rgba(14, 165, 233, 0.16)',
                                  borderRadius: '12px',
                                  padding: '12px',
                                }}
                              >
                                <div style={{ fontSize: '0.68rem', color: '#7dd3fc', textTransform: 'uppercase', letterSpacing: '0.08em', marginBottom: '8px', fontWeight: 700 }}>
                                  Output
                                </div>
                                <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                                  {Object.entries(group.latestPayload).map(([key, value]) => (
                                    <div key={key} style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
                                      <span style={{ fontSize: '0.72rem', color: '#94a3b8', textTransform: 'uppercase', letterSpacing: '0.08em' }}>{formatOutputLabel(key)}</span>
                                      <div
                                        style={{
                                          background: '#030712',
                                          border: '1px solid rgba(148, 163, 184, 0.12)',
                                          borderRadius: '10px',
                                          padding: '10px 12px',
                                          color: '#f8fafc',
                                          fontFamily: 'monospace',
                                          fontSize: '0.9rem',
                                          ...getPayloadValueStyle(key, value),
                                        }}
                                      >
                                        {formatPayloadValue(value)}
                                      </div>
                                    </div>
                                  ))}
                                </div>
                              </div>
                            )}

                            {/* Remediation card — placed after the failure so the read is cause → fix. Uses
                                the app's "action needed" amber, and turns a policy denial into a one-click grant. */}
                            {normalizeStatusValue(group.status) === 'Failed' && onGrantFileAccess && (() => {
                              const rem = fileAccessRemediation(group);
                              if (!rem) return null;
                              const verb = group.nodeType === 'fileRead' ? 'read it here'
                                : group.nodeType === 'fileWrite' ? 'write here' : 'reach it here';
                              return (
                                <div style={{ display: 'flex', flexDirection: 'column', gap: '10px', padding: '14px', borderRadius: '12px', background: 'rgba(240,180,41,0.08)', border: '1px solid rgba(240,180,41,0.35)' }}>
                                  <div style={{ display: 'flex', alignItems: 'flex-start', gap: '10px' }}>
                                    <span style={{ flex: 'none', width: '30px', height: '30px', borderRadius: '8px', display: 'grid', placeItems: 'center', background: 'rgba(240,180,41,0.14)', border: '1px solid rgba(240,180,41,0.34)', color: '#f0b429' }}>
                                      <ShieldAlert size={16} />
                                    </span>
                                    <div style={{ minWidth: 0 }}>
                                      <div style={{ fontSize: '0.62rem', fontWeight: 800, letterSpacing: '0.1em', color: '#f0b429', textTransform: 'uppercase' }}>How to fix</div>
                                      <div style={{ fontSize: '0.95rem', fontWeight: 700, color: '#f6e6bf', marginTop: '1px' }}>{rem.title}</div>
                                    </div>
                                  </div>
                                  <div style={{ fontSize: '0.82rem', lineHeight: 1.55, color: '#cbd5e1' }}>
                                    File access is sandboxed. Add this location to File Access to let the node {verb}.{' '}
                                    <code style={{ fontFamily: 'monospace', fontSize: '0.78rem', background: 'rgba(0,0,0,0.35)', border: '1px solid rgba(240,180,41,0.25)', color: '#f0b429', borderRadius: '6px', padding: '2px 6px' }}>{rem.grantPath}</code>
                                  </div>
                                  <div style={{ display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap' }}>
                                    <button
                                      onClick={() => { requestFileAccessGrant(rem.grantPath); onGrantFileAccess(); }}
                                      style={{ display: 'inline-flex', alignItems: 'center', gap: '8px', padding: '9px 14px', borderRadius: '9px', border: 'none', cursor: 'pointer', background: 'linear-gradient(160deg, #f6c445, #f0b429)', color: '#1a1206', fontSize: '0.8rem', fontWeight: 700 }}
                                    >
                                      <FolderLock size={14} /> Grant access in File Access settings →
                                    </button>
                                    <button
                                      onClick={() => { navigator.clipboard?.writeText(rem.grantPath); setCopiedGrantPath(rem.grantPath); }}
                                      style={{ display: 'inline-flex', alignItems: 'center', gap: '6px', padding: '9px 10px', borderRadius: '9px', border: 'none', background: 'transparent', color: '#b59a63', cursor: 'pointer', fontSize: '0.78rem', fontWeight: 600 }}
                                    >
                                      <Copy size={13} /> {copiedGrantPath === rem.grantPath ? 'Copied' : 'Copy path'}
                                    </button>
                                  </div>
                                </div>
                              );
                            })()}
                          </div>
                        )}
                      </div>
                    </div>
                  </div>
                );
              })}

              {/* Skipped rows (e.g. un-fired device-event branches) are hidden by default — reveal on demand. */}
              {skippedCount > 0 && (
                <button
                  type="button"
                  onClick={() => setShowSkipped((prev) => !prev)}
                  aria-pressed={showSkipped}
                  style={{
                    display: 'flex', alignItems: 'center', gap: '8px', alignSelf: 'flex-start',
                    marginTop: '4px', padding: '6px 12px', borderRadius: '999px', cursor: 'pointer',
                    color: '#94a3b8', background: 'rgba(148, 163, 184, 0.08)',
                    border: '1px solid rgba(148, 163, 184, 0.2)', fontSize: '0.76rem', fontWeight: 600,
                  }}
                >
                  {showSkipped ? <EyeOff size={14} /> : <Eye size={14} />}
                  {showSkipped
                    ? `Hide ${skippedCount} skipped`
                    : `Show ${skippedCount} skipped ${skippedCount === 1 ? 'branch' : 'branches'}`}
                </button>
              )}

              <div
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: '10px',
                  marginTop: '8px',
                  padding: '14px 16px',
                  borderRadius: '14px',
                  color: '#a7f3d0',
                  background: 'linear-gradient(90deg, rgba(16, 185, 129, 0.16), rgba(16, 185, 129, 0.04))',
                  border: '1px solid rgba(16, 185, 129, 0.22)',
                  fontSize: '0.88rem',
                  fontWeight: 700,
                }}
              >
                <span
                  style={{
                    width: '10px',
                    height: '10px',
                    borderRadius: '999px',
                    background: 'currentColor',
                    boxShadow: '0 0 12px currentColor',
                    flex: '0 0 10px',
                  }}
                />
                <span>{getGroupFooterMessage(timelineSummaryStatus)}</span>
              </div>

              {/* Affordance D — the failure toast becomes an action instead of a dead end. */}
              {showHandlerAffordance && (
                <button
                  onClick={onOpenHandler}
                  style={{
                    display: 'flex', alignItems: 'center', gap: '8px', width: '100%',
                    marginTop: '8px', padding: '10px 14px', borderRadius: '12px', cursor: 'pointer',
                    color: '#f5a623', textAlign: 'left',
                    background: 'linear-gradient(90deg, rgba(245,166,35,0.14), rgba(245,166,35,0.03))',
                    border: '1px solid rgba(245,166,35,0.3)', fontSize: '0.82rem', fontWeight: 700,
                  }}
                >
                  ⚡ An error handler caught it and ran — view handler run →
                </button>
              )}
            </div>
          )}
          <div ref={consoleEndRef} />
        </div>
      </div>
    </div>
  );
}