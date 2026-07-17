// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { ArrowLeft, Upload, Play, Radio, Activity, Zap, AlertTriangle } from 'lucide-react';
import type { ActiveWorkflowVersion, WorkflowVersionSummary } from '../types';
import { VersionRuntimeSelect } from './VersionRuntimeSelect';

interface CanvasToolbarProps {
  workflowName: string;
  setWorkflowName: (value: string) => void;
  onBack?: () => void;
  onSaved?: () => void;
  workflowVersions: WorkflowVersionSummary[];
  activeWorkflowVersion: ActiveWorkflowVersion | null;
  selectedActivationVersionId: string;
  setSelectedActivationVersionId: (value: string) => void;
  /**
   * Called when a version is picked in the runtime dropdown. Selecting a version opens a read-only
   * preview of it (it does not activate — activation has live side effects and stays explicit).
   * Falls back to {@link setSelectedActivationVersionId} when not provided.
   */
  onSelectVersion?: (versionId: string) => void;
  /** Transiently preview a version while the user lingers on it in the dropdown (null = revert). */
  onHoverPreview?: (versionId: string | null) => void;
  /** Opens the compare/history surface from the picker's footer. */
  onCompareVersions?: () => void;
  saving: boolean;
  handleSave: () => void;
  isDirty: boolean;
  currentId: string;
  triggering: boolean;
  handleRun: () => void;
  /** True while previewing/diffing a version (V2) — publish is disabled. */
  readOnly?: boolean;
  /** True specifically while previewing (not diffing) — Run is still allowed on the previewed version. */
  previewing?: boolean;
  /**
   * True when the workflow is an event-driven device graph (device blocks, no manual/start trigger):
   * a manual Run has nothing to start, so the button is disabled with an explanatory hint — these
   * workflows run when their wired device events fire, not on demand.
   */
  isEventDrivenDeviceGraph?: boolean;
  /** Open the simulate-signal dialog (event-driven graphs only). */
  onSimulate?: () => void;
  /** Whether there's at least one wired device pin to simulate. */
  canSimulate?: boolean;
  /** Jump to the Execution Visualizer on this workflow's latest run (shown for event-driven graphs). */
  onWatchLiveRuns?: (workflowId: string) => void;
  /** Global runtime armed state — when disarmed, device events start no runs, so "watch live" is gated. */
  armed?: boolean | null;
  /** Count of blocking (Error-severity) diagnostics on the current graph. When > 0 the Save & Publish
   * button reflects that publishing will fail until they're fixed. */
  blockingErrorCount?: number;
}

