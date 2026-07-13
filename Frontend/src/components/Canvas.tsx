import { startTransition, useEffect, useState, useCallback, useMemo, useRef } from 'react';
import type { DragEvent, CSSProperties } from 'react';
import {
  ReactFlow,
  MiniMap,
  Controls,
  Background,
  useNodesState,
  useEdgesState,
  addEdge,
  reconnectEdge,
  ReactFlowProvider,
  BackgroundVariant,
  SelectionMode,
  useReactFlow,
  useStoreApi,
  useNodesInitialized,
  getNodesBounds,
} from '@xyflow/react';
import type {
  Connection,
  Edge,
  Node as RFNode,
  FinalConnectionState,
  IsValidConnection,
  OnConnectStartParams,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { api } from '../utils/api';
import { schemaMapper, definitionHasSavedPositions } from '../utils/schemaMapper';
import { createNodeTypes } from '../utils/nodeTypes';
import { createNodePackageMetadataMap, enrichNodesWithPackageMetadata, type NodePackageMetadata } from '../utils/nodePackages';
import { extractSubflowInterface, type SubflowInterface } from '../utils/subflowInterface';
import {
  findContainingLoopNode,
  isContainerNodeType,
  getPortPositions,
  getFreePorts,
  findNearestCompatiblePort,
  findEdgeUnderPoint,
  collectDownstream,
  DEFAULT_NODE_WIDTH,
  EDGE_HIT_TOLERANCE,
  PROXIMITY_THRESHOLD,
  type PortPosition,
  type InternalNodeLike,
  type EdgeLike,
} from '../node-editor/canvasGeometry';
import { connectionFailureReason } from '../node-editor/connectionFeedback';
import {
  createUndoHistory,
  record as recordHistory,
  applyUndo,
  applyRedo,
  type UndoHistory,
} from '../node-editor/undoHistory';
import { buildNode, createNodeId } from '../node-editor/nodeFactory';
import { deviceHandleIds } from '../node-editor/externalDevicePins';
import { referencedActionIds, signalFieldGroupsForNode, simulatablePins, type ActionFieldsById, type SignalField } from '../node-editor/signalFieldBinding';
import { upstreamReferenceGroups } from '../node-editor/upstreamReferences';
import { useSignalFieldStore } from '../stores/useSignalFieldStore';
import { SimulateSignalDialog } from './SimulateSignalDialog';
import { cloneSubgraph } from '../node-editor/clipboard';
import { createStickyNoteNode, STICKY_NOTE_DEFAULT_SIZE, STICKY_NOTE_TYPE } from '../node-editor/stickyNote';
import { StickyNoteNode } from './StickyNoteNode';
import {
  groupNodes,
  ungroupNodes,
  isGroupNodeType,
  findContainingGroupNode,
  applyGroupCollapseOnLoad,
  GROUP_TYPE,
} from '../node-editor/nodeGroup';
import { GroupNode } from './GroupNode';
import {
  computeAutoLayout,
  computeNestedAutoLayout,
  alignNodes,
  distributeNodes,
  snapPointToGrid,
  type AlignEdge,
  type DistributeAxis,
} from '../node-editor/autoLayout';
import { inferLoopContainment, orderParentsBeforeChildren } from '../node-editor/loopContainment';
import { CanvasToolbar } from './CanvasToolbar';
import { isApiError, getErrorMessage, getErrorDiagnostics } from '../utils/apiErrors';
import { decorateEdgesWithDiagnostics } from '../utils/edgeDiagnostics';
import { mergeDiagnostics, resolveDiagnosticFocus, countBySeverity } from '../utils/diagnosticsNavigation';
import { DiagnosticsPanel } from './DiagnosticsPanel';
import { variablePathHead, hasVariablePath, pathContainerKind } from '../utils/variablePath';
import { PropertiesPanel } from './PropertiesPanel';
import { SidebarPalette } from './SidebarPalette';
import { EmptyCanvasHint } from './EmptyCanvasHint';
import { CanvasImportModal } from './CanvasImportModal';
import { useCanvasStore } from '../stores/useCanvasStore';
import type { ActiveWorkflowVersion, CompilationDiagnostic, NodePackageSummary, WorkflowVersionSummary, WorkflowVersion, RestoreVersionResult, WorkflowDefinition } from '../types';
import { CircleHelp, Eye, Hash, History, Maximize2, StickyNote, Group, Ungroup, LayoutTemplate, Crosshair, Combine } from 'lucide-react';
import { analyzeMultiExtraction, planParametrizedExtraction, type ExNode, type ExEdge } from '../node-editor/extractSubflow';

// ── Per-workflow viewport persistence ───────────────────────────────────────
// Re-entering a workflow should land where you left it, not re-center on (often empty) middle.
const VIEWPORT_KEY = (id: string) => `kg-canvas-viewport:${id}`;

function saveViewport(id: string, vp: { x: number; y: number; zoom: number }) {
  if (!id) return;
  try { localStorage.setItem(VIEWPORT_KEY(id), JSON.stringify({ x: vp.x, y: vp.y, zoom: vp.zoom })); } catch { /* storage full / disabled */ }
}

// Returns a saved viewport, or null when absent/invalid (invalid entries are pruned).
function loadViewport(id: string): { x: number; y: number; zoom: number } | null {
  if (!id) return null;
  try {
    const raw = localStorage.getItem(VIEWPORT_KEY(id));
    if (!raw) return null;
    const v = JSON.parse(raw) as { x?: unknown; y?: unknown; zoom?: unknown };
    const ok = [v.x, v.y, v.zoom].every((n) => typeof n === 'number' && Number.isFinite(n))
      && (v.zoom as number) > 0.02 && (v.zoom as number) < 8;
    if (ok) return { x: v.x as number, y: v.y as number, zoom: v.zoom as number };
    localStorage.removeItem(VIEWPORT_KEY(id)); // invalid → reset
  } catch { /* malformed → ignore */ }
  return null;
}
import { VariablesPanel } from './VariablesPanel';
import { NodeSearchPalette } from './NodeSearchPalette';
import { TemplateInsertPicker } from './TemplateInsertPicker';
import { collectSlotNames, rewriteSlotsForInsert } from '../utils/templateSlots';
import type { TemplatePayloadResponse } from '../types';
import { VersionHistoryPanel } from './VersionHistoryPanel';
import { PreviewBanner } from './PreviewBanner';
import { RestoreVersionDialog } from './RestoreVersionDialog';
import { UnsavedChangesDialog } from './UnsavedChangesDialog';
import { VersionDiffView } from './VersionDiffView';
import { useWorkflowVersions } from '../hooks/useWorkflowVersions';
import {
  DRAFT_MODE,
  editorModeReducer,
  isEditingDisabled,
  type EditorMode,
} from '../node-editor/editorMode';
import { diffVersions, type DiffablePayload, type VersionDiff } from '../utils/versionDiff';
import { KeyboardShortcutsHelp } from './KeyboardShortcutsHelp';
import { GlobalReadEdge } from './GlobalReadEdge';
import { useVariableStore } from '../stores/useVariableStore';
import type { VariableRecord } from '../stores/useVariableStore';
import { useInlineCodeEditorStore } from '../stores/useInlineCodeEditorStore';
import { useSubflowOpenStore } from '../stores/useSubflowOpenStore';
import { useConditionEditorOpenStore } from '../stores/useConditionEditorOpenStore';
import { setInlineCodeVariableNames } from './shared/InlineCodeEditorModal';

const edgeTypes = {
  globalRead: GlobalReadEdge,
};

// Stable content signature of the canvas, derived from the backend projection so
// it ignores transient UI state (selection, hover, package-metadata enrichment)
// and the workflow id. Used to tell whether anything meaningful changed since the
// last save, so "Save & Publish" can be disabled when there's nothing to publish.
function workflowSignature(name: string, nodes: RFNode[], edges: Edge[]): string {
  const def = schemaMapper.toBackend('', name, nodes, edges);
  return JSON.stringify({ name: def.name, nodes: def.nodes, edges: def.edges });
}

interface CanvasProps {
  workflowId: string | null;
  // An AI-generated workflow to load as an UNSAVED preview when there's no workflowId. The generator
  // emits topology only, so geometry is assigned here by the same dagre tidy the toolbar button uses.
  previewDefinition?: WorkflowDefinition | null;
  // Fires after a successful save/publish with the workflow's id. For a brand-new workflow this is the
  // freshly-minted id — the parent uses it to remember which workflow is open, so navigating away and back
  // returns to it instead of spawning a new blank draft.
  onSaved?: (workflowId: string) => void;
  onBack?: () => void;
  onTriggered: (executionId: string) => void;
  /** Open the execution directly (used by Simulate, which is an explicit test — unlike a background Run
   * whose non-intrusive "Run started" toast lets you keep editing). Falls back to onTriggered if absent. */
  onSimulated?: (executionId: string) => void;
  onWorkflowLoadFailed?: (workflowId: string) => void;
  // Drill into the referenced child workflow when a subflow node is double-clicked.
  onOpenSubflow?: (subflowId: string) => void;
  // True when the workflow currently open is a subflow being edited (drilled into from a parent).
  isSubflow?: boolean;
  // Register a "save+publish then exit" handler so external back affordances (the breadcrumb)
  // persist+publish the subflow before leaving. Passing null clears it.
  registerSubflowExit?: (handler: (() => void) | null) => void;
  // Register a getter for the CURRENT on-canvas workflow (backend shape), so "Refine with AI" can send
  // the live definition — including unsaved edits and just-generated previews. Passing null clears it.
  registerGetDefinition?: (getter: (() => WorkflowDefinition) | null) => void;
  // Jump to the Execution Visualizer on this workflow's latest run (event-driven device graphs).
  onWatchLiveRuns?: (workflowId: string) => void;
  // Global runtime armed state — when disarmed, device events start no runs, so "watch live" is gated.
  armed?: boolean | null;
}

// Fan-in points that accept MULTIPLE incoming branches instead of the usual single input:
// a container's 'end' loopback (parallelForEach / forLoop body converging back) and the join
// node's input (wait-for-all). Every other input still replaces its existing wire.
function acceptsMultipleIncoming(
  targetId: string | null | undefined,
  targetHandle: string | null | undefined,
  nodes: { id: string; type?: string }[],
): boolean {
  if ((targetHandle ?? '') === 'end') return true;
  return nodes.find((n) => n.id === targetId)?.type === 'join';
}

// Snap-to-grid step (px) — matches the Background dot gap so nodes land on dots.
const SNAP_GRID_SIZE = 24;

// Floating layout-tools toolbar styles (Tidy + Align/Distribute).
const layoutToolbarStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '2px',
  padding: '4px',
  background: 'var(--bg-surface-opaque, #101625)',
  border: '1px solid var(--border-color)',
  borderRadius: '10px',
  boxShadow: '0 6px 20px rgba(0,0,0,0.3)',
};
const layoutBtnStyle: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  gap: '4px',
  minWidth: '28px',
  height: '28px',
  padding: '0 8px',
  background: 'transparent',
  border: 'none',
  borderRadius: '6px',
  color: 'var(--text-primary, #e5e7eb)',
  fontSize: '0.9rem',
  cursor: 'pointer',
};
const layoutDividerStyle: CSSProperties = {
  width: '1px',
  height: '18px',
  background: 'var(--border-color)',
  margin: '0 2px',
};

