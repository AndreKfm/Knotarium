// Custom @xyflow/react node components for the auto-laid-out flow editor (Phase 8 redesign). Reuses the
// original node visuals (amber comparator cards, type-colored input cards, TRUE/FALSE output) and adds
// GroupNode (AND/OR) + NotNode for nesting. All editing flows through ConditionTreeContext; DATA comes
// from conditionFlowTree. Data flows left→right, so every node sources on the right, targets on the left.

import { useContext } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import { ChevronDown, Pencil, Plus, X } from 'lucide-react';
import type { ConditionStatus } from './conditionEval';
import {
  refPath,
  type ComparatorNodeData,
  type GroupNodeData,
  type InputNodeData,
  type NotNodeData,
  type OutputNodeData,
} from './conditionFlowTree';
import { ConditionTreeContext } from './conditionTreeContext';
import { OperatorMenu } from './OperatorMenu';
import { InputEditor } from './InputEditor';

// `awaiting` = Incomplete only because a configured ref has no design-time value → show a calm "runtime"
// chip (reusing the neutral styling) instead of the ambiguous "—", so an imported condition that keys off
// runtime signal fields reads as healthy rather than broken.
function chipClass(status: ConditionStatus): string {
  return status === 'True' ? 'cne-chip cne-chip-true' : status === 'False' ? 'cne-chip cne-chip-false' : 'cne-chip cne-chip-neutral';
}
function chipText(status: ConditionStatus, awaiting?: boolean): string {
  if (status === 'True') return 'true';
  if (status === 'False') return 'false';
  if (status === 'Error') return 'error';
  return awaiting ? 'runtime' : '—';
}

export function InputNode({ data }: NodeProps) {
  const d = data as unknown as InputNodeData;
  const ctx = useContext(ConditionTreeContext);
  const open = ctx.openInputFor?.nodeId === d.cmpId && ctx.openInputFor?.slot === d.slot;
  const sample = d.operand.kind === 'ref' ? ctx.sampleValues[d.operand.ref] : undefined;
  // d.label is already the distinctive tail segment; surface the FULL ref path on hover so the dropped
  // boilerplate prefix (signal.params.…) is still discoverable.
  const fullTitle = d.operand.kind === 'ref' ? refPath(d.operand.ref) : d.label;

  // Test mode: a signal-ref operand becomes an inline editable field (type the value the incoming signal
  // would carry); a literal/threshold stays a fixed read-only chip. No popover — config isn't touched.
  if (ctx.testMode) {
    const isRef = d.operand.kind === 'ref';
    const refStr = d.operand.kind === 'ref' ? d.operand.ref : '';
    return (
      <div className="cne-input-wrap nodrag nopan">
        <div className={`cne-input cne-input-test ${isRef ? 'cne-input-sig' : 'cne-input-fixed'}`}>
          <span className="cne-diamond" style={{ background: d.typeColor }} aria-hidden />
          <span className="cne-input-label" title={fullTitle}>{d.label}</span>
          {isRef ? (
            <label className="cne-test-fieldwrap" title="Type a simulated signal value">
              <Pencil size={11} className="cne-test-ico" aria-hidden />
              <input
                className="cne-test-field nodrag"
                value={sample == null ? '' : String(sample)}
                placeholder="type value"
                aria-label={`Test value for ${d.label}`}
                onChange={(e) => ctx.onChangeSample(refStr, e.target.value)}
              />
            </label>
          ) : (
            <span className="cne-input-badge" style={{ color: d.typeColor }} title={d.badge}>{d.badge}</span>
          )}
        </div>
        <Handle type="source" position={Position.Right} />
      </div>
    );
  }

  return (
    <div className="cne-input-wrap nodrag nopan">
      <button
        type="button"
        className="cne-input"
        aria-label={`Edit input ${d.cmpId} ${d.slot}`}
        onClick={() => ctx.setOpenInput(open ? null : { nodeId: d.cmpId, slot: d.slot })}
      >
        <span className="cne-diamond" style={{ background: d.typeColor }} aria-hidden />
        <span className="cne-input-label" title={fullTitle}>
          {d.variant === 'lit' ? <span className="cne-lit-eq">=</span> : null}
          {d.label}
        </span>
        <span className="cne-input-badge" style={{ color: d.typeColor }} title={d.badge}>
          {d.badge}
        </span>
      </button>
      {open && (
        <InputEditor
          operand={d.operand}
          variables={ctx.variables}
          sampleValue={sample}
          isList={d.isList}
          onChangeOperand={(op) => ctx.onChangeOperand(d.cmpId, d.slot, op)}
          onChangeSample={(value) => {
            if (d.operand.kind === 'ref') ctx.onChangeSample(d.operand.ref, value);
          }}
          onClose={() => ctx.setOpenInput(null)}
        />
      )}
      <Handle type="source" position={Position.Right} />
    </div>
  );
}

