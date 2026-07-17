// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { memo, useCallback, useEffect, useMemo, useState } from 'react';
import type { Node as RFNode, Edge as RFEdge } from '@xyflow/react';
import { Trash2, ShieldAlert, Link, Plus } from 'lucide-react';
import { ManifestForm } from './shared/ManifestForm';
import { RestCallerPropertyForm } from './RestCallerPropertyForm';
import { ResourcePickerPropertyForm } from './ResourcePickerPropertyForm';
import { PollingTriggerPropertyForm } from './PollingTriggerPropertyForm';
import { SchedulerPropertyForm } from './SchedulerPropertyForm';
import { HttpRequestPropertyForm } from './HttpRequestPropertyForm';
import { NodeIoInspector } from './NodeIoInspector';
import { PinOutputEditor } from './PinOutputEditor';
import { api } from '../utils/api';
import type { NodePackageSummary, NodeManifest, WorkflowDefinition } from '../types';
import { useVariableStore } from '../stores/useVariableStore';
import { extractSubflowInterface } from '../utils/subflowInterface';
import { getTypeStyles } from './VariableToken';
import type { SignalFieldGroup } from '../node-editor/signalFieldBinding';
import type { ReferenceGroup } from '../node-editor/upstreamReferences';
import { useSignalFieldStore, signalGroupsFor } from '../stores/useSignalFieldStore';

interface PropertiesPanelProps {
  workflowId: string | null;
  selectedNode: RFNode | null;
  selectedEdge: RFEdge | null;
  /** Insertable `{{ $node.<id>.output.<field> }}` references from the selected node's upstream outputs. */
  referenceGroups?: ReferenceGroup[];
  onUpdateNodeProperties: (nodeId: string, properties: Record<string, unknown>) => void;
  onDeleteNode: (nodeId: string) => void;
  onDeleteEdge: (edgeId: string) => void;
}

/**
 * Schema-driven expression references: the data the selected node's UPSTREAM nodes expose, as clickable
 * chips that copy a ready-to-paste `{{ $node.<id>.output.<field> }}` reference into any expression field.
 * Mirrors SignalFieldsSection; widens automatically as node manifests declare structured output fields.
 */
function ReferenceFieldsSection({ groups }: { groups: ReferenceGroup[] }) {
  const [copied, setCopied] = useState<string | null>(null);
  if (groups.length === 0) return null;

  const copy = (id: string, expr: string) => {
    void navigator.clipboard?.writeText(expr);
    setCopied(id);
    window.setTimeout(() => setCopied((c) => (c === id ? null : c)), 1200);
  };

  return (
    <div style={{ marginBottom: '16px', padding: '12px 14px', borderRadius: '10px', border: '1px solid var(--border-color)', background: 'rgba(255,255,255,0.03)', display: 'flex', flexDirection: 'column', gap: '10px' }}>
      <span style={{ fontSize: '0.72rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
        Insert data reference
      </span>
      <span style={{ fontSize: '0.68rem', color: 'var(--text-muted)', marginTop: '-4px' }}>
        Output data from upstream nodes. Click to copy its <code style={{ fontFamily: 'ui-monospace, Menlo, monospace' }}>{'{{ … }}'}</code> reference, then paste into an expression field.
      </span>
      {groups.map((group) => (
        <div key={group.nodeId} style={{ display: 'flex', flexDirection: 'column', gap: '5px' }}>
          <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>
            from <strong style={{ color: '#dbe4ee' }}>{group.label}</strong> <span style={{ opacity: 0.6 }}>({group.nodeId})</span>
          </span>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '5px' }}>
            {group.fields.map((field) => {
              const id = `${group.nodeId}:${field.field}`;
              const isCopied = copied === id;
              return (
                <button
                  key={field.field}
                  type="button"
                  title={`Copy ${field.expr}`}
                  onClick={() => copy(id, field.expr)}
                  style={{ display: 'inline-flex', alignItems: 'center', gap: '5px', padding: '2px 8px', borderRadius: '6px', cursor: 'pointer', fontFamily: 'ui-monospace, Menlo, monospace', fontSize: '0.74rem', background: 'rgba(56,189,248,0.1)', border: '1px solid rgba(56,189,248,0.25)', color: '#9fdcf5' }}
                >
                  {field.field}
                  <span style={{ color: '#34d399', fontWeight: 700 }}>{isCopied ? '✓' : ''}</span>
                </button>
              );
            })}
          </div>
        </div>
      ))}
    </div>
  );
}

/**
 * Scoped inbound-signal fields for the selected node. The inbound `signal` is one instance per run, so
 * its `params.<key>` fields belong to the originating action — shown here grouped by that action, not as
 * canvas-wide globals. Clicking a field copies the `{{ $variables.signal.params.<key> }}` expression so
 * it can be pasted into any expression field (e.g. the Log message, a Condition operand).
 */
