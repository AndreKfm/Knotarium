// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Dashboard } from './components/Dashboard';
import { Canvas } from './components/Canvas';
import { ExecutionDetail } from './components/ExecutionDetail/index';
import { OpenApiView } from './components/OpenApiView';
import { ArrowLeft } from 'lucide-react';
import { AiGenerateModal } from './components/AiGenerateModal';
import type { WorkflowDefinition, VersionInfo } from './types';
import { NodeEditorShell } from './node-editor/NodeEditorShell';
import { SettingsView } from './components/SettingsView';
import { DeadLetterView } from './components/DeadLetterView';
import { TopBar, type TopBarTarget } from './components/topbar/TopBar';
import { UsersPanel } from './components/auth/UsersPanel';
import { GuidedTour } from './components/tour/GuidedTour';
import { BundlesView } from './components/BundlesView';
import { TemplatesView } from './components/TemplatesView';
import { SettingImporter } from './components/SettingImporter';
import { api } from './utils/api';

type View = 'dashboard' | 'editor' | 'execution' | 'node-editor' | 'api-importer' | 'settings' | 'dead-letter' | 'bundles' | 'templates' | 'imports';
type NonExecutionView = Exclude<View, 'execution'>;

interface NavigationState {
  currentView: View;
  selectedWorkflowId: string | null;
  selectedExecutionId: string | null;
  lastNonExecutionView: NonExecutionView;
}

const NAVIGATION_STATE_STORAGE_KEY = 'knotarium-navigation-state';

function loadNavigationState(): NavigationState | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const rawValue = window.sessionStorage.getItem(NAVIGATION_STATE_STORAGE_KEY);
  if (!rawValue) {
    return null;
  }

  try {
    const parsed = JSON.parse(rawValue) as Partial<NavigationState>;
    const currentView = parsed.currentView;
    if (currentView !== 'dashboard' && currentView !== 'editor' && currentView !== 'execution' && currentView !== 'node-editor' && currentView !== 'api-importer' && currentView !== 'settings' && currentView !== 'dead-letter' && currentView !== 'bundles' && currentView !== 'templates' && currentView !== 'imports') {
      return null;
    }

    const parsedLastNonExecutionView = parsed.lastNonExecutionView;
    const lastNonExecutionView: NonExecutionView = parsedLastNonExecutionView === 'editor'
      ? 'editor'
      : parsedLastNonExecutionView === 'node-editor'
        ? 'node-editor'
        : parsedLastNonExecutionView === 'api-importer'
          ? 'api-importer'
          : parsedLastNonExecutionView === 'settings'
            ? 'settings'
            : parsedLastNonExecutionView === 'dead-letter'
              ? 'dead-letter'
              : parsedLastNonExecutionView === 'bundles'
                ? 'bundles'
                : parsedLastNonExecutionView === 'templates'
                  ? 'templates'
                  : parsedLastNonExecutionView === 'imports'
                    ? 'imports'
                    : 'dashboard';

    return {
      currentView,
      selectedWorkflowId: typeof parsed.selectedWorkflowId === 'string' && parsed.selectedWorkflowId.length > 0
        ? parsed.selectedWorkflowId
        : null,
      selectedExecutionId: typeof parsed.selectedExecutionId === 'string' && parsed.selectedExecutionId.length > 0
        ? parsed.selectedExecutionId
        : null,
      lastNonExecutionView,
    };
  } catch {
    return null;
  }
}

