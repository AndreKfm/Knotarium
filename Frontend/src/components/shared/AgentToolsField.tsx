// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo, useState } from 'react';
import type { ParameterDefinition, WorkflowDefinition } from '../../types';
import { api } from '../../utils/api';
import {
  readToolBindings,
  validateToolBindings,
  emptyBinding,
  type AgentToolBinding,
  type ToolParameter,
  type ToolParameterType,
} from '../../node-editor/agentTools';

interface AgentToolsFieldProps {
  param: ParameterDefinition;
  value: unknown;
  onChange: (value: unknown) => void;
}

const labelStyle: React.CSSProperties = { fontSize: '0.8rem', color: 'var(--text-secondary)', fontWeight: 500 };
const smallLabel: React.CSSProperties = { fontSize: '0.7rem', color: 'var(--text-muted)' };
const inputStyle: React.CSSProperties = {
  width: '100%', background: 'var(--input-bg, transparent)', border: '1px solid var(--border-color)',
  color: 'var(--text-primary)', borderRadius: '6px', padding: '5px 7px', fontSize: '0.78rem', boxSizing: 'border-box',
};
const ghostButton: React.CSSProperties = {
  background: 'transparent', border: '1px solid var(--border-color)', color: 'var(--text-secondary)',
  borderRadius: '6px', padding: '3px 8px', fontSize: '0.7rem', cursor: 'pointer',
};

const PARAM_TYPES: ToolParameterType[] = ['string', 'number', 'boolean'];

/**
 * List editor for the AI Agent node's `tools` property. Each row binds a workflow (target), the model-facing
 * name/description, a parameter contract, and the outputs projected back as the tool result. The stored value
 * is a live array of tool bindings; validation mirrors the backend (see node-editor/agentTools.ts).
 */
