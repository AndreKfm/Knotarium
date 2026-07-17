// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { CSSProperties } from 'react';
import { Play, GitBranch, Variable, Globe, Clock, FileText, Square, Check, AlertCircle, RotateCcw, ShieldAlert, Ban, Repeat, Layers, GitMerge, Code, Database, Zap, Send, Hourglass } from 'lucide-react';

// External device node roles: a source/trigger reads green, a dispatch/action reads cyan.
const EVENT_COLOR = '#34d399';
const ACTION_COLOR = '#22d3ee';
import { summarizeConditionLines } from '../node-editor/condition/conditionSummary';

export type NodeExecStatus =
  | 'Pending' | 'Running' | 'Completed' | 'Failed'
  | 'Waiting' | 'Retrying' | 'RequiresManualDecision' | 'Cancelled';

export const getTypeColor = (t: 'string' | 'number' | 'boolean' | 'object') => {
  switch (t) {
    case 'string': return '#10b981';
    case 'number': return '#f59e0b';
    case 'boolean': return '#ef4444';
    case 'object': return '#6366f1';
  }
};

// Custom icons mapper for each node type
export const getNodeIcon = (type: string) => {
  switch (type) {
    case 'start':
      return <Play size={15} fill="var(--color-success)" color="var(--color-success)" />;
    case 'condition':
      return <GitBranch size={15} color="var(--color-warning)" />;
    case 'setVariable':
      return <Variable size={15} color="var(--color-info)" />;
    case 'setVariables':
      return <Variable size={15} color="var(--color-accent)" />;
    case 'httpRequest':
      return <Globe size={15} color="var(--color-accent)" />;
    case 'delay':
      return <Clock size={15} color="var(--color-delay)" />;
    case 'log':
      return <FileText size={15} color="#0ea5e9" />;
    case 'forLoop':
      return <Repeat size={15} color="var(--color-forloop)" />;
    case 'parallelForEach':
      return <Layers size={15} color="var(--color-forloop)" />;
    case 'join':
      return <GitMerge size={15} color="var(--color-accent)" />;
    case 'inlineCode':
      return <Code size={15} color="var(--color-accent)" />;
    case 'resourcePicker':
      return <Database size={15} color="#22d3ee" />;
    // External device nodes — role-colored so an imported graph reads at a glance.
    case 'eventTrigger':
      return <Zap size={15} color={EVENT_COLOR} fill={EVENT_COLOR} />;
    case 'actionTrigger':
      return <Zap size={15} color={EVENT_COLOR} />;
    case 'fireAction':
      return <Play size={15} color={ACTION_COLOR} fill={ACTION_COLOR} />;
    case 'setEvent':
      return <Send size={15} color={ACTION_COLOR} />;
    case 'waitForEvent':
      return <Hourglass size={15} color={ACTION_COLOR} />;
    case 'end':
      return <Square size={15} fill="var(--color-error)" color="var(--color-error)" />;
    default:
      return <FileText size={15} />;
  }
};

export interface NodeSummaryProperties {
  operator?: string;
  left?: unknown;
  right?: unknown;
  variableName?: string;
  value?: unknown;
  method?: string;
  url?: string;
  delayMs?: number;
  duration?: string;
  message?: string;
  label?: string;
  mode?: string;
  collection?: unknown;
  count?: unknown;
  condition?: unknown;
  maxParallelism?: unknown;
  code?: unknown;
  language?: unknown;
  variables?: unknown;
  subflowId?: unknown;
  subflowName?: unknown;
  subflowInputs?: unknown;
  subflowOutputs?: unknown;
  selection?: unknown;
  path?: unknown;
  event?: unknown;
  action?: unknown;
  instance?: unknown;
}

const formatSummaryValue = (val: unknown) => {
  if (val && typeof val === 'object' && (val as { __type?: string }).__type === 'variable_ref') {
    return `$${(val as { variableName?: string }).variableName || 'var'}`;
  }
  if (val === undefined || val === null) return '';
  if (typeof val === 'object') return JSON.stringify(val);
  return String(val);
};

// The condition card's TRUE/FALSE branch labels are absolutely positioned at the right edge, so the
// summary reserves room on the right (paddingRight) to never run under them, and clamps to 3 lines so a
// complex expression grows the card a little then ellipsizes (full text on hover via the title attr).
const conditionSummaryStyle: CSSProperties = {
  fontWeight: 600,
  display: '-webkit-box',
  WebkitBoxOrient: 'vertical',
  WebkitLineClamp: 6, // room for a wrapped membership set + AND/OR lines before truncating
  overflow: 'hidden',
  wordBreak: 'break-word',
  whiteSpace: 'pre-wrap', // honor the explicit AND/OR + set-wrap line breaks from summarizeConditionLines
  paddingRight: '46px',
};