function SignalFieldsSection({ groups }: { groups: SignalFieldGroup[] }) {
  const [copied, setCopied] = useState<string | null>(null);
  if (groups.length === 0) return null;

  const copy = (id: string, expr: string) => {
    void navigator.clipboard?.writeText(expr);
    setCopied(id);
    window.setTimeout(() => setCopied((c) => (c === id ? null : c)), 1200);
  };

  return (
    <div style={{ marginBottom: '16px', padding: '12px 14px', borderRadius: '10px', border: '1px solid var(--border-color)', background: 'rgba(255,255,255,0.03)', display: 'flex', flexDirection: 'column', gap: '10px' }}>
      <span style={{ fontSize: '0.72rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
        Signal fields
      </span>
      <span style={{ fontSize: '0.68rem', color: 'var(--text-muted)', marginTop: '-4px' }}>
        Local to this run — each inbound signal starts its own run with its own values; parallel runs never share them.
      </span>
      {groups.map((group) => (
        <div key={group.actionId} style={{ display: 'flex', flexDirection: 'column', gap: '5px' }}>
          <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>
            from <strong style={{ color: '#dbe4ee' }}>{group.label}</strong>
          </span>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '5px' }}>
            {group.fields.map((field) => {
              const styles = getTypeStyles(field.type);
              const fieldId = `${group.actionId}:${field.key}`;
              const expr = `{{ $variables.${group.refPrefix}.${field.key} }}`;
              const isCopied = copied === fieldId;
              return (
                <button
                  key={field.key}
                  type="button"
                  title={`Copy ${expr}`}
                  onClick={() => copy(fieldId, expr)}
                  style={{ display: 'inline-flex', alignItems: 'center', gap: '5px', padding: '2px 8px', borderRadius: '6px', cursor: 'pointer', fontFamily: 'ui-monospace, Menlo, monospace', fontSize: '0.74rem', background: styles.bg, border: styles.border, color: styles.text }}
                >
                  {field.key}
                  <span style={{ color: styles.color, fontWeight: 700 }}>{isCopied ? '✓' : ''}</span>
                </button>
              );
            })}
          </div>
        </div>
      ))}
      <span style={{ fontSize: '0.68rem', color: 'var(--text-muted)' }}>
        Click to copy the <code style={{ fontFamily: 'ui-monospace, Menlo, monospace' }}>{'{{ $variables.signal.<action>.… }}'}</code> reference for a text field. In a Condition, pick the same field from the operand's reference list.
      </span>
    </div>
  );
}

function OverviewSection({ title, empty, children }: { title: string; empty: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
      <span style={{ fontSize: '0.7rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: '0.04em' }}>{title}</span>
      {children || <span style={{ fontSize: '0.78rem', color: 'var(--text-muted)' }}>{empty}</span>}
    </div>
  );
}

// Read-only, always-in-sync summary of a subflow node's variable mapping. Editing now happens on
// the node itself (drag-drop + inline fields); this is the "overview that was getting lost".
function SubflowMappingOverview({
  inputs, outputs, knownGlobals,
}: {
  inputs: Record<string, unknown>[];
  outputs: Record<string, unknown>[];
  knownGlobals: Set<string>;
}) {
  const refName = (value: unknown): string => {
    if (value && typeof value === 'object') {
      const ref = value as { __type?: unknown; variableName?: unknown };
      if (ref.__type === 'variable_ref' && typeof ref.variableName === 'string') return ref.variableName;
    }
    if (typeof value === 'string') {
      const m = value.match(/\{\{\s*\$variables\.([A-Za-z0-9_$]+)\s*\}\}/);
      if (m) return m[1];
      return value.length ? value : '—';
    }
    return '—';
  };
  const mono: React.CSSProperties = { fontFamily: 'monospace', fontSize: '0.78rem' };
  const arrow = <span style={{ color: 'var(--text-muted)' }}>→</span>;
  const newTag = (
    <span style={{ fontSize: '0.56rem', fontWeight: 800, color: '#f0a93b', background: 'rgba(240,169,59,0.14)', border: '1px solid rgba(240,169,59,0.35)', borderRadius: 4, padding: '0 4px' }}>NEW</span>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '14px' }}>
      <OverviewSection title="Inputs · global → local" empty="None — drop a variable on the node's Inputs folder.">
        {inputs.length > 0 ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '3px' }}>
            {inputs.map((row, i) => (
              <div key={i} style={{ ...mono, display: 'flex', alignItems: 'center', gap: '6px', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                <span style={{ color: 'var(--color-accent)' }}>{refName(row.value)}</span> {arrow} <span style={{ color: '#dbe4ee' }}>{(typeof row.target === 'string' && row.target) || '—'}</span>
              </div>
            ))}
          </div>
        ) : null}
      </OverviewSection>
      <OverviewSection title="Outputs · local → global" empty="None — drop a variable on the node's Outputs folder.">
        {outputs.length > 0 ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '3px' }}>
            {outputs.map((row, i) => {
              const global = (typeof row.target === 'string' && row.target) || '';
              const isNew = global.length > 0 && !knownGlobals.has(global);
              return (
                <div key={i} style={{ ...mono, display: 'flex', alignItems: 'center', gap: '6px', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                  <span style={{ color: '#dbe4ee' }}>{(typeof row.source === 'string' && row.source) || '—'}</span> {arrow} <span style={{ color: 'var(--color-accent)' }}>{global || '—'}</span> {isNew ? newTag : null}
                </div>
              );
            })}
          </div>
        ) : null}
      </OverviewSection>
    </div>
  );
}