export function AgentToolsField({ param, value, onChange }: AgentToolsFieldProps) {
  const bindings = useMemo(() => readToolBindings(value), [value]);
  const problems = useMemo(() => validateToolBindings(bindings), [bindings]);
  const [workflows, setWorkflows] = useState<WorkflowDefinition[]>([]);

  useEffect(() => {
    let live = true;
    api.getWorkflows().then((w) => { if (live) setWorkflows(w); }).catch(() => { /* dropdown stays empty */ });
    return () => { live = false; };
  }, []);

  const commit = (next: AgentToolBinding[]) => onChange(next.length > 0 ? next : undefined);
  const patchTool = (i: number, patch: Partial<AgentToolBinding>) =>
    commit(bindings.map((b, idx) => (idx === i ? { ...b, ...patch } : b)));
  const addTool = () => commit([...bindings, emptyBinding()]);
  const removeTool = (i: number) => commit(bindings.filter((_, idx) => idx !== i));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <label style={labelStyle}>{param.name}</label>
        <button type="button" onClick={addTool} style={ghostButton}>+ Add tool</button>
      </div>

      {param.description && <span style={smallLabel}>{param.description}</span>}

      {problems.length > 0 && (
        <ul style={{ margin: 0, paddingLeft: '16px', fontSize: '0.72rem', color: 'var(--color-error)' }}>
          {problems.map((p, i) => <li key={i}>{p}</li>)}
        </ul>
      )}

      {bindings.length === 0 && (
        <span style={smallLabel}>
          No tools yet. Add a workflow the agent may call. <strong>List only workflows you would let the incoming data invoke.</strong>
        </span>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
        {bindings.map((tool, i) => (
          <ToolCard
            key={i}
            tool={tool}
            workflows={workflows}
            onChange={(patch) => patchTool(i, patch)}
            onRemove={() => removeTool(i)}
          />
        ))}
      </div>
    </div>
  );
}

interface ToolCardProps {
  tool: AgentToolBinding;
  workflows: WorkflowDefinition[];
  onChange: (patch: Partial<AgentToolBinding>) => void;
  onRemove: () => void;
}

function ToolCard({ tool, workflows, onChange, onRemove }: ToolCardProps) {
  // Local buffer for the comma-separated outputs so a trailing comma survives typing (the committed value
  // is the split+trimmed array; displaying that back would strip the separator mid-keystroke).
  const [outputsText, setOutputsText] = useState(tool.outputs.join(', '));

  const setParam = (idx: number, patch: Partial<ToolParameter>) =>
    onChange({ parameters: tool.parameters.map((p, j) => (j === idx ? { ...p, ...patch } : p)) });
  const addParam = () =>
    onChange({ parameters: [...tool.parameters, { name: '', type: 'string', required: false }] });
  const removeParam = (idx: number) =>
    onChange({ parameters: tool.parameters.filter((_, j) => j !== idx) });

  return (
    <div style={{ border: '1px solid var(--border-color)', borderRadius: '8px', padding: '10px', display: 'flex', flexDirection: 'column', gap: '8px' }}>
      <div style={{ display: 'flex', gap: '6px', alignItems: 'center' }}>
        <select
          aria-label="Target workflow"
          value={tool.workflowId}
          onChange={(e) => onChange({ workflowId: e.target.value })}
          style={{ ...inputStyle, flex: 1 }}
        >
          <option value="">Select a workflow…</option>
          {tool.workflowId && !workflows.some((w) => w.id.value === tool.workflowId) && (
            <option value={tool.workflowId}>{tool.workflowId} (not found)</option>
          )}
          {workflows.map((w) => (
            <option key={w.id.value} value={w.id.value}>{w.name}</option>
          ))}
        </select>
        <button type="button" onClick={onRemove} title="Remove tool" style={ghostButton}>✕</button>
      </div>

      <div style={{ display: 'flex', gap: '6px' }}>
        <input
          aria-label="Tool name"
          placeholder="tool_name"
          value={tool.name}
          onChange={(e) => onChange({ name: e.target.value })}
          style={{ ...inputStyle, flex: 1 }}
        />
      </div>

      <textarea
        aria-label="Tool description"
        placeholder="What the tool does — the model's only guidance on when to use it."
        value={tool.description}
        onChange={(e) => onChange({ description: e.target.value })}
        rows={2}
        style={{ ...inputStyle, resize: 'vertical' }}
      />

      <div style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
          <span style={smallLabel}>Parameters</span>
          <button type="button" onClick={addParam} style={ghostButton}>+ Add</button>
        </div>
        {tool.parameters.map((p, idx) => (
          <div key={idx} style={{ display: 'flex', gap: '4px', alignItems: 'center' }}>
            <input
              aria-label="Parameter name"
              placeholder="name"
              value={p.name}
              onChange={(e) => setParam(idx, { name: e.target.value })}
              style={{ ...inputStyle, flex: 2 }}
            />
            <select
              aria-label="Parameter type"
              value={p.type}
              onChange={(e) => setParam(idx, { type: e.target.value as ToolParameterType })}
              style={{ ...inputStyle, flex: 1 }}
            >
              {PARAM_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
            </select>
            <label style={{ ...smallLabel, display: 'flex', alignItems: 'center', gap: '3px', whiteSpace: 'nowrap' }}>
              <input
                type="checkbox"
                checked={p.required}
                onChange={(e) => setParam(idx, { required: e.target.checked })}
              />
              req
            </label>
            <button type="button" onClick={() => removeParam(idx)} title="Remove parameter" style={ghostButton}>✕</button>
          </div>
        ))}
      </div>

      <label style={{ display: 'flex', flexDirection: 'column', gap: '3px' }}>
        <span style={smallLabel}>Outputs (comma-separated global names projected as the tool result)</span>
        <input
          aria-label="Tool outputs"
          placeholder="customer, found"
          value={outputsText}
          onChange={(e) => {
            setOutputsText(e.target.value);
            onChange({ outputs: e.target.value.split(',').map((s) => s.trim()).filter((s) => s.length > 0) });
          }}
          style={inputStyle}
        />
      </label>
    </div>
  );
}
