// Full-screen v2 logic editor — auto-laid-out dataflow FLOW (Phase 8 redesign, user-chosen over nested
// boxes). The draft tree is built into a @xyflow/react graph (inputs → comparators → groups/NOTs →
// TRUE/FALSE output) by conditionFlowTree + Dagre; every edit re-resolves and re-routes synchronously.
// Save is gated on a fully-valid tree (coerceTreeToLogic). Opens BOTH v1 and v2 logic; saves v2.

import { useEffect, useMemo, useRef, useState } from 'react';
import {
  Background,
  BackgroundVariant,
  ReactFlow,
  ReactFlowProvider,
  useReactFlow,
  type Edge as RFEdge,
  type Node as RFNode,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { ArrowLeft, GitBranch, Plus, Save } from 'lucide-react';
import { type ConditionError, type ConditionStatus } from './conditionEval';
import { legacyToDraft, type ConditionLogic as ConditionLogicV1, type LegacyCondition } from './conditionModel';
import {
  addComparator,
  addGroup,
  coerceTreeToLogic,
  emptyTree,
  logicToTree,
  removeNode,
  setGroupOp,
  setLeafOperand,
  setLeafOperator,
  unwrap,
  wrapInGroup,
  wrapInNot,
  type ConditionLogicTree,
  type DraftTree,
} from './conditionTree';
import { evaluateDraftTree, type PreviewValueProvider } from './conditionPreview';
import { buildConditionTreeFlow, relayoutFlow, type ConditionTreeFlow, type FlowEdge, type OutputNodeData, type ComparatorNodeData, type InputNodeData } from './conditionFlowTree';
import { conditionFlowNodeTypes } from './conditionFlowNodeTypes';
import { ConditionTreeContext, type TreeInputTarget } from './conditionTreeContext';
import { presetSamples } from './conditionTestPresets';
import type { RefOption } from './InputEditor';
import { CONDITION_EDITOR_CSS } from './conditionEditorCss';
import { CONDITION_TREE_CSS } from './conditionTreeCss';

export interface ConditionLastRunInfo {
  createdAt: string | null;
  stale: boolean;
  values: Record<string, { found: boolean; value?: unknown }>;
}

export interface ConditionTreeEditorViewProps {
  initialLogic?: ConditionLogicV1 | ConditionLogicTree | null;
  initialLegacy?: LegacyCondition | null;
  sampleValues?: Record<string, unknown>;
  lastRun?: ConditionLastRunInfo | null;
  variables?: RefOption[];
  onSave: (logic: ConditionLogicTree) => void;
  onCancel: () => void;
  title?: string;
}

function initialDraft(props: ConditionTreeEditorViewProps): DraftTree {
  if (props.initialLogic) return logicToTree(props.initialLogic);
  if (props.initialLegacy) {
    const seed = legacyToDraft(props.initialLegacy).draft;
    if (seed) return { root: { kind: 'cmp', ...seed.cmps[0] } };
  }
  return emptyTree();
}

function booleanWireStyle(status: FlowEdge['status']): React.CSSProperties {
  switch (status) {
    case 'true':
      return { stroke: '#34d399', strokeWidth: 2.4, filter: 'drop-shadow(0 0 5px rgba(52,211,153,0.5))' };
    case 'false':
      // A clear (but muted) red so the wire actually carries the "did not pass" result — the old near-black
      // maroon vanished, especially on low-gamma screens. Kept dimmer/glow-less so it stays distinct from
      // the brighter 'error' red (a false result is normal; an error is not).
      return { stroke: '#b3445a', strokeWidth: 2.4 };
    case 'error':
      return { stroke: '#f0556d', strokeWidth: 2.4, filter: 'drop-shadow(0 0 5px rgba(240,85,109,0.5))' };
    case 'awaiting':
      // Calm "pending runtime value" look — a soft blue-grey dotted line, distinct from the dashed
      // grey of a genuinely-unwired 'incomplete'. Reads as "will resolve at runtime", not "broken".
      return { stroke: '#3f5168', strokeWidth: 2.2, strokeDasharray: '1 5', strokeLinecap: 'round' };
    default:
      return { stroke: '#2c3a4d', strokeWidth: 2.4, strokeDasharray: '5 5' }; // incomplete
  }
}
function edgeStyle(e: FlowEdge): React.CSSProperties {
  if (e.wire === 'value') return { stroke: `${e.typeColor ?? '#8593a6'}66`, strokeWidth: 2.4 };
  return booleanWireStyle(e.status);
}

// Breathing-room margin (px) kept between the graph and the canvas edges when framing.
const FIT_MARGIN = 56;

// The React Flow canvas, inside the provider so its hooks work. Once the nodes render we re-run the Dagre
// layout with their REAL sizes (relayoutFlow) and override the positions — so centers align and wires
// stay straight no matter what a card renders to. We then frame the graph ourselves with setViewport.
//
// Why not React Flow's own measurement + fitView? These fully-controlled, non-draggable custom nodes are
// never auto-measured by RF (`node.measured` stays undefined, `useNodesInitialized` stays false forever),
// AND we don't pass width/height (that would force the wrapper's box and clip the cards). With no
// dimensions RF treats every node as 0×0, so its bounds/`fitView` frame the node POSITIONS as bare points
// — the graph lands mis-centered and the AND group / output card end up off-center. So we instead measure
// real sizes straight off the DOM (`offsetWidth/offsetHeight` ignore the viewport's CSS transform, so
// they're already in flow space), re-run Dagre with them, and compute the fit viewport from the true
// bounds via getViewportForBounds. An rAF retry loop waits for the cards to have a real height first.
function FlowCanvas({ flow, baseNodes, edges }: { flow: ConditionTreeFlow; baseNodes: RFNode[]; edges: RFEdge[] }) {
  const { setViewport } = useReactFlow();
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const [override, setOverride] = useState<Record<string, { x: number; y: number }>>({});
  const framedStructureRef = useRef<string | null>(null);

  // Identity of the node set; changing it (add/remove/restructure) re-triggers measure + relayout. The
  // override map is keyed by node id and only read for nodes that currently exist, so stale entries for
  // removed nodes are harmless — no separate reset needed.
  const structureKey = baseNodes.map((n) => n.id).join('|');

  useEffect(() => {
    let raf = 0;
    let tries = 0;
    const measureAndLayout = () => {
      const root = wrapRef.current;
      const els = root ? [...root.querySelectorAll<HTMLElement>('.react-flow__node')] : [];
      const measured = new Map<string, { width: number; height: number }>();
      let anyZero = els.length === 0;
      for (const el of els) {
        const id = el.getAttribute('data-id');
        if (!id) continue;
        const width = el.offsetWidth;
        const height = el.offsetHeight; // transform-independent, already in flow space
        if (height <= 0) anyZero = true;
        measured.set(id, { width, height });
      }
      // Cards not painted/sized yet — retry on the next frame (bounded, so a genuinely empty graph stops).
      if (anyZero && tries++ < 30) {
        raf = requestAnimationFrame(measureAndLayout);
        return;
      }
      if (measured.size === 0) return;
      const relaid = relayoutFlow(flow, measured);
      const next = Object.fromEntries(relaid.nodes.map((n) => [n.id, { x: Math.round(n.x), y: Math.round(n.y) }]));
      // Only commit when positions actually moved. Typing into a test-mode value field re-runs this effect
      // (the draft/provider changed) but doesn't change any card's SIZE, so the layout is identical — and a
      // no-op setOverride would still re-render the whole flow, which steals focus from the field mid-edit.
      setOverride((prev) => {
        const ids = Object.keys(next);
        const same =
          ids.length === Object.keys(prev).length &&
          ids.every((id) => prev[id] && prev[id].x === next[id].x && prev[id].y === next[id].y);
        return same ? prev : next;
      });

      // Frame the graph ourselves from the true bounds. Only on a structure change (add/remove/restructure)
      // — never on a plain operand edit — so typing a value doesn't yank the viewport.
      if (framedStructureRef.current !== structureKey && root) {
        framedStructureRef.current = structureKey;
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const n of relaid.nodes) {
          minX = Math.min(minX, n.x); minY = Math.min(minY, n.y);
          maxX = Math.max(maxX, n.x + n.width); maxY = Math.max(maxY, n.y + n.height);
        }
        const boundsW = maxX - minX;
        const boundsH = maxY - minY;
        const rect = root.getBoundingClientRect();

        // The toolbar + summary/test bar float over the TOP of the board, so the genuinely visible canvas
        // is the band below them. Frame the graph centred in THAT band (not the full board) — otherwise it
        // reads as floating high with dead space underneath. Covered band is measured live so it adapts to
        // whichever bars are present (summary vs test bar vs neither).
        let coverBottom = rect.top;
        for (const sel of ['.cne-toolbar', '.cne-summary', '.cne-testbar']) {
          const bar = document.querySelector(sel);
          if (bar) coverBottom = Math.max(coverBottom, bar.getBoundingClientRect().bottom);
        }
        const coverBand = Math.max(0, coverBottom - rect.top);

        const zoom = Math.max(
          0.2,
          Math.min(1.25, (rect.width - 2 * FIT_MARGIN) / boundsW, (rect.height - coverBand - 2 * FIT_MARGIN) / boundsH),
        );
        const vp = {
          x: rect.width / 2 - (minX + boundsW / 2) * zoom,
          y: (coverBand + rect.height) / 2 - (minY + boundsH / 2) * zoom,
          zoom,
        };
        setViewport(vp, { duration: 200 });
      }
    };
    raf = requestAnimationFrame(measureAndLayout);
    return () => cancelAnimationFrame(raf);
  }, [structureKey, flow, setViewport]);

  const nodes = baseNodes.map((n) => (override[n.id] ? { ...n, position: override[n.id] } : n));

  return (
    <div ref={wrapRef} style={{ width: '100%', height: '100%' }}>
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={conditionFlowNodeTypes}
        proOptions={{ hideAttribution: true }}
        nodesDraggable={false}
        nodesConnectable={false}
        elementsSelectable={false}
        panOnDrag
        zoomOnScroll
      >
        <Background variant={BackgroundVariant.Dots} gap={28} size={1} color="#161f2c" />
      </ReactFlow>
    </div>
  );
}

