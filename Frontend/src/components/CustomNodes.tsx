// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { memo, useMemo, useState, type CSSProperties } from 'react';
import { Handle, Position, useReactFlow, useConnection, useNodeConnections, useStore, NodeResizer } from '@xyflow/react';
import type { NodeProps } from '@xyflow/react';
import { useVariableStore } from '../stores/useVariableStore';
import { getTypeColor, getNodeIcon, renderPropertiesSummary, getStatusBadge, isLowDetailZoom, type NodeSummaryProperties, type NodeExecStatus } from './CustomNodes.helpers';
import { SubflowLanes, type SubflowInterface } from './SubflowLanes';
import { ExternalDeviceLanes } from './ExternalDeviceLanes';
import { readDeviceSurface } from '../node-editor/externalDevicePins';
import { aiRouterOutputHandles } from '../node-editor/aiRouterPorts';
import { useSubflowOpenStore } from '../stores/useSubflowOpenStore';
import { canRenameNode, applyNodeRename } from '../node-editor/nodeRename';

export function getNodeDataOutputs(nodeType: string, properties?: Record<string, unknown>): string[] {
  const type = nodeType.toLowerCase();
  if (type === 'httprequest') {
    return ['body', 'statusCode', 'isSuccess'];
  }
  if (type === 'aiverify') {
    // status = overall verdict, result = full audited record, claims = per-claim breakdown.
    return ['status', 'result', 'claims'];
  }
  if (type === 'airouter') {
    return ['category', 'reply'];
  }
  if (type === 'aidiff') {
    // changeType = overall verdict, materialChanges = the meaningful diffs, result = full record.
    return ['changeType', 'materialChanges', 'result'];
  }
  if (type.startsWith('openapi.') || type === 'restcaller') {
    return ['body', 'statusCode'];
  }
  if (type === 'forloop') {
    const mode = (properties?.mode as string | undefined)?.toLowerCase() ?? 'foreach';
    // In count mode item === index, so hide it. collected/results removed — use SetVariable to aggregate.
    return mode === 'count' ? ['index'] : ['item', 'index'];
  }
  if (type === 'parallelforeach') {
    // Each parallel iteration exposes the current element and its index to the body subgraph.
    return ['item', 'index'];
  }
  if (type === 'resourcepicker') {
    // value + label individually, plus `record` = both combined into one object.
    return ['value', 'label', 'record'];
  }
  if (type === 'errortrigger') {
    // The whole failure context (`result`) plus each field as its own promotable variable.
    // Keep field names in sync with ErrorWorkflowRunEnqueuer.FieldKeys (backend).
    return [
      'result', 'errorMessage', 'errorFailedNodeType', 'errorFailedNodeId',
      'errorWorkflowName', 'errorWorkflowId', 'errorExecutionId', 'errorTriggerOrigin', 'errorTimestampUtc',
    ];
  }
  if (type === 'inlinecode') {
    // Inline-code outputs are dynamic (whatever the script returns via Success(new { ... })).
    // We learn the field names from a successful test run and persist them in _outputKeys,
    // so they show up as draggable chips you can drop into a downstream field.
    const keys = properties?._outputKeys;
    return Array.isArray(keys) ? keys.filter((k): k is string => typeof k === 'string') : [];
  }
  return [];
}

// Human-readable label overrides for output handle keys.
const OUTPUT_DISPLAY_LABELS: Record<string, string> = {};

// Tooltip descriptions shown on output chips.
const OUTPUT_TOOLTIPS: Record<string, string> = {
  item:  'Current collection element for this iteration',
  index: 'Zero-based iteration counter',
  record: 'Both value + label combined as one object',
  result: 'The whole failure context as one object',
  errorMessage: 'Error message from the failed run',
  errorFailedNodeType: 'Node type that failed (e.g. inlineCode, httpRequest)',
  errorFailedNodeId: 'Id of the node that failed',
  errorWorkflowName: 'Name of the workflow that failed',
  errorWorkflowId: 'Id of the workflow that failed',
  errorExecutionId: 'Execution id of the failed run',
  errorTriggerOrigin: 'How the failed run was triggered (manual/schedule/…)',
  errorTimestampUtc: 'When the failure occurred (UTC)',
};

export function getNodePrimaryInputParameter(nodeType: string): string | null {
  const t = nodeType.toLowerCase();
  if (t === 'log') return 'message';
  if (t === 'setvariable') return 'value';
  if (t === 'httprequest') return 'url';
  if (t === 'condition') return 'left';
  if (t === 'delay') return 'delayMs';
  if (t === 'forloop') return 'collection';
  if (t === 'parallelforeach') return 'collection';
  return null;
}

export function getDefaultOutputType(nodeType: string, handle: string): 'string' | 'number' | 'boolean' | 'object' {
  const h = handle.toLowerCase();
  // errorTrigger: `result` is the whole failure object; every other field is a string.
  if (nodeType.toLowerCase() === 'errortrigger') return h === 'result' ? 'object' : 'string';
  if (h === 'body') return 'object';
  if (h === 'statuscode') return 'number';
  if (h === 'issuccess') return 'boolean';
  const t = nodeType.toLowerCase();
  if (t === 'condition') return 'boolean';
  if (t === 'transform') return 'object';
  if (t === 'forloop' || t === 'parallelforeach') {
    if (h === 'item') return 'object';
    if (h === 'index') return 'number';
    if (h === 'results') return 'object';
  }
  if (h === 'true' || h === 'false') return 'boolean';
  if (h === 'error' || h === 'failure') return 'object';
  if (h === 'record' || h === 'item') return 'object';
  return 'string';
}

export function getDefaultOutputValue(_nodeType: string, _handle: string, type: 'string' | 'number' | 'boolean' | 'object') {
  if (type === 'boolean') return true;
  if (type === 'number') return 42;
  if (type === 'object') return { status: 200, message: "OK" };
  return "Node output data";
}

// Keyboard activation for connection ports: Enter/Space synthesizes the click
// that React Flow's click-to-connect already listens for, so ports are operable
// without a pointer (Tab to a port, Enter to start/complete a connection).
function activatePortOnKey(e: React.KeyboardEvent<HTMLDivElement>) {
  if (e.key === 'Enter' || e.key === ' ') {
    e.preventDefault();
    e.currentTarget.click();
  }
}

// Shared accessibility props that make a Handle a focusable, labelled button.
function portA11yProps(label: string) {
  return {
    tabIndex: 0,
    role: 'button' as const,
    'aria-label': label,
    onKeyDown: activatePortOnKey,
  };
}