const INTERFACE_VAR_TYPES = ['string', 'number', 'boolean', 'object'] as const;

// Declares a subflow's interface locals (name + type) on its Start (inputs) / End (outputs) node.
// These surface in the subflow's Global Store so they can be referenced while editing it.
function InterfaceVarEditor({ title, description, rows, onChange }: {
  title: string;
  description: string;
  rows: Record<string, unknown>[];
  onChange: (rows: Record<string, unknown>[]) => void;
}) {
  const fieldStyle: React.CSSProperties = {
    padding: '8px 10px', borderRadius: '6px', background: 'rgba(255,255,255,0.03)',
    border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.82rem', boxSizing: 'border-box',
  };
  const update = (i: number, key: string, value: string) =>
    onChange(rows.map((r, idx) => (idx === i ? { ...r, [key]: value } : r)));
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
      <div>
        <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>{title}</label>
        <span style={{ fontSize: '0.72rem', color: 'var(--text-muted)' }}>{description}</span>
      </div>
      {rows.map((row, i) => (
        <div key={i} style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
          <input
            type="text"
            value={typeof row.name === 'string' ? row.name : ''}
            onChange={(e) => update(i, 'name', e.target.value)}
            placeholder="variable name"
            style={{ ...fieldStyle, flex: 1, minWidth: 0 }}
          />
          <select
            value={typeof row.type === 'string' ? row.type : 'string'}
            onChange={(e) => update(i, 'type', e.target.value)}
            style={{ ...fieldStyle, flex: '0 0 auto', colorScheme: 'dark' }}
          >
            {INTERFACE_VAR_TYPES.map((t) => (
              <option key={t} value={t} style={{ background: 'var(--bg-surface-opaque)', color: '#fff' }}>{t}</option>
            ))}
          </select>
          <button
            onClick={() => onChange(rows.filter((_, idx) => idx !== i))}
            title="Remove"
            style={{ flex: '0 0 auto', padding: '6px', borderRadius: '6px', background: 'rgba(239,68,68,0.1)', border: '1px solid rgba(239,68,68,0.2)', color: 'var(--color-error)', cursor: 'pointer', display: 'flex' }}
          >
            <Trash2 size={13} />
          </button>
        </div>
      ))}
      <button
        onClick={() => onChange([...rows, { name: '', type: 'string' }])}
        style={{ alignSelf: 'flex-start', display: 'flex', alignItems: 'center', gap: '5px', padding: '6px 10px', borderRadius: '6px', background: 'rgba(255,255,255,0.04)', border: '1px solid var(--border-color)', color: 'var(--text-secondary)', fontSize: '0.78rem', cursor: 'pointer' }}
      >
        <Plus size={13} /> Add
      </button>
    </div>
  );
}