function CanvasInner({ workflowId, previewDefinition, onSaved, onBack, onTriggered, onSimulated, onWorkflowLoadFailed, onOpenSubflow, isSubflow, registerSubflowExit, registerGetDefinition, onWatchLiveRuns, armed }: CanvasProps) {
  const { screenToFlowPosition, getInternalNode, getNodes, setCenter, fitView, getZoom, setViewport, getViewport } = useReactFlow();
  const reactFlowStore = useStoreApi();
  const [nodes, setNodes, onNodesChange] = useNodesState<RFNode>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  // Live mirrors so callbacks/keybindings always snapshot the latest graph without
  // being re-created (and without stale closures) on every node/edge change. Synced in
  // an effect (post-commit) — handlers run after render, so they read fresh values.
  const nodesRef = useRef(nodes);
  const edgesRef = useRef(edges);
  useEffect(() => {
    nodesRef.current = nodes;
    edgesRef.current = edges;
  }, [nodes, edges]);
  // True after a workflow loads WITHOUT saved positions (gallery examples, imports): the graph is then
  // auto-tidied once React Flow has measured the nodes (so wide nodes get real spacing, not the default).
  // State, not a ref, so setting it re-runs the tidy effect even when the graph was already measured before
  // this load (otherwise the effect, keyed only on nodesInitialized, would miss the no-transition case).
  const [autoTidyPending, setAutoTidyPending] = useState(false);
  const [selectedNode, setSelectedNode] = useState<RFNode | null>(null);
  const [selectedEdge, setSelectedEdge] = useState<Edge | null>(null);
  const [selectedNodeCount, setSelectedNodeCount] = useState(0);
  const [extracting, setExtracting] = useState(false);
  
  const [workflowName, setWorkflowName] = useState('New Workflow');
  const workflowNameRef = useRef(workflowName);
  useEffect(() => { workflowNameRef.current = workflowName; }, [workflowName]);
  const [currentId, setCurrentId] = useState('');
  // Armed when a brand-new workflow is created, consumed on its first save: a fresh graph is assembled at
  // wherever the user dropped nodes, so framing it once at the first save keeps it from landing in a corner.
  // Only for new workflows (never for ones opened from the dashboard) and only once.
  const pendingFirstFrameRef = useRef(false);

  // Expose the CURRENT on-canvas definition (backend shape) to the host so "Refine with AI" can send the
  // live workflow — reads nodesRef/edgesRef so it always reflects the latest edits, incl. unsaved ones.
  useEffect(() => {
    registerGetDefinition?.(() => schemaMapper.toBackend(currentId, workflowName, nodesRef.current, edgesRef.current));
    return () => registerGetDefinition?.(null);
  }, [registerGetDefinition, currentId, workflowName]);

  // Signature of the last saved/published canvas; null until first load completes.
  const [savedSignature, setSavedSignature] = useState<string | null>(null);
  const currentSignature = useMemo(
    () => workflowSignature(workflowName, nodes, edges),
    [workflowName, nodes, edges],
  );
  const isDirty = savedSignature !== null && currentSignature !== savedSignature;
  const [diagnostics, setDiagnostics] = useState<CompilationDiagnostic[]>([]);
  // Non-blocking diagnostics (e.g. edge type-mismatch warnings) fetched live from the validate
  // endpoint and used to colour offending edges. Separate from `diagnostics`, which is the
  // blocking publish/run failure overlay.
  const [edgeDiagnostics, setEdgeDiagnostics] = useState<CompilationDiagnostic[]>([]);
  // Dockable diagnostics panel (#9): collapse state, defaulting to expanded.
  const [diagnosticsCollapsed, setDiagnosticsCollapsed] = useState(false);

  // Live, debounced compile pass so the editor can mark type-mismatch edges as you wire the graph.
  // Keyed off currentSignature (which ignores selection/position churn), so it only re-runs when
  // the graph's meaningful shape changes.
  useEffect(() => {
    if (!currentId || edges.length === 0) {
      setEdgeDiagnostics([]);
      return;
    }

    let cancelled = false;
    const handle = setTimeout(() => {
      api.validateWorkflow(currentId, nodes, edges)
        .then((result) => { if (!cancelled) setEdgeDiagnostics(result); })
        .catch(() => { if (!cancelled) setEdgeDiagnostics([]); });
    }, 500);

    return () => { cancelled = true; clearTimeout(handle); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentId, currentSignature]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [triggering, setTriggering] = useState(false);
  // Connection feedback toast: a success pulse, or an error explaining why a drop didn't wire up.
  const [connectToast, setConnectToast] = useState<{ kind: 'success' | 'error'; message: string } | null>(null);
  const connectToastTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Pending click-to-connect source (output handle a click-connection began on).
  const clickConnectRef = useRef<{ nodeId: string; handleId: string | null } | null>(null);
  // Timestamp of the last wire-up — used to swallow the node click that
  // immediately follows a handle-completed connection (so it doesn't toggle a pin).
  const lastConnectAtRef = useRef(0);
  // Edge-reconnect: tracks whether a drag of an existing edge endpoint landed on a
  // valid handle. If it ends in empty space (still false), the edge is deleted.
  const edgeReconnectSuccessful = useRef(true);
  // Input-pickup: when a drag starts on an already-wired *input* handle, the existing
  // incoming wire is lifted off and follows the cursor. Dropping on another node moves
  // its target there (source unchanged); dropping in empty space restores it.
  const inputPickupRef = useRef<{ source: string; sourceHandle: string | null; target: string; targetHandle: string | null } | null>(null);
  // Pre-lift snapshot for an input-pickup, committed to history only if the wire is re-homed.
  const pickupSnapshotRef = useRef<{ nodes: RFNode[]; edges: Edge[] } | null>(null);
  // Latest proximity-snap scheduler, kept in a ref so handlers declared earlier in the
  // component (e.g. handleNodeDragStop) can trigger it without a use-before-declare cycle.
  const scheduleProximityRef = useRef<(nodeId: string, triggerOnly: boolean) => void>(() => {});

  // ── Undo/Redo ── The canvas is the live "present"; historyRef holds snapshots to
  // restore. recordUndo() pushes a pre-change snapshot before each structural edit.
  type CanvasSnapshot = { nodes: RFNode[]; edges: Edge[] };
  const historyRef = useRef<UndoHistory<CanvasSnapshot>>(createUndoHistory<CanvasSnapshot>());
  // Pre-move snapshot captured at drag start, committed on drag stop only if changed.
  const dragStartSnapshotRef = useRef<CanvasSnapshot | null>(null);
  // Proximity-snap cache for the in-flight node drag: the free ports of every OTHER node, computed once
  // at drag start (they don't move while one node is dragged), so each mousemove only recomputes the
  // dragged node's own ports instead of rebuilding all nodes' ports every frame.
  const dragProximityRef = useRef<{ nodeId: string; otherFree: PortPosition[] } | null>(null);
  // Copy/paste clipboard (selected subgraph with original ids) + repeat-paste offset counter.
  const clipboardRef = useRef<{ nodes: RFNode[]; edges: Edge[] } | null>(null);
  const pasteCountRef = useRef(0);

  const variables = useVariableStore((state) => state.variables[currentId] || []);
  const syncConsumers = useVariableStore((state) => state.syncConsumers);
  const syncDeclaredVariables = useVariableStore((state) => state.syncDeclaredVariables);

  // Distinct external-action ids referenced by the graph — the device block's action pins and any
  // Action Trigger's picked action. Their static field schema (read from the provider) names the keys
  // the inbound `signal.params` can carry, so we can offer `signal.params.<key>` as concrete globals.
  const referencedActions = useMemo(() => referencedActionIds(nodes, edges), [nodes, edges]);
  const referencedActionsKey = useMemo(() => referencedActions.join('|'), [referencedActions]);

  // Static field schema (key + type) per referenced action, fetched once from the provider via the
  // reactor.actionFields loader. NOT registered as canvas globals — the inbound `signal` is one
  // instance per run, so its `params.<key>` fields belong to the originating action, not the whole
  // canvas. They're surfaced per-node (scoped to the action that can reach a given node) in the
  // properties panel instead.
  const [actionFieldsById, setActionFieldsById] = useState<ActionFieldsById>({});
  useEffect(() => {
    let cancelled = false;
    if (referencedActions.length === 0) {
      setActionFieldsById({});
      return;
    }
    const inferType = (description?: string): SignalField['type'] => {
      const d = (description || '').toLowerCase();
      if (d.startsWith('integer') || d.startsWith('number')) return 'number';
      if (d.startsWith('boolean')) return 'boolean';
      return 'string';
    };
    (async () => {
      const map: ActionFieldsById = {};
      await Promise.all(referencedActions.map(async (action) => {
        try {
          // integrationType is a routing segment the host ignores (loaders resolve by name); keep the
          // generic 'reactor' family so no specific provider is named on the public side.
          const result = await api.loadNodeOptions('reactor', 'reactor.actionFields', { dependsOn: { action } });
          map[action] = result.options
            .filter((opt) => opt.value)
            .map((opt) => ({ key: opt.value, type: inferType(opt.description) }));
        } catch {
          // Provider offline / loader absent → no static keys; the generic signal.params bag still works.
        }
      }));
      if (cancelled) return;
      setActionFieldsById(map);
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [referencedActionsKey]);

  // Events all share one field layout (the provider's common event slots), so they're fetched once via
  // reactor.eventFields — but only when the graph actually has an inbound-signal source.
  const hasInboundSignalSource = useMemo(
    () => nodes.some((n) => { const t = (n.type || '').toLowerCase(); return t === 'externaldevice' || t === 'eventtrigger' || t === 'actiontrigger'; }),
    [nodes],
  );
  const [eventFields, setEventFields] = useState<SignalField[]>([]);
  useEffect(() => {
    let cancelled = false;
    if (!hasInboundSignalSource) { setEventFields([]); return; }
    const inferType = (description?: string): SignalField['type'] => {
      const d = (description || '').toLowerCase();
      if (d.startsWith('integer') || d.startsWith('number')) return 'number';
      if (d.startsWith('boolean')) return 'boolean';
      return 'string';
    };
    (async () => {
      try {
        const result = await api.loadNodeOptions('reactor', 'reactor.eventFields', {});
        if (cancelled) return;
        setEventFields(result.options.filter((opt) => opt.value).map((opt) => ({ key: opt.value, type: inferType(opt.description) })));
      } catch {
        // Provider offline / loader absent → no static event keys; signal.params still works by hand.
      }
    })();
    return () => { cancelled = true; };
  }, [hasInboundSignalSource]);

  // Scoped signal fields for the currently-selected node: the originating action(s) whose inbound
  // signal can reach it (each with its fields) plus the shared event group — shown in the node's panel.
  const selectedNodeSignalGroups = useMemo(
    () => (selectedNode ? signalFieldGroupsForNode(nodes, edges, selectedNode.id, actionFieldsById, eventFields) : []),
    [selectedNode, nodes, edges, actionFieldsById, eventFields],
  );
  // Publish them to the per-node store so the node's editors (properties panel chips + Condition operand
  // reference picker) can read them without threading props through ManifestForm.
  useEffect(() => {
    useSignalFieldStore.getState().setSignalFields(selectedNode?.id ?? null, selectedNodeSignalGroups);
  }, [selectedNode, selectedNodeSignalGroups]);

  // Variables declared on the canvas via Set Variable / Set Variables nodes. These become
  // first-class globals (auto-registered in the Global Store below), so they get a draggable
  // pill and show up alongside promoted node outputs.
  const declaredVariables = useMemo(() => {
    const out: Array<{ producer: string; name: string; type: VariableRecord['type']; value: unknown; containerKind?: 'object' | 'array' }> = [];
    const inferType = (v: unknown): VariableRecord['type'] => {
      if (typeof v === 'boolean') return 'boolean';
      if (typeof v === 'number') return 'number';
      if (typeof v === 'object' && v !== null) return 'object';
      if (typeof v === 'string') {
        const s = v.trim();
        if (s === 'true' || s === 'false') return 'boolean';
        if (s !== '' && !s.includes('{{') && !Number.isNaN(Number(s))) return 'number';
      }
      return 'string';
    };
    // A keyed write (myDict["name"], list[0]) targets the head container; register that
    // global, not the literal path. Its concrete value resolves at run time, so a keyed
    // write contributes an object/array with no design-time preview value. Used by both the
    // single Set Variable and the bulk Set Variables rows so they derive identically.
    const pushDeclared = (producer: string, rawName: string, rawValue: unknown) => {
      const keyed = hasVariablePath(rawName);
      out.push({
        producer,
        name: variablePathHead(rawName),
        type: keyed ? 'object' : inferType(rawValue),
        value: keyed ? undefined : rawValue,
        containerKind: keyed ? pathContainerKind(rawName) : undefined,
      });
    };
    nodes.forEach((n) => {
      const t = (n.type || '').toLowerCase();
      const props = (n.data?.properties as Record<string, unknown>) || {};
      if (t === 'setvariable' && typeof props.variableName === 'string' && props.variableName) {
        pushDeclared(n.id, props.variableName, props.value);
      }
      if (t === 'setvariables' && Array.isArray(props.variables)) {
        (props.variables as { name?: string; value?: unknown }[]).forEach((r) => {
          if (typeof r?.name === 'string' && r.name) pushDeclared(n.id, r.name, r.value);
        });
      }
      // Declared subflow-interface locals: inputs on the Start node, outputs on the End node. These
      // surface in the Global Store while editing the subflow so its locals are visible/draggable.
      const interfaceRows =
        t === 'start' && Array.isArray(props.interfaceInputs) ? (props.interfaceInputs as { name?: string; type?: VariableRecord['type'] }[])
        : t === 'end' && Array.isArray(props.interfaceOutputs) ? (props.interfaceOutputs as { name?: string; type?: VariableRecord['type'] }[])
        : [];
      interfaceRows.forEach((r) => {
        if (r?.name) out.push({ producer: n.id, name: r.name, type: r.type || 'string', value: undefined });
      });
    });

    // Inbound signal fields: when the graph can be started by an external signal (a device block or an
    // Event/Action Trigger), surface the fixed `signal.*` fields as draggable globals. Dropping one
    // writes a variable_ref the backend resolves via dotted path (see WorkflowStateProjection). The
    // per-event/action `params` fields are dynamic, so only the whole `signal.params` bag is offered.
    const hasInboundSignal = nodes.some((n) => {
      const t = (n.type || '').toLowerCase();
      return t === 'externaldevice' || t === 'eventtrigger' || t === 'actiontrigger';
    });
    if (hasInboundSignal) {
      const signalFields: Array<{ name: string; type: VariableRecord['type'] }> = [
        { name: 'signal.type', type: 'string' },
        { name: 'signal.active', type: 'boolean' },
        { name: 'signal.kind', type: 'string' },
        { name: 'signal.camera', type: 'number' },
        { name: 'signal.channel', type: 'string' },
        { name: 'signal.params', type: 'object' },
      ];
      for (const f of signalFields) {
        out.push({ producer: '__signal', name: f.name, type: f.type, value: undefined });
      }
      // NOTE: per-action `signal.params.<key>` fields are deliberately NOT registered here — they're
      // instance-scoped (one action per run), so they're surfaced per-node in the properties panel
      // (selectedNodeSignalGroups) rather than flattened into the canvas-wide store.
    }
    return out;
  }, [nodes]);

  // Re-sync only when the set of declarations actually changes (not on every node drag).
  const declaredVariablesKey = useMemo(() => JSON.stringify(declaredVariables), [declaredVariables]);
  useEffect(() => {
    syncDeclaredVariables(currentId, declaredVariables);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [declaredVariablesKey, currentId]);

  // Feed every known variable name to the inline-code editor's autocomplete.
  useEffect(() => {
    setInlineCodeVariableNames(variables.map((v) => v.name).filter(Boolean));
  }, [variables]);
  const isDraggingOutput = useVariableStore((state) => state.isDraggingOutput);

  const hoveredNodeId = useVariableStore((state) => state.hoveredNodeId);
  const hoveredVariableId = useVariableStore((state) => state.hoveredVariableId);
  const pinnedNodeIds = useVariableStore((state) => state.pinnedNodeIds);
  const pinnedVariableIds = useVariableStore((state) => state.pinnedVariableIds);
  const densityMode = useVariableStore((state) => state.densityMode);
  const setDensityMode = useVariableStore((state) => state.setDensityMode);

  const [isDensityPopoverOpen, setIsDensityPopoverOpen] = useState(false);
  const popoverRef = useRef<HTMLDivElement>(null);

  // Close popover when clicking outside
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (popoverRef.current && !popoverRef.current.contains(event.target as Node)) {
        setIsDensityPopoverOpen(false);
      }
    };
    if (isDensityPopoverOpen) {
      document.addEventListener('mousedown', handleClickOutside);
    }
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
    };
  }, [isDensityPopoverOpen]);

  // Sync consumers whenever nodes change
  useEffect(() => {
    if (currentId) {
      syncConsumers(currentId, nodes);
    }
  }, [nodes, currentId, syncConsumers]);

  // Keep selectedNode in sync with nodes array changes (e.g. from canvas drops)
  useEffect(() => {
    if (selectedNode) {
      const current = nodes.find(n => n.id === selectedNode.id);
      if (current && JSON.stringify(current.data?.properties) !== JSON.stringify(selectedNode.data?.properties)) {
        setSelectedNode(current);
      }
    }
  }, [nodes, selectedNode]);

  // The set of node IDS (NOT positions) — stable across a position-only drag, so memos that only need to
  // know which nodes exist don't recompute every drag tick. The key is rebuilt each render (a cheap id
  // join) but only changes when nodes are added/removed.
  const nodeIdsKey = useMemo(() => nodes.map((n) => n.id).join(','), [nodes]);
  const nodeIdSet = useMemo(() => new Set(nodeIdsKey ? nodeIdsKey.split(',') : []), [nodeIdsKey]);

  const displayEdges = useMemo(() => {
    if (!currentId) return edges;
    const virtualEdges: Edge[] = [];
    const workflowVars = variables;

    workflowVars.forEach((v) => {
      const producerId = v.producer;
      if (!producerId) return;

      v.consumers.forEach((consumerId) => {
        const producerExists = nodeIdSet.has(producerId);
        const consumerExists = nodeIdSet.has(consumerId);
        if (!producerExists || !consumerExists) return;

        const isHovered =
          hoveredNodeId === producerId ||
          hoveredNodeId === consumerId ||
          hoveredVariableId === v.id;

        const isPinned =
          pinnedNodeIds.includes(producerId) ||
          pinnedNodeIds.includes(consumerId) ||
          pinnedVariableIds.includes(v.id);

        virtualEdges.push({
          id: `virtual-read-${v.id}-${producerId}-${consumerId}`,
          source: producerId,
          target: consumerId,
          type: 'globalRead',
          reconnectable: false,
          data: {
            variableName: v.name,
            variableType: v.type,
            variableValue: v.value,
            variableStatus: v.status,
            variableContainerKind: v.containerKind,
            variableId: v.id,
            producerId,
            consumerId,
            densityMode,
            isHovered,
            isPinned,
          },
          animated: true,
        });
      });
    });

    return [...decorateEdgesWithDiagnostics(edges, edgeDiagnostics), ...virtualEdges];
    // Depends on the node-id SET, not `nodes` — so a position-only drag doesn't rebuild every edge object
    // (which would force React Flow to re-render all edges each frame).
  }, [edges, edgeDiagnostics, variables, nodeIdSet, currentId, hoveredNodeId, hoveredVariableId, pinnedNodeIds, pinnedVariableIds, densityMode]);
  const [availableNodes, setAvailableNodes] = useState<NodePackageSummary[]>([]);
  // id -> name for every workflow, used to label subflow nodes with the workflow they call.
  const [workflowNameById, setWorkflowNameById] = useState<Record<string, string>>({});
  // id -> declared interface (input/output locals) of each workflow, so a subflow node can render
  // one bind-slot per declared local of the child it calls.
  const [subflowInterfaceById, setSubflowInterfaceById] = useState<Record<string, SubflowInterface>>({});
  const [showOpenApiImportModal, setShowOpenApiImportModal] = useState(false);
  const [workflowVersions, setWorkflowVersions] = useState<WorkflowVersionSummary[]>([]);
  const [activeWorkflowVersion, setActiveWorkflowVersion] = useState<ActiveWorkflowVersion | null>(null);
  // Live mirror so preview/diff handlers read the latest active version without
  // re-creating on every change (e.g. a remote activation arriving mid-preview).
  const activeWorkflowVersionRef = useRef<ActiveWorkflowVersion | null>(null);
  useEffect(() => { activeWorkflowVersionRef.current = activeWorkflowVersion; }, [activeWorkflowVersion]);
  const [selectedActivationVersionId, setSelectedActivationVersionId] = useState('');
  const [workflowStatusMessage, setWorkflowStatusMessage] = useState<string | null>(null);
  const addRecentNode = useCanvasStore((state) => state.addRecentNode);

  const availableNodeMetadata = useMemo(
    () => createNodePackageMetadataMap(availableNodes),
    [availableNodes],
  );
  const availableNodeMetadataRef = useRef(availableNodeMetadata);

  useEffect(() => {
    availableNodeMetadataRef.current = availableNodeMetadata;
  }, [availableNodeMetadata]);

  // Insertable `{{ $node.<id>.output.<field> }}` references from the selected node's UPSTREAM outputs,
  // for the properties-panel reference picker (schema-driven expression discovery).
  const upstreamRefGroups = useMemo(
    () => upstreamReferenceGroups(selectedNode?.id ?? null, nodes, edges, availableNodeMetadata),
    [selectedNode, nodes, edges, availableNodeMetadata],
  );

  // Sync activeWorkflowId in store
  useEffect(() => {
    useVariableStore.setState({ activeWorkflowId: currentId || null });
    return () => {
      useVariableStore.setState({ activeWorkflowId: null });
    };
  }, [currentId]);

  // Fix stuck drag-and-drop overlay hint
  useEffect(() => {
    const handleGlobalDragEnd = () => {
      if (useVariableStore.getState().isDraggingOutput) {
        useVariableStore.getState().setDraggingOutput(false, null);
      }
      if (useVariableStore.getState().isDraggingToken) {
        useVariableStore.getState().setDraggingToken(false, null);
      }
    };
    window.addEventListener('dragend', handleGlobalDragEnd);
    return () => {
      window.removeEventListener('dragend', handleGlobalDragEnd);
    };
  }, []);

  // Load custom packages on mount
  useEffect(() => {
    api.getNodePackages()
      .then(setAvailableNodes)
      .catch(err => console.error("Error loading node packages:", err));
  }, []);

  // Load the id -> name map so subflow nodes can show the workflow they call (live, so renames
  // and pre-existing nodes resolve without needing a name baked into the node).
  useEffect(() => {
    api.getWorkflows()
      .then((list) => {
        setWorkflowNameById(Object.fromEntries(list.map((w) => [w.id.value, w.name])));
        setSubflowInterfaceById(Object.fromEntries(list.map((w) => [w.id.value, extractSubflowInterface(w)])));
      })
      .catch(err => console.error("Error loading workflow names:", err));
  }, [currentId]);

  // Identity of the subflow nodes only (id + referenced workflow id) — stable across position-only drags,
  // so the enrichment effect below skips the bulk of the time.
  const subflowSyncKey = useMemo(
    () => nodes
      .filter((n) => n.type === 'subflow')
      .map((n) => `${n.id}:${(n.data?.properties as Record<string, unknown> | undefined)?.subflowId ?? ''}`)
      .join('|'),
    [nodes],
  );

  // Stamp each subflow node with the resolved name of the workflow it references (data-level,
  // not persisted into the definition). Only writes when the value actually changes to avoid loops.
  useEffect(() => {
    setNodes((current) => {
      let changed = false;
      const next = current.map((node) => {
        if (node.type !== 'subflow') return node;
        const props = (node.data?.properties as Record<string, unknown>) || {};
        const subflowId = props.subflowId;
        const resolved = typeof subflowId === 'string' ? (workflowNameById[subflowId] ?? '') : '';
        const resolvedInterface = typeof subflowId === 'string' ? subflowInterfaceById[subflowId] : undefined;

        // Once the child declares an interface, the only valid bindings are its declared locals.
        // Drop orphan rows left over from earlier free-form edits so the node matches the contract.
        let nextProps = props;
        if (resolvedInterface) {
          const inNames = new Set(resolvedInterface.inputs.map((v) => v.name));
          const outNames = new Set(resolvedInterface.outputs.map((v) => v.name));
          const rawIn = Array.isArray(props.subflowInputs) ? (props.subflowInputs as Record<string, unknown>[]) : null;
          const rawOut = Array.isArray(props.subflowOutputs) ? (props.subflowOutputs as Record<string, unknown>[]) : null;
          const prunedIn = rawIn ? rawIn.filter((r) => typeof r.target === 'string' && inNames.has(r.target)) : null;
          const prunedOut = rawOut ? rawOut.filter((r) => typeof r.source === 'string' && outNames.has(r.source)) : null;
          const inPruned = !!rawIn && !!prunedIn && prunedIn.length !== rawIn.length;
          const outPruned = !!rawOut && !!prunedOut && prunedOut.length !== rawOut.length;
          if (inPruned || outPruned) {
            nextProps = {
              ...props,
              ...(inPruned ? { subflowInputs: prunedIn } : {}),
              ...(outPruned ? { subflowOutputs: prunedOut } : {}),
            };
          }
        }

        const nameSame = (node.data?.subflowName ?? '') === resolved;
        const ifaceSame = JSON.stringify(node.data?.subflowInterface ?? null) === JSON.stringify(resolvedInterface ?? null);
        const propsSame = nextProps === props;
        if (nameSame && ifaceSame && propsSame) return node;
        changed = true;
        return { ...node, data: { ...node.data, subflowName: resolved, subflowInterface: resolvedInterface, properties: nextProps } };
      });
      return changed ? next : current;
    });
    // Depend on the SET of subflow nodes (id + referenced workflow), not the whole `nodes` array — else
    // this full-array map (with per-subflow JSON.stringify) would re-run on every drag tick.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workflowNameById, subflowInterfaceById, subflowSyncKey, setNodes]);

  useEffect(() => {
    setNodes(currentNodes => enrichNodesWithPackageMetadata(currentNodes, availableNodeMetadata));
    setEdges(currentEdges => currentEdges.map(edge => ({ ...edge })));
  }, [availableNodeMetadata, setNodes, setEdges]);

  // External-device pin churn: when an externalDevice node's ticked events/actions change, its pins
  // (and thus handle ids) change. Drop any edges that now reference a removed pin so no wire dangles
  // against a missing handle (same concern subflow I/O prunes above; React Flow won't do this for us).
  useEffect(() => {
    const deviceHandlesByNode = new Map<string, ReturnType<typeof deviceHandleIds>>();
    for (const node of nodes) {
      if (node.type === 'externalDevice') {
        deviceHandlesByNode.set(node.id, deviceHandleIds(node.data?.properties as Record<string, unknown> | undefined));
      }
    }
    if (deviceHandlesByNode.size === 0) return;
    setEdges((current) => {
      const next = current.filter((e) => {
        const src = deviceHandlesByNode.get(e.source);
        if (src && e.sourceHandle && !src.sourceHandles.has(e.sourceHandle)) return false;
        const tgt = deviceHandlesByNode.get(e.target);
        if (tgt && e.targetHandle && !tgt.targetHandles.has(e.targetHandle)) return false;
        return true;
      });
      return next.length === current.length ? current : next;
    });
  }, [nodes, setEdges]);

  // Keep the latest onWorkflowLoadFailed in a ref so the load effect below doesn't depend on its
  // identity — it's an unmemoized App callback, and listing it in the deps re-ran the load (reloading
  // from the backend and discarding unsaved edits) on every App re-render, e.g. opening the AI modal.
  const onWorkflowLoadFailedRef = useRef(onWorkflowLoadFailed);
  onWorkflowLoadFailedRef.current = onWorkflowLoadFailed;

  // Load existing workflow
  useEffect(() => {
    let isCancelled = false;

    async function loadWorkflowDefinition(targetWorkflowId: string) {
      setLoading(true);

      try {
        const [wf, versions, activeVersion] = await Promise.all([
          api.getWorkflow(targetWorkflowId),
          api.getWorkflowVersions(targetWorkflowId),
          api.getActiveWorkflowVersion(targetWorkflowId),
        ]);

        if (isCancelled) {
          return;
        }

        setWorkflowName(wf.name);
        setCurrentId(wf.id.value);
        setWorkflowVersions(versions);
        setActiveWorkflowVersion(activeVersion);
        setSelectedActivationVersionId(activeVersion?.workflowVersionId ?? versions[0]?.id ?? '');
        const { nodes: rfNodes, edges: rfEdges } = schemaMapper.toReactFlow(wf);
        // A definition without saved positions gets a measured auto-tidy once React Flow lays it out
        // (see the useNodesInitialized effect) — the mapper's default-width pass can overlap wide nodes.
        setAutoTidyPending(!definitionHasSavedPositions(wf));
        // Re-derive child visibility from any persisted collapsed groups (#14): the
        // runtime `hidden` flag isn't persisted, only the group's `collapsed` property.
        setNodes(applyGroupCollapseOnLoad(enrichNodesWithPackageMetadata(rfNodes, availableNodeMetadataRef.current)));
        setEdges(rfEdges);
        setSavedSignature(workflowSignature(wf.name, rfNodes, rfEdges));
        historyRef.current = createUndoHistory<CanvasSnapshot>(); // fresh history per loaded workflow
      } catch (err) {
        if (isCancelled) {
          return;
        }

        console.error(err);
        if (isApiError(err) && err.status === 404) {
          onWorkflowLoadFailedRef.current?.(targetWorkflowId);
          alert('Workflow could not be loaded because it no longer exists in the current database.');
          return;
        }

        alert('Failed to load workflow definition.');
      } finally {
        if (!isCancelled) {
          setLoading(false);
        }
      }
    }

    if (workflowId) {
      void loadWorkflowDefinition(workflowId);
    } else if (previewDefinition) {
      // AI-generated preview: convert, then — since the generator emits no coordinates OR loop
      // containment — infer each forLoop's body from its wiring, parent those nodes into the container,
      // and lay out container-aware so the body sits INSIDE the loop rather than as top-level siblings.
      const { nodes: rfNodes, edges: rfEdges } = schemaMapper.toReactFlow(previewDefinition);
      const enriched = enrichNodesWithPackageMetadata(rfNodes, availableNodeMetadataRef.current);
      const parentByChild = inferLoopContainment(
        enriched.map((n) => ({ id: n.id, type: n.type })),
        rfEdges.map((e) => ({ source: e.source, sourceHandle: e.sourceHandle, target: e.target })),
      );
      const parented = enriched.map((n) => {
        const parentId = n.parentId ?? parentByChild.get(n.id);
        return parentId ? { ...n, parentId, extent: 'parent' as const } : n;
      });
      const dim = (v: unknown) => (typeof v === 'number' ? v : undefined);
      const positions = computeNestedAutoLayout(
        parented.map((n) => ({ id: n.id, type: n.type, parentId: n.parentId, width: dim(n.style?.width), height: dim(n.style?.height) })),
        rfEdges.map((e) => ({ source: e.source, target: e.target })),
        { direction: 'LR' },
      );
      const posById = new Map(positions.map((p) => [p.id, p]));
      const positioned = parented.map((n) => {
        const p = posById.get(n.id);
        if (!p) return n;
        const next = { ...n, position: { x: p.x, y: p.y } };
        if (p.width && p.height) next.style = { ...n.style, width: p.width, height: p.height };
        return next;
      });
      // React Flow requires a parent node to precede its children in the array — the inferred loop
      // parenting can otherwise leave a child before its container, which React Flow drops.
      const ordered = orderParentsBeforeChildren(positioned);
      const previewName = previewDefinition.name || 'Generated workflow';

      startTransition(() => {
        // Use the definition's id: on refine it's the original workflow id (so saving updates it in
        // place); on from-scratch generation it's the backend-minted fresh id.
        setCurrentId(previewDefinition.id?.value || crypto.randomUUID());
        setWorkflowName(previewName);
        setWorkflowVersions([]);
        setActiveWorkflowVersion(null);
        setSelectedActivationVersionId('');
        setWorkflowStatusMessage(null);
        setNodes(ordered);
        setEdges(rfEdges);
        // Baseline against an EMPTY canvas so the generated content reads as unsaved (dirty) and the
        // user is prompted to save; saving then goes through the normal create-workflow path.
        setSavedSignature(workflowSignature(previewName, [], []));
        historyRef.current = createUndoHistory<CanvasSnapshot>();
      });
    } else {
      const startNodeMetadata = availableNodeMetadataRef.current.start;

      const initialNodes: RFNode[] = [
        {
          id: 'start-1',
          type: 'start',
          position: { x: 150, y: 200 },
          data: {
            properties: {},
            displayName: startNodeMetadata?.displayName || 'Start',
            triggerOnly: startNodeMetadata?.triggerOnly ?? true,
            outputHandles: startNodeMetadata?.outputHandles || ['result'],
          },
        },
      ];

      pendingFirstFrameRef.current = true;
      startTransition(() => {
        setCurrentId(crypto.randomUUID());
        setWorkflowName('Knotarium Flow');
        setWorkflowVersions([]);
        setActiveWorkflowVersion(null);
        setSelectedActivationVersionId('');
        setWorkflowStatusMessage(null);
        setNodes(initialNodes);
        setEdges([]);
        setSavedSignature(workflowSignature('Knotarium Flow', initialNodes, []));
        historyRef.current = createUndoHistory<CanvasSnapshot>();
      });
    }

    return () => {
      isCancelled = true;
    };
    // onWorkflowLoadFailed intentionally excluded — read via ref so an App re-render (e.g. opening the
    // AI modal) doesn't re-run this effect and reload the workflow over unsaved edits.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workflowId, previewDefinition, setNodes, setEdges]);

  // Restore the last viewport for this workflow once its nodes are in (else fit). Runs once per workflow id
  // — re-entering lands where you left off instead of re-centering on the (often empty) middle.
  // True when applying `vp` would leave at least a visible sliver of the graph on screen. Used to reject a
  // stale saved viewport that would hide the graph (e.g. graphs at negative coordinates + an origin viewport).
  const viewportShowsGraph = useCallback((vp: { x: number; y: number; zoom: number }) => {
    const graphNodes = getNodes();
    if (graphNodes.length === 0) return true;
    const bounds = getNodesBounds(graphNodes);
    const { width, height } = reactFlowStore.getState();
    if (!width || !height) return true; // pane not measured yet — don't override the restore
    const left = bounds.x * vp.zoom + vp.x;
    const top = bounds.y * vp.zoom + vp.y;
    const right = (bounds.x + bounds.width) * vp.zoom + vp.x;
    const bottom = (bounds.y + bounds.height) * vp.zoom + vp.y;
    const overlapW = Math.min(right, width) - Math.max(left, 0);
    const overlapH = Math.min(bottom, height) - Math.max(top, 0);
    return overlapW > 60 && overlapH > 60;
  }, [getNodes, reactFlowStore]);

  const restoredForRef = useRef<string>('');
  useEffect(() => {
    if (!currentId || nodes.length === 0 || restoredForRef.current === currentId) return;
    restoredForRef.current = currentId;
    const saved = loadViewport(currentId);
    // rAF so React Flow has measured the freshly-set nodes before the camera moves.
    requestAnimationFrame(() => {
      // Restore the remembered viewport only if it still actually shows the graph. A stale/degenerate
      // saved viewport (e.g. the origin {0,0,1}) would leave the graph off-screen — common because graphs
      // can sit at negative coordinates — so fall back to fitView instead of landing in an empty corner.
      if (saved && viewportShowsGraph(saved)) {
        setViewport(saved, { duration: 0 });
      } else {
        fitView({ padding: 0.15, duration: 0 });
      }
    });
  }, [currentId, nodes.length, setViewport, fitView, viewportShowsGraph]);

  // After an AI generation/refinement lands, center the new graph — it lays out from the origin, so
  // without this it reads as pinned to the top-left corner. Runs once per generated definition.
  const fittedPreviewRef = useRef<unknown>(null);
  useEffect(() => {
    if (!previewDefinition || nodes.length === 0 || fittedPreviewRef.current === previewDefinition) return;
    fittedPreviewRef.current = previewDefinition;
    requestAnimationFrame(() => fitView({ padding: 0.2, duration: 300 }));
  }, [previewDefinition, nodes.length, fitView]);

  // Frame the whole graph when the workflow is armed. Arming is a deliberate "go live" action, so a
  // re-render that leaves the graph pinned to a corner reads as broken — centering makes the now-live
  // surface obvious. Only on the false/null → true transition (not on disarm, and not every render).
  const prevArmedRef = useRef(armed);
  useEffect(() => {
    const wasArmed = prevArmedRef.current;
    prevArmedRef.current = armed;
    if (armed === true && wasArmed !== true && nodes.length > 0) {
      requestAnimationFrame(() => fitView({ padding: 0.15, duration: 400 }));
    }
  }, [armed, nodes.length, fitView]);

  // Handle selected nodes and edges. (The PropertiesPanel/VariablesPanel are memoized and don't re-render
  // during a drag, so selection no longer needs to be a transition — keeping it synchronous avoids the
  // concurrent-render interleaving that could show an intermediate frame and read as a jump.)
  const onSelectionChange = useCallback((params: { nodes: RFNode[]; edges: Edge[] }) => {
    setSelectedNode(params.nodes[0] || null);
    setSelectedEdge(params.edges[0] || null);
    setSelectedNodeCount(params.nodes.length);
  }, []);

  // Update properties from PropertiesPanel
  const onUpdateNodeProperties = useCallback((nodeId: string, newProperties: Record<string, unknown>) => {
    setNodes((nds) =>
      nds.map((node) => {
        if (node.id === nodeId) {
          return {
            ...node,
            data: {
              ...node.data,
              properties: newProperties,
            },
          };
        }
        return node;
      })
    );
    // Sync current selected node properties to keep panel in sync
    setSelectedNode((prev) => {
      if (prev && prev.id === nodeId) {
        return {
          ...prev,
          data: {
            ...prev.data,
            properties: newProperties,
          },
        };
      }
      return prev;
    });
  }, [setNodes]);

  // Capture the pre-move snapshot so a drag becomes one undo step (committed on stop). The proximity-snap
  // free-port cache is (re)built lazily on the first drag move (handleNodeDrag), where isFanInTarget is
  // already initialized.
  const onNodeDragStart = useCallback(() => {
    dragStartSnapshotRef.current = {
      nodes: structuredClone(nodesRef.current),
      edges: structuredClone(edgesRef.current),
    };
    dragProximityRef.current = null;
  }, []);

  const handleNodeDragStop = useCallback((_event: React.MouseEvent, node: RFNode) => {
    // Clear the drag-time snap highlight + proximity cache regardless of node type.
    useVariableStore.getState().setSnapCandidateKeys([]);
    dragProximityRef.current = null;

    // Commit the pre-move snapshot to history if the drag actually moved the node.
    // Compare the dragged node's final position to the pre-move snapshot (RF passes the
    // settled position here; reparenting always shifts position too, so it's covered).
    const startSnap = dragStartSnapshotRef.current;
    dragStartSnapshotRef.current = null;
    if (startSnap) {
      const before = startSnap.nodes.find((n) => n.id === node.id);
      const moved =
        !before ||
        before.position?.x !== node.position?.x ||
        before.position?.y !== node.position?.y ||
        (before.parentId ?? null) !== (node.parentId ?? null);
      if (moved) {
        historyRef.current = recordHistory(historyRef.current, startSnap);
      }
    }

    // Containers (loop/parallel) and group boxes manage their own children and are
    // never themselves reparented; sticky notes are inert annotations left where dropped.
    if (isContainerNodeType(node.type) || isGroupNodeType(node.type) || node.type === STICKY_NOTE_TYPE
        || node.type === 'start' || node.type === 'scheduler') {
      return;
    }

    // Calculate node's absolute position on canvas
    let absX = node.position.x;
    let absY = node.position.y;
    
    // If the node already had a parent, position is relative, so we must calculate absolute
    if (node.parentId) {
      const parentNode = nodes.find(n => n.id === node.parentId);
      if (parentNode) {
        absX += parentNode.position.x;
        absY += parentNode.position.y;
      }
    }

    // Find any intersecting container: a loop/parallel box wires its body, a group
    // box is purely visual — but both own membership via parentId, so a node dropped
    // into either is reparented, and a node dragged out of either is detached.
    const loopNode = findContainingLoopNode(nodes, { x: absX, y: absY }, node.id)
      ?? findContainingGroupNode(nodes, { x: absX, y: absY }, node.id);

    if (loopNode) {
      // Reparent node inside the container
      setNodes(nds => nds.map(n => {
        if (n.id === node.id) {
          return {
            ...n,
            parentId: loopNode.id,
            extent: 'parent',
            position: {
              x: absX - loopNode.position.x,
              y: absY - loopNode.position.y
            }
          };
        }
        return n;
      }));
    } else if (node.parentId) {
      // Dragged out of any loop container - clear parenting
      setNodes(nds => nds.map(n => {
        if (n.id === node.id) {
          return {
            ...n,
            parentId: undefined,
            extent: undefined,
            position: {
              x: absX,
              y: absY
            }
          };
        }
        return n;
      }));
    }

    // Proximity snap (Feature A) for a node moved in the open canvas. Skipped when the
    // drop reparents into a container — the container owns its body wiring there.
    if (!loopNode) {
      scheduleProximityRef.current(node.id, Boolean(node.data?.triggerOnly));
    }
  }, [nodes, setNodes]);

  // Brief "Connected ✓" confirmation pulse after a successful wire-up
  const triggerConnectToast = useCallback(() => {
    setConnectToast({ kind: 'success', message: 'Connected ✓' });
    if (connectToastTimer.current) {
      clearTimeout(connectToastTimer.current);
    }
    connectToastTimer.current = setTimeout(() => setConnectToast(null), 1100);
  }, []);

  // Explain why a connection drop didn't take. Lingers longer than the success
  // pulse so the reason is readable.
  const triggerConnectError = useCallback((message: string) => {
    setConnectToast({ kind: 'error', message });
    if (connectToastTimer.current) {
      clearTimeout(connectToastTimer.current);
    }
    connectToastTimer.current = setTimeout(() => setConnectToast(null), 2600);
  }, []);

  // Single place that adds a wire. Enforces the invariant that an input accepts
  // at most one incoming connection — a new wire replaces any existing one on
  // the same target handle. Outputs are unrestricted (fan-out: one output may
  // drive several inputs).
  const addConnection = useCallback((conn: Connection) => {
    const allowMulti = acceptsMultipleIncoming(conn.target, conn.targetHandle, getNodes());
    setEdges((eds) => {
      // Single-input handles replace their existing wire; fan-in handles (end / join) keep them all.
      const deduped = allowMulti
        ? eds
        : eds.filter(
            (e) => !(e.target === conn.target && (e.targetHandle ?? null) === (conn.targetHandle ?? null)),
          );
      const edgeId = `e-${conn.source}-${conn.sourceHandle}-${conn.target}-${conn.targetHandle}`;
      const newEdge: Edge = { ...conn, id: edgeId, animated: false };
      return addEdge(newEdge, deduped);
    });
    lastConnectAtRef.current = Date.now();
    triggerConnectToast();
  }, [setEdges, triggerConnectToast, getNodes]);

  // ── Undo/Redo helpers ──
  // A snapshot clones the current graph so later edits can't mutate stored history.
  const snapshotNow = useCallback((): CanvasSnapshot => ({
    nodes: structuredClone(nodesRef.current),
    edges: structuredClone(edgesRef.current),
  }), []);
  // Record a pre-change snapshot before a structural edit (call once per user gesture,
  // never inside addConnection — compound ops like splice must stay a single undo step).
  const recordUndo = useCallback(() => {
    historyRef.current = recordHistory(historyRef.current, snapshotNow());
  }, [snapshotNow]);
  const applySnapshot = useCallback((s: CanvasSnapshot) => {
    setNodes(structuredClone(s.nodes));
    setEdges(structuredClone(s.edges));
    setSelectedNode(null);
    setSelectedEdge(null);
  }, [setNodes, setEdges]);
  const doUndo = useCallback(() => {
    const r = applyUndo(historyRef.current, snapshotNow());
    if (!r) return;
    historyRef.current = r.history;
    applySnapshot(r.restored);
  }, [snapshotNow, applySnapshot]);
  const doRedo = useCallback(() => {
    const r = applyRedo(historyRef.current, snapshotNow());
    if (!r) return;
    historyRef.current = r.history;
    applySnapshot(r.restored);
  }, [snapshotNow, applySnapshot]);

  // ── Snap-to-grid (grid matches the 24px Background dot gap) ──
  const [snapEnabled, setSnapEnabled] = useState(false);
  // Ref mirror so placement callbacks (drop/paste/tidy) can snap without being
  // re-created on every toggle. React Flow already snaps manual drags itself.
  const snapEnabledRef = useRef(snapEnabled);
  useEffect(() => { snapEnabledRef.current = snapEnabled; }, [snapEnabled]);
  const snapIfEnabled = useCallback(
    (p: { x: number; y: number }) => (snapEnabledRef.current ? snapPointToGrid(p, SNAP_GRID_SIZE) : p),
    [],
  );

  // ── Copy / Paste / Duplicate ──
  // Append a freshly-cloned subgraph (new ids, offset) and leave it selected. Shared by
  // paste (from clipboard) and duplicate (direct from the current selection).
  const appendClones = useCallback(
    (sourceNodes: RFNode[], sourceEdges: Edge[], offset: number) => {
      if (sourceNodes.length === 0) return;
      const { nodes: cloned, edges: cloneEdges } = cloneSubgraph(sourceNodes, sourceEdges, {
        newId: (type) => createNodeId(type ?? 'node'),
        offset: { x: offset, y: offset },
      });
      const clones = cloned.map((n) => ({ ...n, position: snapIfEnabled(n.position) }));
      recordUndo();
      setNodes((nds) => [...nds.map((n) => ({ ...n, selected: false })), ...clones]);
      setEdges((eds) => [...eds, ...cloneEdges]);
      setSelectedNode(clones[0] ?? null);
      setSelectedEdge(null);
    },
    [recordUndo, setNodes, setEdges, snapIfEnabled],
  );

  // Insert a template's graph into the OPEN workflow (vs. creating a new workflow). Reuses the paste
  // path: fresh node ids, internal edges rewired, single undo step. Credential refs stay as `slot:`
  // placeholders — the publish gate blocks running until they're bound on the nodes.
  const insertTemplatePayload = useCallback(
    (payload: TemplatePayloadResponse, dropPosition?: { x: number; y: number }) => {
      if (!payload.compatibility.supported) {
        setWorkflowStatusMessage(
          `“${payload.manifest.name}” can't run on this engine and was not inserted. ${payload.compatibility.warnings.join(' ')}`.trim(),
        );
        return;
      }

      const definition = { id: { value: currentId || 'template' }, name: '', nodes: payload.nodes, edges: payload.edges };
      const { nodes: rfNodes, edges: rfEdges } = schemaMapper.toReactFlow(definition);
      if (rfNodes.length === 0) {
        setWorkflowStatusMessage('That template has no nodes to insert.');
        return;
      }

      // Templates that carry no saved positions (e.g. hand-authored gallery sources) would otherwise fall
      // back to a by-index layout over the canonical, id-sorted node order — which mirrors Start/End. When no
      // node has a real position, lay the graph out left-to-right with dagre instead.
      const hasPositions = payload.nodes.some((n) => {
        const meta = (n.properties?._metadata ?? {}) as { x?: number; y?: number };
        return typeof meta.x === 'number' && typeof meta.y === 'number';
      });
      const positionedNodes = hasPositions
        ? rfNodes
        : (() => {
            const layout = computeAutoLayout(
              rfNodes.map((n) => ({ id: n.id })),
              rfEdges.map((e) => ({ source: e.source, target: e.target })),
              { direction: 'LR' },
            );
            const byId = new Map(layout.map((p) => [p.id, p]));
            return rfNodes.map((n) => {
              const p = byId.get(n.id);
              return p ? { ...n, position: { x: p.x, y: p.y } } : n;
            });
          })();

      // Slot-namespace safety: rename the template's slot keys that collide with the open workflow's
      // existing `slot:` placeholders, so a later credential binding can't conflate two distinct slots.
      const existingSlots = collectSlotNames(nodesRef.current);
      const { nodes: deconflicted, renamed } = rewriteSlotsForInsert(positionedNodes, existingSlots);

      // When dropped from the palette, anchor the graph's top-left at the cursor; otherwise keep the
      // template's own coordinates (offset 0) and recenter the viewport onto it afterwards.
      const minX = Math.min(...positionedNodes.map((n) => n.position.x));
      const minY = Math.min(...positionedNodes.map((n) => n.position.y));
      const offset = dropPosition ? { x: dropPosition.x - minX, y: dropPosition.y - minY } : { x: 0, y: 0 };
      const { nodes: cloned, edges: clonedEdges } = cloneSubgraph(deconflicted, rfEdges, {
        newId: (type) => createNodeId(type ?? 'node'),
        offset,
      });
      const placed = cloned.map((n) => ({ ...n, position: snapIfEnabled(n.position) }));

      recordUndo();
      setNodes((nds) => [...nds.map((n) => ({ ...n, selected: false })), ...placed]);
      setEdges((eds) => [...eds, ...clonedEdges]);
      setSelectedNode(placed[0] ?? null);
      setSelectedEdge(null);

      // A drop lands where the user is already looking — don't yank the viewport. Recenter only for the
      // modal-picker insert, which has no spatial context.
      const top = placed[0];
      if (top && !dropPosition) {
        setCenter(top.position.x + 120, top.position.y + 60, { zoom: 1, duration: 400 });
      }

      const slotCount = payload.credentialSlots.length;
      const renameNote = renamed.length > 0
        ? ` ${renamed.length} slot(s) renamed to avoid clashing with this workflow (${renamed.map((r) => `${r.from}→${r.to}`).join(', ')}).`
        : '';
      setWorkflowStatusMessage(
        `Inserted ${placed.length} node(s) from “${payload.manifest.name}”.` +
          (slotCount > 0
            ? ` ${slotCount} credential slot(s) left as placeholders — set them on the nodes before running.`
            : '') +
          renameNote,
      );
    },
    [currentId, recordUndo, setNodes, setEdges, snapIfEnabled, setCenter],
  );

  // Drop a template from the palette onto the canvas: fetch its graph and stamp it at the cursor. Templates
  // that need parameter values up front (a required parameter with no default) can't be satisfied mid-drop,
  // so they route to the Insert panel's configure flow instead.
  const insertTemplateByDrop = useCallback(
    async (source: 'gallery' | 'library', templateId: string, needsConfig: boolean, dropPosition: { x: number; y: number }) => {
      if (needsConfig) {
        setTemplatePickerOpen(true);
        setWorkflowStatusMessage('This template needs parameter values — configure it in the Insert panel.');
        return;
      }
      try {
        const payload = source === 'library'
          ? await api.getLibraryTemplatePayload(templateId)
          : await api.getGalleryTemplatePayload(templateId);
        insertTemplatePayload(payload, dropPosition);
      } catch (err) {
        setWorkflowStatusMessage(getErrorMessage(err, 'Could not insert that template.'));
      }
    },
    [insertTemplatePayload],
  );

  const copySelection = useCallback(() => {
    const selected = nodesRef.current.filter((n) => n.selected);
    if (selected.length === 0) return false;
    const ids = new Set(selected.map((n) => n.id));
    const internalEdges = edgesRef.current.filter((e) => ids.has(e.source) && ids.has(e.target));
    clipboardRef.current = { nodes: structuredClone(selected), edges: structuredClone(internalEdges) };
    pasteCountRef.current = 0;
    return true;
  }, []);

  const pasteClipboard = useCallback(() => {
    const clip = clipboardRef.current;
    if (!clip || clip.nodes.length === 0) return false;
    pasteCountRef.current += 1;
    appendClones(clip.nodes, clip.edges, 40 * pasteCountRef.current);
    return true;
  }, [appendClones]);

  const duplicateSelection = useCallback(() => {
    const selected = nodesRef.current.filter((n) => n.selected);
    if (selected.length === 0) return false;
    const ids = new Set(selected.map((n) => n.id));
    const internalEdges = edgesRef.current.filter((e) => ids.has(e.source) && ids.has(e.target));
    appendClones(selected, internalEdges, 40);
    return true;
  }, [appendClones]);

  // ── Search / jump palette (Ctrl+F / Cmd+K) ──
  const [searchOpen, setSearchOpen] = useState(false);
  const [templatePickerOpen, setTemplatePickerOpen] = useState(false);
  // ── Keyboard-shortcut help overlay ("?") ──
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  // ── Version history drawer (Ctrl/⌘ + Shift + H) ──
  const [historyOpen, setHistoryOpen] = useState(false);
  const historyOpenRef = useRef(historyOpen);
  historyOpenRef.current = historyOpen;
  // Fetch version metadata only while the drawer is open. The drawer shares no
  // state with the toolbar's `workflowVersions` (which drives the activate flow).
  const {
    versions: historyVersions,
    loading: historyLoading,
    error: historyError,
    refresh: refreshHistory,
  } = useWorkflowVersions(currentId || undefined, historyOpen);

  // ── Editor-mode state machine (plan §7.3) ──
  // Draft = live editable graph; PublishedPreview / Diff = read-only snapshots.
  // While in a read-only mode we render `previewNodes/previewEdges` instead of the
  // live `nodes/edges`, hold the working draft aside (it stays in the live state and
  // is simply not rendered), and disable editing/autosave/publish/run.
  const [editorMode, setEditorMode] = useState<EditorMode>(DRAFT_MODE);
  const dispatchMode = useCallback((action: Parameters<typeof editorModeReducer>[1]) => {
    setEditorMode((prev) => editorModeReducer(prev, action));
  }, []);
  const readOnly = isEditingDisabled(editorMode);
  // Read-only canvas state shown during preview (separate from the live draft).
  const [previewNodes, setPreviewNodes] = useState<RFNode[]>([]);
  const [previewEdges, setPreviewEdges] = useState<Edge[]>([]);
  const [previewVersionNumber, setPreviewVersionNumber] = useState<number | null>(null);
  // Restore dialog state.
  const [restoreTarget, setRestoreTarget] = useState<WorkflowVersionSummary | null>(null);
  const [restoreBusy, setRestoreBusy] = useState(false);
  const [restoreError, setRestoreError] = useState<string | null>(null);
  const [restoreResult, setRestoreResult] = useState<RestoreVersionResult | null>(null);
  // Diff view state (left → right). `diff` is computed by the pure versionDiff module.
  const [diffState, setDiffState] = useState<{ leftLabel: string; rightLabel: string; diff: VersionDiff } | null>(null);

  // A workflow version's payload as a DiffablePayload (already in NodeDefinition/EdgeDefinition shape).
  const versionToPayload = useCallback((version: WorkflowVersion): DiffablePayload => {
    return { nodes: version.nodes, edges: version.edges };
  }, []);

  // Switching workflows must drop any in-flight preview/diff so a stale read-only
  // snapshot can't leak across workflows.
  useEffect(() => {
    setEditorMode(DRAFT_MODE);
    setPreviewNodes([]);
    setPreviewEdges([]);
    setPreviewVersionNumber(null);
    setDiffState(null);
    setRestoreTarget(null);
  }, [currentId]);

  // The live working draft as a DiffablePayload, derived through the same backend
  // mapper used for save/publish so the comparison is apples-to-apples.
  const draftPayload = useCallback((): DiffablePayload => {
    const def = schemaMapper.toBackend(currentId, workflowName, nodesRef.current, edgesRef.current);
    return { nodes: def.nodes, edges: def.edges };
  }, [currentId, workflowName]);

  // Enter read-only preview of a committed version (snapshots stay in the live state,
  // which is simply not rendered while read-only — exiting restores them verbatim).
  const handlePreviewVersion = useCallback(async (versionId: string) => {
    if (!currentId) return;
    try {
      const version = await api.getWorkflowVersionDetail(currentId, versionId);
      const def = { id: { value: currentId }, name: workflowName, nodes: version.nodes, edges: version.edges };
      const { nodes: rfNodes, edges: rfEdges } = schemaMapper.toReactFlow(def);
      setPreviewNodes(applyGroupCollapseOnLoad(enrichNodesWithPackageMetadata(rfNodes, availableNodeMetadataRef.current)));
      setPreviewEdges(rfEdges);
      setPreviewVersionNumber(version.versionNumber);
      dispatchMode({ type: 'openPreview', versionId });
    } catch (err) {
      setWorkflowStatusMessage(`Could not load version for preview: ${getErrorMessage(err, 'Unknown error')}`);
    }
  }, [currentId, workflowName, dispatchMode]);

  // Exit any read-only mode → live draft is rendered again (never mutated).
  const exitReadOnly = useCallback(() => {
    dispatchMode({ type: 'exit' });
    setPreviewNodes([]);
    setPreviewEdges([]);
    setPreviewVersionNumber(null);
    setDiffState(null);
  }, [dispatchMode]);

  // The history drawer and a read-only preview are one "version overview" — leaving either should
  // return you to the draft in a single action, so closing the drawer also exits the preview and
  // exiting the preview also closes the drawer.
  const closeVersionOverview = useCallback(() => {
    setHistoryOpen(false);
    exitReadOnly();
  }, [exitReadOnly]);

  // Runtime dropdown selection → preview the chosen version read-only (semi-transparent). Selecting
  // the active version returns to the live editable draft. Activation never happens on select — it
  // has live trigger side effects and stays explicit (Run, or restore from the preview banner).
  const handleSelectVersion = useCallback((versionId: string) => {
    setSelectedActivationVersionId(versionId);
    if (!versionId || versionId === activeWorkflowVersion?.workflowVersionId) {
      exitReadOnly();
      return;
    }
    void handlePreviewVersion(versionId);
  }, [activeWorkflowVersion, exitReadOnly, handlePreviewVersion]);

  // Transient preview while the user lingers on a version in the runtime dropdown. `null` reverts to
  // the committed selection's view. Never commits the selection and never activates.
  const handleHoverPreviewVersion = useCallback((versionId: string | null) => {
    if (!versionId) {
      handleSelectVersion(selectedActivationVersionId);
      return;
    }
    if (versionId === activeWorkflowVersion?.workflowVersionId) {
      exitReadOnly();
      return;
    }
    void handlePreviewVersion(versionId);
  }, [selectedActivationVersionId, activeWorkflowVersion, handleSelectVersion, exitReadOnly, handlePreviewVersion]);

  // Diff a committed version against the working draft (committed = left, draft = right).
  const handleDiffAgainstDraft = useCallback(async (versionId: string) => {
    if (!currentId) return;
    try {
      const version = await api.getWorkflowVersionDetail(currentId, versionId);
      const diff = diffVersions(versionToPayload(version), draftPayload());
      setDiffState({ leftLabel: `v${version.versionNumber}`, rightLabel: 'working draft', diff });
      dispatchMode({ type: 'openDiff', leftVersionId: versionId, rightVersionId: 'draft' });
    } catch (err) {
      setWorkflowStatusMessage(`Could not load version for diff: ${getErrorMessage(err, 'Unknown error')}`);
    }
  }, [currentId, versionToPayload, draftPayload, dispatchMode]);

  // The most-wanted diff (plan §7.4): working draft vs the active version.
  const handleDiffDraftVsActive = useCallback(async () => {
    if (!currentId) return;
    const activeId = activeWorkflowVersionRef.current?.workflowVersionId;
    if (!activeId) {
      setWorkflowStatusMessage('No active version to diff against — publish one first.');
      return;
    }
    try {
      const version = await api.getWorkflowVersionDetail(currentId, activeId);
      const diff = diffVersions(versionToPayload(version), draftPayload());
      setDiffState({ leftLabel: `active v${version.versionNumber}`, rightLabel: 'working draft', diff });
      dispatchMode({ type: 'openDiff', leftVersionId: activeId, rightVersionId: 'draft' });
    } catch (err) {
      setWorkflowStatusMessage(`Could not load active version for diff: ${getErrorMessage(err, 'Unknown error')}`);
    }
  }, [currentId, versionToPayload, draftPayload, dispatchMode]);

  // Open the restore confirmation for a version id (resolves its summary for the dialog).
  const openRestoreDialog = useCallback((versionId: string) => {
    const summary =
      historyVersions.find((v) => v.id === versionId) ||
      workflowVersions.find((v) => v.id === versionId) ||
      null;
    setRestoreResult(null);
    setRestoreError(null);
    setRestoreTarget(summary ?? { id: versionId, versionNumber: 0, createdAt: '', createdBy: null, label: null, origin: 'Published', isActive: false, restoredFromVersionId: null, nodeCount: 0, executionCount: 0 });
  }, [historyVersions, workflowVersions]);

  const confirmRestore = useCallback(async ({ activate }: { activate: boolean }) => {
    if (!currentId || !restoreTarget) return;
    setRestoreBusy(true);
    setRestoreError(null);
    try {
      const result = await api.restoreVersion(currentId, restoreTarget.id, activate);
      setRestoreResult(result);
      // Refresh the panel list + active badge so the new forward copy shows up.
      void refreshHistory();
      const [versions, activeVersion] = await Promise.all([
        api.getWorkflowVersions(currentId),
        api.getActiveWorkflowVersion(currentId),
      ]);
      setWorkflowVersions(versions);
      setSelectedActivationVersionId(activeVersion?.workflowVersionId ?? versions[0]?.id ?? '');
      setActiveWorkflowVersion(activeVersion);
    } catch (err) {
      const errorDiagnostics = getErrorDiagnostics(err);
      if (isApiError(err) && err.status === 400 && errorDiagnostics && errorDiagnostics.length > 0) {
        setRestoreError(`Activation failed — fix these first: ${errorDiagnostics.map((d) => `[${d.code}] ${d.message}`).join('; ')}`);
      } else if (isApiError(err) && err.status === 409) {
        setRestoreError('Another activation happened concurrently. Reopen and try again.');
      } else {
        setRestoreError(getErrorMessage(err, 'Restore failed.'));
      }
    } finally {
      setRestoreBusy(false);
    }
  }, [currentId, restoreTarget, refreshHistory]);
  // Centre the canvas on a node and select it (clearing any other selection).
  const jumpToNode = useCallback(
    (node: RFNode) => {
      const internal = getInternalNode(node.id);
      const width = internal?.measured?.width ?? 220;
      const height = internal?.measured?.height ?? 80;
      const x = node.position.x + width / 2;
      const y = node.position.y + height / 2;
      setCenter(x, y, { zoom: 1.2, duration: 400 });
      setNodes((nds) => nds.map((n) => ({ ...n, selected: n.id === node.id })));
      setSelectedNode(node);
      setSelectedEdge(null);
    },
    [getInternalNode, setCenter, setNodes],
  );

  // Diagnostics panel (#9): blocking publish/run failures merged with the live
  // edge-validation warnings into one ordered, de-duplicated list.
  const panelDiagnostics = useMemo(
    () => mergeDiagnostics(diagnostics, edgeDiagnostics),
    [diagnostics, edgeDiagnostics],
  );
  // Blocking (Error-severity) count — surfaced on the Save & Publish button so the corner panel isn't
  // the only signal that publishing will fail.
  const blockingErrorCount = useMemo(() => countBySeverity(panelDiagnostics).Error, [panelDiagnostics]);

  // Centre the canvas on the node / edge a diagnostic points at, and select it.
  const focusDiagnostic = useCallback(
    (diagnostic: CompilationDiagnostic) => {
      const focus = resolveDiagnosticFocus(diagnostic, edgesRef.current);
      if (!focus) return;
      const centerOf = (nodeId: string): { x: number; y: number } | null => {
        const internal = getInternalNode(nodeId);
        if (!internal) return null;
        const w = internal.measured?.width ?? DEFAULT_NODE_WIDTH;
        const h = internal.measured?.height ?? 80;
        const pos = internal.internals?.positionAbsolute ?? internal.position;
        return { x: pos.x + w / 2, y: pos.y + h / 2 };
      };
      if (focus.kind === 'node') {
        const c = centerOf(focus.nodeId);
        if (!c) return;
        setCenter(c.x, c.y, { zoom: 1.2, duration: 400 });
        setNodes((nds) => nds.map((n) => ({ ...n, selected: n.id === focus.nodeId })));
        setSelectedNode(nodesRef.current.find((n) => n.id === focus.nodeId) ?? null);
        setSelectedEdge(null);
      } else {
        const pts = [centerOf(focus.source), centerOf(focus.target)].filter(
          (p): p is { x: number; y: number } => p !== null,
        );
        if (pts.length === 0) return;
        const x = pts.reduce((s, p) => s + p.x, 0) / pts.length;
        const y = pts.reduce((s, p) => s + p.y, 0) / pts.length;
        setCenter(x, y, { zoom: 1.2, duration: 400 });
        setEdges((eds) =>
          eds.map((e) => ({ ...e, selected: e.source === focus.source && e.target === focus.target })),
        );
        setSelectedNode(null);
      }
    },
    [getInternalNode, setCenter, setNodes, setEdges],
  );

  // ── Auto-layout (dagre) + Align / Distribute (multi-select) ──
  // Measured size of a node (container nodes carry size in style; fall back later).
  const nodeSize = useCallback(
    (n: RFNode): { width?: number; height?: number } => {
      const internal = getInternalNode(n.id);
      const mw = internal?.measured?.width;
      const mh = internal?.measured?.height;
      const sw = n.style?.width != null ? Number(n.style.width) : undefined;
      const sh = n.style?.height != null ? Number(n.style.height) : undefined;
      return { width: mw ?? sw, height: mh ?? sh };
    },
    [getInternalNode],
  );

  // One-click "tidy": dagre-layout the top-level graph left-to-right and write
  // the new positions back (one undo step). Children inside containers are left
  // in place by computeAutoLayout.
  const runAutoLayout = useCallback(() => {
    const current = nodesRef.current;
    if (current.length === 0) return;
    // Container-aware: lays out each loop/subflow body inside its container too (never shrinking a
    // container below its current size), so Tidy arranges the body — not just the top-level graph.
    const positions = computeNestedAutoLayout(
      current.map((n) => ({ id: n.id, type: n.type, parentId: n.parentId, ...nodeSize(n) })),
      edgesRef.current.map((e) => ({ source: e.source, target: e.target })),
      { direction: 'LR' },
    );
    if (positions.length === 0) return;
    const posById = new Map(positions.map((p) => [p.id, { ...p, ...snapIfEnabled(p) }]));
    recordUndo();
    setNodes((nds) => nds.map((n) => {
      const p = posById.get(n.id);
      if (!p) return n;
      const next = { ...n, position: { x: p.x, y: p.y } };
      if (p.width && p.height) next.style = { ...n.style, width: p.width, height: p.height };
      return next;
    }));
    // Tidy can move every node to a new region; without re-fitting, the viewport stays put and the
    // canvas looks empty (especially for large/disconnected graphs). Fit once the new positions render.
    requestAnimationFrame(() => fitView({ padding: 0.15, duration: 400 }));
  }, [nodeSize, setNodes, recordUndo, snapIfEnabled, fitView]);

  // Same dagre layout as the Tidy button, but run automatically once a freshly loaded position-less graph
  // has been MEASURED — so wide nodes (e.g. a Log with a long message) get real spacing instead of the
  // mapper's default-width guess, which overlaps them. Unlike the button this records no undo step and
  // rebaselines the saved signature, since an auto-arrange on open isn't a user edit.
  const autoTidyOnLoad = useCallback(() => {
    const current = nodesRef.current;
    if (current.length === 0) return;
    const positions = computeNestedAutoLayout(
      current.map((n) => ({ id: n.id, type: n.type, parentId: n.parentId, ...nodeSize(n) })),
      edgesRef.current.map((e) => ({ source: e.source, target: e.target })),
      { direction: 'LR' },
    );
    if (positions.length === 0) return;
    const posById = new Map(positions.map((p) => [p.id, { ...p, ...snapIfEnabled(p) }]));
    const next = current.map((n) => {
      const p = posById.get(n.id);
      if (!p) return n;
      const moved: RFNode = { ...n, position: { x: p.x, y: p.y } };
      if (p.width && p.height) moved.style = { ...n.style, width: p.width, height: p.height };
      return moved;
    });
    setNodes(next);
    setSavedSignature(workflowSignature(workflowNameRef.current, next, edgesRef.current));
    requestAnimationFrame(() => fitView({ padding: 0.15, duration: 400 }));
  }, [nodeSize, setNodes, snapIfEnabled, fitView]);

  const nodesInitialized = useNodesInitialized();
  useEffect(() => {
    if (nodesInitialized && autoTidyPending) {
      setAutoTidyPending(false);
      autoTidyOnLoad();
    }
  }, [nodesInitialized, autoTidyPending, autoTidyOnLoad]);

  // Apply new positions for a set of node ids in one undo step.
  const applyPositions = useCallback(
    (positions: { id: string; x: number; y: number }[]) => {
      const posById = new Map(positions.map((p) => [p.id, p]));
      recordUndo();
      setNodes((nds) => nds.map((n) => {
        const p = posById.get(n.id);
        return p ? { ...n, position: { x: p.x, y: p.y } } : n;
      }));
    },
    [setNodes, recordUndo],
  );

  const alignSelection = useCallback(
    (edge: AlignEdge) => {
      const selected = nodesRef.current.filter((n) => n.selected);
      if (selected.length < 2) return;
      applyPositions(
        alignNodes(
          selected.map((n) => ({ id: n.id, x: n.position.x, y: n.position.y, ...nodeSize(n) })),
          edge,
        ),
      );
    },
    [applyPositions, nodeSize],
  );

  const distributeSelection = useCallback(
    (axis: DistributeAxis) => {
      const selected = nodesRef.current.filter((n) => n.selected);
      if (selected.length < 3) return;
      applyPositions(
        distributeNodes(
          selected.map((n) => ({ id: n.id, x: n.position.x, y: n.position.y, ...nodeSize(n) })),
          axis,
        ),
      );
    },
    [applyPositions, nodeSize],
  );

  // Absolute-flow-space positions of every measured handle on the canvas. Brand-new
  // nodes (not yet measured by React Flow) contribute nothing until their bounds exist.
  const collectMeasuredPorts = useCallback((): PortPosition[] => {
    const ports: PortPosition[] = [];
    for (const n of getNodes()) {
      const internal = getInternalNode(n.id);
      if (internal) ports.push(...getPortPositions(internal as unknown as InternalNodeLike));
    }
    return ports;
  }, [getNodes, getInternalNode]);

  // Fan-in predicate shared by the proximity helpers (mirrors acceptsMultipleIncoming):
  // a loop 'end' loopback or a join node's input accepts many wires and is never "free".
  const isFanInTarget = useCallback(
    (nodeId: string, handleId: string) => acceptsMultipleIncoming(nodeId, handleId, getNodes()),
    [getNodes],
  );

  // Connect two nodes (fired when a drag ends on a real handle, incl. magnetic snap).
  // During an input-pickup the move is resolved in onConnectEnd (using the drop node),
  // so ignore the snap-driven connection here to avoid creating a reversed edge.
  const onConnect = useCallback((params: Connection) => {
    if (inputPickupRef.current) return;
    recordUndo();
    addConnection(params);
  }, [addConnection, recordUndo]);

  // Drag start: if it begins on an already-wired input handle, lift that wire off
  // (pickup) so it can be re-dropped onto another input. Output drags are unaffected.
  const onConnectStart = useCallback((_event: MouseEvent | TouchEvent, params: OnConnectStartParams) => {
    inputPickupRef.current = null;
    if (params.handleType !== 'target' || !params.nodeId) {
      return;
    }
    const handleId = params.handleId ?? null;
    // Fan-in handles hold several wires; dragging from one shouldn't lift an arbitrary branch.
    if (acceptsMultipleIncoming(params.nodeId, handleId, getNodes())) {
      return;
    }
    // Capture the pre-lift state so a re-home becomes a single undo step (committed in onConnectEnd).
    pickupSnapshotRef.current = snapshotNow();
    setEdges((eds) => {
      const existing = eds.find(
        (e) => e.target === params.nodeId && (e.targetHandle ?? null) === handleId,
      );
      if (!existing) {
        return eds;
      }
      inputPickupRef.current = {
        source: existing.source,
        sourceHandle: existing.sourceHandle ?? null,
        target: existing.target,
        targetHandle: existing.targetHandle ?? null,
      };
      return eds.filter((e) => e.id !== existing.id);
    });
  }, [setEdges, getNodes, snapshotNow]);

  // ── Edge reconnection ── Grab the endpoint of an existing wire to re-route it onto
  // another handle, or drop it in empty space to delete it. Keeps the same invariant
  // as addConnection: an input accepts at most one incoming connection.
  const onReconnectStart = useCallback(() => {
    edgeReconnectSuccessful.current = false;
  }, []);

  const onReconnect = useCallback((oldEdge: Edge, newConnection: Connection) => {
    edgeReconnectSuccessful.current = true;
    recordUndo();
    const allowMulti = acceptsMultipleIncoming(newConnection.target, newConnection.targetHandle, getNodes());
    setEdges((eds) => {
      // Drop any *other* wire already on the new target handle (single incoming per input),
      // unless it is a fan-in handle (end / join) that keeps all converging branches.
      const cleaned = allowMulti
        ? eds
        : eds.filter(
            (e) =>
              e.id === oldEdge.id ||
              !(e.target === newConnection.target && (e.targetHandle ?? null) === (newConnection.targetHandle ?? null)),
          );
      const newId = `e-${newConnection.source}-${newConnection.sourceHandle}-${newConnection.target}-${newConnection.targetHandle}`;
      return reconnectEdge(oldEdge, newConnection, cleaned, { shouldReplaceId: false }).map((e) =>
        e.id === oldEdge.id ? { ...e, id: newId } : e,
      );
    });
    lastConnectAtRef.current = Date.now();
    triggerConnectToast();
  }, [setEdges, triggerConnectToast, getNodes, recordUndo]);

  const onReconnectEnd = useCallback((_event: MouseEvent | TouchEvent, edge: Edge) => {
    if (!edgeReconnectSuccessful.current) {
      recordUndo();
      setEdges((eds) => eds.filter((e) => e.id !== edge.id));
    }
    edgeReconnectSuccessful.current = true;
  }, [setEdges, recordUndo]);

  // Reject self-connections; React Flow already prevents source→source / target→target.
  const isValidConnection = useCallback<IsValidConnection>(
    (conn) => conn.source !== conn.target,
    [],
  );

  // Technique 2 — "drop on node": releasing a drag anywhere over a target node
  // (not precisely on its input dot) still connects, to that node's first input.
  const onConnectEnd = useCallback((_event: MouseEvent | TouchEvent, connectionState: FinalConnectionState) => {
    // Input-pickup: the original wire was lifted off in onConnectStart. Re-home it.
    const pickup = inputPickupRef.current;
    if (pickup) {
      inputPickupRef.current = null;
      const pickupSnapshot = pickupSnapshotRef.current;
      pickupSnapshotRef.current = null;
      const dropNode = connectionState.toNode;
      if (dropNode && dropNode.id !== pickup.source && !isContainerNodeType(dropNode.type)) {
        const targetHandle = dropNode.internals.handleBounds?.target?.[0];
        if (targetHandle) {
          // Re-home is a real change → commit the pre-lift snapshot as one undo step.
          if (pickupSnapshot) historyRef.current = recordHistory(historyRef.current, pickupSnapshot);
          // Move the connection's target onto the drop node's input (source unchanged).
          addConnection({
            source: pickup.source,
            sourceHandle: pickup.sourceHandle,
            target: dropNode.id,
            targetHandle: targetHandle.id ?? null,
          });
          return;
        }
      }
      // Dropped on nothing valid → restore the original wire (move cancelled, no history entry).
      addConnection({
        source: pickup.source,
        sourceHandle: pickup.sourceHandle,
        target: pickup.target,
        targetHandle: pickup.targetHandle,
      });
      return;
    }

    // A precise handle / magnetic-snap drop already produced an onConnect — skip.
    if (connectionState.isValid) {
      return;
    }
    const { fromHandle, fromNode, toNode } = connectionState;
    const targetHandle = toNode?.internals.handleBounds?.target?.[0];
    // Explain a failed drop (over a node) instead of silently doing nothing.
    // A drop on empty pane returns null here and stays quiet.
    const reason = connectionFailureReason({
      fromHandleType: fromHandle?.type ?? null,
      fromNodeId: fromNode?.id ?? null,
      toNodeId: toNode?.id ?? null,
      toNodeIsContainer: isContainerNodeType(toNode?.type),
      toNodeHasInput: Boolean(targetHandle),
    });
    if (reason) {
      triggerConnectError(reason);
      return;
    }
    // No reason + incomplete drop context (e.g. released on empty pane) → cancel quietly.
    if (!fromHandle || !fromNode || !toNode || !targetHandle) {
      return;
    }
    // Valid output → node drop: wire to the node's first input.
    recordUndo();
    addConnection({
      source: fromNode.id,
      sourceHandle: fromHandle.id ?? null,
      target: toNode.id,
      targetHandle: targetHandle.id ?? null,
    });
  }, [addConnection, recordUndo, triggerConnectError]);

  // ── Technique: click-to-connect (touch / accessibility friendly) ──
  // Click an output → click an input handle OR anywhere on a target node → wire.
  // Click empty pane or press Esc → cancel.

  // Fully reset a click-connect, including React Flow's internal pending handle.
  const clearClickConnect = useCallback(() => {
    clickConnectRef.current = null;
    useVariableStore.getState().setClickConnectSource(null);
    reactFlowStore.setState({ connectionClickStartHandle: null });
  }, [reactFlowStore]);

  // React Flow fires this when an output handle is clicked (connectOnClick).
  const onClickConnectStart = useCallback((_event: MouseEvent | TouchEvent, params: OnConnectStartParams) => {
    if (params.handleType !== 'source' || !params.nodeId) {
      return;
    }
    clickConnectRef.current = { nodeId: params.nodeId, handleId: params.handleId ?? null };
    useVariableStore.getState().setClickConnectSource(params.nodeId);
  }, []);

  // Fires when a second handle click resolves the click-connection (RF handled the
  // wire via onConnect already); just drop our highlight state.
  const onClickConnectEnd = useCallback(() => {
    clickConnectRef.current = null;
    useVariableStore.getState().setClickConnectSource(null);
  }, []);

  // Node click: complete a pending click-connect onto this node's input, else
  // fall back to the existing pin-toggle behavior.
  const onNodeClick = useCallback((_event: React.MouseEvent, node: RFNode) => {
    const start = clickConnectRef.current;
    if (start && start.nodeId !== node.id && !isContainerNodeType(node.type)) {
      const targetHandle = getInternalNode(node.id)?.internals.handleBounds?.target?.[0];
      if (targetHandle) {
        recordUndo();
        addConnection({
          source: start.nodeId,
          sourceHandle: start.handleId,
          target: node.id,
          targetHandle: targetHandle.id ?? null,
        });
        clearClickConnect();
        return;
      }
    }
    // Swallow the click that bubbles up right after a handle-completed connection.
    if (Date.now() - lastConnectAtRef.current < 150) {
      return;
    }
    useVariableStore.getState().togglePinnedNodeId(node.id);
  }, [addConnection, clearClickConnect, getInternalNode, recordUndo]);

  // Pane click: clear pins and cancel any pending click-connect.
  const onPaneClick = useCallback(() => {
    useVariableStore.getState().clearPins();
    if (clickConnectRef.current) {
      clearClickConnect();
    }
  }, [clearClickConnect]);

  // Delete node callback
  const onDeleteNode = useCallback((nodeId: string) => {
    recordUndo();
    setNodes((nds) => nds.filter((n) => n.id !== nodeId));
    setEdges((eds) => eds.filter((e) => e.source !== nodeId && e.target !== nodeId));
    setSelectedNode(null);
    setSelectedEdge(null);
  }, [setNodes, setEdges, recordUndo]);

  // Delete edge callback
  const onDeleteEdge = useCallback((edgeId: string) => {
    recordUndo();
    setEdges((eds) => eds.filter((e) => e.id !== edgeId));
    setSelectedEdge(null);
  }, [setEdges, recordUndo]);

  // Global keydown handler to support Delete / Backspace key deletions and Escape clearing
  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        useVariableStore.getState().clearPins();
        clearClickConnect();
        return;
      }

      const activeEl = document.activeElement;
      const editingText = !!activeEl && (
        activeEl.tagName === 'INPUT' ||
        activeEl.tagName === 'TEXTAREA' ||
        activeEl.tagName === 'SELECT' ||
        activeEl.getAttribute('contenteditable') === 'true'
      );

      // Search / jump palette (Ctrl+F / Cmd+K). Allowed even from a field so the
      // palette can always be summoned; it owns its own input afterwards.
      if (
        (event.ctrlKey || event.metaKey) &&
        ((event.key === 'f' || event.key === 'F') || (event.key === 'k' || event.key === 'K'))
      ) {
        event.preventDefault();
        setSearchOpen(true);
        return;
      }

      // Keyboard-shortcut help ("?" = Shift+/). Ignored while typing in a field.
      if (!editingText && event.key === '?') {
        event.preventDefault();
        setShortcutsOpen((v) => !v);
        return;
      }

      // Version history drawer (Ctrl/⌘ + Shift + H). Shift avoids the bare
      // Ctrl+H browser-history / ⌘+H macOS-hide collisions. Allowed even from a
      // field so it can always be summoned.
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && (event.key === 'h' || event.key === 'H')) {
        event.preventDefault();
        if (historyOpenRef.current) {
          closeVersionOverview();
        } else {
          setHistoryOpen(true);
        }
        return;
      }

      // Undo / Redo (ignored while typing in a field).
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'z' || event.key === 'Z')) {
        event.preventDefault();
        if (event.shiftKey) doRedo();
        else doUndo();
        return;
      }
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'y' || event.key === 'Y')) {
        event.preventDefault();
        doRedo();
        return;
      }

      // Copy / Paste / Duplicate (ignored while typing in a field).
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'c' || event.key === 'C')) {
        if (copySelection()) event.preventDefault();
        return;
      }
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'v' || event.key === 'V')) {
        if (pasteClipboard()) event.preventDefault();
        return;
      }
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'd' || event.key === 'D')) {
        event.preventDefault(); // also stops the browser bookmark shortcut
        duplicateSelection();
        return;
      }

      // Select-all nodes (multi-select).
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'a' || event.key === 'A')) {
        event.preventDefault();
        setNodes((nds) => nds.map((n) => ({ ...n, selected: true })));
        return;
      }

      if (event.key === 'Delete' || event.key === 'Backspace') {
        if (editingText) return;

        // Snapshot once before removing whatever is selected.
        if (nodesRef.current.some((n) => n.selected) || edgesRef.current.some((e) => e.selected)) {
          recordUndo();
        }

        // Delete selected nodes and their connected edges
        setNodes((nds) => {
          const selectedNodeIds = nds.filter((n) => n.selected).map((n) => n.id);
          if (selectedNodeIds.length > 0) {
            setEdges((eds) => eds.filter((e) => !selectedNodeIds.includes(e.source) && !selectedNodeIds.includes(e.target)));
            setSelectedNode(null);
            return nds.filter((n) => !selectedNodeIds.includes(n.id));
          }
          return nds;
        });

        // Delete selected edges
        setEdges((eds) => {
          const selectedEdgeIds = eds.filter((e) => e.selected).map((e) => e.id);
          if (selectedEdgeIds.length > 0) {
            setSelectedEdge(null);
            return eds.filter((e) => !selectedEdgeIds.includes(e.id));
          }
          return eds;
        });
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => {
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [setNodes, setEdges, clearClickConnect, doUndo, doRedo, recordUndo, copySelection, pasteClipboard, duplicateSelection]);

  // Add new nodes toolbar handler
  const addNode = useCallback((nodePackage: NodePackageSummary, position?: { x: number; y: number }) => {
    const type = nodePackage.id;
    const metadata = availableNodeMetadata[type];

    recordUndo();
    setNodes((nds) => {
      const baseSlot = nds.length;
      const row = Math.floor(baseSlot / 5);
      const col = baseSlot % 5;
      const fallbackX = 150 + col * 280 + (row * 40);
      const fallbackY = 150 + row * 180 + (col * 20);

      const newNode = buildNode({
        type,
        position: position || { x: fallbackX, y: fallbackY },
        metadata,
        fallbackDisplayName: nodePackage.displayName,
      });
      return [...nds, newNode];
    });
    addRecentNode(nodePackage.id);
  }, [addRecentNode, availableNodeMetadata, setNodes, recordUndo]);

  // Flow-space coordinate at the centre of the currently-visible viewport, used to
  // place toolbar-inserted annotations (sticky notes, groups) where the user is looking.
  const viewportCenterFlow = useCallback(() => {
    const { width, height, transform } = reactFlowStore.getState();
    const [tx, ty, zoom] = transform;
    return { x: (width / 2 - tx) / zoom, y: (height / 2 - ty) / zoom };
  }, [reactFlowStore]);

  // ── Feature #13 — sticky note ── Insert an editable annotation card at the
  // viewport centre and select it so it's ready to type into.
  const addStickyNote = useCallback(() => {
    recordUndo();
    const c = viewportCenterFlow();
    const note = createStickyNoteNode({
      id: createNodeId(STICKY_NOTE_TYPE),
      position: snapIfEnabled({
        x: c.x - STICKY_NOTE_DEFAULT_SIZE.width / 2,
        y: c.y - STICKY_NOTE_DEFAULT_SIZE.height / 2,
      }),
    });
    setNodes((nds) => [...nds.map((n) => ({ ...n, selected: false })), { ...note, selected: true }]);
  }, [recordUndo, viewportCenterFlow, snapIfEnabled, setNodes]);

  // ── Feature #14 — node groups ── Wrap the current multi-selection in a visual
  // group container (≥2 top-level nodes); the group can later be collapsed/ungrouped.
  const groupSelection = useCallback(() => {
    const selectedIds = getNodes().filter((n) => n.selected).map((n) => n.id);
    if (selectedIds.length < 2) return;
    const groupId = createNodeId(GROUP_TYPE);
    setNodes((nds) => {
      const result = groupNodes(nds, selectedIds, groupId);
      if (!result) return nds;
      recordUndo();
      return result.nodes.map((n) => ({ ...n, selected: n.id === groupId }));
    });
  }, [getNodes, setNodes, recordUndo]);

  // ── Extract to subflow ── Lift the selected nodes into a brand-new subflow workflow and replace them
  // with a single `subflow` call node wired so the parent behaves exactly as before (see
  // node-editor/extractSubflow.ts for the SESE + variable-I/O analysis it relies on).
  const extractToSubflow = useCallback(async () => {
    if (extracting) return;
    const all = nodesRef.current;
    const allEdges = edgesRef.current;
    const selectedIds = all.filter((n) => n.selected).map((n) => n.id);
    if (selectedIds.length === 0) return;

    const exNodes: ExNode[] = all.map((n) => ({
      id: n.id,
      type: n.type ?? 'log',
      properties: (n.data?.properties as Record<string, unknown>) ?? {},
      triggerOnly: Boolean(n.data?.triggerOnly),
    }));
    const exEdges: ExEdge[] = allEdges.map((e) => ({
      id: e.id, source: e.source, sourceHandle: e.sourceHandle ?? null, target: e.target, targetHandle: e.targetHandle ?? null,
    }));

    // One pass handles both a single region and several isomorphic chains: differing literals across
    // the chains become subflow parameters, and each chain is replaced by a call node binding its values.
    const multi = analyzeMultiExtraction(exNodes, exEdges, selectedIds);
    if (!multi.ok) {
      setWorkflowStatusMessage(multi.reason ?? 'This selection can’t be extracted into a subflow.');
      return;
    }

    const name = window.prompt('Name for the new subflow', 'Extracted subflow')?.trim();
    if (!name) return;

    const plan = planParametrizedExtraction(exNodes, multi, (t) => createNodeId(t), () => createNodeId('e'));

    const posOf = (id: string) => all.find((n) => n.id === id)?.position ?? { x: 0, y: 0 };

    // Lay the canonical child out over region-0's positions, flanked by start/end.
    const order0 = multi.regions[0].order;
    const xs = order0.map((id) => posOf(id).x);
    const ys = order0.map((id) => posOf(id).y);
    const cyChild = ys.reduce((a, b) => a + b, 0) / ys.length;
    const startDef = plan.child.nodes[0];
    const endDef = plan.child.nodes[plan.child.nodes.length - 1];
    const chainDefs = plan.child.nodes.slice(1, -1);
    const childRfNodes: RFNode[] = [
      { id: startDef.id, type: 'start', position: { x: Math.min(...xs) - 240, y: cyChild }, data: { properties: startDef.properties, triggerOnly: true, outputHandles: ['result'] } },
      ...chainDefs.map((cn, k) => ({ id: cn.id, type: cn.type, position: posOf(order0[k]), data: { properties: cn.properties } } as RFNode)),
      { id: endDef.id, type: 'end', position: { x: Math.max(...xs) + 240, y: cyChild }, data: { properties: endDef.properties } },
    ];
    const childRfEdges: Edge[] = plan.child.edges.map((e) => ({
      id: e.id, source: e.source, sourceHandle: e.sourceHandle, target: e.target, targetHandle: e.targetHandle, animated: false,
    }));

    const childId = (typeof crypto !== 'undefined' && crypto.randomUUID) ? crypto.randomUUID() : `wf-${Date.now()}`;
    const childDef = schemaMapper.toBackend(childId, name, childRfNodes, childRfEdges);

    setExtracting(true);
    try {
      await api.saveWorkflow(childDef);
      await api.publishWorkflowDefinition(childDef);
    } catch (err: unknown) {
      setExtracting(false);
      alert(`Couldn’t create the subflow: ${getErrorMessage(err, 'Unknown error')}`);
      return;
    }
    setExtracting(false);

    // One call node per region, positioned at that region's centroid.
    const callNodes: RFNode[] = plan.calls.map((call) => {
      const cx = call.regionIds.reduce((s, id) => s + posOf(id).x, 0) / call.regionIds.length;
      const cy = call.regionIds.reduce((s, id) => s + posOf(id).y, 0) / call.regionIds.length;
      return {
        id: call.callNodeId, type: 'subflow', position: { x: cx, y: cy },
        data: {
          properties: { subflowId: childId, subflowName: name, subflowInputs: call.subflowInputs, subflowOutputs: call.subflowOutputs },
          displayName: name, outputHandles: ['result'],
        },
      };
    });

    recordUndo();
    const removeNodes = new Set(plan.nodesToRemove);
    const removeEdges = new Set(plan.parentEdgesToRemove);
    const addEdges = plan.calls.flatMap((c) => c.edgesToAdd).map((e) => ({
      id: e.id, source: e.source, sourceHandle: e.sourceHandle, target: e.target, targetHandle: e.targetHandle, animated: false,
    }));
    setNodes((nds) => [
      ...nds.filter((n) => !removeNodes.has(n.id)).map((n) => ({ ...n, selected: false })),
      ...callNodes.map((c, i) => ({ ...c, selected: i === 0 })),
    ]);
    setEdges((eds) => [...eds.filter((e) => !removeEdges.has(e.id)), ...addEdges]);
    setSelectedNode(null);
    const paramNote = plan.params.length ? ` with ${plan.params.length} parameter${plan.params.length === 1 ? '' : 's'}` : '';
    const callNote = plan.calls.length > 1 ? ` and replaced ${plan.calls.length} chains` : '';
    setWorkflowStatusMessage(`Extracted into subflow “${name}”${paramNote}${callNote}. Save & Publish to keep the change.`);
  }, [extracting, recordUndo, setNodes, setEdges, setWorkflowStatusMessage]);

  // Ungroup every selected group (or the group owning a selected child), restoring
  // children to the top level.
  const ungroupSelection = useCallback(() => {
    const selected = getNodes().filter((n) => n.selected);
    const groupIds = new Set<string>();
    for (const n of selected) {
      if (isGroupNodeType(n.type)) groupIds.add(n.id);
      else if (n.parentId) {
        const parent = getNodes().find((p) => p.id === n.parentId);
        if (parent && isGroupNodeType(parent.type)) groupIds.add(parent.id);
      }
    }
    if (groupIds.size === 0) return;
    recordUndo();
    setNodes((nds) => {
      let next = nds;
      for (const gid of groupIds) next = ungroupNodes(next, gid);
      return next;
    });
  }, [getNodes, setNodes, recordUndo]);

  // Whether the Ungroup button shows: a group (or a grouped child) is selected.
  const canUngroupSelection = useMemo(
    () => nodes.some((n) => n.selected && (isGroupNodeType(n.type) || (n.parentId != null && nodes.some((p) => p.id === n.parentId && isGroupNodeType(p.type))))),
    [nodes],
  );

  // ── Feature B — insert-on-edge ──
  // If `dropPosition` lands squarely on an existing wire A→B, splice the new node in:
  // A→new→B, shifting B's downstream subgraph aside to make room. Returns true when it
  // spliced (caller skips the plain drop). Triggers/containers/output-less nodes opt out.
  const tryInsertOnEdge = useCallback(
    (
      nodePackage: NodePackageSummary,
      metadata: NodePackageMetadata | undefined,
      dropPosition: { x: number; y: number },
    ): boolean => {
      // Containers manage their own body wiring; never splice one onto a wire.
      if (isContainerNodeType(nodePackage.id)) return false;
      // Trigger-only nodes have no input to receive the upstream half of the splice.
      if (metadata?.triggerOnly) return false;

      const ports = collectMeasuredPorts();
      const hit = findEdgeUnderPoint(edges as EdgeLike[], ports, dropPosition, EDGE_HIT_TOLERANCE);
      if (!hit) return false;

      const outHandles = metadata?.outputHandles;
      const primaryOut = Array.isArray(outHandles) && outHandles.length > 0 ? outHandles[0] : 'result';

      const width = DEFAULT_NODE_WIDTH;

      // Open space for the inserted node by shifting the downstream subgraph right.
      // Only top-level nodes move — children stay within their container's extent.
      const downstream = collectDownstream(hit.edge.target, edges as EdgeLike[]);
      const delta = width + 80;

      // Centre the node in the *expanded* gap (after the downstream shift), not on the
      // original edge midpoint — otherwise it hugs the upstream node with a long wire to
      // the downstream one. Adding delta/2 balances the A→new and new→B wire lengths.
      const newNode = buildNode({
        type: nodePackage.id,
        position: snapIfEnabled({ x: hit.midpoint.x - width / 2 + delta / 2, y: hit.midpoint.y - 40 }),
        metadata,
        fallbackDisplayName: nodePackage.displayName,
      });

      setNodes((nds) => [
        ...nds.map((n) =>
          downstream.has(n.id) && !n.parentId
            ? { ...n, position: { x: n.position.x + delta, y: n.position.y } }
            : n,
        ),
        newNode,
      ]);

      // Re-wire A → new → B. Removing the hit edge and the two addConnection calls all
      // compose as queued functional updates, so they land in a single render batch.
      setEdges((eds) => eds.filter((e) => e.id !== hit.edge.id));
      addConnection({
        source: hit.edge.source,
        sourceHandle: hit.edge.sourceHandle ?? null,
        target: newNode.id,
        targetHandle: 'in',
      });
      addConnection({
        source: newNode.id,
        sourceHandle: primaryOut,
        target: hit.edge.target,
        targetHandle: hit.edge.targetHandle ?? null,
      });

      addRecentNode(nodePackage.id);
      return true;
    },
    [collectMeasuredPorts, edges, setNodes, setEdges, addConnection, addRecentNode, snapIfEnabled],
  );

  // ── Feature A — proximity snap ──
  // After a node is dropped or moved, auto-wire it to the nearest *free, compatible*
  // port of another node when one lands within PROXIMITY_THRESHOLD. Both ends must be
  // free, so this never steals a single-input wire or fights a fan-in. At most one
  // downstream (self.source → other.target) and one upstream (other.source → self.target)
  // link are drawn; the upstream half is skipped for trigger-only nodes (no input).
  // Shared core: the nearest free, compatible downstream (self.source → other.target)
  // and upstream (other.source → self.target) matches for `nodeId`. Both the connect
  // (on drop / drag-stop) and the drag-time highlight paths read from this.
  const findProximityMatches = useCallback(
    (nodeId: string, triggerOnly: boolean) => {
      const self = getInternalNode(nodeId) as unknown as InternalNodeLike | undefined;
      if (!self || getPortPositions(self).length === 0) return { down: null, up: null };

      const internals: InternalNodeLike[] = [];
      for (const n of getNodes()) {
        const ni = getInternalNode(n.id);
        if (ni) internals.push(ni as unknown as InternalNodeLike);
      }
      const free = getFreePorts(internals, edges as EdgeLike[], isFanInTarget);
      const selfFree = free.filter((p) => p.nodeId === nodeId);
      const otherFree = free.filter((p) => p.nodeId !== nodeId);
      if (selfFree.length === 0 || otherFree.length === 0) return { down: null, up: null };

      const valid = (c: { source: string; sourceHandle: string; target: string; targetHandle: string }) =>
        isValidConnection(c as unknown as Connection);

      const down = findNearestCompatiblePort(selfFree.filter((p) => p.kind === 'source'), otherFree, PROXIMITY_THRESHOLD, valid);
      const up = triggerOnly
        ? null
        : findNearestCompatiblePort(selfFree.filter((p) => p.kind === 'target'), otherFree, PROXIMITY_THRESHOLD, valid);
      return { down, up };
    },
    [getInternalNode, getNodes, edges, isFanInTarget, isValidConnection],
  );

  const runProximityConnect = useCallback(
    (nodeId: string, triggerOnly: boolean) => {
      const { down, up } = findProximityMatches(nodeId, triggerOnly);
      if (down) {
        addConnection({
          source: down.source.nodeId,
          sourceHandle: down.source.handleId,
          target: down.target.nodeId,
          targetHandle: down.target.handleId,
        });
      }
      if (up) {
        addConnection({
          source: up.source.nodeId,
          sourceHandle: up.source.handleId,
          target: up.target.nodeId,
          targetHandle: up.target.handleId,
        });
      }
    },
    [findProximityMatches, addConnection],
  );

  // Drag-time affordance: while a node is dragged, glow the ports that would auto-connect
  // on release. Skips containers (their drag reparents rather than snaps).
  const handleNodeDrag = useCallback(
    (_event: React.MouseEvent, node: RFNode) => {
      const setKeys = useVariableStore.getState().setSnapCandidateKeys;
      if (isContainerNodeType(node.type)) {
        setKeys([]);
        dragProximityRef.current = null;
        return;
      }
      // Build the other-nodes free-port cache once on the first move of this drag (their positions/edges
      // don't change while one node is dragged), then reuse it every frame.
      let cache = dragProximityRef.current;
      if (!cache || cache.nodeId !== node.id) {
        const internals: InternalNodeLike[] = [];
        for (const n of getNodes()) {
          if (n.id === node.id) continue;
          const ni = getInternalNode(n.id);
          if (ni) internals.push(ni as unknown as InternalNodeLike);
        }
        cache = { nodeId: node.id, otherFree: getFreePorts(internals, edgesRef.current as unknown as EdgeLike[], isFanInTarget) };
        dragProximityRef.current = cache;
      }
      // Recompute only the DRAGGED node's free ports each frame; match against the cached (static) other
      // nodes' ports — so the per-mousemove cost is independent of total node count.
      const self = getInternalNode(node.id) as unknown as InternalNodeLike | undefined;
      const selfFree = self ? getFreePorts([self], edgesRef.current as unknown as EdgeLike[], isFanInTarget).filter((p) => p.nodeId === node.id) : [];
      const keys: string[] = [];
      if (selfFree.length > 0 && cache.otherFree.length > 0) {
        const valid = (c: { source: string; sourceHandle: string; target: string; targetHandle: string }) =>
          isValidConnection(c as unknown as Connection);
        const down = findNearestCompatiblePort(selfFree.filter((p) => p.kind === 'source'), cache.otherFree, PROXIMITY_THRESHOLD, valid);
        const up = node.data?.triggerOnly
          ? null
          : findNearestCompatiblePort(selfFree.filter((p) => p.kind === 'target'), cache.otherFree, PROXIMITY_THRESHOLD, valid);
        if (down) keys.push(`${down.source.nodeId} ${down.source.handleId}`, `${down.target.nodeId} ${down.target.handleId}`);
        if (up) keys.push(`${up.source.nodeId} ${up.source.handleId}`, `${up.target.nodeId} ${up.target.handleId}`);
      }
      const prev = useVariableStore.getState().snapCandidateKeys;
      // Only write when the candidate set actually changes (drag fires every mousemove).
      if (keys.length !== prev.length || keys.some((k, i) => k !== prev[i])) {
        setKeys(keys);
      }
    },
    [getNodes, getInternalNode, isFanInTarget, isValidConnection],
  );

  // Defer two frames so React Flow has mounted + measured the node's handle bounds
  // before we read their positions. Falls back to setTimeout where rAF is absent.
  const scheduleProximityConnect = useCallback(
    (nodeId: string, triggerOnly: boolean) => {
      const raf: (cb: () => void) => void =
        typeof requestAnimationFrame === 'function' ? (cb) => requestAnimationFrame(cb) : (cb) => { setTimeout(cb, 0); };
      raf(() => raf(() => runProximityConnect(nodeId, triggerOnly)));
    },
    [runProximityConnect],
  );
  useEffect(() => {
    scheduleProximityRef.current = scheduleProximityConnect;
  }, [scheduleProximityConnect]);

  const handlePaletteDragStart = useCallback((event: DragEvent<HTMLButtonElement>, nodePackage: NodePackageSummary) => {
    event.dataTransfer.setData('application/knotarium-node-package', nodePackage.id);
    event.dataTransfer.effectAllowed = 'move';
  }, []);

  // Stable so the memoized SidebarPalette doesn't re-render on every Canvas render (hover/drag).
  const handleOpenApiImport = useCallback(() => setShowOpenApiImportModal(true), []);

  const handleCanvasDragOver = useCallback((event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
  }, []);

  const handleCanvasDrop = useCallback((event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();

    // 0. A template dragged from the palette — stamp its subgraph at the cursor.
    const templateRaw = event.dataTransfer.getData('application/knotarium-template');
    if (templateRaw) {
      try {
        const dragged = JSON.parse(templateRaw) as { source: 'gallery' | 'library'; templateId: string; needsConfig?: boolean };
        const dropPosition = snapIfEnabled(screenToFlowPosition({ x: event.clientX, y: event.clientY }));
        void insertTemplateByDrop(dragged.source, dragged.templateId, !!dragged.needsConfig, dropPosition);
      } catch (err) {
        console.error('Error handling canvas drop of template:', err);
      }
      return;
    }

    // 1. Check if it is an OpenAPI operation drop
    const rawJson = event.dataTransfer.getData('application/json');
    if (rawJson) {
      try {
        const dragData = JSON.parse(rawJson);
        if (dragData && dragData.type === 'openapi-operation') {
          const { packageId, operationId } = dragData;
          const nodePackage = availableNodes.find(candidate => candidate.id === packageId);
          if (nodePackage) {
            const dropPosition = snapIfEnabled(screenToFlowPosition({ x: event.clientX, y: event.clientY }));

            // Check if dropping inside a forLoop
            const loopNode = findContainingLoopNode(nodes, dropPosition);

            const newNode = buildNode({
              type: packageId,
              position: loopNode ? {
                x: dropPosition.x - loopNode.position.x,
                y: dropPosition.y - loopNode.position.y
              } : dropPosition,
              parentId: loopNode?.id,
              metadata: availableNodeMetadataRef.current[packageId],
              fallbackDisplayName: nodePackage.displayName,
              properties: { operationId, arguments: {} },
            });

            recordUndo();
            setNodes(nds => [...nds, newNode]);
            addRecentNode(packageId);
            return;
          }
        }
      } catch (err) {
        console.error('Error handling canvas drop of openapi-operation:', err);
      }
    }

    // 2. Standard palette package drop
    const nodePackageId = event.dataTransfer.getData('application/knotarium-node-package');
    if (!nodePackageId) {
      return;
    }

    const nodePackage = availableNodes.find(candidate => candidate.id === nodePackageId);
    if (!nodePackage) {
      return;
    }

    const dropPosition = snapIfEnabled(screenToFlowPosition({ x: event.clientX, y: event.clientY }));

    // Check if dropping inside a forLoop
    const loopNode = findContainingLoopNode(nodes, dropPosition);

    // One snapshot for the whole drop gesture (add + any auto-wire/splice stays a single undo).
    recordUndo();

    if (loopNode) {
      // Add node as child
      const newNode = buildNode({
        type: nodePackage.id,
        position: {
          x: dropPosition.x - loopNode.position.x,
          y: dropPosition.y - loopNode.position.y
        },
        parentId: loopNode.id,
        metadata: availableNodeMetadataRef.current[nodePackage.id],
        fallbackDisplayName: nodePackage.displayName,
      });

      // Convenience: the FIRST body node dropped into an empty container is auto-wired
      // start -> node -> end, giving an immediately-runnable single-node body. Once the
      // container already has children we stay hands-off so we never disturb the user's wiring.
      const containerIsEmpty = !nodes.some(n => n.parentId === loopNode.id);
      const isTrigger = Boolean(newNode.data?.triggerOnly);
      const outHandles = newNode.data?.outputHandles as string[] | undefined;
      const primaryOut = Array.isArray(outHandles) && outHandles.length > 0 ? outHandles[0] : 'result';

      setNodes(nds => [...nds, newNode]);
      addRecentNode(nodePackage.id);

      if (containerIsEmpty && !isTrigger) {
        addConnection({ source: loopNode.id, sourceHandle: 'start', target: newNode.id, targetHandle: 'in' });
        addConnection({ source: newNode.id, sourceHandle: primaryOut, target: loopNode.id, targetHandle: 'end' });
      }
    } else {
      // Feature B (insert-on-edge) takes precedence over a plain drop when the drop
      // lands on a wire. Otherwise place the node and try Feature A proximity snap.
      const metadata = availableNodeMetadataRef.current[nodePackage.id];
      if (!tryInsertOnEdge(nodePackage, metadata, dropPosition)) {
        const newNode = buildNode({
          type: nodePackage.id,
          position: dropPosition,
          metadata,
          fallbackDisplayName: nodePackage.displayName,
        });
        setNodes((nds) => [...nds, newNode]);
        addRecentNode(nodePackage.id);
        scheduleProximityConnect(newNode.id, Boolean(metadata?.triggerOnly));
      }
    }
  }, [availableNodes, screenToFlowPosition, nodes, addRecentNode, setNodes, addConnection, tryInsertOnEdge, scheduleProximityConnect, recordUndo, snapIfEnabled, insertTemplateByDrop]);

  // Save & Publish in one step: persist the draft, then snapshot + activate a
  // runtime version so the workflow is immediately runnable. The backend dedups
  // identical content, so re-saving without changes won't spawn a new version.
  const handleSave = async () => {
    if (!currentId || readOnly) {
      return;
    }
    setSaving(true);
    setWorkflowStatusMessage(null);
    setDiagnostics([]);
    try {
      const backendDefinition = schemaMapper.toBackend(currentId, workflowName, nodes, edges);
      await api.saveWorkflow(backendDefinition);

      const published = await api.publishWorkflow(currentId, nodes, edges);
      const versions = await api.getWorkflowVersions(currentId);
      setWorkflowVersions(versions);
      setSelectedActivationVersionId(versions[0]?.id ?? '');
      const activeVersion = await api.getActiveWorkflowVersion(currentId);
      setActiveWorkflowVersion(activeVersion);

      // The current canvas is now the saved baseline → button goes clean.
      setSavedSignature(workflowSignature(workflowName, nodes, edges));
      setWorkflowStatusMessage(`Saved and published version ${published.version.versionNumber} — active for runtime execution.`);
      onSaved?.(currentId);

      // Frame the whole graph once, on the first save of a newly-created workflow — until now the viewport
      // sat wherever nodes were dropped (often pinned to a corner). Subsequent saves leave the view alone.
      if (pendingFirstFrameRef.current) {
        pendingFirstFrameRef.current = false;
        requestAnimationFrame(() => fitView({ padding: 0.15, duration: 400 }));
      }
    } catch (err: unknown) {
      const errorDiagnostics = getErrorDiagnostics(err);
      if (isApiError(err) && err.status === 400 && errorDiagnostics) {
        setDiagnostics(errorDiagnostics);
      } else {
        alert(`Error saving workflow: ${getErrorMessage(err, 'Unknown error')}`);
      }
    } finally {
      setSaving(false);
    }
  };

  // When editing a subflow, leaving it should Save & Publish the child first — so callers pick up
  // the new interface and no edits are lost. Refs keep the registered handler reading latest state.
  const handleSaveRef = useRef(handleSave);
  const onBackRef = useRef(onBack);
  useEffect(() => {
    handleSaveRef.current = handleSave;
    onBackRef.current = onBack;
  });
  const exitSubflowWithSave = useCallback(async () => {
    await handleSaveRef.current();
    onBackRef.current?.();
  }, []);

  // Unsaved-changes guard for the top-level canvas (the subflow path already auto-saves on exit).
  // Back with pending edits opens a Save / Discard / Cancel choice; a browser close/refresh gets the
  // native beforeunload prompt.
  const [leaveConfirmOpen, setLeaveConfirmOpen] = useState(false);
  const handleBack = useCallback(() => {
    if (!readOnly && isDirty) { setLeaveConfirmOpen(true); return; }
    onBackRef.current?.();
  }, [readOnly, isDirty]);
  const confirmLeaveSave = useCallback(async () => {
    setLeaveConfirmOpen(false);
    await handleSaveRef.current();
    onBackRef.current?.();
  }, []);
  const confirmLeaveDiscard = useCallback(() => {
    setLeaveConfirmOpen(false);
    onBackRef.current?.();
  }, []);
  useEffect(() => {
    if (readOnly || !isDirty) return;
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [readOnly, isDirty]);
  useEffect(() => {
    if (!registerSubflowExit) return;
    registerSubflowExit(isSubflow ? () => { void exitSubflowWithSave(); } : null);
    return () => registerSubflowExit(null);
  }, [isSubflow, registerSubflowExit, exitSubflowWithSave]);

  // Drill into a subflow node's child workflow. Persist the parent as a draft FIRST so the
  // in-memory edits (including this node's subflowId) survive the navigation — otherwise coming
  // back reloads the parent from the backend and silently drops everything that wasn't saved.
  const openSubflowFromNode = async (node: RFNode) => {
    const properties = (node.data?.properties ?? {}) as Record<string, unknown>;
    const subflowId = typeof properties.subflowId === 'string' ? properties.subflowId : '';
    if (!subflowId) {
      setWorkflowStatusMessage('Pick a workflow for this subflow before opening it.');
      return;
    }
    if (currentId) {
      try {
        const backendDefinition = schemaMapper.toBackend(currentId, workflowName, nodes, edges);
        await api.saveWorkflow(backendDefinition);
        setSavedSignature(workflowSignature(workflowName, nodes, edges));
      } catch (err: unknown) {
        alert(`Could not save before opening the subflow: ${getErrorMessage(err, 'Unknown error')}`);
        return;
      }
    }
    onOpenSubflow?.(subflowId);
  };

  // Drill-down affordance: the subflow card's "open" icon (and double-click) post a
  // request to useSubflowOpenStore; consume it here so navigation reuses the
  // save-before-open path above. A ref keeps the latest closure without re-subscribing.
  const openSubflowRef = useRef(openSubflowFromNode);
  useEffect(() => {
    openSubflowRef.current = openSubflowFromNode;
  });
  useEffect(() => {
    return useSubflowOpenStore.subscribe((state) => {
      const reqId = state.requestNodeId;
      if (!reqId) return;
      useSubflowOpenStore.getState().clearRequest();
      const node = nodesRef.current.find((n) => n.id === reqId);
      if (node) void openSubflowRef.current(node);
    });
  }, []);

  // An event-driven device graph (device blocks, no manual/start trigger) has nothing for a manual
  // Run to start — it runs when its wired device events fire — so the toolbar disables Run for it.
  const isEventDrivenDeviceGraph = useMemo(
    () => nodes.some((n) => n.type === 'externalDevice')
      && !nodes.some((n) => n.type === 'start' || n.type === 'manualTrigger'),
    [nodes],
  );

  // Wired device pins that can be fired from the editor (the "simulate a device event" path), and the
  // dialog's open state. Replaces the (disabled) manual Run for event-driven graphs.
  const simulatable = useMemo(() => simulatablePins(nodes, edges), [nodes, edges]);
  const [showSimulate, setShowSimulate] = useState(false);

  // Run = activate the selected version, then trigger it. Activation happens
  // every time, so the dropdown selection is always what executes — no separate
  // "Activate" step needed, and running works even when nothing has changed.
  const handleRun = async () => {
    if (!currentId) {
      return;
    }
    // Running from a preview is allowed (Run targets the previewed/selected version); exit the
    // read-only snapshot first so the live editor returns once the run starts. Diff mode blocks Run.
    if (readOnly) {
      if (editorMode.kind !== 'preview') {
        return;
      }
      exitReadOnly();
    }
    const selectedVersion = workflowVersions.find(version => version.id === selectedActivationVersionId);
    if (!selectedVersion) {
      setWorkflowStatusMessage('Publish a version (Save & Publish) before running.');
      return;
    }

    setTriggering(true);
    setWorkflowStatusMessage(null);
    // Clear variable values before running to reset status to 'awaiting run'
    useVariableStore.getState().clearVariableValues(currentId);
    // Starting a run re-initializes the flow to its default viewport, jumping the graph into the
    // upper-left corner. Snapshot the current framing now and restore it once that reset settles, so
    // the camera stays exactly where the user left it. Two attempts cover slight timing variance.
    const framingBeforeRun = getViewport();
    const keepFraming = () => setViewport(framingBeforeRun, { duration: 0 });
    window.setTimeout(keepFraming, 300);
    window.setTimeout(keepFraming, 650);
    try {
      const activeVersion = await api.activateWorkflowVersion(currentId, selectedVersion.id);
      setActiveWorkflowVersion(activeVersion);
      const instance = await api.triggerWorkflow(currentId);
      onTriggered(instance.id);
    } catch (err: unknown) {
      const errorDiagnostics = getErrorDiagnostics(err);
      if (isApiError(err) && err.status === 400 && errorDiagnostics) {
        setDiagnostics(errorDiagnostics);
      } else {
        alert(`Error starting workflow: ${getErrorMessage(err, 'Unknown error')}`);
      }
    } finally {
      setTriggering(false);
    }
  };

  const nodeTypesString = nodes.map(n => n.type).join(',');
  const combinedNodeTypes = useMemo(() => {
    const availableNodeIds = Array.from(
      new Set([
        ...availableNodes.map(nodePackage => nodePackage.id),
        ...nodes.map(node => node.type || '')
      ].filter(Boolean))
    );
    const types = createNodeTypes(availableNodeIds);
    // Editor-only annotation types render with dedicated components rather than the
    // generic node card (they have no ports and bespoke editing chrome).
    types[STICKY_NOTE_TYPE] = StickyNoteNode as (typeof types)[string];
    types[GROUP_TYPE] = GroupNode as (typeof types)[string];
    return types;
  }, [availableNodes, nodeTypesString]);

  return (
    <div style={{ display: 'flex', height: '100%', width: '100%' }}>
      <SidebarPalette
        availableNodes={availableNodes}
        onAddNode={addNode}
        onDragStart={handlePaletteDragStart}
        onImportOpenApi={handleOpenApiImport}
      />

      {/* Visual Canvas Panel */}
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', position: 'relative', borderRight: '1px solid var(--border-color)' }}>
        
        <CanvasToolbar
          workflowName={workflowName}
          setWorkflowName={setWorkflowName}
          onBack={isSubflow ? () => { void exitSubflowWithSave(); } : handleBack}
          workflowVersions={workflowVersions}
          activeWorkflowVersion={activeWorkflowVersion}
          selectedActivationVersionId={selectedActivationVersionId}
          setSelectedActivationVersionId={setSelectedActivationVersionId}
          onSelectVersion={handleSelectVersion}
          onHoverPreview={handleHoverPreviewVersion}
          onCompareVersions={() => setHistoryOpen(true)}
          saving={saving}
          handleSave={handleSave}
          isDirty={isDirty}
          blockingErrorCount={blockingErrorCount}
          currentId={currentId}
          triggering={triggering}
          handleRun={handleRun}
          readOnly={readOnly}
          previewing={editorMode.kind === 'preview'}
          isEventDrivenDeviceGraph={isEventDrivenDeviceGraph}
          onSimulate={() => setShowSimulate(true)}
          canSimulate={simulatable.length > 0}
          onWatchLiveRuns={onWatchLiveRuns}
          armed={armed}
        />
        {showSimulate && currentId && (
          <SimulateSignalDialog
            workflowId={currentId}
            pins={simulatable}
            actionFieldsById={actionFieldsById}
            onClose={() => setShowSimulate(false)}
            onStarted={(executionId) => { setShowSimulate(false); (onSimulated ?? onTriggered)(executionId); }}
          />
        )}

        {leaveConfirmOpen && (
          <UnsavedChangesDialog
            saving={saving}
            onCancel={() => setLeaveConfirmOpen(false)}
            onDiscard={confirmLeaveDiscard}
            onSave={() => void confirmLeaveSave()}
          />
        )}

        {workflowStatusMessage ? (
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              padding: '10px 24px',
              background: 'rgba(148, 163, 184, 0.08)',
              borderBottom: '1px solid var(--border-color)',
              color: 'var(--text-secondary)',
              fontSize: '0.82rem',
            }}
          >
            <span>{workflowStatusMessage}</span>
          </div>
        ) : null}

        {editorMode.kind === 'preview' && previewVersionNumber != null && (
          <PreviewBanner
            versionNumber={previewVersionNumber}
            isActiveVersion={editorMode.versionId === activeWorkflowVersion?.workflowVersionId}
            onExit={closeVersionOverview}
            onRestore={() => openRestoreDialog(editorMode.versionId)}
            onDiffAgainstDraft={() => void handleDiffAgainstDraft(editorMode.versionId)}
          />
        )}

        {loading ? (
          <div style={{ display: 'flex', flex: 1, alignItems: 'center', justifyContent: 'center' }}>
            <span style={{ color: 'var(--text-secondary)' }}>Scaffolding visual graph layout...</span>
          </div>
        ) : (
          <div
            style={{
              flex: 1,
              position: 'relative',
              // Preview renders the selected version semi-transparent to signal "read-only snapshot".
              opacity: editorMode.kind === 'preview' ? 0.6 : 1,
              transition: 'opacity 0.15s ease',
            }}
            onDragOver={handleCanvasDragOver}
            onDrop={handleCanvasDrop}
          >
            {/* React Flow Editor. In PublishedPreview we render the read-only
                version snapshot instead of the live draft (the draft stays in
                `nodes`/`edges`, just not rendered, and returns on exit). Diff mode
                keeps the live draft visible but read-only behind the diff panel. */}
            <ReactFlow
              nodes={editorMode.kind === 'preview' ? previewNodes : nodes}
              edges={editorMode.kind === 'preview' ? previewEdges : displayEdges}
              edgeTypes={edgeTypes}
              onNodesChange={readOnly ? undefined : onNodesChange}
              onEdgesChange={readOnly ? undefined : onEdgesChange}
              nodesDraggable={!readOnly}
              nodesConnectable={!readOnly}
              elementsSelectable={!readOnly}
              onConnect={onConnect}
              onConnectStart={onConnectStart}
              onConnectEnd={onConnectEnd}
              onReconnectStart={onReconnectStart}
              onReconnect={onReconnect}
              onReconnectEnd={onReconnectEnd}
              onClickConnectStart={onClickConnectStart}
              onClickConnectEnd={onClickConnectEnd}
              isValidConnection={isValidConnection}
              connectionRadius={70}
              // Large imported graphs (e.g. a large vendor setting: ~3000 nodes spanning >100k px)
              // need to zoom out far below React Flow's default floor of 0.5 to frame the whole
              // graph — otherwise fitView/Center clamp at 0.5 and land on empty space between
              // nodes, so the canvas looks blank. Allow a very low minimum.
              minZoom={0.01}
              // Only mount nodes/edges inside the viewport. Without this, a multi-thousand-node
              // graph mounts every node into the DOM on every render, which is the source of the
              // lag on big imports.
              onlyRenderVisibleElements
              nodeTypes={combinedNodeTypes}
              onSelectionChange={onSelectionChange}
              onNodeMouseEnter={(_e, node) => useVariableStore.getState().setHoveredNodeId(node.id)}
              onNodeMouseLeave={() => useVariableStore.getState().setHoveredNodeId(null)}
              onNodeClick={onNodeClick}
              onNodeDoubleClick={(e, node) => {
                if (node.type === 'inlineCode') {
                  // Open the code editor directly; also stop the canvas double-click zoom.
                  e.stopPropagation();
                  useInlineCodeEditorStore.getState().requestOpen(node.id);
                } else if (node.type === 'subflow') {
                  // Drill into the referenced child workflow on its own canvas
                  // (saves the parent draft first to avoid losing unsaved edits).
                  e.stopPropagation();
                  void openSubflowFromNode(node);
                } else if (node.type === 'condition') {
                  // Open the full-screen logic editor; also stop the canvas double-click zoom.
                  e.stopPropagation();
                  useConditionEditorOpenStore.getState().requestOpen(node.id);
                }
              }}
              onPaneClick={onPaneClick}
              onNodeDragStart={onNodeDragStart}
              onNodeDrag={handleNodeDrag}
              onNodeDragStop={handleNodeDragStop}
              // Editor-style multi-select: left-drag draws a selection box (partial = touch
              // to select); pan with middle/right mouse. Shift/Ctrl add to the selection.
              selectionOnDrag
              selectionMode={SelectionMode.Partial}
              panOnDrag={[1, 2]}
              snapToGrid={snapEnabled}
              snapGrid={[SNAP_GRID_SIZE, SNAP_GRID_SIZE]}
              // Persist the viewport per workflow so re-entry restores it (see the restore effect).
              onMoveEnd={(_e, vp) => saveViewport(currentId, vp)}
            >
              <Controls position="bottom-right" />
              {/* offset = gap puts the dots on the grid corners (flow multiples of the gap),
                  so a snapped node's top-left lands on a dot. With the default offset of 0,
                  React Flow shifts the dot pattern by half a cell (dots at cell centres). */}
              <Background variant={BackgroundVariant.Dots} color="rgba(255,255,255,0.10)" size={1.5} gap={SNAP_GRID_SIZE} offset={SNAP_GRID_SIZE} />
              <MiniMap
                position="bottom-left"
                // Position-only navigation: click jumps the viewport to that spot and drag pans from there,
                // both keeping the current zoom (no zoomable — the minimap must never change the zoom level).
                pannable
                onClick={(_event, position) => setCenter(position.x, position.y, { zoom: getZoom(), duration: 300 })}
                style={{
                  background: 'var(--bg-surface-opaque)',
                  border: '1px solid var(--border-color)',
                  borderRadius: '10px',
                }}
                nodeColor={(node) => {
                  switch (node.type) {
                    case 'start': return 'var(--color-success)';
                    case 'end': return 'var(--color-error)';
                    case 'condition': return 'var(--color-warning)';
                    default: return 'var(--color-accent)';
                  }
                }}
                maskColor="rgba(0,0,0,0.6)"
              />
            </ReactFlow>

            {/* First-run coach hint on an empty, editable canvas (not in read-only preview). */}
            {editorMode.kind !== 'preview' && nodes.length === 0 && <EmptyCanvasHint />}

            {searchOpen && (
              <NodeSearchPalette
                nodes={nodes}
                onClose={() => setSearchOpen(false)}
                onPick={jumpToNode}
              />
            )}

            {shortcutsOpen && <KeyboardShortcutsHelp onClose={() => setShortcutsOpen(false)} />}

            {templatePickerOpen && (
              <TemplateInsertPicker
                onClose={() => setTemplatePickerOpen(false)}
                onInsert={insertTemplatePayload}
              />
            )}

            <VersionHistoryPanel
              open={historyOpen}
              versions={historyVersions}
              loading={historyLoading}
              error={historyError}
              activeVersionId={activeWorkflowVersion?.workflowVersionId ?? null}
              previewVersionId={editorMode.kind === 'preview' ? editorMode.versionId : null}
              onClose={closeVersionOverview}
              onPreview={(versionId) => void handlePreviewVersion(versionId)}
              onRestore={openRestoreDialog}
              onDiffAgainstDraft={(versionId) => void handleDiffAgainstDraft(versionId)}
              onDiffDraftVsActive={() => void handleDiffDraftVsActive()}
            />

            {diffState && (
              <VersionDiffView
                leftLabel={diffState.leftLabel}
                rightLabel={diffState.rightLabel}
                diff={diffState.diff}
                onClose={exitReadOnly}
              />
            )}

            {restoreTarget && (
              <RestoreVersionDialog
                versionNumber={restoreTarget.versionNumber}
                busy={restoreBusy}
                result={restoreResult}
                error={restoreError}
                onConfirm={(options) => void confirmRestore(options)}
                onClose={() => {
                  setRestoreTarget(null);
                  setRestoreResult(null);
                  setRestoreError(null);
                }}
              />
            )}

            {/* Layout tools: Tidy (auto-layout) always; Align/Distribute when ≥2 selected. */}
            <div
              style={{
                position: 'absolute',
                top: '12px',
                right: '12px',
                zIndex: 900,
                display: 'flex',
                gap: '8px',
                alignItems: 'flex-start',
              }}
            >
              {selectedNodeCount >= 2 && (
                <div style={layoutToolbarStyle}>
                  <button type="button" style={layoutBtnStyle} title="Align left" onClick={() => alignSelection('left')}>⊢</button>
                  <button type="button" style={layoutBtnStyle} title="Align horizontal centres" onClick={() => alignSelection('centerX')}>↔</button>
                  <button type="button" style={layoutBtnStyle} title="Align right" onClick={() => alignSelection('right')}>⊣</button>
                  <span style={layoutDividerStyle} />
                  <button type="button" style={layoutBtnStyle} title="Align top" onClick={() => alignSelection('top')}>⊤</button>
                  <button type="button" style={layoutBtnStyle} title="Align vertical centres" onClick={() => alignSelection('centerY')}>↕</button>
                  <button type="button" style={layoutBtnStyle} title="Align bottom" onClick={() => alignSelection('bottom')}>⊥</button>
                  {selectedNodeCount >= 3 && (
                    <>
                      <span style={layoutDividerStyle} />
                      <button type="button" style={layoutBtnStyle} title="Distribute horizontally" onClick={() => distributeSelection('horizontal')}>⇿</button>
                      <button type="button" style={layoutBtnStyle} title="Distribute vertically" onClick={() => distributeSelection('vertical')}>⇕</button>
                    </>
                  )}
                </div>
              )}
              <div className="lt-group">
                <button
                  type="button"
                  className="lt-btn"
                  title="Center / fit all nodes in view"
                  onClick={() => fitView({ padding: 0.15, duration: 400 })}
                >
                  <span className="lt-btn-icon"><Crosshair size={15} /></span>
                  Center
                </button>
                <button
                  type="button"
                  className="lt-btn"
                  title="Tidy layout (auto-arrange left → right)"
                  onClick={runAutoLayout}
                >
                  <span className="lt-btn-icon"><Maximize2 size={15} /></span>
                  Tidy
                </button>
                <button
                  type="button"
                  className={`lt-btn${snapEnabled ? ' lt-active' : ''}`}
                  aria-pressed={snapEnabled}
                  title={snapEnabled ? 'Snap to grid: on' : 'Snap to grid: off'}
                  onClick={() => setSnapEnabled((v) => !v)}
                >
                  <span className="lt-btn-icon"><Hash size={15} /></span>
                  Grid
                </button>
                {!readOnly && (
                  <button
                    type="button"
                    className="lt-btn"
                    title="Add a sticky note"
                    aria-label="Add a sticky note"
                    onClick={addStickyNote}
                  >
                    <span className="lt-btn-icon"><StickyNote size={15} /></span>
                    Note
                  </button>
                )}
                {!readOnly && (
                  <button
                    type="button"
                    className="lt-btn"
                    title="Insert a template's nodes into this workflow"
                    aria-label="Insert from template"
                    onClick={() => setTemplatePickerOpen(true)}
                  >
                    <span className="lt-btn-icon"><LayoutTemplate size={15} /></span>
                    Template
                  </button>
                )}
                {!readOnly && selectedNodeCount >= 2 && (
                  <button
                    type="button"
                    className="lt-btn"
                    title="Group selected nodes"
                    aria-label="Group selected nodes"
                    onClick={groupSelection}
                  >
                    <span className="lt-btn-icon"><Group size={15} /></span>
                    Group
                  </button>
                )}
                {!readOnly && selectedNodeCount >= 1 && (
                  <button
                    type="button"
                    className="lt-btn"
                    title="Extract the selected nodes into a new subflow"
                    aria-label="Extract selection to a subflow"
                    onClick={extractToSubflow}
                    disabled={extracting}
                  >
                    <span className="lt-btn-icon"><Combine size={15} /></span>
                    {extracting ? 'Extracting…' : 'Extract'}
                  </button>
                )}
                {!readOnly && canUngroupSelection && (
                  <button
                    type="button"
                    className="lt-btn"
                    title="Ungroup"
                    aria-label="Ungroup"
                    onClick={ungroupSelection}
                  >
                    <span className="lt-btn-icon"><Ungroup size={15} /></span>
                    Ungroup
                  </button>
                )}
                <button
                  type="button"
                  className={`lt-btn${historyOpen ? ' lt-active' : ''}`}
                  aria-pressed={historyOpen}
                  title="Version history (Ctrl/⌘+Shift+H)"
                  aria-label="Version history"
                  onClick={() => (historyOpen ? closeVersionOverview() : setHistoryOpen(true))}
                >
                  <span className="lt-btn-icon"><History size={15} /></span>
                  History
                </button>
                <span className="lt-divider" />
                <button
                  type="button"
                  className="lt-btn lt-help"
                  title="Keyboard shortcuts (?)"
                  aria-label="Keyboard shortcuts"
                  onClick={() => setShortcutsOpen(true)}
                >
                  <CircleHelp size={16} />
                </button>
              </div>
            </div>

            {/* Density Popover & Trigger Button */}
            <div ref={popoverRef} style={{ position: 'absolute', bottom: '16px', right: '90px', zIndex: 1000, display: 'flex', flexDirection: 'column', alignItems: 'flex-end' }}>
              {isDensityPopoverOpen && (
                <div
                  style={{
                    background: 'rgba(16, 22, 37, 0.95)',
                    backdropFilter: 'blur(12px)',
                    border: '1px solid var(--border-color)',
                    borderRadius: '10px',
                    padding: '12px 16px',
                    width: '240px',
                    boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.7)',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: '10px',
                    marginBottom: '8px',
                  }}
                >
                  <span style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
                    Data Wire Density
                  </span>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                    {[
                      {
                        mode: 'reveal' as const,
                        label: 'Reveal on demand',
                        desc: 'Canvas shows execution flow only. Hover/click nodes to trace.',
                      },
                      {
                        mode: 'dots' as const,
                        label: 'Compact dots',
                        desc: 'Dashed wires always on, with collapsed midpoint diamonds.',
                      },
                      {
                        mode: 'boxes' as const,
                        label: 'Always-on value boxes',
                        desc: 'Dashed wires and value tokens always visible.',
                      },
                    ].map((opt) => (
                      <button
                        key={opt.mode}
                        onClick={() => {
                          setDensityMode(opt.mode);
                          setIsDensityPopoverOpen(false);
                        }}
                        style={{
                          background: densityMode === opt.mode ? 'rgba(99, 102, 241, 0.15)' : 'transparent',
                          border: densityMode === opt.mode ? '1px solid var(--color-accent)' : '1px solid transparent',
                          borderRadius: '6px',
                          padding: '8px 10px',
                          textAlign: 'left',
                          cursor: 'pointer',
                          color: '#fff',
                          transition: 'all 0.15s ease',
                          display: 'flex',
                          flexDirection: 'column',
                          gap: '2px',
                        }}
                        onMouseOver={(e) => {
                          if (densityMode !== opt.mode) {
                            e.currentTarget.style.background = 'rgba(255, 255, 255, 0.03)';
                          }
                        }}
                        onMouseOut={(e) => {
                          if (densityMode !== opt.mode) {
                            e.currentTarget.style.background = 'transparent';
                          }
                        }}
                      >
                        <span style={{ fontSize: '0.8rem', fontWeight: 700, color: densityMode === opt.mode ? 'var(--color-accent)' : '#fff' }}>
                          {opt.label}
                        </span>
                        <span style={{ fontSize: '0.65rem', color: 'var(--text-muted)', lineHeight: '1.25' }}>
                          {opt.desc}
                        </span>
                      </button>
                    ))}
                  </div>
                </div>
              )}
              
              <button
                onClick={() => setIsDensityPopoverOpen(!isDensityPopoverOpen)}
                title="Data Wire Density Settings"
                style={{
                  background: 'rgba(16, 22, 37, 0.85)',
                  backdropFilter: 'blur(10px)',
                  border: '1px solid var(--border-color)',
                  color: '#fff',
                  borderRadius: '8px',
                  width: '38px',
                  height: '38px',
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  cursor: 'pointer',
                  boxShadow: '0 4px 12px rgba(0, 0, 0, 0.5)',
                  transition: 'background 0.2s, border-color 0.2s',
                }}
                onMouseOver={(e) => {
                  e.currentTarget.style.background = 'rgba(16, 22, 37, 0.95)';
                  e.currentTarget.style.borderColor = 'rgba(255, 255, 255, 0.15)';
                }}
                onMouseOut={(e) => {
                  e.currentTarget.style.background = 'rgba(16, 22, 37, 0.85)';
                  e.currentTarget.style.borderColor = 'var(--border-color)';
                }}
              >
                <Eye size={18} color={densityMode !== 'reveal' ? 'var(--color-accent)' : '#fff'} />
              </button>
            </div>

            <style>{`
              @keyframes drag-hint-pulse {
                0%, 100% { box-shadow: 0 0 0 0 rgba(99, 102, 241, 0.4); }
                70% { box-shadow: 0 0 0 10px rgba(99, 102, 241, 0); }
              }
            `}</style>

            {isDraggingOutput && (
              <div
                style={{
                  position: 'absolute',
                  bottom: '36px',
                  left: '50%',
                  transform: 'translateX(-50%)',
                  background: 'rgba(16, 22, 37, 0.85)',
                  backdropFilter: 'blur(10px)',
                  border: '1px dashed var(--color-accent)',
                  boxShadow: '0 0 20px var(--color-accent-glow)',
                  borderRadius: '20px',
                  padding: '12px 24px',
                  color: '#fff',
                  zIndex: 1000,
                  display: 'flex',
                  alignItems: 'center',
                  gap: '10px',
                  pointerEvents: 'none',
                  animation: 'drag-hint-pulse 1.5s infinite',
                }}
              >
                <div style={{ width: '8px', height: '8px', borderRadius: '50%', background: 'var(--color-accent)' }}></div>
                <span style={{ fontWeight: 600, fontSize: '0.85rem' }}>Drop output into the global store</span>
              </div>
            )}

            {/* Connection feedback: success pulse or a reason the drop failed */}
            {connectToast && (
              <div
                role={connectToast.kind === 'error' ? 'alert' : 'status'}
                style={{
                  position: 'absolute',
                  bottom: '30px',
                  left: '50%',
                  transform: 'translateX(-50%)',
                  background: connectToast.kind === 'error'
                    ? 'rgba(239, 68, 68, 0.14)'
                    : 'rgba(52, 211, 153, 0.12)',
                  border: connectToast.kind === 'error'
                    ? '1px solid rgba(239, 68, 68, 0.45)'
                    : '1px solid rgba(52, 211, 153, 0.4)',
                  color: connectToast.kind === 'error' ? '#fecaca' : '#b6f3d9',
                  fontSize: '0.82rem',
                  fontWeight: 600,
                  padding: '9px 18px',
                  borderRadius: '999px',
                  zIndex: 1000,
                  pointerEvents: 'none',
                  maxWidth: '420px',
                  textAlign: 'center',
                }}
              >
                {connectToast.kind === 'error' ? `⚠ ${connectToast.message}` : connectToast.message}
              </div>
            )}

            {/* Dockable diagnostics panel — click a row to locate it on the canvas (#9) */}
            <DiagnosticsPanel
              diagnostics={panelDiagnostics}
              collapsed={diagnosticsCollapsed}
              onToggleCollapse={() => setDiagnosticsCollapsed((c) => !c)}
              onFocus={focusDiagnostic}
            />
          </div>
        )}
      </div>

      {/* Split Sidebar (Right side) */}
      <div style={{
        width: '380px',
        display: 'flex',
        flexDirection: 'column',
        height: '100%',
        background: 'rgba(16, 22, 37, 0.4)',
        backdropFilter: 'blur(10px)',
        borderLeft: '1px solid var(--border-color)',
      }}>
        {/* Top: Properties Panel */}
        <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', borderBottom: '1px solid var(--border-color)' }}>
          <PropertiesPanel
            workflowId={currentId || null}
            selectedNode={selectedNode}
            selectedEdge={selectedEdge}
            referenceGroups={upstreamRefGroups}
            onUpdateNodeProperties={onUpdateNodeProperties}
            onDeleteNode={onDeleteNode}
            onDeleteEdge={onDeleteEdge}
          />
        </div>
        {/* Bottom: Variables Panel */}
        <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          {currentId && <VariablesPanel workflowId={currentId} />}
        </div>
      </div>

      {/* OpenAPI import modal — triggered from the node palette */}
      <CanvasImportModal
        open={showOpenApiImportModal}
        onClose={() => setShowOpenApiImportModal(false)}
        onImported={() => { api.getNodePackages().then(setAvailableNodes).catch(console.error); }}
      />
    </div>
  );
}

// Wrap CanvasInner in ReactFlowProvider to avoid react flow state boundary errors
export function Canvas(props: CanvasProps) {
  return (
    <ReactFlowProvider>
      <CanvasInner {...props} />
    </ReactFlowProvider>
  );
}