export default function App() {
  const persistedNavigationState = loadNavigationState();
  const initialExecutionId = persistedNavigationState?.selectedExecutionId ?? null;
  const initialView = persistedNavigationState?.currentView ?? 'dashboard';
  const initialLastNonExecutionView = persistedNavigationState?.lastNonExecutionView ?? 'dashboard';

  const [currentView, setCurrentView] = useState<View>(() => {
    if (initialView === 'execution' && !initialExecutionId) {
      return 'dashboard';
    }
    return initialView as View;
  });
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<string | null>(() => persistedNavigationState?.selectedWorkflowId ?? null);
  // Increments on every "create a new workflow" request. See navigateToEditor for why the empty id
  // alone is not enough. Deliberately NOT persisted — a reload already yields a pristine canvas.
  const [newWorkflowRequest, setNewWorkflowRequest] = useState(0);
  // AI-generated workflow pending preview on the canvas, and the "Generate with AI" dialog's open state.
  const [previewDefinition, setPreviewDefinition] = useState<WorkflowDefinition | null>(null);
  const [showAiModal, setShowAiModal] = useState(false);
  // The open workflow to REFINE (captured when the AI dialog opens over the editor), and a live getter the
  // Canvas registers for its current on-canvas definition.
  const [aiRefineBase, setAiRefineBase] = useState<WorkflowDefinition | null>(null);
  const getEditorDefinitionRef = useRef<(() => WorkflowDefinition) | null>(null);
  // 'extend' seeds the dialog with what is currently on the canvas; 'new' always starts empty.
  const openAiGenerate = (mode: 'new' | 'extend') => {
    const def = mode === 'extend' && getEditorDefinitionRef.current ? getEditorDefinitionRef.current() : null;
    setAiRefineBase(def && def.nodes.length > 0 ? def : null);
    setShowAiModal(true);
  };
  // Breadcrumb trail of parent workflow ids while drilling into nested subflows.
  // In-memory only (not persisted): a page reload lands on the current workflow without the trail.
  const [subflowStack, setSubflowStack] = useState<string[]>([]);
  const [selectedExecutionId, setSelectedExecutionId] = useState<string | null>(initialExecutionId);
  const [lastNonExecutionView, setLastNonExecutionView] = useState<NonExecutionView>(initialLastNonExecutionView as NonExecutionView);
  // A run was just started from the editor. Surfaced as a non-disruptive toast so the editor (and its
  // version dropdown) stays mounted instead of navigating away to the execution view.
  const [runStartedExecutionId, setRunStartedExecutionId] = useState<string | null>(null);

  // Global runtime arming switch (null = still loading).
  const [armed, setArmed] = useState<boolean | null>(null);
  const [armingBusy, setArmingBusy] = useState(false);

  // Build identity shown in the header so a stale instance is obvious after a rebuild.
  const [version, setVersion] = useState<VersionInfo | null>(null);
  const [showTour, setShowTour] = useState(false);
  // User management is a top-bar destination but renders as an overlay panel.
  const [showUsers, setShowUsers] = useState(false);
  const closeTour = () => {
    setShowTour(false);
    try { localStorage.setItem('kg-tour-done', '1'); } catch { /* ignore */ }
  };
  // First run: open the guided tour once (persisted). Re-openable from the header "Tour" button.
  useEffect(() => {
    try { if (localStorage.getItem('kg-tour-done') !== '1') setShowTour(true); } catch { /* ignore */ }
  }, []);
  useEffect(() => {
    api.getVersion().then(setVersion).catch(() => setVersion(null));
  }, []);

  // Load the current arming state once on mount.
  useEffect(() => {
    let cancelled = false;
    api.getRuntimeArming()
      // Only seed the initial value — don't clobber a state an auto-disarm/toggle
      // may have already set while this request was in flight (e.g. restoring
      // directly into the editor view, which disarms on mount).
      .then((state) => { if (!cancelled) setArmed((prev) => (prev === null ? state.armed : prev)); })
      .catch((err) => { console.error('Failed to load runtime arming state:', err); });
    return () => { cancelled = true; };
  }, []);

  const setRuntimeArmed = async (next: boolean) => {
    setArmingBusy(true);
    try {
      const state = await api.setRuntimeArming(next);
      setArmed(state.armed);
    } catch (err) {
      console.error('Failed to change runtime arming state:', err);
    } finally {
      setArmingBusy(false);
    }
  };

  // Stage 2 — auto-disarm whenever the editor is entered (design-time = safe mode).
  // Re-arming is then a deliberate, manual action via the header toggle.
  const previousViewRef = useRef<View | null>(null);
  useEffect(() => {
    const previousView = previousViewRef.current;
    previousViewRef.current = currentView;
    if (currentView === 'editor' && previousView !== 'editor') {
      void setRuntimeArmed(false);
    }
  }, [currentView]);

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    const normalizedView = currentView === 'execution' && !selectedExecutionId
      ? 'dashboard'
      : currentView === 'editor' && selectedWorkflowId === ''
        ? 'editor'
        : currentView;

    const navigationState: NavigationState = {
      currentView: normalizedView,
      selectedWorkflowId,
      selectedExecutionId,
      lastNonExecutionView,
    };

    window.sessionStorage.setItem(NAVIGATION_STATE_STORAGE_KEY, JSON.stringify(navigationState));
  }, [currentView, lastNonExecutionView, selectedExecutionId, selectedWorkflowId]);

  const navigateToDashboard = () => {
    setCurrentView('dashboard');
    setLastNonExecutionView('dashboard');
  };

  const navigateToEditor = (workflowId: string | null) => {
    // Opening an existing workflow discards any pending AI preview so it can't leak into the wrong canvas.
    if (workflowId) setPreviewDefinition(null);
    setSubflowStack([]);
    setSelectedWorkflowId(workflowId);
    // "New workflow" is signalled by an empty id, which is a VALUE, not an event — so two creates in a
    // row set the same value, React bails out, and the canvas never hears about the second one. It then
    // keeps the previous graph and name under a fresh workflow, and saving would persist the old graph
    // under a new id. This counter makes each request distinguishable; the canvas resets on it.
    if (!workflowId) setNewWorkflowRequest((n) => n + 1);
    setCurrentView('editor');
    setLastNonExecutionView('editor');
  };

  // Drill into a subflow: push the current workflow onto the trail and load the child.
  const openSubflow = (subflowId: string) => {
    if (!subflowId || subflowId === selectedWorkflowId) return;
    setSubflowStack((stack) => [...stack, selectedWorkflowId ?? '']);
    setSelectedWorkflowId(subflowId);
  };

  // Pop one level back out of a subflow to its parent.
  const exitSubflow = () => {
    if (subflowStack.length === 0) return;
    const parentId = subflowStack[subflowStack.length - 1];
    setSubflowStack((stack) => stack.slice(0, -1));
    setSelectedWorkflowId(parentId.length > 0 ? parentId : null);
  };

  // The Canvas registers a "save+publish the subflow, then exit" handler here while a subflow is
  // open, so the breadcrumb back button persists the child before leaving (same as the Canvas's
  // own back button). Falls back to a plain exit if nothing is registered.
  const subflowExitRef = useRef<(() => void) | null>(null);
  const registerSubflowExit = useCallback((handler: (() => void) | null) => {
    subflowExitRef.current = handler;
  }, []);
  const exitSubflowSaving = () => {
    if (subflowExitRef.current) {
      subflowExitRef.current();
    } else {
      exitSubflow();
    }
  };

  // Editor "back" is subflow-aware: step out of a nested subflow if inside one,
  // otherwise leave the editor entirely.
  const handleEditorBack = () => {
    if (subflowStack.length > 0) {
      exitSubflow();
    } else {
      navigateToDashboard();
    }
  };

  const navigateToExecution = (executionId: string) => {
    setSelectedExecutionId(executionId);
    setCurrentView('execution');
  };

  const navigateBackFromExecution = () => {
    setCurrentView(lastNonExecutionView);
  };

  // From the editor's "Watch live runs" affordance (event-driven device graphs): jump to the Execution
  // Visualizer on this workflow's most recent run, where it then auto-follows new device-event runs.
  // Falls back to the Dashboard when the workflow has no runs yet.
  const handleWatchLiveRuns = async (workflowId: string) => {
    try {
      const [executions, activeVersion] = await Promise.all([
        api.getExecutions(),
        api.getActiveWorkflowVersion(workflowId).catch(() => null),
      ]);
      const runs = executions
        .filter((e) => e.workflowDefinitionId?.value === workflowId)
        .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());
      // Prefer the latest run of the ACTIVE runtime version — that's the graph you're editing and the one
      // new device-event runs will use — so the viewer matches the current workflow instead of an older
      // run whose graph is "quite different". Fall back to the workflow's latest run, then the dashboard.
      const activeVersionId = activeVersion?.workflowVersionId;
      const target = (activeVersionId ? runs.find((e) => e.workflowVersionId === activeVersionId) : undefined) ?? runs[0];
      if (target) {
        navigateToExecution(target.id);
      } else {
        navigateToDashboard();
      }
    } catch (error) {
      console.error('Failed to open latest run for workflow:', error);
      navigateToDashboard();
    }
  };

  const handleWorkflowLoadFailed = () => {
    setSelectedWorkflowId(null);
    navigateToDashboard();
  };

  // The Execution Visualizer is a permanent destination in the top bar, so it must lead somewhere
  // even when no run is selected yet: open the most recent one, or fall back to the dashboard,
  // which is where runs are listed when there are none.
  const openLatestRun = async () => {
    try {
      const executions = await api.getExecutions();
      const latest = [...executions]
        .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())[0];
      if (latest) {
        navigateToExecution(latest.id);
        return;
      }
    } catch (error) {
      console.error('Failed to open the most recent run:', error);
    }
    navigateToDashboard();
  };

  const handleTopBarSelect = (target: TopBarTarget) => {
    switch (target) {
      case 'dashboard':
        navigateToDashboard();
        break;
      case 'editor':
        setPreviewDefinition(null);
        navigateToEditor(null);
        break;
      case 'execution':
        if (selectedExecutionId) {
          setCurrentView('execution');
        } else {
          void openLatestRun();
        }
        break;
      case 'users':
        setShowUsers(true);
        break;
      default:
        setCurrentView(target);
        setLastNonExecutionView(target);
        break;
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100vh', width: '100vw', background: 'var(--bg-main)', overflow: 'hidden' }}>
      {/* Adaptive top bar — never hides a destination, sheds labels under width pressure. */}
      <TopBar
        view={currentView}
        onSelect={handleTopBarSelect}
        onAiGenerate={openAiGenerate}
        onOpenWorkflow={(id) => navigateToEditor(id)}
        onOpenRun={(id) => navigateToExecution(id)}
        onOpenLatestRun={() => { void openLatestRun(); }}
        onOpenTour={() => setShowTour(true)}
        armed={armed}
        armingBusy={armingBusy}
        onSetArmed={(next) => { void setRuntimeArmed(next); }}
        version={version}
        onGoHome={navigateToDashboard}
      />

      {/* Main Content Area */}
      <main style={{ flex: 1, position: 'relative', overflow: 'hidden' }}>
        {currentView === 'dashboard' && (
          <Dashboard
            onEditWorkflow={(id) => navigateToEditor(id)}
            onViewExecution={(id) => navigateToExecution(id)}
            onTriggeredExecution={(id) => navigateToExecution(id)}
          />
        )}
        {currentView === 'editor' && (
          <>
            {subflowStack.length > 0 && (
              <div
                style={{
                  position: 'absolute',
                  top: 12,
                  left: '50%',
                  transform: 'translateX(-50%)',
                  zIndex: 10,
                  display: 'flex',
                  alignItems: 'center',
                  gap: 8,
                  padding: '6px 12px',
                  borderRadius: 999,
                  background: 'var(--bg-surface-opaque)',
                  border: '1px solid var(--border-color)',
                  fontSize: '0.8rem',
                  color: 'var(--text-secondary)',
                  boxShadow: '0 4px 16px rgba(0,0,0,0.25)',
                }}
              >
                <button
                  onClick={exitSubflowSaving}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: 4,
                    background: 'transparent',
                    border: 'none',
                    color: 'var(--text-primary)',
                    cursor: 'pointer',
                    fontSize: '0.8rem',
                    padding: 0,
                  }}
                >
                  <ArrowLeft size={14} /> Back to parent
                </button>
                <span style={{ color: 'var(--text-muted)' }}>
                  · inside subflow (depth {subflowStack.length})
                </span>
              </div>
            )}
            <Canvas
              workflowId={selectedWorkflowId}
              newWorkflowRequest={newWorkflowRequest}
              previewDefinition={previewDefinition}
              onSaved={(id) => setSelectedWorkflowId(id)}
              onBack={handleEditorBack}
              onTriggered={(id) => setRunStartedExecutionId(id)}
              onSimulated={(id) => navigateToExecution(id)}
              onWorkflowLoadFailed={handleWorkflowLoadFailed}
              onOpenSubflow={openSubflow}
              isSubflow={subflowStack.length > 0}
              registerSubflowExit={registerSubflowExit}
              registerGetDefinition={(g) => { getEditorDefinitionRef.current = g; }}
              onWatchLiveRuns={handleWatchLiveRuns}
              armed={armed}
            />
          </>
        )}
        {currentView === 'execution' && selectedExecutionId && (
          <ExecutionDetail
            executionId={selectedExecutionId}
            onBack={navigateBackFromExecution}
            onTriggeredExecution={(id) => navigateToExecution(id)}
            onGrantFileAccess={() => { setCurrentView('settings'); setLastNonExecutionView('settings'); }}
          />
        )}
        {currentView === 'node-editor' && (
          <NodeEditorShell
            onBack={navigateToDashboard}
          />
        )}
        {currentView === 'api-importer' && (
          <OpenApiView />
        )}
        {currentView === 'settings' && (
          <SettingsView armed={armed} onDisarm={() => { void setRuntimeArmed(false); }} />
        )}
        {currentView === 'dead-letter' && (
          <DeadLetterView onOpenExecution={navigateToExecution} />
        )}
        {currentView === 'bundles' && (
          <BundlesView />
        )}
        {currentView === 'templates' && (
          <TemplatesView onOpenWorkflow={navigateToEditor} />
        )}
        {currentView === 'imports' && (
          <SettingImporter onGoToDashboard={navigateToDashboard} />
        )}
      </main>

      <AiGenerateModal
        open={showAiModal}
        currentWorkflow={aiRefineBase}
        onClose={() => setShowAiModal(false)}
        onGenerated={(workflow) => {
          setShowAiModal(false);
          setAiRefineBase(null);
          setPreviewDefinition(workflow);
          navigateToEditor(null);
        }}
      />

      {runStartedExecutionId && createPortal(
        <div
          role="status"
          style={{
            // Portaled to <body> and centred along the bottom so it clears the right-side properties
            // panel (which previously overlapped/clipped it) and is always plainly visible.
            position: 'fixed',
            bottom: '28px',
            left: '50%',
            transform: 'translateX(-50%)',
            zIndex: 2000,
            display: 'flex',
            alignItems: 'center',
            gap: '12px',
            padding: '12px 16px',
            borderRadius: '10px',
            background: 'rgba(16, 22, 37, 0.98)',
            border: '1px solid rgba(94, 234, 212, 0.35)',
            boxShadow: '0 12px 40px rgba(0, 0, 0, 0.55)',
            color: 'var(--text-secondary)',
            fontSize: '0.85rem',
          }}
        >
          <span>Run started.</span>
          <button
            onClick={() => {
              const executionId = runStartedExecutionId;
              setRunStartedExecutionId(null);
              navigateToExecution(executionId);
            }}
            style={{
              padding: '6px 12px',
              borderRadius: '8px',
              background: 'rgba(94, 234, 212, 0.12)',
              border: '1px solid rgba(94, 234, 212, 0.25)',
              color: '#d8fff7',
              fontWeight: 600,
              fontSize: '0.8rem',
              cursor: 'pointer',
            }}
          >
            View execution
          </button>
          <button
            onClick={() => setRunStartedExecutionId(null)}
            aria-label="Dismiss"
            style={{
              background: 'transparent',
              border: 'none',
              color: 'var(--text-muted)',
              cursor: 'pointer',
              fontSize: '1rem',
              lineHeight: 1,
            }}
          >
            ×
          </button>
        </div>,
        document.body,
      )}

      {showUsers && <UsersPanel onClose={() => setShowUsers(false)} />}

      {showTour && <GuidedTour onClose={closeTour} />}
    </div>
  );
}