const OUTPUT_PILL: Record<ConditionStatus, { text: string; cls: string }> = {
  True: { text: 'TRUE', cls: 'cne-pill-true' },
  False: { text: 'FALSE', cls: 'cne-pill-false' },
  Incomplete: { text: 'incomplete', cls: 'cne-pill-neutral' },
  Error: { text: 'error', cls: 'cne-pill-error' },
};

export function ConditionTreeEditorView(props: ConditionTreeEditorViewProps) {
  const { onSave, onCancel, variables = [], lastRun, title = 'Condition logic' } = props;
  const [draft, setDraft] = useState<DraftTree>(() => initialDraft(props));
  const [sampleValues, setSampleValues] = useState<Record<string, unknown>>(() => ({ ...(props.sampleValues ?? {}) }));
  const [openOperatorFor, setOpenOperator] = useState<string | null>(null);
  const [openInputFor, setOpenInput] = useState<TreeInputTarget | null>(null);

  const hasLastRun = !!lastRun && Object.keys(lastRun.values).length > 0;
  const [valueSource, setValueSource] = useState<'lastRun' | 'manual'>(hasLastRun ? 'lastRun' : 'manual');
  // Test mode: type temporary signal values and watch the condition evaluate. Reuses the manual-sample
  // machinery; exiting clears the samples. Never touches the saved config.
  const [testMode, setTestMode] = useState(false);
  const exitTest = () => { setTestMode(false); setSampleValues({}); };

  const [initialSnapshot] = useState(() => JSON.stringify(initialDraft(props)));
  const dirty = JSON.stringify(draft) !== initialSnapshot;

  const provider = useMemo<PreviewValueProvider>(() => {
    // Test mode always reads the typed samples (manual), regardless of the last-run/manual toggle.
    if (!testMode && valueSource === 'lastRun' && lastRun) {
      return (ref) => {
        const hit = lastRun.values[ref];
        if (hit?.found) return { found: true, value: hit.value };
        return { found: false, authoritativeMiss: true };
      };
    }
    return (ref) =>
      Object.prototype.hasOwnProperty.call(sampleValues, ref) ? { found: true, value: sampleValues[ref] } : { found: false };
  }, [testMode, valueSource, lastRun, sampleValues]);

  const addTopLevel = () =>
    setDraft((d) => {
      if (!d.root) return addComparator(d, null);
      if (d.root.kind === 'group') return addComparator(d, d.root.id);
      const wrapped = wrapInGroup(d, d.root.id, 'and');
      return addComparator(wrapped, wrapped.root!.id);
    });

  const handlers = useMemo(
    () => ({
      onPickOperator: (id: string, op: string) => setDraft((d) => setLeafOperator(d, id, op)),
      onChangeOperand: (id: string, slot: 'a' | 'b', operand: Parameters<typeof setLeafOperand>[3]) =>
        setDraft((d) => setLeafOperand(d, id, slot, operand)),
      onChangeSample: (ref: string, value: unknown) => setSampleValues((s) => ({ ...s, [ref]: value })),
      onAddComparator: (groupId: string) => setDraft((d) => addComparator(d, groupId)),
      onAddGroup: (groupId: string) => setDraft((d) => addGroup(d, groupId)),
      onWrapGroup: (nodeId: string) => setDraft((d) => wrapInGroup(d, nodeId)),
      onWrapNot: (nodeId: string) => setDraft((d) => wrapInNot(d, nodeId)),
      onSetGroupOp: (groupId: string, op: 'and' | 'or') => setDraft((d) => setGroupOp(d, groupId, op)),
      onRemove: (nodeId: string) => setDraft((d) => removeNode(d, nodeId)),
      onUnwrap: (nodeId: string) => setDraft((d) => unwrap(d, nodeId)),
    }),
    [],
  );

  const requestCancel = () => {
    if (!dirty || window.confirm('Discard unsaved changes to this condition?')) onCancel();
  };

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      if (openOperatorFor !== null || openInputFor !== null) return;
      e.stopPropagation();
      requestCancel();
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  });

  const { flow, outcome, leafStatus, leafError, canSave, logic } = useMemo(() => {
    const flow = buildConditionTreeFlow(draft, provider);
    const outcome = evaluateDraftTree(draft, provider);
    const leafStatus: Record<string, ConditionStatus> = {};
    const leafError: Record<string, ConditionError | null> = {};
    for (const r of outcome.comparators) {
      leafStatus[r.comparatorId] = r.status;
      leafError[r.comparatorId] = r.error;
    }
    const { logic } = coerceTreeToLogic(draft);
    return { flow, outcome, leafStatus, leafError, canSave: logic !== null, logic };
  }, [draft, provider]);

  const rfNodes: RFNode[] = useMemo(
    () =>
      flow.nodes.map((n) => {
        // Lift the node whose popover is open above its neighbours so the menu isn't hidden behind them.
        const editing =
          (n.kind === 'comparator' && openOperatorFor === (n.data as { cmpId?: string }).cmpId) ||
          (n.kind === 'input' &&
            openInputFor?.nodeId === (n.data as { cmpId?: string }).cmpId &&
            openInputFor?.slot === (n.data as { slot?: 'a' | 'b' }).slot);
        return {
          id: n.id,
          type: n.kind,
          position: { x: n.x, y: n.y },
          data: n.data as unknown as Record<string, unknown>,
          draggable: false,
          selectable: false,
          connectable: false,
          zIndex: editing ? 1000 : undefined,
        };
      }),
    [flow.nodes, openOperatorFor, openInputFor],
  );
  const rfEdges: RFEdge[] = useMemo(
    () =>
      flow.edges.map((e) => ({
        id: e.id,
        source: e.source,
        target: e.target,
        style: edgeStyle(e),
        label: e.label ?? undefined,
        labelBgPadding: [5, 2] as [number, number],
        labelBgBorderRadius: 5,
        // SVG label: dark pill on the board, not React Flow's default white.
        labelBgStyle: { fill: '#0e1622', fillOpacity: 0.92, stroke: '#22304333', strokeWidth: 1 },
        labelStyle: { fill: '#9fb0c3', fontSize: 11, fontFamily: 'ui-monospace, monospace' },
      })),
    [flow.edges],
  );

  // When the whole condition is Incomplete only because it references runtime fields with no design-time
  // value, present the output as a calm "runtime" pill rather than the alarming "incomplete" — the
  // condition is valid and evaluates at run time. (Reuses the neutral pill styling; only the word changes.)
  const outputAwaiting = (flow.nodes.find((n) => n.kind === 'output')?.data as OutputNodeData | undefined)?.awaiting ?? false;
  const pill = outputAwaiting ? { text: 'runtime', cls: 'cne-pill-neutral' } : OUTPUT_PILL[outcome.status];

  // Plain-language summary strip: one labelled pill per comparator showing its expression + live
  // pass/fail, plus the top-level combinator. Letters follow the VISUAL top-to-bottom order (sorted by
  // laid-out y), NOT tree order, so "A" is the topmost comparator in the graph — otherwise the labels
  // didn't line up with what's drawn. The test-mode presets reuse this same ordering (via cmpId).
  const summaryPills = useMemo(() => {
    const short = (inp?: InputNodeData) =>
      !inp ? '?' : inp.variant === 'ref' ? (inp.label.split('.').pop() || inp.label) : inp.label;
    return flow.nodes
      .filter((n) => n.kind === 'comparator')
      .slice()
      .sort((a, b) => a.y - b.y)
      .map((n, i) => {
        const d = n.data as ComparatorNodeData;
        const a = flow.nodes.find((x) => x.id === `in:${d.cmpId}:a`)?.data as InputNodeData | undefined;
        const b = flow.nodes.find((x) => x.id === `in:${d.cmpId}:b`)?.data as InputNodeData | undefined;
        const expr = b ? `${short(a)} ${d.symbol} ${short(b)}` : `${short(a)} ${d.label}`;
        const tone = d.status === 'True' ? 'pass' : d.status === 'False' ? 'fail' : d.status === 'Error' ? 'error' : d.awaiting ? 'runtime' : 'neutral';
        const icon = tone === 'pass' ? '✓' : tone === 'fail' ? '✗' : tone === 'error' ? '!' : '•';
        return { key: n.id, cmpId: d.cmpId, letter: String.fromCharCode(65 + i), expr, tone, icon };
      });
  }, [flow.nodes]);
  const rootOp = draft.root?.kind === 'group' ? draft.root.op : null;

  return (
    <div className="cne-root">
      <style>{CONDITION_EDITOR_CSS}</style>
      <style>{CONDITION_TREE_CSS}</style>

      <div className="cne-topbar">
        <button type="button" className="cne-back" onClick={requestCancel}>
          <ArrowLeft size={16} /> Back
        </button>
        <div className="cne-topbar-title">
          <GitBranch size={14} className="cne-amber" /> Condition
        </div>
        <button
          type="button"
          className="cne-save"
          disabled={!canSave}
          title={canSave ? 'Save & Publish' : 'Complete every operand to save'}
          onClick={() => logic && onSave(logic)}
        >
          <Save size={14} /> Save &amp; Publish
        </button>
      </div>

      <div className="cne-board">
        <div className="cne-toolbar">
          <div className="cne-toolbar-left">
            <GitBranch size={14} className="cne-amber" />
            <span className="cne-toolbar-title">{title}</span>
            <span className="cne-hint">Group with AND/OR, negate with NOT — the result flows right →</span>
          </div>
          <div className="cne-toolbar-right">
            {hasLastRun && lastRun && (
              <div className="cne-source" role="group" aria-label="Value source">
                <div className="cne-segment">
                  <button type="button" className={valueSource === 'lastRun' ? 'cne-seg-on' : ''} aria-pressed={valueSource === 'lastRun'} onClick={() => setValueSource('lastRun')}>
                    Last run
                  </button>
                  <button type="button" className={valueSource === 'manual' ? 'cne-seg-on' : ''} aria-pressed={valueSource === 'manual'} onClick={() => setValueSource('manual')}>
                    Manual
                  </button>
                </div>
                {valueSource === 'lastRun' && (
                  <span className="cne-source-meta">
                    {lastRun.createdAt ? `from ${new Date(lastRun.createdAt).toLocaleString()}` : 'last run'}
                    {lastRun.stale && (
                      <span className="cne-stale" title="This run came from a different version">
                        stale
                      </span>
                    )}
                  </span>
                )}
              </div>
            )}
            <button type="button" className="cne-add" onClick={addTopLevel}>
              <Plus size={14} /> Add condition
            </button>
            {!testMode && (
              <button type="button" className="cne-test-enter" onClick={() => setTestMode(true)} title="Type temporary signal values and watch it evaluate">
                ▶ Test
              </button>
            )}
            <span className={`cne-pill ${pill.cls}`} aria-label="output">
              output: <strong>{pill.text}</strong>
            </span>
          </div>
        </div>

        {testMode && (
          <div className="cne-testbar" aria-label="Test run">
            <div className="cne-testbar-main">
              <div className="cne-testbar-row">
                <span className="cne-test-dot" />
                <span className="cne-test-title">TEST RUN</span>
                <span className="cne-test-sub">values are temporary · config unchanged</span>
              </div>
              <div className="cne-test-presets">
                <span className="cne-summary-k">PRESETS</span>
                <button type="button" onClick={() => setSampleValues(presetSamples(draft, { kind: 'allPass' }))}>both match ✓</button>
                {summaryPills.map((p) => (
                  <button key={p.cmpId} type="button" onClick={() => setSampleValues(presetSamples(draft, { kind: 'failOne', id: p.cmpId }))}>
                    {p.letter} fails
                  </button>
                ))}
                <button type="button" onClick={() => setSampleValues(presetSamples(draft, { kind: 'allFail' }))}>both fail ✗</button>
                <button type="button" className="cne-test-clear" onClick={() => setSampleValues({})}>clear</button>
              </div>
            </div>
            <button type="button" className="cne-test-exit" onClick={exitTest}>✕ Exit test</button>
          </div>
        )}

        {!testMode && summaryPills.length > 0 && (
          <div className="cne-summary" aria-label="Condition summary">
            <span className="cne-summary-k">CONDITIONS</span>
            <div className="cne-summary-pills">
              {summaryPills.map((p) => (
                <span key={p.key} className={`cne-summary-pill cne-sp-${p.tone}`} title={`${p.letter}: ${p.expr}`}>
                  <span className="cne-sp-letter">{p.letter}</span>
                  <span className="cne-sp-expr">{p.expr}</span>
                  <span className="cne-sp-icon">{p.icon}</span>
                </span>
              ))}
            </div>
            {rootOp && (
              <>
                <span className="cne-summary-sep" />
                <span className="cne-summary-k">OPERATOR</span>
                <span className={`cne-summary-op cne-op-${rootOp}`}>{rootOp.toUpperCase()}</span>
              </>
            )}
          </div>
        )}

        <ConditionTreeContext.Provider
          value={{
            ...handlers,
            openOperatorFor,
            openInputFor,
            setOpenOperator,
            setOpenInput,
            variables,
            sampleValues,
            testMode,
            leafStatus,
            leafError,
          }}
        >
          <ReactFlowProvider>
            <FlowCanvas flow={flow} baseNodes={rfNodes} edges={rfEdges} />
          </ReactFlowProvider>
        </ConditionTreeContext.Provider>
      </div>
    </div>
  );
}