// A resourceLocator value persists as { value, label, mode } (an editor pick), a legacy plain string
// (importer-written), or a variable_ref (bound to an expression). Read the best human-facing text for
// the card — preferring the display label, then the stable value.
const locatorText = (raw: unknown): string => {
  if (typeof raw === 'string') return raw.trim();
  if (raw && typeof raw === 'object') {
    const o = raw as Record<string, unknown>;
    if (o.__type === 'variable_ref' && typeof o.variableName === 'string') return `{{ ${o.variableName} }}`;
    if (typeof o.label === 'string' && o.label.trim()) return o.label.trim();
    if (typeof o.value === 'string' && o.value.trim()) return o.value.trim();
  }
  return '';
};

// Shows which event/action an external device node addresses, in the role color, with the target instance under it.
const renderSignalSummary = (type: string, props: NodeSummaryProperties) => {
  const isSource = type === 'eventTrigger' || type === 'actionTrigger';
  const usesAction = type === 'actionTrigger' || type === 'fireAction';
  const color = isSource ? EVENT_COLOR : ACTION_COLOR;
  const name = locatorText(usesAction ? props.action : props.event);
  const instText = locatorText(props.instance);
  const inst = instText && instText.toLowerCase() !== 'default' ? instText : '';

  if (!name) {
    // A trigger with no signal listens to anything (valid); a dispatch with no action is unconfigured.
    return isSource
      ? <span style={{ color: 'var(--text-muted)', fontStyle: 'italic' }}>any {usesAction ? 'action' : 'event'}</span>
      : <span style={{ color: 'var(--color-warning)' }}>⚠ no {usesAction ? 'action' : 'event'}</span>;
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '2px' }} title={inst ? `${name} · ${inst}` : name}>
      <span style={{ fontWeight: 600, color, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
      {inst && <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{inst}</span>}
    </div>
  );
};

// Summary renderer for node properties inside the card
export const renderPropertiesSummary = (
  type: string,
  props: NodeSummaryProperties | null | undefined,
  options?: { readOnly?: boolean },
) => {
  if (!props) return <span style={{ fontStyle: 'italic' }}>no config</span>;

  switch (type) {
    case 'start':
      return <span style={{ color: 'var(--text-muted)' }}>Trigger Entry Point</span>;
    case 'condition': {
      // Prefer the real logic summary (v1/v2). Fall back to a genuinely-configured legacy
      // left/operator/right; otherwise say "Not configured" rather than the meaningless
      // "left Equal right" placeholder.
      const logicLines = summarizeConditionLines((props as { logic?: unknown }).logic);
      if (logicLines) {
        const text = logicLines.join('\n');
        return <span style={conditionSummaryStyle} title={text}>{text}</span>;
      }
      if (props.left !== undefined && props.right !== undefined) {
        const legacyLine = `${formatSummaryValue(props.left)} ${props.operator || 'Equal'} ${formatSummaryValue(props.right)}`;
        return (
          <span style={conditionSummaryStyle} title={legacyLine}>
            {legacyLine}
          </span>
        );
      }
      return <span style={{ fontStyle: 'italic', color: 'var(--text-muted)' }}>Not configured</span>;
    }
    case 'setVariable':
      return (
        <span>
          set <strong style={{ color: 'var(--color-info)' }}>{props.variableName || 'var'}</strong> = {props.value !== undefined ? formatSummaryValue(props.value) : 'value'}
        </span>
      );
    case 'setVariables': {
      const rows = Array.isArray(props.variables) ? (props.variables as { name?: string }[]) : [];
      const names = rows.map(r => r?.name).filter(Boolean);
      if (names.length === 0) return <span style={{ color: 'var(--color-warning)' }}>⚠ no variables set</span>;
      return (
        <span>
          set <strong style={{ color: 'var(--color-accent)' }}>{names.slice(0, 3).join(', ')}{names.length > 3 ? `, +${names.length - 3}` : ''}</strong>
        </span>
      );
    }
    case 'httpRequest':
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '2px' }}>
          <span style={{ fontWeight: 700, color: 'var(--color-accent)' }}>{props.method || 'GET'}</span>
          <span style={{ fontSize: '0.75rem', opacity: 0.8, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{props.url !== undefined ? formatSummaryValue(props.url) : 'http://api...'}</span>
        </div>
      );
    case 'delay':
      return (
        <span>
          wait {props.delayMs !== undefined ? `${formatSummaryValue(props.delayMs)}ms` : props.duration || 'duration'}
        </span>
      );
    case 'log':
      return (
        <span style={{ fontSize: '0.75rem', display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontStyle: 'italic' }}>
          "{props.message !== undefined ? formatSummaryValue(props.message) : 'log message'}"
        </span>
      );
    case 'forLoop': {
      const mode = props.mode || 'foreach';
      if (mode === 'foreach') {
        return (
          <span>
            For each in <strong style={{ color: 'var(--color-forloop)' }}>{formatSummaryValue(props.collection)}</strong>
          </span>
        );
      } else if (mode === 'count') {
        return (
          <span>
            Repeat <strong style={{ color: 'var(--color-forloop)' }}>{formatSummaryValue(props.count)}</strong> times
          </span>
        );
      } else if (mode === 'while') {
        return (
          <span>
            While <strong style={{ color: 'var(--color-forloop)' }}>{formatSummaryValue(props.condition)}</strong>
          </span>
        );
      }
      return <span>For Loop ({mode})</span>;
    }
    case 'parallelForEach': {
      const lanes = props.maxParallelism !== undefined ? formatSummaryValue(props.maxParallelism) : '8';
      const collectionText = formatSummaryValue(props.collection);
      return (
        <span>
          {collectionText
            ? <>For each in <strong style={{ color: 'var(--color-forloop)' }}>{collectionText}</strong></>
            : <span style={{ color: 'var(--color-warning)' }}>⚠ no collection set</span>}
          {' '}· up to <strong style={{ color: 'var(--color-accent)' }}>{lanes}</strong> <strong>items</strong> at once
        </span>
      );
    }
    case 'join':
      return <span style={{ color: 'var(--text-muted)' }}>Wait for all branches, then merge</span>;
    case 'subflow': {
      const subflowName = typeof props.subflowName === 'string' ? props.subflowName : '';
      const hasId = typeof props.subflowId === 'string' && props.subflowId.length > 0;
      if (!hasId) return <span style={{ color: 'var(--color-warning)' }}>⚠ no subflow selected</span>;
      return (
        <span style={{ color: 'var(--text-muted)', fontSize: '0.72rem' }}>
          {subflowName && !options?.readOnly ? 'Subflow · double-click to edit' : 'Subflow'}
        </span>
      );
    }
    case 'inlineCode': {
      const codeStr = typeof props.code === 'string' ? props.code.trim() : '';
      if (!codeStr) {
        return <span style={{ color: 'var(--color-warning)' }}>⚠ empty script</span>;
      }
      const firstLine = codeStr.split('\n')[0].slice(0, 40);
      const lang = typeof props.language === 'string' ? props.language : 'csharp';
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '2px' }}>
          <span style={{ fontWeight: 700, color: 'var(--color-accent)', fontSize: '0.7rem', textTransform: 'uppercase' }}>{lang}</span>
          <span style={{ fontSize: '0.75rem', fontFamily: 'monospace', opacity: 0.8, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{firstLine}</span>
        </div>
      );
    }
    case 'resourcePicker': {
      const sel = props.selection;
      let value: string | undefined;
      let label: string | undefined;
      if (typeof sel === 'string') {
        value = sel;
      } else if (sel && typeof sel === 'object') {
        const o = sel as { value?: unknown; label?: unknown };
        if (typeof o.value === 'string') value = o.value;
        if (typeof o.label === 'string') label = o.label;
      }
      if (!value) {
        return <span style={{ color: 'var(--color-warning)' }}>⚠ nothing selected</span>;
      }
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '2px' }}>
          <span style={{ fontWeight: 600, color: '#22d3ee', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label ?? value}</span>
          <span style={{ fontSize: '0.72rem', fontFamily: 'monospace', color: 'var(--text-muted)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{value}</span>
        </div>
      );
    }
    // External device nodes: surface *which* event/action (the whole point — an imported graph is
    // otherwise a wall of identical "Event Trigger" / "Fire Action" cards). Name in the role color,
    // target instance underneath; full text on hover.
    case 'eventTrigger':
    case 'actionTrigger':
    case 'setEvent':
    case 'waitForEvent':
    case 'fireAction':
      return renderSignalSummary(type, props);
    case 'end':
      return <span style={{ color: 'var(--text-muted)' }}>End Execution</span>;
    default:
      return null;
  }
};

