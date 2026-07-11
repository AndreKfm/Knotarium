// The Condition node's properties-panel field (Phase 3, slice 2b — minimal entry; the richer
// read-only summary is Phase 4). Replaces the generic ManifestForm rendering of the condition node's
// raw `logic`/`left`/`operator`/`right` params with a one-line status + an "Edit logic" button that
// opens the full-screen ConditionEditorView. On Save it writes the typed `logic` object onto the node
// and strips the legacy `left`/`operator`/`right` fields (the FIX migration step). The editor is
// mounted here, inside the selected node's panel, so deleting the node unmounts it (the
// node-deleted-while-open guard comes for free).

import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { GitBranch, Pencil } from 'lucide-react';
import { useVariableStore, type VariableRecord } from '../../stores/useVariableStore';
import { useConditionEditorOpenStore } from '../../stores/useConditionEditorOpenStore';
import { useSignalFieldStore, signalGroupsFor } from '../../stores/useSignalFieldStore';
import { variableRefExpression } from '../../utils/variableExpression';
import { api } from '../../utils/api';
import { ConditionTreeEditorView, type ConditionLastRunInfo } from '../../node-editor/condition/ConditionTreeEditorView';
import type { RefOption } from '../../node-editor/condition/InputEditor';
import type { OperandType } from '../../node-editor/condition/conditionEval';
import type { ConditionLogic, LegacyCondition } from '../../node-editor/condition/conditionModel';
import { collectTreeRefs, type ConditionLogicTree } from '../../node-editor/condition/conditionTree';
import { summarizeLogic, summarizeTree } from '../../node-editor/condition/conditionSummary';

/** Persisted logic on a node may be v1 (flat) or v2 (tree) — both round-trip through the editor. */
type AnyLogic = ConditionLogic | ConditionLogicTree;
const isTree = (l: AnyLogic): l is ConditionLogicTree => l.version === 2;

interface ConditionLogicFieldProps {
  workflowId?: string | null;
  nodeId?: string | null;
  properties: Record<string, unknown>;
  onChange: (properties: Record<string, unknown>) => void;
}

// Variable store types are string|number|boolean|object; the operand declared type is the scalar set,
// so object refs seed as 'string' (the author can retype the operand in the editor).
function toOperandType(t: VariableRecord['type']): OperandType {
  return t === 'number' || t === 'boolean' ? t : 'string';
}

/** Read the persisted logic (v1 or v2), tolerating both a stored object and a stringified-JSON shape. */
function readLogic(raw: unknown): AnyLogic | null {
  if (raw && typeof raw === 'object') return raw as AnyLogic;
  if (typeof raw === 'string' && raw.trim().length > 0) {
    try {
      const parsed = JSON.parse(raw);
      return parsed && typeof parsed === 'object' ? (parsed as AnyLogic) : null;
    } catch {
      return null;
    }
  }
  return null;
}

// A legacy operand may be a plain expression string or a dropped variable_ref token; turn the latter
// into its read expression so the best-effort migration seed resolves it as a reference.
function legacyValue(raw: unknown, variables: VariableRecord[]): unknown {
  if (raw && typeof raw === 'object' && (raw as { __type?: string }).__type === 'variable_ref') {
    const id = (raw as { variableId?: string }).variableId;
    const v = variables.find((x) => x.id === id);
    return v ? variableRefExpression(v) : '';
  }
  return raw;
}

const chipStyle: React.CSSProperties = {
  fontFamily: 'monospace',
  fontSize: '0.78rem',
  color: '#fff',
  background: 'rgba(0, 0, 0, 0.25)',
  border: '1px solid var(--border-color)',
  borderRadius: '5px',
  padding: '1px 6px',
  maxWidth: '100%',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const opStyle: React.CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  minWidth: '20px',
  height: '20px',
  borderRadius: '5px',
  background: 'rgba(240, 180, 41, 0.18)',
  color: '#f0b429',
  fontWeight: 800,
  flex: '0 0 auto',
};

