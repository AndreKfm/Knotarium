// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Dashboard } from './components/Dashboard';
import { Canvas } from './components/Canvas';
import { ExecutionDetail } from './components/ExecutionDetail/index';
import { OpenApiView } from './components/OpenApiView';
import { Activity, Edit3, Grid, Code, Globe, Settings, ArrowLeft, Inbox, Package, LayoutTemplate, FileInput, Sparkles, Compass } from 'lucide-react';
import { AiGenerateModal } from './components/AiGenerateModal';
import type { WorkflowDefinition, VersionInfo } from './types';
import { NodeEditorShell } from './node-editor/NodeEditorShell';
import { SettingsView } from './components/SettingsView';
import { DeadLetterView } from './components/DeadLetterView';
import { RuntimeArmingToggle } from './components/RuntimeArmingToggle';
import { UserMenu } from './components/auth/UserMenu';
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

// Build time for the header badge in a FIXED ISO-like format (local time, 24h) — deliberately NOT
// locale-formatted, so the build stamp reads identically on any machine (no "5. Juli" vs "Jul 5").
function formatBuildTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

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
  // AI-generated workflow pending preview on the canvas, and the "Generate with AI" dialog's open state.
  const [previewDefinition, setPreviewDefinition] = useState<WorkflowDefinition | null>(null);
  const [showAiModal, setShowAiModal] = useState(false);
  // The open workflow to REFINE (captured when the AI dialog opens over the editor), and a live getter the
  // Canvas registers for its current on-canvas definition.
  const [aiRefineBase, setAiRefineBase] = useState<WorkflowDefinition | null>(null);
  const getEditorDefinitionRef = useRef<(() => WorkflowDefinition) | null>(null);
  const openAiModal = () => {
    const def = currentView === 'editor' && getEditorDefinitionRef.current ? getEditorDefinitionRef.current() : null;
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

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100vh', width: '100vw', background: 'var(--bg-main)', overflow: 'hidden' }}>
      {/* Premium Glassmorphic Header */}
      <header
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          padding: '0 24px',
          height: '70px',
          background: 'rgba(16, 22, 37, 0.7)',
          backdropFilter: 'blur(10px)',
          borderBottom: '1px solid var(--border-color)',
          zIndex: 10,
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: '12px', cursor: 'pointer' }} onClick={navigateToDashboard}>
          <div
            style={{
              background: 'linear-gradient(135deg, var(--color-accent), var(--color-info))',
              width: '36px',
              height: '36px',
              borderRadius: '10px',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              boxShadow: '0 0 15px var(--color-accent-glow)',
            }}
          >
            <Activity size={18} color="#fff" />
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', lineHeight: 1.15 }}>
            <span style={{ fontWeight: 800, fontSize: '1.25rem', letterSpacing: '0.05em', color: '#fff' }}>
              KNOT<span style={{ color: 'var(--text-secondary, #94a3b8)', fontWeight: 600 }}>ARIUM</span><span style={{ color: 'var(--color-accent)', textShadow: '0 0 10px var(--color-accent-glow)' }}>.</span>
            </span>
            {version && (
              <span
                title={version.buildTimeUtc ? `Built ${new Date(version.buildTimeUtc).toLocaleString()}` : undefined}
                style={{ fontSize: '0.62rem', fontWeight: 600, letterSpacing: '0.02em', color: 'var(--text-secondary, #94a3b8)' }}
              >
                v{version.version}{version.buildTimeUtc ? ` · built ${formatBuildTime(version.buildTimeUtc)}` : ''}
              </span>
            )}
          </div>
        </div>

        {/* Custom Premium Tabs Navigation */}
        <nav style={{ display: 'flex', gap: '8px' }}>
          <button
            data-tour="dashboard"
            onClick={navigateToDashboard}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: currentView === 'dashboard' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: currentView === 'dashboard' ? '#fff' : 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <Grid size={16} />
            Dashboard
          </button>
          <button
            data-tour="canvas-editor"
            onClick={() => { setPreviewDefinition(null); navigateToEditor(null); }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: currentView === 'editor' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: currentView === 'editor' ? '#fff' : 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <Edit3 size={16} />
            Canvas Editor
          </button>
          <button
            data-tour="ai-generate"
            onClick={openAiModal}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <Sparkles size={16} />
            AI Generate
          </button>
          <button
            onClick={() => {
              setCurrentView('node-editor');
              setLastNonExecutionView('node-editor');
            }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: currentView === 'node-editor' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: currentView === 'node-editor' ? '#fff' : 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <Code size={16} />
            Node Editor
          </button>
          <button
            onClick={() => {
              setCurrentView('api-importer');
              setLastNonExecutionView('api-importer');
            }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: currentView === 'api-importer' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: currentView === 'api-importer' ? '#fff' : 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <Globe size={16} />
            API Importer
          </button>
          <button
            data-tour="settings"
            onClick={() => {
              setCurrentView('settings');
              setLastNonExecutionView('settings');
            }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: currentView === 'settings' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: currentView === 'settings' ? '#fff' : 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <Settings size={16} />
            Settings
          </button>
          <button
            data-tour="dead-letter"
            onClick={() => {
              setCurrentView('dead-letter');
              setLastNonExecutionView('dead-letter');
            }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: currentView === 'dead-letter' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: currentView === 'dead-letter' ? '#fff' : 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <Inbox size={16} />
            Dead Letter
          </button>
          <button
            onClick={() => {
              setCurrentView('bundles');
              setLastNonExecutionView('bundles');
            }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: currentView === 'bundles' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: currentView === 'bundles' ? '#fff' : 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <Package size={16} />
            Bundles
          </button>
          <button
            data-tour="templates"
            onClick={() => {
              setCurrentView('templates');
              setLastNonExecutionView('templates');
            }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: currentView === 'templates' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: currentView === 'templates' ? '#fff' : 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <LayoutTemplate size={16} />
            Templates
          </button>
          <button
            onClick={() => {
              setCurrentView('imports');
              setLastNonExecutionView('imports');
            }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 16px',
              background: currentView === 'imports' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
              border: 'none',
              borderRadius: '8px',
              color: currentView === 'imports' ? '#fff' : 'var(--text-secondary)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
              transition: 'all 0.2s ease',
            }}
          >
            <FileInput size={16} />
            Import
          </button>
          {selectedExecutionId && (
            <button
              onClick={() => setCurrentView('execution')}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '8px',
                padding: '10px 16px',
                background: currentView === 'execution' ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
                border: 'none',
                borderRadius: '8px',
                color: currentView === 'execution' ? '#fff' : 'var(--text-secondary)',
                cursor: 'pointer',
                fontWeight: 600,
                fontSize: '0.85rem',
                transition: 'all 0.2s ease',
              }}
            >
              <Activity size={16} />
              Execution Visualizer
            </button>
          )}
        </nav>

        <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
          <RuntimeArmingToggle
            armed={armed}
            busy={armingBusy}
            onToggle={() => { void setRuntimeArmed(!(armed === true)); }}
          />
          <button
            onClick={() => setShowTour(true)}
            title="Take the product tour"
            style={{ display: 'flex', alignItems: 'center', gap: '6px', padding: '8px 12px', background: 'transparent', border: '1px solid var(--border-color)', borderRadius: '8px', color: 'var(--text-secondary)', cursor: 'pointer', fontWeight: 600, fontSize: '0.8rem' }}
          >
            <Compass size={15} /> Tour
          </button>
          <UserMenu />
          <div style={{ fontSize: '0.8rem', color: 'var(--text-muted)', fontWeight: 500 }}>
            Local time: {new Date().toLocaleTimeString()}
          </div>
        </div>
      </header>

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

      {showTour && <GuidedTour onClose={closeTour} />}
    </div>
  );
}