export function getStatusBadge(status: NodeExecStatus | undefined) {
  switch (status) {
    case 'Completed':
      return { label: 'Completed', icon: <Check size={12} color="var(--color-success)" />, className: 'node-status-badge node-status-badge-completed' };
    case 'Failed':
      return { label: 'Failed', icon: <AlertCircle size={12} color="var(--color-error)" />, className: 'node-status-badge node-status-badge-failed' };
    case 'Waiting':
      return { label: 'Waiting', icon: <Clock size={12} color="var(--color-info)" />, className: 'node-status-badge node-status-badge-waiting' };
    case 'Retrying':
      return { label: 'Retrying', icon: <RotateCcw size={12} color="var(--color-info)" />, className: 'node-status-badge node-status-badge-retrying' };
    case 'RequiresManualDecision':
      return { label: 'Manual', icon: <ShieldAlert size={12} color="var(--color-error)" />, className: 'node-status-badge node-status-badge-manual' };
    case 'Cancelled':
      return { label: 'Cancelled', icon: <Ban size={12} color="var(--text-secondary)" />, className: 'node-status-badge node-status-badge-cancelled' };
    default:
      return null;
  }
}

// ── Low-zoom level-of-detail ────────────────────────────────────────────────
// Below this canvas zoom, node cards drop their body (ports/summary) and show
// just the icon + name header, so large graphs stay legible when zoomed out.
export const LOD_ZOOM_THRESHOLD = 0.5;

/** True when the canvas is zoomed out far enough to render compact node cards. */
export function isLowDetailZoom(zoom: number, threshold: number = LOD_ZOOM_THRESHOLD): boolean {
  return zoom < threshold;
}