// Every operand reference the editor might want last-run values for: the logic graph's `ref` operands
// plus any legacy left/right that's an expression. Used to ask the backend for resolved last-run values.
function collectRefs(logic: AnyLogic | null, legacy: LegacyCondition): string[] {
  const refs = new Set<string>();
  if (logic) {
    if (isTree(logic)) {
      for (const r of collectTreeRefs(logic.root)) refs.add(r);
    } else {
      for (const c of logic.cmps) {
        for (const op of [c.a, c.b]) {
          if (op && op.kind === 'ref' && op.ref.trim()) refs.add(op.ref.trim());
        }
      }
    }
  }
  for (const v of [legacy.left, legacy.right]) {
    if (typeof v === 'string' && v.includes('{{')) refs.add(v.trim());
  }
  return [...refs];
}

export function ConditionLogicField({ workflowId, nodeId, properties, onChange }: ConditionLogicFieldProps) {
  const [open, setOpen] = useState(false);
  const variables = useVariableStore((s) => (workflowId ? s.variables[workflowId] || [] : []));

  // Bridge: the canvas requests an open by node id (double-click on a Condition node). Consumed below
  // once lastRun's setter is in scope.
  const editorRequestNodeId = useConditionEditorOpenStore((s) => s.requestNodeId);
  const clearEditorRequest = useConditionEditorOpenStore((s) => s.clearRequest);

  // Inbound-signal fields scoped to THIS node (the action whose signal can reach it). Offered as
  // reference operands so a picked field becomes a resolving `ref` ({{ $variables.signal.params.<key> }}),
  // not a literal. Kept out of the canvas-wide variable store on purpose — they belong to the action
  // instance, not every node — so they're merged in here rather than registered globally.
  const signalGroups = useSignalFieldStore((s) => signalGroupsFor(s, nodeId));

  const refOptions = useMemo<RefOption[]>(
    () => {
      const fromVariables = variables.map((v) => ({
        id: v.id,
        label: v.name,
        type: toOperandType(v.type),
        ref: variableRefExpression(v),
      }));
      const fromSignal = signalGroups.flatMap((group) =>
        group.fields.map((field) => ({
          id: `__signal:${field.key}`,
          // Action-scoped label ("Custom Action › String") + a ref under the per-run signal namespace —
          // `signal.customAction.String` for actions (a payload alias nested in `signal`), or
          // `signal.params.<key>` for events. Resolves via the dotted variable path; reads clearly local.
          label: `${group.label} › ${field.key}`,
          type: toOperandType(field.type),
          ref: `${group.refPrefix}.${field.key}`,
        })),
      );
      return [...fromVariables, ...fromSignal];
    },
    [variables, signalGroups],
  );

  // Seed the live preview's manual samples from any already-resolved variable values.
  const sampleValues = useMemo(() => {
    const out: Record<string, unknown> = {};
    for (const v of variables) {
      if (v.value !== undefined) out[variableRefExpression(v)] = v.value;
    }
    return out;
  }, [variables]);

  const logic = readLogic(properties.logic);
  const legacy: LegacyCondition = {
    left: legacyValue(properties.left, variables),
    operator: properties.operator,
    right: legacyValue(properties.right, variables),
  };
  const hasLegacy = legacy.operator !== undefined && legacy.operator !== null && legacy.operator !== '';

  // v1 renders operand·op·operand rows; v2 renders a one-line parenthesized expression.
  const summaryRows = logic && !isTree(logic) ? summarizeLogic(logic) : null;
  const summaryText = logic && isTree(logic) ? summarizeTree(logic.root) : null;
  const emptyText = hasLegacy ? 'Legacy condition — open to migrate' : 'Not configured';

  // Resolve the operand refs against the workflow's last run while the editor is open (the "Last run"
  // value source). Memoized on the raw properties so it's stable across the editor's own re-renders
  // (its draft is local until Save) — the fetch fires on open, not on every keystroke.
  const refs = useMemo(
    () =>
      collectRefs(readLogic(properties.logic), {
        left: properties.left,
        operator: properties.operator,
        right: properties.right,
      }),
    [properties.logic, properties.left, properties.operator, properties.right],
  );
  const [lastRun, setLastRun] = useState<ConditionLastRunInfo | null>(null);
  useEffect(() => {
    if (!open || !workflowId || refs.length === 0) return;
    let cancelled = false;
    api
      .getConditionLastRunValues(workflowId, refs)
      .then((res) => {
        if (!cancelled) setLastRun(res.runId ? { createdAt: res.createdAt, stale: res.stale, values: res.values } : null);
      })
      .catch(() => {
        if (!cancelled) setLastRun(null);
      });
    return () => {
      cancelled = true;
    };
  }, [open, workflowId, refs]);

  // Open the editor when the canvas requests it (double-click). Addressed by node id, so only the
  // field for the selected (and matching) node opens.
  useEffect(() => {
    if (!editorRequestNodeId || editorRequestNodeId !== nodeId) return;
    setLastRun(null); // start each session fresh; the open effect refetches
    setOpen(true);
    clearEditorRequest();
  }, [editorRequestNodeId, nodeId, clearEditorRequest]);

  const handleSave = (next: ConditionLogicTree) => {
    // Write the typed v2 logic and remove the legacy operands (migration is one-way on Save).
    const rest = { ...properties };
    delete rest.left;
    delete rest.operator;
    delete rest.right;
    onChange({ ...rest, logic: next });
    setOpen(false);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '10px' }}>
        <label style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
          <GitBranch size={14} style={{ color: '#f0b429' }} /> Condition logic
        </label>
        <button
          type="button"
          onClick={() => {
            setLastRun(null); // start each session fresh; the open effect refetches
            setOpen(true);
          }}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '6px',
            background: 'transparent',
            border: '1px solid var(--border-color)',
            borderRadius: '6px',
            color: 'var(--text-secondary)',
            fontSize: '0.72rem',
            fontWeight: 600,
            padding: '5px 10px',
            cursor: 'pointer',
            flex: '0 0 auto',
          }}
          title="Open the full-screen condition editor"
        >
          <Pencil size={12} /> Edit logic
        </button>
      </div>

      <div
        role="button"
        tabIndex={0}
        aria-label="Open the full-screen condition editor"
        onClick={() => {
          setLastRun(null);
          setOpen(true);
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            setLastRun(null);
            setOpen(true);
          }
        }}
        title="Open the full-screen condition editor"
        style={{
          display: 'flex',
          flexDirection: 'column',
          gap: '6px',
          padding: '12px 14px',
          borderRadius: '8px',
          background: 'rgba(255, 255, 255, 0.03)',
          border: '1px solid var(--border-color)',
          cursor: 'pointer',
        }}
      >
        {summaryText !== null ? (
          <code style={{ ...chipStyle, whiteSpace: 'normal', maxWidth: '100%' }}>{summaryText}</code>
        ) : summaryRows ? (
          summaryRows.rows.map((row, i) => (
            <div key={row.id} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
              {i > 0 && (
                <span style={{ fontSize: '0.6rem', fontWeight: 800, letterSpacing: '0.05em', color: '#a99bff' }}>
                  {summaryRows.comb.toUpperCase()}
                </span>
              )}
              <div style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: '6px', fontSize: '0.8rem' }}>
                <code style={chipStyle}>{row.a}</code>
                <span title={row.opLabel} style={opStyle}>{row.symbol}</span>
                {row.b !== null && <code style={chipStyle}>{row.b}</code>}
              </div>
            </div>
          ))
        ) : (
          <span style={{ fontSize: '0.82rem', color: 'var(--text-muted)' }}>{emptyText}</span>
        )}
      </div>

      {open &&
        // Portal to <body> so the fixed overlay escapes the right sidebar's `backdrop-filter`
        // containing block (otherwise `inset: 0` resolves against the 380px panel, not the viewport).
        createPortal(
          <div style={{ position: 'fixed', inset: 0, zIndex: 1000 }}>
            <ConditionTreeEditorView
              initialLogic={logic}
              initialLegacy={logic ? null : legacy}
              variables={refOptions}
              sampleValues={sampleValues}
              lastRun={lastRun}
              onSave={handleSave}
              onCancel={() => setOpen(false)}
            />
          </div>,
          document.body,
        )}
    </div>
  );
}