export function CanvasToolbar({
  workflowName,
  setWorkflowName,
  onBack,
  onSaved,
  workflowVersions,
  activeWorkflowVersion,
  selectedActivationVersionId,
  setSelectedActivationVersionId,
  onSelectVersion,
  onHoverPreview,
  onCompareVersions,
  saving,
  handleSave,
  isDirty,
  currentId,
  triggering,
  handleRun,
  readOnly = false,
  previewing = false,
  isEventDrivenDeviceGraph = false,
  onSimulate,
  canSimulate = false,
  onWatchLiveRuns,
  armed,
  blockingErrorCount = 0,
}: CanvasToolbarProps) {
  // Publish is available when there are changes — or when no version exists yet,
  // so a freshly loaded/created workflow can always publish its first version.
  // Disabled entirely while previewing/diffing a read-only version snapshot.
  const canPublish = !readOnly && !!currentId && !saving && (isDirty || workflowVersions.length === 0);
  // Blocking errors don't disable the button (Save still persists the draft), but they turn it into a
  // warning so the corner diagnostics aren't the only signal that publishing will fail.
  const hasErrors = blockingErrorCount > 0 && !readOnly;
  // Run activates the selected version and triggers it — available whenever a published version is
  // selected. Allowed while previewing (Run targets the previewed version) but not while diffing.
  // An event-driven device graph has nothing for a manual Run to start, so Run is disabled there.
  const canRun = (!readOnly || previewing) && !triggering && !!currentId && workflowVersions.length > 0 && !!selectedActivationVersionId && !isEventDrivenDeviceGraph;
  return (
        <div
          style={{
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            padding: '16px 24px',
            background: 'rgba(10, 13, 22, 0.8)',
            borderBottom: '1px solid var(--border-color)',
            zIndex: 5,
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
            <button
              onClick={onBack || onSaved}
              style={{
                background: 'transparent',
                border: 'none',
                color: 'var(--text-secondary)',
                cursor: 'pointer',
                display: 'flex',
                alignItems: 'center',
                gap: '6px',
                fontSize: '0.85rem',
              }}
            >
              <ArrowLeft size={16} />
              Back
            </button>
            <input
              type="text"
              value={workflowName}
              onChange={(e) => setWorkflowName(e.target.value)}
              style={{
                background: 'transparent',
                border: 'none',
                color: '#fff',
                fontSize: '1.2rem',
                fontWeight: 700,
                outline: 'none',
                borderBottom: '1.5px solid transparent',
                transition: 'border-color 0.2s',
              }}
              onFocus={(e) => e.target.style.borderBottomColor = 'var(--color-accent)'}
              onBlur={(e) => e.target.style.borderBottomColor = 'transparent'}
            />
          </div>
          
          <div style={{ display: 'flex', gap: '12px' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: '10px', color: 'var(--text-secondary)', fontSize: '0.8rem', marginRight: '8px' }}>
              <span>
                Latest version: {workflowVersions[0]?.versionNumber ?? 'none'}
              </span>
              <span>
                Active: {activeWorkflowVersion ? workflowVersions.find(version => version.id === activeWorkflowVersion.workflowVersionId)?.versionNumber ?? 'custom' : 'none'}
              </span>
            </div>
            <div
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '8px',
                color: 'var(--text-secondary)',
                fontSize: '0.8rem',
              }}
            >
              <span>Runtime version</span>
              <VersionRuntimeSelect
                versions={workflowVersions}
                value={selectedActivationVersionId}
                activeVersionId={activeWorkflowVersion?.workflowVersionId ?? null}
                disabled={workflowVersions.length === 0}
                onSelect={(versionId) => (onSelectVersion ?? setSelectedActivationVersionId)(versionId)}
                onHoverPreview={(versionId) => onHoverPreview?.(versionId)}
                onCompare={onCompareVersions}
              />
            </div>
            <button
              disabled={!canPublish}
              onClick={() => handleSave()}
              title={
                !currentId
                  ? 'Load or create a workflow first.'
                  : hasErrors
                    ? `${blockingErrorCount} error${blockingErrorCount === 1 ? '' : 's'} — publishing will fail until fixed. Save still keeps your draft.`
                    : canPublish
                      ? 'Save the definition and publish it as a runtime version.'
                      : 'No changes since the last publish.'
              }
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '8px',
                padding: '10px 18px',
                borderRadius: '8px',
                background: hasErrors ? 'rgba(245, 158, 11, 0.14)' : 'rgba(94, 234, 212, 0.12)',
                border: hasErrors ? '1px solid rgba(245, 158, 11, 0.5)' : '1px solid rgba(94, 234, 212, 0.25)',
                color: hasErrors ? '#fcd9a0' : '#d8fff7',
                fontWeight: 600,
                fontSize: '0.85rem',
                cursor: canPublish ? 'pointer' : 'not-allowed',
                opacity: canPublish ? 1 : 0.4,
                transition: 'background 0.2s, opacity 0.2s',
              }}
            >
              {hasErrors ? <AlertTriangle size={16} /> : <Upload size={16} />}
              {saving
                ? 'Saving & Publishing...'
                : hasErrors
                  ? `Save & Publish (${blockingErrorCount} error${blockingErrorCount === 1 ? '' : 's'})`
                  : canPublish
                    ? 'Save & Publish'
                    : 'Saved'}
            </button>
            {isEventDrivenDeviceGraph ? (
              <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
                {/* Not a disabled action — a status badge. This graph has no manual Run; it runs when its
                   wired device events fire. Rendered as a non-interactive pill so it never reads as a
                   button the user must "enable". */}
                <div
                  title="This graph runs when its wired device events fire — there is no manual Run."
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: '8px',
                    padding: '10px 16px',
                    borderRadius: '999px',
                    background: 'rgba(16, 185, 129, 0.12)',
                    border: '1px solid rgba(16, 185, 129, 0.28)',
                    color: '#6ee7b7',
                    fontWeight: 700,
                    fontSize: '0.8rem',
                    whiteSpace: 'nowrap',
                    cursor: 'default',
                  }}
                >
                  <Radio size={15} />
                  Event-driven
                  <span style={{ color: 'var(--text-secondary)', fontWeight: 500 }}>· runs on device events</span>
                </div>
                {onWatchLiveRuns && currentId && (
                  <button
                    onClick={() => onWatchLiveRuns(currentId)}
                    title={
                      armed === false
                        ? "Opens this workflow's latest run. Note: the runtime is disarmed (editing auto-disarms) — arm it in the header to receive new live runs."
                        : "Open the Execution Visualizer on this workflow's latest run, then watch new event runs arrive live."
                    }
                    style={{
                      display: 'flex',
                      alignItems: 'center',
                      gap: '6px',
                      padding: '10px 14px',
                      borderRadius: '8px',
                      background: 'transparent',
                      border: '1px solid var(--border-color)',
                      color: 'var(--text-secondary)',
                      fontWeight: 600,
                      fontSize: '0.82rem',
                      cursor: 'pointer',
                      whiteSpace: 'nowrap',
                    }}
                  >
                    <Activity size={15} />
                    Watch live runs →
                  </button>
                )}
                {onSimulate && canSimulate && (
                  <button
                    onClick={onSimulate}
                    title="Start a run by simulating one of the wired device pins (pick the action/event and sample values)."
                    style={{
                      display: 'flex',
                      alignItems: 'center',
                      gap: '6px',
                      padding: '10px 14px',
                      borderRadius: '8px',
                      background: 'var(--color-accent)',
                      border: '1px solid var(--color-accent)',
                      color: '#fff',
                      fontWeight: 600,
                      fontSize: '0.82rem',
                      cursor: 'pointer',
                      whiteSpace: 'nowrap',
                    }}
                  >
                    <Zap size={15} />
                    Simulate signal
                  </button>
                )}
              </div>
            ) : (
              <button
                disabled={!canRun}
                onClick={handleRun}
                aria-label="Run selected version"
                title={
                  workflowVersions.length === 0
                    ? 'Publish a version (Save & Publish) before running.'
                    : selectedActivationVersionId
                      ? 'Activate the selected version and run it.'
                      : 'Select a version to run.'
                }
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: '8px',
                  padding: '10px 18px',
                  borderRadius: '8px',
                  background: canRun ? 'var(--color-success)' : 'rgba(255, 255, 255, 0.05)',
                  border: canRun ? 'none' : '1px solid var(--border-color)',
                  color: canRun ? '#fff' : '#4d5b6e',
                  fontWeight: 700,
                  fontSize: '0.85rem',
                  cursor: canRun ? 'pointer' : 'not-allowed',
                  boxShadow: canRun ? '0 4px 10px var(--color-success-glow)' : 'none',
                  transition: 'transform 0.2s, background 0.2s, color 0.2s, border-color 0.2s',
                }}
                onMouseOver={(e) => {
                  if (canRun) {
                    e.currentTarget.style.transform = 'translateY(-1px)';
                  }
                }}
                onMouseOut={(e) => {
                  e.currentTarget.style.transform = 'none';
                }}
              >
                <Play size={16} fill={canRun ? '#fff' : '#4d5b6e'} />
                {triggering ? 'Starting...' : 'Run'}
              </button>
            )}
          </div>
        </div>
  );
}