interface BaseNodeProps extends NodeProps {
  // Extra data could include live status overlays from Execution Tracking
  execStatus?: NodeExecStatus;
  errorMessage?: string;
}

// Wrapped in React.memo below — the parent (React Flow's NodeRenderer) re-renders on internal-state
// churn (selection, transform, etc.), and without memo every one of N cards' function bodies run on
// every such cascade. Memo lets cards skip those re-renders entirely; the Zustand subscriptions inside
// (isThisCardHovered / isProducerActive / mySnapKeys / activeChipVarKey) still trigger re-renders of
// exactly the cards whose visual state actually changed.
function GenericCustomNodeImpl({ id, type, data, selected, width: measuredWidth, height: measuredHeight }: BaseNodeProps) {
  const rf = useReactFlow();
  const node = rf.getNode(id);
  // Prefer React Flow's measured width/height (NodeProps) — they update reactively while the
  // container is resized, so the loopback SVG below tracks the box. node.style / data are only
  // non-reactive fallbacks for the first paint before the node has been measured.
  const styleWidth = typeof node?.style?.width === 'number'
    ? node.style.width
    : typeof node?.style?.width === 'string'
      ? parseInt(node.style.width, 10)
      : typeof data?.width === 'number'
        ? data.width
        : 500;
  const styleHeight = typeof node?.style?.height === 'number'
    ? node.style.height
    : typeof node?.style?.height === 'string'
      ? parseInt(node.style.height, 10)
      : typeof data?.height === 'number'
        ? data.height
        : 280;
  const width = typeof measuredWidth === 'number' && measuredWidth > 0 ? measuredWidth : styleWidth;
  const height = typeof measuredHeight === 'number' && measuredHeight > 0 ? measuredHeight : styleHeight;

  const { setNodes } = rf;
  const [isDragOver, setIsDragOver] = useState(false);
  // Inline rename (Feature #11): double-click the header label to edit the name in place.
  const [isRenaming, setIsRenaming] = useState(false);
  const [renameDraft, setRenameDraft] = useState('');
  const workflowId = useVariableStore((state) => state.activeWorkflowId);
  const isDraggingToken = useVariableStore((state) => state.isDraggingToken);
  const isDraggingOutput = useVariableStore((state) => state.isDraggingOutput);

  const currentVars = useVariableStore((state) => state.variables[workflowId || ''] || []);
  // ─ PER-CARD derived subscriptions ─
  // The hover/pin store slices used to be subscribed as bare scalars/arrays (`s.hoveredNodeId`,
  // `s.pinnedNodeIds`, etc.). Every one of N node cards subscribed to the same shared values, so a
  // single hover/click/leave rebroadcast new selector results to ALL cards and re-rendered every one —
  // ~500 cards × 2 cascades per click, the dominant cause of the bad INP on large graphs. The fix is
  // the same shape as the snap-keys fix: each card subscribes to a PRIMITIVE derived from the store
  // (boolean / joined string), so Zustand's Object.is bail-out only re-renders the 1–2 cards whose
  // visual state actually changed.
  const isThisCardHovered = useVariableStore((state) => state.hoveredNodeId === id);
  // True when any variable THIS card produces is involved in the current hover/pin selection — used to
  // halo the card's border. Folds in all four prior signals (hoveredNodeId/hoveredVariableId/pinnedNodeIds/
  // pinnedVariableIds) into one primitive boolean.
  const isProducerActive = useVariableStore((state) => {
    const wfId = state.activeWorkflowId;
    const vars = state.variables[wfId || ''] || [];
    for (const v of vars) {
      if (v.producer !== id) continue;
      if (state.hoveredNodeId && v.consumers.includes(state.hoveredNodeId)) return true;
      if (state.hoveredVariableId === v.id) return true;
      if (state.pinnedVariableIds.includes(v.id)) return true;
      if (state.pinnedNodeIds.some((pnId) => v.consumers.includes(pnId))) return true;
    }
    return false;
  });
  // Pipe-joined ids of THIS card's produced variables that are currently highlighted (chip styling on
  // output handles). A plain string → Object.is gates re-render to exactly the cards whose own chip
  // set changed.
  const activeChipVarKey = useVariableStore((state) => {
    const wfId = state.activeWorkflowId;
    const vars = state.variables[wfId || ''] || [];
    let out = '';
    for (const v of vars) {
      if (v.producer !== id) continue;
      if (state.hoveredVariableId === v.id || state.pinnedVariableIds.includes(v.id)) {
        out = out ? `${out}|${v.id}` : v.id;
      }
    }
    return out;
  });
  const activeChipVarSet = useMemo(
    () => new Set(activeChipVarKey ? activeChipVarKey.split('|') : []),
    [activeChipVarKey],
  );
  // Proximity-snap (Feature A): handles that would auto-connect on drag release glow. Subscribe to ONLY
  // this node's snap keys (a stable joined string) — a whole-`snapCandidateKeys` subscription re-rendered
  // every card on each snap change during a drag (catastrophic at hundreds of nodes); now only the two
  // nodes whose snap state actually changed re-render.
  const mySnapKeys = useVariableStore((state) => {
    let out = '';
    for (const k of state.snapCandidateKeys) if (k.startsWith(`${id} `)) out = out ? `${out}\n${k}` : k;
    return out;
  });
  const glowFor = (handleId: string): CSSProperties =>
    mySnapKeys.split('\n').includes(`${id} ${handleId}`)
      ? { boxShadow: '0 0 0 3px var(--color-accent-glow, rgba(99, 102, 241, 0.55))', borderColor: 'var(--color-accent)' }
      : {};

  const props = (data?.properties as NodeSummaryProperties) || {};
  const execStatus = (data?.execStatus as BaseNodeProps['execStatus'] | undefined) || 'Pending';
  const errorMessage = data?.errorMessage as string | undefined;
  const displayName = (data?.displayName as string | undefined) || props.label || type;
  // For a subflow node, prefer the referenced workflow's name (resolved live in Canvas and stamped
  // onto data.subflowName, with the persisted property as a fallback) so the card reads "Sub1".
  const resolvedSubflowName = (data?.subflowName as string | undefined)
    || (typeof props.subflowName === 'string' ? props.subflowName : '')
    || '';
  const headerLabel = type === 'subflow' && resolvedSubflowName ? resolvedSubflowName : displayName;
  // Design-time pin badge: this node returns a sample instead of executing on manual runs.
  const isPinned = !!(props as Record<string, unknown>).__pinnedOutput
    && typeof (props as Record<string, unknown>).__pinnedOutput === 'object'
    && ((props as Record<string, unknown>).__pinnedOutput as { enabled?: boolean }).enabled === true;
  // Count what's actually rendered: declared interface slots when present, else free-form rows.
  const subflowIface = data?.subflowInterface as SubflowInterface | undefined;
  const subflowInCount = (subflowIface?.inputs.length ?? 0) > 0
    ? subflowIface!.inputs.length
    : (Array.isArray(props.subflowInputs) ? props.subflowInputs.length : 0);
  const subflowOutCount = (subflowIface?.outputs.length ?? 0) > 0
    ? subflowIface!.outputs.length
    : (Array.isArray(props.subflowOutputs) ? props.subflowOutputs.length : 0);

  // Interface-driven binding: upsert the row for a declared local (matched by name) rather than
  // appending. Inputs bind a value to `target=localName`; outputs bind a global to `source=localName`.
  const bindSubflowInput = (localName: string, value: unknown) => {
    setNodes((nds) => nds.map((n) => {
      if (n.id !== id) return n;
      const p = (n.data?.properties as Record<string, unknown>) || {};
      const rows = Array.isArray(p.subflowInputs) ? (p.subflowInputs as Record<string, unknown>[]) : [];
      const exists = rows.some((r) => r.target === localName);
      const next = exists
        ? rows.map((r) => (r.target === localName ? { ...r, value } : r))
        : [...rows, { target: localName, value }];
      return { ...n, data: { ...n.data, properties: { ...p, subflowInputs: next } } };
    }));
  };

  const bindSubflowOutput = (localName: string, globalName: string) => {
    setNodes((nds) => nds.map((n) => {
      if (n.id !== id) return n;
      const p = (n.data?.properties as Record<string, unknown>) || {};
      const rows = Array.isArray(p.subflowOutputs) ? (p.subflowOutputs as Record<string, unknown>[]) : [];
      const exists = rows.some((r) => r.source === localName);
      const next = exists
        ? rows.map((r) => (r.source === localName ? { ...r, target: globalName } : r))
        : [...rows, { source: localName, target: globalName }];
      return { ...n, data: { ...n.data, properties: { ...p, subflowOutputs: next } } };
    }));
  };

  // The run view renders the same nodes but disables connecting — use that as the read-only signal
  // so editor-only affordances (e.g. "double-click to edit") aren't shown there.
  const isReadOnly = useStore((s) => !s.nodesConnectable);
  // Inline rename affordance: editable in the editor, never in the read-only run view,
  // and not for subflow cards (their label is the derived child-workflow name).
  const canRename = canRenameNode({ type }) && !isReadOnly;
  const beginRename = () => {
    if (!canRename) return;
    setRenameDraft(headerLabel);
    setIsRenaming(true);
  };
  const commitRename = () => {
    setIsRenaming(false);
    setNodes((nds) => applyNodeRename(nds, id, renameDraft));
  };
  const cancelRename = () => setIsRenaming(false);
  // Low-zoom level-of-detail: when zoomed far out, render just the header (icon +
  // name) and drop the body. Handles live outside the body, so edges still anchor.
  const zoom = useStore((s) => s.transform[2]);
  const lowDetail = isLowDetailZoom(zoom);
  const triggerOnly = Boolean(data?.triggerOnly) || type === 'start';
  // Container nodes (forLoop / parallelForEach) share the body-holding chrome: resizer, loopback
  // path, start/end/Done ports. parallelForEach runs its body once per item concurrently.
  const isContainer = type === 'forLoop' || type === 'parallelForEach';
  const isParallelContainer = type === 'parallelForEach';
  const outputHandles = Array.isArray(data?.outputHandles) && data.outputHandles.length > 0
    ? (data.outputHandles as string[])
    : ['result'];
  const primaryOutputHandle = outputHandles[0];
  const statusBadge = getStatusBadge(execStatus);

  // AI Classify node: one branch handle per configured category label plus the 'otherwise'
  // fallback — derived from the node's own properties (see aiRouterPorts.ts), not the manifest,
  // so the handles follow the config live while editing.
  const isAiRouter = type === 'aiRouter';
  const aiRouterHandles = isAiRouter
    ? aiRouterOutputHandles(data?.properties as Record<string, unknown> | undefined)
    : [];

  // External device node: pins are derived from the selected events/actions (not static ports).
  // Pure inbound surface — events AND incoming actions are both source pins (right). See externalDevicePins.ts.
  const isExternalDevice = type === 'externalDevice';
  const deviceSurface = isExternalDevice ? readDeviceSurface(props as Record<string, unknown>) : null;
  const deviceTargetLabel = (() => {
    if (!isExternalDevice) return '';
    const t = (props as Record<string, unknown>).targetId;
    if (typeof t === 'string') return t;
    if (t && typeof t === 'object') {
      const o = t as { label?: unknown; value?: unknown };
      if (typeof o.label === 'string') return o.label;
      if (typeof o.value === 'string') return o.value;
    }
    return '';
  })();

  // Technique 4 — while a connection is being made, reflect this node's role as a
  // target: dim invalid nodes, light up valid input handles, and emphasize the one
  // currently under the cursor / snapped (the "locked" state). Works for both the
  // drag gesture (useConnection) and click-to-connect (clickConnectSourceNodeId).
  const canBeTarget = !triggerOnly && type !== 'start' && !isContainer;
  // An input that already has an incoming wire is still a valid (replacing) target,
  // but we keep it quiet rather than lighting it green — only the actively-targeted
  // one still gets emphasized. `valid` (resting glow) collapses to `idle` when busy.
  // Count only real control-flow wires into the "in" handle — virtual globalRead
  // data-edges carry no targetHandle, so handleId scoping excludes them.
  const inputAlreadyConnected = useNodeConnections({ id, handleType: 'target', handleId: 'in' }).length > 0;
  const restingTarget = inputAlreadyConnected ? 'idle' : 'valid';
  const dragStatus = useConnection((c) => {
    if (!c.inProgress) return 'idle';
    if (c.fromNode?.id === id) return 'source';
    if (!canBeTarget) return 'invalid';
    return c.toNode?.id === id ? 'active' : restingTarget;
  });
  const clickConnectSourceNodeId = useVariableStore((state) => state.clickConnectSourceNodeId);
  let connectStatus = dragStatus;
  if (connectStatus === 'idle' && clickConnectSourceNodeId) {
    if (clickConnectSourceNodeId === id) connectStatus = 'source';
    else if (!canBeTarget) connectStatus = 'invalid';
    else connectStatus = isThisCardHovered ? 'active' : restingTarget;
  }
  const connectClass =
    connectStatus === 'invalid' ? 'connect-dim'
    : connectStatus === 'valid' ? 'connect-valid'
    : connectStatus === 'active' ? 'connect-active'
    : '';
  const nodeClass = `custom-node node-${type} node-exec-${execStatus} ${connectClass}`.trim();

  const handleDragOver = (e: React.DragEvent) => {
    const hasPrimaryParam = !!getNodePrimaryInputParameter(type);
    if (!hasPrimaryParam) return;

    if (isDraggingToken || isDraggingOutput) {
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
      e.stopPropagation();
      setIsDragOver(true);
    }
  };

  const handleDragLeave = () => {
    setIsDragOver(false);
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragOver(false);

    const targetParam = getNodePrimaryInputParameter(type);
    if (!targetParam) return;

    let variableId: string | null = null;
    let variableName: string | null = null;

    const tokenDataRaw = e.dataTransfer.getData('application/knotarium-variable-token');
    if (tokenDataRaw) {
      try {
        const tokenData = JSON.parse(tokenDataRaw);
        if (tokenData && tokenData.variableId) {
          variableId = tokenData.variableId;
          variableName = tokenData.variableName;
        }
      } catch (err) {
        console.error('Failed to parse dropped variable token on node:', err);
      }
    }

    const outputDataRaw = e.dataTransfer.getData('application/knotarium-node-output');
    if (!variableId && outputDataRaw && workflowId) {
      try {
        const outputData = JSON.parse(outputDataRaw);
        if (outputData && outputData.nodeId && outputData.outputHandle) {
          const { variables, addVariable } = useVariableStore.getState();
          const currentVars = variables[workflowId] || [];
          const existing = currentVars.find(
            v => v.producer === outputData.nodeId && v.producerOutput === outputData.outputHandle
          );
          if (existing) {
            variableId = existing.id;
            variableName = existing.name;
          } else {
            const created = addVariable(workflowId, {
              name: outputData.proposedName,
              type: outputData.type,
              producer: outputData.nodeId,
              producerOutput: outputData.outputHandle,
              value: outputData.value,
            });
            if (created) {
              variableId = created.id;
              variableName = created.name;
            }
          }
        }
      } catch (err) {
        console.error('Failed to parse dropped node output on node:', err);
      }
    }

    if (variableId && variableName) {
      setNodes((nds) =>
        nds.map((node) => {
          if (node.id === id) {
            return {
              ...node,
              data: {
                ...node.data,
                properties: {
                  ...(node.data?.properties as Record<string, unknown> || {}),
                  [targetParam]: {
                    __type: 'variable_ref',
                    variableId,
                    variableName,
                  },
                },
              },
            };
          }
          return node;
        })
      );
    }
  };

  const handleOutputDragStart = (
    e: React.DragEvent,
    handle: string,
    outputType: 'string' | 'number' | 'boolean' | 'object',
    outputValue: unknown
  ) => {
    const proposedName = `${id}_${handle}`;
    const dragData = {
      nodeId: id,
      outputHandle: handle,
      proposedName,
      type: outputType,
      value: outputValue,
    };
    e.dataTransfer.setData('application/knotarium-node-output', JSON.stringify(dragData));
    e.dataTransfer.effectAllowed = 'copy';
    useVariableStore.getState().setDraggingOutput(true, dragData);
  };

  const handleOutputDragEnd = () => {
    useVariableStore.getState().setDraggingOutput(false, null);
  };
  
  const haloStyle = isProducerActive ? {
    borderColor: 'var(--color-success)',
    boxShadow: '0 0 0 1.5px rgba(16, 185, 129, 0.6), 0 0 20px rgba(16, 185, 129, 0.4)',
  } : {};

  // Nodes that render branch/verdict labels down the right edge (AI Verify's 4, AI Diff's 3, AI Router's
  // dynamic categories) need a right gutter on the OUTPUTS chip section so the promotable-output chips don't
  // slide under those labels. The header title is short and left-aligned, so it does NOT get the gutter
  // (padding it would squeeze the title in the execution view, where a status badge shares the header row,
  // and wrap it onto 3 lines). For AI Router the gutter is sized to the longest category (labels ellipsize
  // at 45% of the node, so it is capped accordingly).
  const rightBranchCount = type === 'aiVerify' ? 4 : type === 'aiDiff' ? 3 : isAiRouter ? aiRouterHandles.length : 0;
  const branchLabelGutter = type === 'aiVerify' ? 104
    : type === 'aiDiff' ? 92
    : type === 'httpRequest' ? 46  // short DONE/FAIL labels — just enough to clear the isSuccess chip
    : isAiRouter && aiRouterHandles.length > 0
      ? Math.min(150, Math.max(...aiRouterHandles.map((h) => h.length)) * 7 + 18)
      : 0;
  // Give branch-heavy nodes a little more height so 3–4 labels spread down the right edge with breathing
  // room instead of stacking tightly (they were reading as "condensed").
  const branchMinHeight = rightBranchCount >= 3 ? rightBranchCount * 30 + 44 : undefined;

  return (
    <div
      className={nodeClass}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
      style={{
        width: isContainer ? '100%' : undefined,
        height: isContainer ? '100%' : undefined,
        minHeight: isContainer ? undefined : branchMinHeight,
        // Cap a normal node's width so long config values (e.g. a Set Variable holding a paragraph)
        // wrap and grow the card DOWNWARD instead of stretching it across the canvas. Containers are
        // user-resizable, so they opt out.
        maxWidth: isContainer ? undefined : 340,
        borderWidth: (selected || isDragOver || isProducerActive) ? '2px' : '1.5px',
        borderColor: isDragOver ? 'var(--color-accent)' : selected ? 'var(--color-accent)' : undefined,
        boxShadow: isDragOver
          ? '0 0 15px var(--color-accent-glow), 0 10px 25px -5px rgba(0, 0, 0, 0.5)'
          : selected
            ? '0 0 0 3px rgba(99, 102, 241, 0.25), 0 10px 25px -5px rgba(0, 0, 0, 0.5)'
            : undefined,
        transition: 'all 0.2s ease',
        ...haloStyle,
      }}
    >
      {isContainer && (
        <NodeResizer
          minWidth={250}
          minHeight={150}
          isVisible={selected}
          lineStyle={{ borderColor: 'var(--color-forloop)' }}
          handleStyle={{ backgroundColor: 'var(--color-forloop)', border: 'none', borderRadius: '4px' }}
        />
      )}
      {isContainer && (
        <svg
          style={{
            position: 'absolute',
            top: 0,
            left: 0,
            width: '100%',
            height: '100%',
            pointerEvents: 'none',
            overflow: 'visible',
            zIndex: 1,
          }}
        >
          <defs>
            <marker
              id={`loopback-arrow-${id}`}
              viewBox="0 0 10 10"
              refX="6"
              refY="5"
              markerWidth="6"
              markerHeight="6"
              orient="auto-start-reverse"
            >
              <path d="M 0 1.5 L 8 5 L 0 8.5 z" fill="var(--color-forloop)" />
            </marker>
          </defs>
          <path
            id={`loopback-path-${id}`}
            d={`M 24 ${height * 0.5}
                C 24 ${height * 0.5 + 40}, 34 ${height - 22}, 70 ${height - 22}
                L ${width - 70} ${height - 22}
                C ${width - 34} ${height - 22}, ${width - 24} ${height * 0.5 + 40}, ${width - 24} ${height * 0.5}`}
            fill="none"
            stroke="var(--color-forloop)"
            strokeWidth="1.5"
            strokeDasharray="4 3"
            opacity="0.5"
            markerStart={`url(#loopback-arrow-${id})`}
          />
          {/* "next item" label floated below the path, not sitting on it */}
          <text
            x={width / 2}
            y={height - 8}
            textAnchor="middle"
            fontStyle="italic"
            fill="var(--color-forloop)"
            opacity="0.75"
            style={{ fontSize: '9px', fontWeight: 700 }}
          >
            {isParallelContainer ? '⇉ each item · parallel' : '↻ next item'}
          </text>
        </svg>
      )}
      {/* Visual Role tag for the container */}
      {isContainer && (
        <div style={{
          position: 'absolute',
          top: '-10px',
          left: '16px',
          fontSize: '9px',
          fontWeight: 800,
          letterSpacing: '0.1em',
          padding: '2px 9px',
          borderRadius: '6px',
          color: '#0c0a22',
          background: 'var(--color-forloop)',
          zIndex: 10,
        }}>
          {isParallelContainer ? 'PARALLEL CONTAINER' : 'LOOP CONTAINER'}
        </div>
      )}
      {/* Node Header */}
      <div className="custom-node-header">
        {getNodeIcon(type)}
        {isRenaming ? (
          <input
            className="nodrag"
            autoFocus
            value={renameDraft}
            aria-label="Rename node"
            onChange={(e) => setRenameDraft(e.target.value)}
            onClick={(e) => e.stopPropagation()}
            onDoubleClick={(e) => e.stopPropagation()}
            onKeyDown={(e) => {
              e.stopPropagation();
              if (e.key === 'Enter') {
                e.preventDefault();
                commitRename();
              } else if (e.key === 'Escape') {
                e.preventDefault();
                cancelRename();
              }
            }}
            onBlur={commitRename}
            style={{
              flex: 1,
              minWidth: 0,
              font: 'inherit',
              color: 'inherit',
              background: 'rgba(255,255,255,0.08)',
              border: '1px solid var(--color-accent)',
              borderRadius: '4px',
              padding: '0 4px',
              outline: 'none',
            }}
          />
        ) : (
          <span
            style={{ flex: 1, textTransform: 'capitalize', cursor: canRename ? 'text' : undefined }}
            title={canRename ? 'Double-click to rename' : undefined}
            onDoubleClick={canRename ? (e) => { e.stopPropagation(); beginRename(); } : undefined}
          >
            {headerLabel}
          </span>
        )}
        {isPinned ? (
          <span
            title="Pinned output — this node returns a sample on manual runs instead of executing"
            style={{ fontSize: '0.6rem', fontWeight: 700, color: '#f59e0b', background: 'rgba(245,158,11,0.12)', border: '1px solid rgba(245,158,11,0.32)', borderRadius: '999px', padding: '1px 7px', whiteSpace: 'nowrap' }}
          >
            📌 pinned
          </span>
        ) : null}
        {type === 'subflow' && (subflowInCount > 0 || subflowOutCount > 0) ? (
          <span
            title={`${subflowInCount} input(s), ${subflowOutCount} output(s)`}
            style={{ fontSize: '0.6rem', fontWeight: 700, color: 'var(--color-accent)', background: 'rgba(99,102,241,0.12)', border: '1px solid rgba(99,102,241,0.28)', borderRadius: '999px', padding: '1px 7px', whiteSpace: 'nowrap' }}
          >
            {subflowInCount} in · {subflowOutCount} out
          </span>
        ) : null}
        {statusBadge ? (
          <span className={statusBadge.className}>
            {statusBadge.icon}
            <span>{statusBadge.label}</span>
          </span>
        ) : null}
        {type === 'subflow' ? (
          <button
            type="button"
            className="nodrag"
            aria-label="Open subflow"
            title="Open subflow"
            onClick={(e) => {
              // Drill into the child workflow; don't let the click select/drag the node.
              e.stopPropagation();
              useSubflowOpenStore.getState().requestOpen(id);
            }}
            style={{
              display: 'inline-flex',
              alignItems: 'center',
              justifyContent: 'center',
              width: '22px',
              height: '22px',
              padding: 0,
              border: '1px solid rgba(99,102,241,0.28)',
              borderRadius: '6px',
              background: 'rgba(99,102,241,0.12)',
              color: 'var(--color-accent)',
              cursor: 'pointer',
              fontSize: '0.8rem',
              lineHeight: 1,
            }}
          >
            ↗
          </button>
        ) : null}
      </div>

      {/* Node Content Summary (hidden at low zoom for a compact icon+name card) */}
      {!lowDetail && (
      <div className="custom-node-body">
        {isExternalDevice && deviceSurface ? (
          <ExternalDeviceLanes
            targetLabel={deviceTargetLabel}
            events={deviceSurface.events}
            actions={deviceSurface.actions}
            glowFor={glowFor}
            portA11yProps={portA11yProps}
            displayName={displayName}
          />
        ) : type === 'subflow' ? (
          (typeof props.subflowId === 'string' && props.subflowId.length > 0) || subflowInCount > 0 || subflowOutCount > 0 ? (
            <SubflowLanes
              subflowName={resolvedSubflowName}
              inputs={Array.isArray(props.subflowInputs) ? (props.subflowInputs as Record<string, unknown>[]) : []}
              outputs={Array.isArray(props.subflowOutputs) ? (props.subflowOutputs as Record<string, unknown>[]) : []}
              variables={currentVars.map((v) => ({ name: v.name, type: v.type }))}
              subflowInterface={subflowIface}
              onBindInput={isReadOnly ? undefined : bindSubflowInput}
              onBindOutput={isReadOnly ? undefined : bindSubflowOutput}
            />
          ) : (
            renderPropertiesSummary(type, props, { readOnly: isReadOnly })
          )
        ) : type === 'resourcePicker' && !isReadOnly ? (
          (() => {
            const sel = props.selection;
            let selValue: string | undefined;
            let selLabel: string | undefined;
            if (typeof sel === 'string') selValue = sel;
            else if (sel && typeof sel === 'object') {
              const o = sel as { value?: unknown; label?: unknown };
              if (typeof o.value === 'string') selValue = o.value;
              if (typeof o.label === 'string') selLabel = o.label;
            }
            if (!selValue) return <span style={{ color: 'var(--color-warning)' }}>⚠ nothing selected</span>;
            const lineStyle: React.CSSProperties = { cursor: 'grab', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', borderRadius: '5px', padding: '1px 4px', margin: '0 -4px', width: 'fit-content', maxWidth: '100%' };
            return (
              <div style={{ display: 'flex', flexDirection: 'column', gap: '1px' }}>
                {/* Drag the actual name/id; the drop is wired to this node's label/value output by
                    reference, so it auto-updates if the selection changes. */}
                <span
                  draggable className="nodrag node-output-chip"
                  title="Drag to use this record's name elsewhere (wired by reference to the label output)"
                  onDragStart={(e) => handleOutputDragStart(e, 'label', 'string', selLabel ?? selValue!)}
                  onDragEnd={handleOutputDragEnd}
                  style={{ ...lineStyle, fontWeight: 600, color: '#22d3ee' }}
                >
                  {selLabel ?? selValue}
                </span>
                <span
                  draggable className="nodrag node-output-chip"
                  title="Drag to use this record's id elsewhere (wired by reference to the value output)"
                  onDragStart={(e) => handleOutputDragStart(e, 'value', 'string', selValue!)}
                  onDragEnd={handleOutputDragEnd}
                  style={{ ...lineStyle, fontSize: '0.72rem', fontFamily: 'monospace', color: 'var(--text-muted)' }}
                >
                  {selValue}
                </span>
              </div>
            );
          })()
        ) : (
          renderPropertiesSummary(type, props, { readOnly: isReadOnly })
        )}
        {errorMessage && (
          <div style={{ fontSize: '0.7rem', color: 'var(--color-error)', marginTop: '4px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {errorMessage}
          </div>
        )}

        {/* Draggable Outputs */}
        {type === 'resourcePicker' ? (() => {
          const s = props.selection;
          let selValue: string | undefined;
          let selLabel: string | undefined;
          if (typeof s === 'string') selValue = s;
          else if (s && typeof s === 'object') {
            const o = s as { value?: unknown; label?: unknown };
            if (typeof o.value === 'string') selValue = o.value;
            if (typeof o.label === 'string') selLabel = o.label;
          }
          const valueField = (typeof (props as any).valueField === 'string' && (props as any).valueField) || 'id';
          const labelField = (typeof (props as any).labelField === 'string' && (props as any).labelField) || 'name';
          const GLYPH: Record<'string' | 'object', string> = { string: '#34d399', object: '#7c6cf0' };
          const rows = [
            { handle: 'value', t: 'string' as const, src: valueField, display: selValue != null ? `"${selValue}"` : '—', dragValue: selValue ?? '' },
            { handle: 'label', t: 'string' as const, src: labelField, display: selLabel != null ? `"${selLabel}"` : '—', dragValue: selLabel ?? selValue ?? '' },
            { handle: 'record', t: 'object' as const, src: 'record', display: '{ value, label }', dragValue: { value: selValue, label: selLabel } },
          ];
          return (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '4px', borderTop: '1px solid rgba(255,255,255,0.05)', paddingTop: '7px', marginTop: '7px' }}>
              <span style={{ fontSize: '0.6rem', fontWeight: 700, color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.06em' }}>Output Ports</span>
              {rows.map((r) => {
                const v = currentVars.find((c) => c.producer === id && c.producerOutput === r.handle);
                return (
                  <div
                    key={r.handle}
                    draggable
                    className="nodrag node-output-chip"
                    title={`Drag to use ${r.handle} (${r.t}) elsewhere — wired by reference`}
                    onDragStart={(e) => handleOutputDragStart(e, r.handle, r.t, r.dragValue)}
                    onDragEnd={handleOutputDragEnd}
                    style={{ display: 'flex', alignItems: 'center', gap: '7px', padding: '4px 8px', borderRadius: '7px', cursor: 'grab',
                      background: v ? 'rgba(124,108,240,0.10)' : 'rgba(255,255,255,0.03)',
                      border: `1px solid ${v ? 'rgba(124,108,240,0.4)' : 'var(--border-color)'}` }}
                  >
                    <span style={{ width: 7, height: 7, borderRadius: 2, transform: 'rotate(45deg)', background: GLYPH[r.t], flex: 'none' }} />
                    <span style={{ fontWeight: 700, fontFamily: 'monospace', fontSize: '0.7rem', color: '#e6edf3', flex: 'none' }}>{r.handle}</span>
                    <span style={{ color: 'var(--text-muted)', fontSize: '0.7rem' }}>=</span>
                    <span style={{ fontFamily: 'monospace', fontSize: '0.7rem', color: r.t === 'string' ? '#87e8a8' : '#c3b9ff', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1 }}>{r.display}</span>
                    <span style={{ fontFamily: 'monospace', fontSize: '0.62rem', color: 'var(--text-muted)', flex: 'none' }}>{r.src}</span>
                    <span style={{ fontSize: '0.58rem', fontWeight: 700, padding: '1px 6px', borderRadius: 5, flex: 'none', color: GLYPH[r.t],
                      background: r.t === 'string' ? 'rgba(52,211,153,0.13)' : 'rgba(124,108,240,0.14)',
                      border: `1px solid ${r.t === 'string' ? 'rgba(52,211,153,0.32)' : 'rgba(124,108,240,0.3)'}` }}>{r.t}</span>
                  </div>
                );
              })}
              <span style={{ fontSize: '0.58rem', color: 'var(--text-muted)', lineHeight: 1.4, opacity: 0.8 }}>Each port shows its value, source field &amp; type — drag to reuse.</span>
            </div>
          );
        })() : getNodeDataOutputs(type, props as any).length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '4px', borderTop: '1px solid rgba(255, 255, 255, 0.05)', paddingTop: '6px', marginTop: '6px', paddingRight: branchLabelGutter || undefined }}>
            <span style={{ fontSize: '0.62rem', fontWeight: 700, color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.02em' }}>
              Outputs (Drag to Promote)
            </span>
            {/* Cap width so many output chips (e.g. errorTrigger's failure fields) wrap to rows
                instead of stretching the node across the canvas. */}
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px', maxWidth: 320 }}>
              {getNodeDataOutputs(type, props as any).map((handle) => {
                const outputType = getDefaultOutputType(type, handle);
                const outputValue = getDefaultOutputValue(type, handle, outputType);
                const displayLabel = OUTPUT_DISPLAY_LABELS[handle] ?? handle;
                const tooltip = OUTPUT_TOOLTIPS[handle];
                
                // Find promoted variable if any
                const v = currentVars.find(candidate => candidate.producer === id && candidate.producerOutput === handle);

                const chipActiveStyle = v && activeChipVarSet.has(v.id) ? {
                  borderColor: getTypeColor(outputType),
                  background: 'rgba(255, 255, 255, 0.08)',
                  boxShadow: `0 0 8px ${getTypeColor(outputType)}`,
                } : {};

                return (
                  <div
                    key={handle}
                    draggable
                    title={v ? `${handle} — promoted as "${v.name}". Click to pin, drag to re-use.` : (tooltip ?? `Click to capture "${handle}" as a variable, or drag to the store.`)}
                    className="nodrag node-output-chip"
                    onDragStart={(e) => handleOutputDragStart(e, handle, outputType, outputValue)}
                    onDragEnd={handleOutputDragEnd}
                    onMouseEnter={() => {
                      if (v) useVariableStore.getState().setHoveredVariableId(v.id);
                    }}
                    onMouseLeave={() => {
                      if (v) useVariableStore.getState().setHoveredVariableId(null);
                    }}
                    onClick={(e) => {
                      e.stopPropagation();
                      if (v) {
                        useVariableStore.getState().togglePinnedVariableId(v.id);
                      } else if (workflowId) {
                        // Click-to-promote: register the output as a variable without dragging
                        useVariableStore.getState().addVariable(workflowId, {
                          name: `${id}_${handle}`,
                          type: outputType,
                          producer: id,
                          producerOutput: handle,
                          value: outputValue,
                        });
                      }
                    }}
                    style={{
                      display: 'inline-flex',
                      alignItems: 'center',
                      gap: '4px',
                      padding: '2px 6px',
                      background: v ? 'rgba(99,102,241,0.12)' : 'rgba(255, 255, 255, 0.03)',
                      border: v ? '1px solid rgba(99,102,241,0.4)' : '1px solid var(--border-color)',
                      borderRadius: '4px',
                      fontSize: '0.65rem',
                      color: v ? 'rgb(165,167,250)' : 'var(--text-secondary)',
                      cursor: 'grab',
                      userSelect: 'none',
                      fontFamily: 'monospace',
                      transition: 'all 0.15s ease',
                      ...chipActiveStyle,
                    }}
                  >
                    <span style={{ width: '5px', height: '5px', borderRadius: '50%', background: getTypeColor(outputType) }} />
                    {displayLabel}
                    {!v && <span style={{ opacity: 0.5, fontSize: '0.6rem' }}>+</span>}
                  </div>
                );
              })}
            </div>
          </div>
        )}
      </div>
      )}

      {/* Specialized Handles / Connection Ports */}
      
      {/* Target/Input Handles (all except 'start'). The external-device node has no input handle at
          all — it's a pure inbound surface whose event/action pins are all sources (ExternalDeviceLanes). */}
      {!triggerOnly && !isExternalDevice && (
        <Handle
          type="target"
          position={Position.Left}
          id="in"
          style={{ background: 'var(--bg-surface-opaque)', ...glowFor('in') }}
          {...portA11yProps(`${displayName} input`)}
        />
      )}

      {/* Source/Output Handles (all except 'end') */}
      {triggerOnly && (
        <Handle
          type="source"
          position={Position.Right}
          id={primaryOutputHandle}
          style={{ background: 'var(--color-success)', ...glowFor(primaryOutputHandle) }}
          {...portA11yProps(`${displayName} output`)}
        />
      )}

      {type === 'condition' && (
        <>
          {/* True Branch Handle */}
          <Handle
            type="source"
            position={Position.Right}
            id="true"
            style={{
              top: '30%',
              background: 'var(--color-success)',
              borderColor: 'var(--color-success)',
              ...glowFor('true'),
            }}
            {...portA11yProps(`${displayName} TRUE branch output`)}
          />
          <span style={{ position: 'absolute', right: '12px', top: '22%', fontSize: '0.65rem', fontWeight: 800, color: 'var(--color-success)' }}>
            TRUE
          </span>

          {/* False Branch Handle */}
          <Handle
            type="source"
            position={Position.Right}
            id="false"
            style={{
              top: '70%',
              background: 'var(--color-error)',
              borderColor: 'var(--color-error)',
              ...glowFor('false'),
            }}
            {...portA11yProps(`${displayName} FALSE branch output`)}
          />
          <span style={{ position: 'absolute', right: '12px', top: '62%', fontSize: '0.65rem', fontWeight: 800, color: 'var(--color-error)' }}>
            FALSE
          </span>
        </>
      )}

      {isAiRouter && (
        <>
          {aiRouterHandles.map((handle, index) => {
            // Distribute the branch handles evenly down the right edge; the trailing
            // 'otherwise' fallback renders muted so real categories stand out.
            const top = `${Math.round(((index + 1) / (aiRouterHandles.length + 1)) * 100)}%`;
            const isOtherwise = index === aiRouterHandles.length - 1;
            const color = isOtherwise ? 'var(--text-muted, #6b7280)' : 'var(--color-accent)';
            return (
              <span key={handle}>
                <Handle
                  type="source"
                  position={Position.Right}
                  id={handle}
                  style={{ top, background: color, borderColor: color, ...glowFor(handle) }}
                  {...portA11yProps(`${displayName} ${handle} branch output`)}
                />
                <span style={{
                  position: 'absolute',
                  right: '12px',
                  top: `calc(${top} - 7px)`,
                  fontSize: '0.6rem',
                  fontWeight: 800,
                  color,
                  maxWidth: '45%',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}>
                  {handle}
                </span>
              </span>
            );
          })}
        </>
      )}

      {type === 'httpRequest' && (
        <>
          {/* Success Port */}
          <Handle
            type="source"
            position={Position.Right}
            id="success"
            style={{
              top: '30%',
              background: 'var(--color-accent)',
              borderColor: 'var(--color-accent)',
              ...glowFor('success'),
            }}
            {...portA11yProps(`${displayName} DONE output`)}
          />
          <span style={{ position: 'absolute', right: '12px', top: '22%', fontSize: '0.65rem', fontWeight: 800, color: 'var(--color-accent)' }}>
            DONE
          </span>

          {/* Error / Failure Port */}
          <Handle
            type="source"
            position={Position.Right}
            id="failure"
            style={{
              top: '70%',
              background: 'var(--color-error)',
              borderColor: 'var(--color-error)',
              ...glowFor('failure'),
            }}
            {...portA11yProps(`${displayName} FAIL output`)}
          />
          <span style={{ position: 'absolute', right: '12px', top: '62%', fontSize: '0.65rem', fontWeight: 800, color: 'var(--color-error)' }}>
            FAIL
          </span>
        </>
      )}

      {type === 'aiVerify' && (
        <>
          {/* Evidence-gate verdict branches — fixed vocabulary, evenly spaced down the right edge.
              Verified = success green, Contradicted = error red, Unsupported/Uncertain = muted. */}
          {([
            ['verified', 'var(--color-success)', 'VERIFIED', '20%'],
            ['unsupported', 'var(--color-warning)', 'UNSUPPORTED', '40%'],
            ['contradicted', 'var(--color-error)', 'CONTRADICTED', '60%'],
            ['uncertain', 'var(--text-muted, #6b7280)', 'UNCERTAIN', '80%'],
          ] as const).map(([id, color, label, top]) => (
            <span key={id}>
              <Handle
                type="source"
                position={Position.Right}
                id={id}
                style={{ top, background: color, borderColor: color, ...glowFor(id) }}
                {...portA11yProps(`${displayName} ${id} branch output`)}
              />
              <span style={{ position: 'absolute', right: '12px', top: `calc(${top} - 8px)`, fontSize: '0.6rem', fontWeight: 800, color }}>
                {label}
              </span>
            </span>
          ))}
        </>
      )}

      {type === 'aiDiff' && (
        <>
          {/* Semantic-diff verdict branches — fixed vocabulary. Material = attention, none = success. */}
          {([
            ['material', 'var(--color-warning)', 'MATERIAL', '25%'],
            ['cosmetic', 'var(--text-muted, #6b7280)', 'COSMETIC', '50%'],
            ['none', 'var(--color-success)', 'NO CHANGE', '75%'],
          ] as const).map(([id, color, label, top]) => (
            <span key={id}>
              <Handle
                type="source"
                position={Position.Right}
                id={id}
                style={{ top, background: color, borderColor: color, ...glowFor(id) }}
                {...portA11yProps(`${displayName} ${id} branch output`)}
              />
              <span style={{ position: 'absolute', right: '12px', top: `calc(${top} - 8px)`, fontSize: '0.6rem', fontWeight: 800, color }}>
                {label}
              </span>
            </span>
          ))}
        </>
      )}

      {isContainer && (
        <>
          {/* Inner-Left 'start' Port Notch Tab */}
          <div style={{
            position: 'absolute',
            left: '-1.5px',
            top: '50%',
            transform: 'translateY(-50%)',
            display: 'flex',
            alignItems: 'center',
            background: 'var(--bg-surface-opaque, #101625)',
            border: '1.5px solid var(--color-success)',
            borderLeft: 'none',
            borderRadius: '0 6px 6px 0',
            padding: '3px 8px 3px 6px',
            zIndex: 10,
            boxShadow: '2px 0 8px rgba(16, 185, 129, 0.15)',
          }}>
            <span style={{ fontSize: '9px', fontWeight: 800, color: 'var(--color-success)', marginRight: '6px', letterSpacing: '0.05em' }}>start</span>
            <Handle
              type="source"
              position={Position.Right}
              id="start"
              style={{
                position: 'relative',
                right: 'auto',
                top: 'auto',
                transform: 'none',
                background: 'var(--color-success)',
                borderColor: 'var(--color-success)',
                width: '7px',
                height: '7px',
                ...glowFor('start'),
              }}
              {...portA11yProps(`${displayName} loop body start output`)}
            />
          </div>

          {/* Inner-Right 'end' Port Notch Tab */}
          <div style={{
            position: 'absolute',
            right: '-1.5px',
            top: '50%',
            transform: 'translateY(-50%)',
            display: 'flex',
            alignItems: 'center',
            background: 'var(--bg-surface-opaque, #101625)',
            border: '1.5px solid var(--color-forloop)',
            borderRight: 'none',
            borderRadius: '6px 0 0 6px',
            padding: '3px 6px 3px 8px',
            zIndex: 10,
            boxShadow: '-2px 0 8px rgba(124, 108, 240, 0.15)',
          }}>
            <Handle
              type="target"
              position={Position.Left}
              id="end"
              style={{
                position: 'relative',
                left: 'auto',
                top: 'auto',
                transform: 'none',
                background: 'var(--color-forloop)',
                borderColor: 'var(--color-forloop)',
                width: '7px',
                height: '7px',
                ...glowFor('end'),
              }}
              {...portA11yProps(`${displayName} loop body end input`)}
            />
            <span style={{ fontSize: '9px', fontWeight: 800, color: 'var(--color-forloop)', marginLeft: '6px', letterSpacing: '0.05em' }}>end</span>
          </div>

          {/* Outer-Right 'Done' Port Tab — same height as 'end', protrudes outward */}
          <div style={{
            position: 'absolute',
            left: '100%',
            top: '50%',
            transform: 'translateY(-50%)',
            display: 'flex',
            alignItems: 'center',
            background: 'var(--bg-surface-opaque, #101625)',
            border: '1.5px solid var(--text-muted, #6b7280)',
            borderLeft: 'none',
            borderRadius: '0 6px 6px 0',
            padding: '3px 8px 3px 6px',
            zIndex: 10,
          }}>
            <span style={{ fontSize: '9px', fontWeight: 800, color: 'var(--text-muted, #6b7280)', marginRight: '6px', letterSpacing: '0.05em' }}>Done</span>
            <Handle
              type="source"
              position={Position.Right}
              id="success"
              style={{
                position: 'relative',
                right: 'auto',
                top: 'auto',
                transform: 'none',
                background: 'var(--text-muted, #6b7280)',
                borderColor: 'var(--text-muted, #6b7280)',
                width: '7px',
                height: '7px',
                ...glowFor('success'),
              }}
              {...portA11yProps(`${displayName} loop Done output`)}
            />
          </div>
        </>
      )}

      {!triggerOnly && type !== 'condition' && type !== 'httpRequest' && type !== 'aiVerify' && type !== 'aiDiff' && !isContainer && type !== 'end' && !isExternalDevice && !isAiRouter && (
        <Handle
          type="source"
          position={Position.Right}
          id={primaryOutputHandle}
          style={{ background: 'var(--color-accent)', ...glowFor(primaryOutputHandle) }}
          {...portA11yProps(`${displayName} output`)}
        />
      )}
    </div>
  );
}

export const GenericCustomNode = memo(GenericCustomNodeImpl) as typeof GenericCustomNodeImpl;