export function ComparatorNode({ data }: NodeProps) {
  const d = data as unknown as ComparatorNodeData;
  const ctx = useContext(ConditionTreeContext);
  const open = ctx.openOperatorFor === d.cmpId;
  const error = d.status === 'Error' ? ctx.leafError[d.cmpId] : null;
  return (
    <div className="cne-cmp nodrag nopan">
      <Handle type="target" position={Position.Left} />
      <div className="cne-node-tools">
        <button type="button" title="Wrap in group" aria-label={`Wrap ${d.cmpId} in group`} onClick={() => ctx.onWrapGroup(d.cmpId)}>
          ( )
        </button>
        <button type="button" title="Wrap in NOT" aria-label={`Negate ${d.cmpId}`} onClick={() => ctx.onWrapNot(d.cmpId)}>
          NOT
        </button>
        <button type="button" title="Delete" aria-label={`Remove ${d.cmpId}`} onClick={() => ctx.onRemove(d.cmpId)}>
          <X size={11} />
        </button>
      </div>
      <button
        type="button"
        className="cne-op-pill"
        aria-label={`Operator: ${d.label}`}
        title={d.label}
        onClick={() => ctx.setOpenOperator(open ? null : d.cmpId)}
      >
        <span className="cne-op-symbol">{d.symbol}</span>
        <span className="cne-op-label">{d.label}</span>
        <ChevronDown size={12} className="cne-op-caret" />
      </button>
      <span className={chipClass(d.status)} title={error?.message ?? undefined}>
        {chipText(d.status, d.awaiting)}
      </span>
      {error?.message && (
        <div className="cne-cmp-error" title={error.message}>
          {error.message}
        </div>
      )}
      {open && (
        <OperatorMenu
          currentOp={d.op}
          leftType={d.leftType}
          rightType={d.rightType}
          onPick={(op) => {
            ctx.onPickOperator(d.cmpId, op);
            ctx.setOpenOperator(null);
          }}
          onClose={() => ctx.setOpenOperator(null)}
        />
      )}
      <Handle type="source" position={Position.Right} />
    </div>
  );
}

export function GroupNode({ data }: NodeProps) {
  const d = data as unknown as GroupNodeData;
  const ctx = useContext(ConditionTreeContext);
  return (
    <div className="cne-gnode nodrag nopan">
      <Handle type="target" position={Position.Left} />
      <div className="cne-gnode-tools">
        <button type="button" title="Unwrap group" aria-label={`Unwrap ${d.id}`} onClick={() => ctx.onUnwrap(d.id)}>
          ⤴
        </button>
        <button type="button" title="Delete group" aria-label={`Remove ${d.id}`} onClick={() => ctx.onRemove(d.id)}>
          <X size={11} />
        </button>
      </div>
      {/* Operator as hero, but still switchable (compromise): the active combinator is a large colored
          word that toggles AND↔OR on click, with a small "switch to …" caption so the toggle stays
          discoverable (the all-buttons design read as cluttered; a bare hero word hid the switch). */}
      {(() => {
        const other = d.op === 'and' ? 'or' : 'and';
        const toggle = () => ctx.onSetGroupOp(d.id, other);
        return (
          <div className="cne-gnode-op" role="group" aria-label={`Group ${d.id} combinator`}>
            <button
              type="button"
              className={`cne-op-hero cne-op-${d.op}`}
              aria-label={`Combinator ${d.op.toUpperCase()} — switch to ${other.toUpperCase()}`}
              title={`Switch to ${other.toUpperCase()}`}
              onClick={toggle}
            >
              {d.op.toUpperCase()}
            </button>
            <button type="button" className="cne-op-switch" title={`Switch to ${other.toUpperCase()}`} onClick={toggle}>
              ⇄ {other.toUpperCase()}
            </button>
          </div>
        );
      })()}
      <div className="cne-gnode-add">
        <button type="button" aria-label={`Add condition to ${d.id}`} onClick={() => ctx.onAddComparator(d.id)}>
          <Plus size={11} /> cond
        </button>
        <button type="button" aria-label={`Add group to ${d.id}`} onClick={() => ctx.onAddGroup(d.id)}>
          <Plus size={11} /> grp
        </button>
      </div>
      <span className={chipClass(d.status)}>{chipText(d.status, d.awaiting)}</span>
      <Handle type="source" position={Position.Right} />
    </div>
  );
}

export function NotNode({ data }: NodeProps) {
  const d = data as unknown as NotNodeData;
  const ctx = useContext(ConditionTreeContext);
  return (
    <div className="cne-nnode nodrag nopan">
      <Handle type="target" position={Position.Left} />
      <div className="cne-nnode-head">
        <span className="cne-not-label">NOT</span>
        <button type="button" title="Unwrap" aria-label={`Unwrap ${d.id}`} onClick={() => ctx.onUnwrap(d.id)}>
          ⤴
        </button>
        <button type="button" title="Delete" aria-label={`Remove ${d.id}`} onClick={() => ctx.onRemove(d.id)}>
          <X size={11} />
        </button>
      </div>
      <span className={chipClass(d.status)}>{chipText(d.status, d.awaiting)}</span>
      <Handle type="source" position={Position.Right} />
    </div>
  );
}

export function OutputNode({ data }: NodeProps) {
  const d = data as unknown as OutputNodeData;
  const hot = d.status === 'True' ? 'true' : d.status === 'False' ? 'false' : null;
  return (
    <div className="cne-out">
      <Handle type="target" position={Position.Left} />
      <div className="cne-out-title">Condition output</div>
      <div className={`cne-branch cne-branch-true ${hot === 'true' ? 'cne-branch-hot' : ''}`}>
        <span className="cne-branch-dot" /> TRUE
      </div>
      <div className={`cne-branch cne-branch-false ${hot === 'false' ? 'cne-branch-hot' : ''}`}>
        <span className="cne-branch-dot" /> FALSE
      </div>
    </div>
  );
}

export function PlaceholderNode() {
  const ctx = useContext(ConditionTreeContext);
  return (
    <div className="cne-placeholder nodrag nopan">
      <button type="button" className="cne-placeholder-btn" onClick={() => ctx.onAddComparator('')}>
        <span className="cne-placeholder-plus" aria-hidden>
          <Plus size={18} />
        </span>
        <span className="cne-placeholder-title">Add condition</span>
        <span className="cne-placeholder-sub">build AND/OR/NOT logic</span>
      </button>
      <Handle type="source" position={Position.Right} />
    </div>
  );
}