function PropertiesPanelImpl({ workflowId, selectedNode, selectedEdge, referenceGroups, onUpdateNodeProperties, onDeleteNode, onDeleteEdge }: PropertiesPanelProps) {
  const signalFieldGroups = useSignalFieldStore((s) => signalGroupsFor(s, selectedNode?.id));
  const [packages, setPackages] = useState<NodePackageSummary[]>([]);
  const [workflows, setWorkflows] = useState<WorkflowDefinition[]>([]);
  const workflowVariables = useVariableStore((state) => state.variables[workflowId || ''] || []);
  const [creatingSubflow, setCreatingSubflow] = useState(false);
  const [subflowQuery, setSubflowQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [schedulePreview, setSchedulePreview] = useState<{ nextFireAtUtc: string; cronExpression: string; timeZoneId: string; isActive: boolean } | null>(null);
  const [scheduleLoading, setScheduleLoading] = useState(false);

  const [prevSelectedNodeId, setPrevSelectedNodeId] = useState<string | null>(null);
  const currentSelectedNodeId = selectedNode?.id ?? null;
  if (currentSelectedNodeId !== prevSelectedNodeId) {
    setPrevSelectedNodeId(currentSelectedNodeId);
    setSchedulePreview(null);
    const isScheduler = selectedNode?.type?.toLowerCase() === 'scheduler' && !!workflowId;
    setScheduleLoading(isScheduler);
  }

  // Hoisted above the early returns (rules of hooks) so ManifestForm's props stay referentially stable
  // across the several panel re-renders that one selection triggers. The manifest was previously re-parsed
  // (fresh JSON.parse → new object) and the onChange re-created on every render, so a memoized ManifestForm
  // couldn't bail — the expensive field render ran 6–9× per selection. Now it runs once.
  const manifest = useMemo<NodeManifest | null>(() => {
    const nodeType = (selectedNode?.type || '').toLowerCase();
    const matched = packages.find((p) => p.id.toLowerCase() === nodeType);
    const version = matched?.versions?.[0];
    if (!version) return null;
    try {
      return (typeof version.manifestJson === 'string' ? JSON.parse(version.manifestJson) : version.manifestJson) as NodeManifest;
    } catch (e) {
      console.error('Error parsing manifest:', e);
      return null;
    }
  }, [packages, selectedNode?.type]);

  const handlePropertiesChange = useCallback((newProperties: Record<string, unknown>) => {
    if (selectedNode) onUpdateNodeProperties(selectedNode.id, newProperties);
  }, [onUpdateNodeProperties, selectedNode]);

  useEffect(() => {
    api.getNodePackages()
      .then(setPackages)
      .catch(err => console.error("Error loading node packages:", err))
      .finally(() => setLoading(false));
  }, []);

  // Load the workflow list (used to populate the subflow picker) whenever a subflow node is selected.
  useEffect(() => {
    if (selectedNode?.type?.toLowerCase() !== 'subflow') {
      return;
    }
    let cancelled = false;
    api.getWorkflows()
      .then((list) => { if (!cancelled) setWorkflows(list); })
      .catch(err => console.error("Error loading workflows for subflow picker:", err));
    return () => { cancelled = true; };
  }, [selectedNode?.type, selectedNode?.id]);

  useEffect(() => {
    if (!selectedNode || selectedNode.type?.toLowerCase() !== 'scheduler' || !workflowId) {
      return;
    }

    let isCancelled = false;

    api.getWorkflowSchedules(workflowId)
      .then((schedules) => {
        if (isCancelled)
        {
          return;
        }

        const matchingSchedule = schedules.find(schedule => schedule.nodeId === selectedNode.id) ?? null;
        setSchedulePreview(matchingSchedule ? {
          nextFireAtUtc: matchingSchedule.nextFireAtUtc,
          cronExpression: matchingSchedule.cronExpression,
          timeZoneId: matchingSchedule.timeZoneId,
          isActive: matchingSchedule.isActive,
        } : null);
      })
      .catch(err => {
        if (!isCancelled) {
          console.error('Error loading workflow schedules:', err);
          setSchedulePreview(null);
        }
      })
      .finally(() => {
        if (!isCancelled) {
          setScheduleLoading(false);
        }
      });

    return () => {
      isCancelled = true;
    };
  }, [selectedNode, workflowId]);

  if (!selectedNode && selectedEdge) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', width: '100%' }}>
        {/* Title Header */}
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '20px 24px', borderBottom: '1px solid var(--border-color)' }}>
          <div>
            <h2 style={{ fontSize: '1.05rem', fontWeight: 700, color: '#fff', display: 'flex', alignItems: 'center', gap: '8px' }}>
              <Link size={18} color="var(--color-accent)" />
              Connection
            </h2>
            <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>ID: {selectedEdge.id}</span>
          </div>
          <button
            onClick={() => onDeleteEdge(selectedEdge.id)}
            style={{
              padding: '8px',
              borderRadius: '6px',
              background: 'rgba(239, 68, 68, 0.1)',
              border: '1px solid rgba(239, 68, 68, 0.2)',
              color: 'var(--color-error)',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              transition: 'background 0.2s',
            }}
            onMouseOver={(e) => e.currentTarget.style.background = 'rgba(239, 68, 68, 0.2)'}
            onMouseOut={(e) => e.currentTarget.style.background = 'rgba(239, 68, 68, 0.1)'}
          >
            <Trash2 size={16} />
          </button>
        </div>

        {/* Edge Details Container */}
        <div style={{ flex: 1, overflowY: 'auto', padding: '24px', display: 'flex', flexDirection: 'column', gap: '16px' }}>
          <div>
            <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', marginBottom: '6px', textTransform: 'uppercase' }}>Source Node</label>
            <div style={{ padding: '10px 14px', borderRadius: '8px', background: 'rgba(255, 255, 255, 0.03)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.85rem' }}>
              {selectedEdge.source} <span style={{ color: 'var(--color-accent)', fontSize: '0.75rem', marginLeft: '6px' }}>({selectedEdge.sourceHandle || 'default'} handle)</span>
            </div>
          </div>

          <div style={{ display: 'flex', justifyContent: 'center', margin: '4px 0' }}>
            <span style={{ color: 'var(--text-muted)', fontSize: '1.2rem' }}>↓</span>
          </div>

          <div>
            <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', marginBottom: '6px', textTransform: 'uppercase' }}>Target Node</label>
            <div style={{ padding: '10px 14px', borderRadius: '8px', background: 'rgba(255, 255, 255, 0.03)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.85rem' }}>
              {selectedEdge.target} <span style={{ color: 'var(--color-success)', fontSize: '0.75rem', marginLeft: '6px' }}>({selectedEdge.targetHandle || 'default'} handle)</span>
            </div>
          </div>
        </div>
      </div>
    );
  }

  if (!selectedNode) {
    return (
      <div style={{ padding: '24px', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '100%', color: 'var(--text-muted)', textAlign: 'center', gap: '12px' }}>
        <ShieldAlert size={36} color="var(--text-muted)" style={{ opacity: 0.5 }} />
        <div>
          <h3 style={{ color: '#fff', fontSize: '1rem', fontWeight: 700, marginBottom: '4px' }}>Property Inspector</h3>
          <p style={{ fontSize: '0.8rem' }}>Select a node on the canvas to configure its properties and trigger behaviors.</p>
        </div>
      </div>
    );
  }

  const id = selectedNode.id;
  const type = selectedNode.type || '';
  const properties = selectedNode.data?.properties as Record<string, unknown> || {};
  // `manifest` and `handlePropertiesChange` are computed above (hoisted for hooks + referential stability).

  // Mint a fresh child workflow (start -> end, the minimum a subflow must contain) and link it
  // to this node. The child is persisted immediately so the compiler can resolve it and so
  // double-clicking the node can open it on its own canvas.
  const createAndLinkSubflow = async () => {
    setCreatingSubflow(true);
    try {
      const newId = crypto.randomUUID();
      const shortId = newId.slice(0, 8);
      const startId = `start-${shortId}`;
      const endId = `end-${shortId}`;
      const newName = `Subflow ${shortId}`;
      const definition: WorkflowDefinition = {
        id: { value: newId },
        name: newName,
        nodes: [
          { id: { value: startId }, type: 'start', properties: { _metadata: { x: 150, y: 200 } } },
          { id: { value: endId }, type: 'end', properties: { _metadata: { x: 520, y: 200 } } },
        ],
        edges: [
          { id: `e-${startId}-${endId}`, from: { value: startId }, output: 'result', to: { value: endId }, input: 'in' },
        ],
      };
      await api.saveWorkflow(definition);
      // Publish it too, so the compiler (which resolves subflows from published definitions) can
      // find it immediately — otherwise calling it fails with ERR_SUBFLOW_NOT_FOUND.
      await api.publishWorkflowDefinition(definition);
      setWorkflows((prev) => [...prev, definition]);
      handlePropertiesChange({ ...properties, subflowId: newId, subflowName: newName });
    } catch (err) {
      alert(`Could not create subflow: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setCreatingSubflow(false);
    }
  };

  if (type === 'resourcePicker') {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', width: '100%' }}>
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: '12px', padding: '18px 20px 16px', borderBottom: '1px solid var(--border-color)' }}>
          <span style={{ width: 34, height: 34, flex: 'none', borderRadius: 10, background: 'rgba(34,211,238,0.13)', border: '1px solid rgba(34,211,238,0.32)', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#22d3ee" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><ellipse cx="12" cy="5" rx="8" ry="3" /><path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" /></svg>
          </span>
          <div style={{ flex: 1, minWidth: 0 }}>
            <h2 style={{ fontSize: '1rem', fontWeight: 700, color: '#e6edf3', letterSpacing: '-0.01em', margin: 0 }}>
              {manifest?.displayName || 'Resource Picker'}
            </h2>
            <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', fontFamily: 'ui-monospace, Menlo, monospace' }}>ID: {id}</span>
          </div>
          <button
            onClick={() => onDeleteNode(id)}
            style={{ width: 34, height: 34, flex: 'none', borderRadius: 9, background: 'rgba(240,85,109,0.1)', border: '1px solid rgba(240,85,109,0.28)', color: '#f0556d', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center' }}
          >
            <Trash2 size={16} />
          </button>
        </div>
        <div style={{ flex: 1, overflowY: 'auto', padding: '24px' }}>
          <ResourcePickerPropertyForm
            workflowId={workflowId}
            properties={properties}
            onChange={handlePropertiesChange}
          />
        </div>
      </div>
    );
  }

  if (type === 'pollingTrigger') {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', width: '100%' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '20px 24px', borderBottom: '1px solid var(--border-color)' }}>
          <div>
            <h2 style={{ fontSize: '1.05rem', fontWeight: 700, textTransform: 'capitalize', color: '#fff' }}>
              {manifest?.displayName || 'Polling Trigger'} Properties
            </h2>
            <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>ID: {id}</span>
          </div>
          <button
            onClick={() => onDeleteNode(id)}
            style={{
              padding: '8px',
              borderRadius: '6px',
              background: 'rgba(239, 68, 68, 0.1)',
              border: '1px solid rgba(239, 68, 68, 0.2)',
              color: 'var(--color-error)',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              transition: 'background 0.2s',
            }}
            onMouseOver={(e) => e.currentTarget.style.background = 'rgba(239, 68, 68, 0.2)'}
            onMouseOut={(e) => e.currentTarget.style.background = 'rgba(239, 68, 68, 0.1)'}
          >
            <Trash2 size={16} />
          </button>
        </div>
        <div style={{ flex: 1, overflowY: 'auto', padding: '24px' }}>
          <PollingTriggerPropertyForm
            workflowId={workflowId}
            properties={properties}
            onChange={handlePropertiesChange}
          />
        </div>
      </div>
    );
  }

  if (type.startsWith('openapi.')) {
    const specId = type.replace(/^openapi\./, '');
    const operationId = (properties.operationId as string) || '';
    const args = (properties.arguments as Record<string, unknown>) || {};
    const serverConfigId = (properties.serverConfigId as string) || undefined;

    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', width: '100%' }}>
        {/* Title Header */}
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '20px 24px', borderBottom: '1px solid var(--border-color)' }}>
          <div>
            <h2 style={{ fontSize: '1.05rem', fontWeight: 700, textTransform: 'capitalize', color: '#fff' }}>
              {manifest?.displayName || type} Properties
            </h2>
            <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>ID: {id}</span>
          </div>
          <button
            onClick={() => onDeleteNode(id)}
            style={{
              padding: '8px',
              borderRadius: '6px',
              background: 'rgba(239, 68, 68, 0.1)',
              border: '1px solid rgba(239, 68, 68, 0.2)',
              color: 'var(--color-error)',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              transition: 'background 0.2s',
            }}
            onMouseOver={(e) => e.currentTarget.style.background = 'rgba(239, 68, 68, 0.2)'}
            onMouseOut={(e) => e.currentTarget.style.background = 'rgba(239, 68, 68, 0.1)'}
          >
            <Trash2 size={16} />
          </button>
        </div>

        {/* Fields Container */}
        <div style={{ flex: 1, overflowY: 'auto', padding: '24px' }}>
          <RestCallerPropertyForm
            workflowId={workflowId}
            specId={specId}
            operationId={operationId}
            arguments={args}
            onArgumentsChange={(newArgs) => handlePropertiesChange({ ...properties, arguments: newArgs })}
            onOperationIdChange={(newOpId) => handlePropertiesChange({ ...properties, operationId: newOpId, arguments: {} })}
            serverConfigId={serverConfigId}
            onServerConfigIdChange={(newConfigId) => handlePropertiesChange({ ...properties, serverConfigId: newConfigId })}
          />
        </div>
      </div>
    );
  }

  const formattedNextFire = schedulePreview?.nextFireAtUtc
    ? new Date(schedulePreview.nextFireAtUtc).toLocaleString()
    : null;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', width: '100%' }}>
      {/* Title Header */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '20px 24px', borderBottom: '1px solid var(--border-color)' }}>
        <div>
          <h2 style={{ fontSize: '1.05rem', fontWeight: 700, textTransform: 'capitalize', color: '#fff' }}>
            {manifest?.displayName || type} Node Properties
          </h2>
          <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>ID: {id}</span>
        </div>
        <button
          onClick={() => onDeleteNode(id)}
          style={{
            padding: '8px',
            borderRadius: '6px',
            background: 'rgba(239, 68, 68, 0.1)',
            border: '1px solid rgba(239, 68, 68, 0.2)',
            color: 'var(--color-error)',
            cursor: 'pointer',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            transition: 'background 0.2s',
          }}
          onMouseOver={(e) => e.currentTarget.style.background = 'rgba(239, 68, 68, 0.2)'}
          onMouseOut={(e) => e.currentTarget.style.background = 'rgba(239, 68, 68, 0.1)'}
        >
          <Trash2 size={16} />
        </button>
      </div>

      {/* Fields Container */}
      <div style={{ flex: 1, overflowY: 'auto', padding: '24px' }}>
        <SignalFieldsSection groups={signalFieldGroups} />
        <ReferenceFieldsSection groups={referenceGroups ?? []} />
        {type.toLowerCase() === 'scheduler' && (
          <div
            style={{
              marginBottom: '16px',
              padding: '14px 16px',
              borderRadius: '10px',
              border: '1px solid var(--border-color)',
              background: 'rgba(255, 255, 255, 0.03)',
              display: 'flex',
              flexDirection: 'column',
              gap: '6px',
            }}
          >
            <span style={{ fontSize: '0.72rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
              Schedule Status
            </span>
            {scheduleLoading ? (
              <span style={{ fontSize: '0.82rem', color: 'var(--text-muted)' }}>Loading next fire...</span>
            ) : schedulePreview ? (
              <>
                <span style={{ fontSize: '0.92rem', color: '#fff', fontWeight: 600 }}>
                  Next fire: {formattedNextFire || schedulePreview.nextFireAtUtc}
                </span>
                <span style={{ fontSize: '0.78rem', color: 'var(--text-secondary)' }}>
                  {schedulePreview.cronExpression} in {schedulePreview.timeZoneId}
                </span>
                <span style={{ fontSize: '0.76rem', color: schedulePreview.isActive ? 'var(--color-success)' : 'var(--color-warning)' }}>
                  {schedulePreview.isActive ? 'Active schedule' : 'Inactive schedule'}
                </span>
              </>
            ) : (
              <span style={{ fontSize: '0.82rem', color: 'var(--text-muted)' }}>Save the workflow to compute the next scheduled fire.</span>
            )}
          </div>
        )}
        {(type.toLowerCase() === 'start' || type.toLowerCase() === 'end') && (
          <div style={{ marginBottom: '16px' }}>
            {(() => {
              const key = type.toLowerCase() === 'start' ? 'interfaceInputs' : 'interfaceOutputs';
              const rows = Array.isArray(properties[key]) ? (properties[key] as Record<string, unknown>[]) : [];
              return (
                <InterfaceVarEditor
                  title={type.toLowerCase() === 'start' ? 'Subflow inputs (from caller)' : 'Subflow outputs (to caller)'}
                  description={type.toLowerCase() === 'start'
                    ? 'Local variables this subflow expects from its caller. They appear in the Workflow variables panel so you can use them while editing here.'
                    : 'Local variables this subflow produces and returns to its caller.'}
                  rows={rows}
                  onChange={(next) => handlePropertiesChange({ ...properties, [key]: next })}
                />
              );
            })()}
          </div>
        )}
        {type.toLowerCase() === 'subflow' ? (
          (() => {
            const subflowId = (properties.subflowId as string) || '';
            const selectable = workflows.filter((w) => w.id.value !== workflowId);
            const referenced = workflows.find((w) => w.id.value === subflowId);
            const isDangling = subflowId.length > 0 && !referenced;
            const query = subflowQuery.trim().toLowerCase();
            const filtered = query
              ? selectable.filter((w) => (w.name || w.id.value).toLowerCase().includes(query))
              : selectable;
            const inputRows = Array.isArray(properties.subflowInputs) ? (properties.subflowInputs as Record<string, unknown>[]) : [];
            const outputRows = Array.isArray(properties.subflowOutputs) ? (properties.subflowOutputs as Record<string, unknown>[]) : [];
            const knownGlobalNames = new Set(workflowVariables.map((v) => v.name));
            return (
              <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                <div>
                  <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', marginBottom: '6px', textTransform: 'uppercase' }}>
                    Subflow workflow
                  </label>
                  {referenced && (
                    <div style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', marginBottom: '6px' }}>
                      Selected: <strong style={{ color: '#fff' }}>{referenced.name || referenced.id.value}</strong>
                    </div>
                  )}
                  <input
                    type="text"
                    value={subflowQuery}
                    onChange={(e) => setSubflowQuery(e.target.value)}
                    placeholder="Search workflows…"
                    style={{
                      width: '100%',
                      padding: '10px 14px',
                      borderRadius: '8px',
                      background: 'rgba(255, 255, 255, 0.03)',
                      border: '1px solid var(--border-color)',
                      color: '#fff',
                      fontSize: '0.85rem',
                      marginBottom: '6px',
                      boxSizing: 'border-box',
                    }}
                  />
                  <div style={{ maxHeight: '200px', overflowY: 'auto', border: '1px solid var(--border-color)', borderRadius: '8px' }}>
                    {filtered.length === 0 ? (
                      <div style={{ padding: '10px 14px', fontSize: '0.82rem', color: 'var(--text-muted)' }}>
                        {selectable.length === 0 ? 'No other workflows to use as a subflow.' : 'No matches.'}
                      </div>
                    ) : (
                      filtered.map((w) => {
                        const selected = w.id.value === subflowId;
                        return (
                          <button
                            key={w.id.value}
                            onClick={() => {
                              handlePropertiesChange({ ...properties, subflowId: w.id.value, subflowName: w.name ?? '' });
                              setSubflowQuery('');
                            }}
                            style={{
                              display: 'block',
                              width: '100%',
                              textAlign: 'left',
                              padding: '9px 14px',
                              background: selected ? 'rgba(99, 102, 241, 0.18)' : 'transparent',
                              border: 'none',
                              borderBottom: '1px solid rgba(255,255,255,0.04)',
                              color: '#fff',
                              fontSize: '0.85rem',
                              fontWeight: selected ? 700 : 400,
                              cursor: 'pointer',
                            }}
                            onMouseOver={(e) => { if (!selected) e.currentTarget.style.background = 'rgba(255,255,255,0.05)'; }}
                            onMouseOut={(e) => { if (!selected) e.currentTarget.style.background = 'transparent'; }}
                          >
                            {w.name || w.id.value}
                          </button>
                        );
                      })
                    )}
                  </div>
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                  <div style={{ flex: 1, height: '1px', background: 'var(--border-color)' }} />
                  <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>or</span>
                  <div style={{ flex: 1, height: '1px', background: 'var(--border-color)' }} />
                </div>
                <button
                  onClick={createAndLinkSubflow}
                  disabled={creatingSubflow}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    gap: '6px',
                    padding: '10px 14px',
                    borderRadius: '8px',
                    background: 'rgba(99, 102, 241, 0.12)',
                    border: '1px solid rgba(99, 102, 241, 0.35)',
                    color: '#fff',
                    fontSize: '0.85rem',
                    fontWeight: 600,
                    cursor: creatingSubflow ? 'default' : 'pointer',
                    opacity: creatingSubflow ? 0.6 : 1,
                  }}
                >
                  <Plus size={15} /> {creatingSubflow ? 'Creating…' : 'Create new subflow'}
                </button>
                {isDangling && (
                  <p style={{ fontSize: '0.78rem', color: 'var(--color-warning)', margin: 0 }}>
                    Referenced workflow <code>{subflowId}</code> was not found in the list. Pick an existing workflow.
                  </p>
                )}
                {subflowId && !isDangling && (
                  <p style={{ fontSize: '0.78rem', color: 'var(--text-muted)', margin: 0 }}>
                    Double-click the node on the canvas to open this subflow.
                  </p>
                )}
                {subflowId && !isDangling && (() => {
                  const iface = referenced ? extractSubflowInterface(referenced) : { inputs: [], outputs: [] };
                  const hasInterface = iface.inputs.length > 0 || iface.outputs.length > 0;
                  return (
                    <>
                      <div style={{ height: '1px', background: 'var(--border-color)' }} />
                      {hasInterface ? (
                        <>
                          <p style={{ fontSize: '0.76rem', color: 'var(--text-muted)', margin: 0 }}>
                            Bind a variable to each of the subflow's declared inputs/outputs — drop a Workflow variables pill onto a slot on the node, or type the value/name.
                          </p>
                          <SubflowMappingOverview inputs={inputRows} outputs={outputRows} knownGlobals={knownGlobalNames} />
                        </>
                      ) : (
                        <p style={{ fontSize: '0.76rem', color: 'var(--color-warning)', margin: 0 }}>
                          This subflow has no declared inputs or outputs yet. Double-click the node to open it, then declare them on its <strong>Start</strong> (inputs) and <strong>End</strong> (outputs) nodes.
                        </p>
                      )}
                    </>
                  );
                })()}
              </div>
            );
          })()
        ) : type.toLowerCase() === 'scheduler' ? (
          <SchedulerPropertyForm properties={properties} onChange={handlePropertiesChange} />
        ) : type.toLowerCase() === 'httprequest' ? (
          <HttpRequestPropertyForm properties={properties} onChange={handlePropertiesChange} />
        ) : loading ? (
          <p style={{ fontSize: '0.8rem', color: 'var(--text-muted)' }}>Loading property descriptors...</p>
        ) : manifest ? (
          <ManifestForm
            workflowId={workflowId}
            nodeId={id}
            manifest={manifest}
            properties={properties}
            onChange={handlePropertiesChange}
          />
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
            <p style={{ fontSize: '0.8rem', color: 'var(--color-warning)' }}>
              No active package manifest found for type "{type}". Properties cannot be verified.
            </p>
          </div>
        )}
        {type.toLowerCase() !== 'stickynote' && type.toLowerCase() !== 'group' && (
          <>
            <NodeIoInspector workflowId={workflowId} nodeId={id} manifest={manifest} />
            <PinOutputEditor properties={properties} onChange={handlePropertiesChange} />
          </>
        )}
      </div>
    </div>
  );
}

// Memoized: PropertiesPanel is a direct child of Canvas, which re-renders on every node-position change
// (i.e. every drag frame). Its props (selectedNode/selectedEdge/workflowId + stable useCallback handlers)
// don't change during a drag, so without memo the whole panel subtree re-rendered every frame. Memo lets
// it skip those; it still re-renders for real selection changes and its own store subscriptions.
export const PropertiesPanel = memo(PropertiesPanelImpl);
