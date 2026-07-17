// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { lazy, memo, Suspense, useEffect, useMemo, useState } from 'react';
import { api } from '../../utils/api';
import type { ParameterDefinition, NodeManifest, NotificationChannel } from '../../types';
import { useVariableStore } from '../../stores/useVariableStore';
import { useInlineCodeEditorStore } from '../../stores/useInlineCodeEditorStore';
import { VariableToken } from '../VariableToken';
import { InlineCodeEditorModal, registerCsharpInlineCompletions } from './InlineCodeEditorModal';
import { AsyncOptionsField } from './AsyncOptionsField';
import { DynamicFieldsField } from './DynamicFieldsField';
import { AgentToolsField } from './AgentToolsField';
import { ModelCombo } from './ModelCombo';
import { ConditionLogicField } from './ConditionLogicField';
import { ExpressionField } from './ExpressionField';
import { variableRefExpression } from '../../utils/variableExpression';
import { buildExpressionCompletions } from '../../utils/expressionCompletions';
import { hasVariablePath, pathContainerKind, variablePathHead } from '../../utils/variablePath';

// Monaco is heavy; load it only when a code field is actually rendered.
const CodeEditor = lazy(() => import('@monaco-editor/react'));

interface ManifestFormProps {
  workflowId?: string | null;
  nodeId?: string | null;
  manifest: NodeManifest;
  properties: Record<string, unknown>;
  onChange: (properties: Record<string, unknown>) => void;
}

interface CredentialItem {
  id: string;
  name: string;
}

export interface FieldDropWrapperProps {
  workflowId?: string | null;
  value: any;
  onChange: (value: any) => void;
  children: React.ReactNode;
}

export function FieldDropWrapper({ workflowId, value, onChange, children }: FieldDropWrapperProps) {
  const [isDragOver, setIsDragOver] = useState(false);
  const variables = useVariableStore(state => workflowId ? (state.variables[workflowId] || []) : []);
  const addVariable = useVariableStore(state => state.addVariable);
  
  // Detect if value is a variable reference
  const isRef = value && typeof value === 'object' && value.__type === 'variable_ref';
  const refVarId = isRef ? value.variableId : null;
  const refVar = refVarId ? variables.find(v => v.id === refVarId) : null;

  const isDraggingToken = useVariableStore(state => state.isDraggingToken);
  const isDraggingOutput = useVariableStore(state => state.isDraggingOutput);

  const handleDragOver = (e: React.DragEvent) => {
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
        console.error('Failed to parse dropped variable token:', err);
      }
    }

    const outputDataRaw = e.dataTransfer.getData('application/knotarium-node-output');
    if (!variableId && outputDataRaw && workflowId) {
      try {
        const outputData = JSON.parse(outputDataRaw);
        if (outputData && outputData.nodeId && outputData.outputHandle) {
          const existing = variables.find(
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
        console.error('Failed to parse dropped node output:', err);
      }
    }

    if (variableId && variableName) {
      onChange({
        __type: 'variable_ref',
        variableId,
        variableName
      });
    }
  };

  const handleTokenDragStart = (e: React.DragEvent<HTMLDivElement>, v: typeof refVar) => {
    if (!v) return;
    const tokenData = {
      variableId: v.id,
      variableName: v.name,
      type: v.type,
    };
    e.dataTransfer.setData('application/knotarium-variable-token', JSON.stringify(tokenData));
    e.dataTransfer.effectAllowed = 'copy';
    useVariableStore.getState().setDraggingToken(true, tokenData);
  };

  const handleTokenDragEnd = () => {
    useVariableStore.getState().setDraggingToken(false, null);
  };

  const handleRemove = () => {
    onChange('');
  };

  // Expand the pill into the equivalent expression string so it can be mixed with literal
  // text (e.g. "Received: {{ ... }}"). Set Variable globals read via $variables.<name>
  // (they aren't node outputs); promoted node outputs use the $node.<id>.output.<field>
  // form, which also tolerates hyphenated node ids the $variables tokenizer would choke on.
  const handleConvertToText = () => {
    if (refVar) {
      onChange(variableRefExpression(refVar));
    }
  };

  if (isRef) {
    return (
      <div
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
        style={{
          border: isDragOver ? '1px dashed var(--color-accent)' : '1px solid var(--border-color)',
          background: isDragOver ? 'rgba(99, 102, 241, 0.08)' : 'rgba(0, 0, 0, 0.25)',
          borderRadius: '8px',
          padding: '10px 14px',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          minHeight: '42px',
          transition: 'all 0.2s',
        }}
      >
        {refVar ? (
          <>
            <VariableToken
              name={refVar.name}
              type={refVar.type}
              value={refVar.value}
              status={refVar.status}
              draggable={true}
              onDragStart={(e) => handleTokenDragStart(e, refVar)}
              onDragEnd={handleTokenDragEnd}
              onRemove={handleRemove}
              onMouseEnter={() => useVariableStore.getState().setHoveredVariableId(refVar.id)}
              onMouseLeave={() => useVariableStore.getState().setHoveredVariableId(null)}
              onClick={(e) => { e.stopPropagation(); useVariableStore.getState().togglePinnedVariableId(refVar.id); }}
            />
            <button
              onClick={(e) => { e.stopPropagation(); handleConvertToText(); }}
              title="Edit as text — expand into an expression so you can add surrounding text, e.g. Received: {{ … }}"
              style={{
                background: 'rgba(255,255,255,0.04)',
                border: '1px solid var(--border-color)',
                color: 'var(--text-secondary)',
                borderRadius: '6px',
                padding: '3px 8px',
                fontSize: '0.7rem',
                fontFamily: 'ui-monospace, Menlo, monospace',
                cursor: 'pointer',
                flex: 'none',
              }}
            >
              {'{{ }}'}
            </button>
          </>
        ) : (
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px', fontSize: '0.78rem', color: 'var(--color-error)', fontWeight: 600 }}>
            <span>Broken Reference: {value.variableName}</span>
            <button
              onClick={handleRemove}
              style={{
                background: 'rgba(239, 68, 68, 0.1)',
                border: '1px solid rgba(239, 68, 68, 0.2)',
                color: 'var(--color-error)',
                borderRadius: '4px',
                padding: '2px 8px',
                fontSize: '0.68rem',
                cursor: 'pointer',
              }}
            >
              Clear
            </button>
          </div>
        )}
      </div>
    );
  }

  return (
    <div
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
      style={{
        border: isDragOver ? '1px dashed var(--color-accent)' : '1px solid transparent',
        background: isDragOver ? 'rgba(99, 102, 241, 0.05)' : 'transparent',
        borderRadius: '8px',
        transition: 'all 0.2s',
      }}
    >
      {children}
    </div>
  );
}

// Defines which parameters are visible per mode value, keyed by the parameter
// named "mode". Only needed for nodes where mode controls what other fields apply.
const MODE_VISIBLE_PARAMS: Record<string, string[]> = {
  count:   ['mode', 'count'],
  foreach: ['mode', 'collection'],
  while:   ['mode', 'condition'],
  batch:   ['mode', 'collection', 'batchSize'],
};

// Parameters that are always hidden in the properties panel regardless of mode
// (they are wired via edges, not manually configured).
const ALWAYS_HIDDEN = new Set(['end']);

function getVisibleParams(
  params: ParameterDefinition[],
  properties: Record<string, unknown>,
): ParameterDefinition[] {
  const modeParam = params.find(p => p.name === 'mode');
  if (!modeParam) {
    // No mode field — just hide always-hidden params.
    return params.filter(p => !ALWAYS_HIDDEN.has(p.name));
  }

  const currentMode = (properties['mode'] as string | undefined)?.toLowerCase() ?? '';
  const allowed = MODE_VISIBLE_PARAMS[currentMode];

  if (!allowed) {
    // Unknown/unset mode — show everything except always-hidden.
    return params.filter(p => !ALWAYS_HIDDEN.has(p.name));
  }

  // Return only params in the allowed list, preserving manifest order but
  // always putting 'mode' first.
  return [
    ...params.filter(p => p.name === 'mode'),
    ...params.filter(p => p.name !== 'mode' && allowed.includes(p.name)),
  ];
}

function ManifestFormImpl({ workflowId, nodeId, manifest, properties, onChange }: ManifestFormProps) {
  const [credentials, setCredentials] = useState<CredentialItem[]>([]);
  const [notificationChannels, setNotificationChannels] = useState<NotificationChannel[]>([]);
  const [codeModalField, setCodeModalField] = useState<string | null>(null);

  // Schema-driven `{{ }}` autocomplete candidates, sourced from the workflow's referenceable variables
  // (promoted upstream node outputs + Set Variable globals). Perf-safe — no live-graph subscription.
  const variables = useVariableStore((state) => (workflowId ? state.variables[workflowId] || [] : []));
  const expressionCompletions = useMemo(() => buildExpressionCompletions(variables), [variables]);

  // Open the editor when the canvas requests it (double-click on an Inline Code node).
  const editorRequestNodeId = useInlineCodeEditorStore(s => s.requestNodeId);
  const clearEditorRequest = useInlineCodeEditorStore(s => s.clearRequest);
  useEffect(() => {
    if (!editorRequestNodeId || editorRequestNodeId !== nodeId) return;
    const codeParam = manifest.parameters?.find(p => p.type === 'code');
    if (codeParam) setCodeModalField(codeParam.name);
    clearEditorRequest();
  }, [editorRequestNodeId, nodeId, manifest, clearEditorRequest]);

  useEffect(() => {
    const hasCreds = manifest.parameters?.some(p => p.type === 'credentialRef');
    if (hasCreds) {
      api.getCredentials()
        .then((res) => setCredentials(res as CredentialItem[]))
        .catch(err => console.error("Error loading credentials:", err));
    }

    const hasChannelRef = manifest.parameters?.some(p => p.type === 'notificationChannelRef');
    if (hasChannelRef) {
      api.getNotificationChannels()
        .then(setNotificationChannels)
        .catch(err => console.error("Error loading notification channels:", err));
    }
  }, [manifest]);

  const handleFieldChange = (name: string, value: unknown) => {
    onChange({
      ...properties,
      [name]: value,
    });
  };

  const renderField = (param: ParameterDefinition) => {
    const value = (properties[param.name] ?? param.default ?? '') as any;

    switch (param.type) {
      case 'boolean':
        return (
          <FieldDropWrapper key={param.name} workflowId={workflowId} value={value} onChange={(val) => handleFieldChange(param.name, val)}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '12px 14px', borderRadius: '8px', background: 'rgba(255, 255, 255, 0.03)', border: '1px solid var(--border-color)' }}>
              <div>
                <span style={{ fontSize: '0.85rem', fontWeight: 600, color: '#fff' }}>{param.name}</span>
                {param.required && <span style={{ color: 'var(--color-error)', marginLeft: '4px' }}>*</span>}
              </div>
              <input
                type="checkbox"
                checked={!!value}
                onChange={(e) => handleFieldChange(param.name, e.target.checked)}
                style={{
                  width: '38px',
                  height: '20px',
                  appearance: 'none',
                  background: value ? 'var(--color-success)' : 'rgba(255, 255, 255, 0.1)',
                  borderRadius: '20px',
                  position: 'relative',
                  outline: 'none',
                  cursor: 'pointer',
                  transition: 'background 0.2s',
                }}
                className="toggle-switch"
              />
            </div>
          </FieldDropWrapper>
        );

      case 'enum': {
        // Block the language switch while the code editor modal is open — swapping the
        // language mid-edit against the current buffer is confusing (see editor spec).
        const enumDisabled = param.name === 'language' && codeModalField !== null;
        return (
          <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
              {param.name} {param.required && <span style={{ color: 'var(--color-error)' }}>*</span>}
            </label>
            <select
              value={value}
              disabled={enumDisabled}
              title={enumDisabled ? 'Close the code editor to change language' : undefined}
              onChange={(e) => handleFieldChange(param.name, e.target.value)}
              style={{
                width: '100%',
                padding: '10px',
                borderRadius: '8px',
                background: 'var(--bg-surface-opaque)',
                border: '1px solid var(--border-color)',
                color: '#fff',
                fontSize: '0.85rem',
                outline: 'none',
              }}
            >
              <option value="">Select option...</option>
              {param.values?.map(val => (
                <option key={val} value={val}>{val}</option>
              ))}
            </select>
          </div>
        );
      }

      case 'credentialRef':
        return (
          <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
              {param.name} (Secret) {param.required && <span style={{ color: 'var(--color-error)' }}>*</span>}
            </label>
            <select
              value={value}
              onChange={(e) => handleFieldChange(param.name, e.target.value)}
              style={{
                width: '100%',
                padding: '10px',
                borderRadius: '8px',
                background: 'var(--bg-surface-opaque)',
                border: '1px solid var(--border-color)',
                color: '#fff',
                fontSize: '0.85rem',
                outline: 'none',
              }}
            >
              <option value="">Select credential...</option>
              {credentials.map(c => (
                <option key={c.id} value={c.id}>{c.name} ({c.id})</option>
              ))}
            </select>
          </div>
        );

      case 'notificationChannelRef':
        return (
          <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
              {param.name} (Channel) {param.required && <span style={{ color: 'var(--color-error)' }}>*</span>}
            </label>
            <select
              value={value}
              onChange={(e) => handleFieldChange(param.name, e.target.value)}
              style={{
                width: '100%',
                padding: '10px',
                borderRadius: '8px',
                background: 'var(--bg-surface-opaque)',
                border: '1px solid var(--border-color)',
                color: '#fff',
                fontSize: '0.85rem',
                outline: 'none',
              }}
            >
              <option value="">Select channel...</option>
              {notificationChannels.map(c => (
                <option key={c.id} value={c.id}>{c.name} ({c.type})</option>
              ))}
            </select>
          </div>
        );

      case 'number':
        return (
          <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
              {param.name} {param.required && <span style={{ color: 'var(--color-error)' }}>*</span>}
            </label>
            <FieldDropWrapper workflowId={workflowId} value={value} onChange={(val) => handleFieldChange(param.name, val)}>
              <input
                type="number"
                value={value}
                onChange={(e) => handleFieldChange(param.name, e.target.value ? parseFloat(e.target.value) : undefined)}
                placeholder="Enter number..."
                style={{
                  width: '100%',
                  padding: '10px',
                  borderRadius: '8px',
                  background: 'rgba(0, 0, 0, 0.2)',
                  border: '1px solid var(--border-color)',
                  color: '#fff',
                  fontSize: '0.85rem',
                  outline: 'none',
                }}
              />
            </FieldDropWrapper>
          </div>
        );

      case 'keyValue': {
        type Row = { name: string; value: string };
        const rows: Row[] = Array.isArray(value) ? (value as Row[]) : [];
        const setRows = (next: Row[]) => handleFieldChange(param.name, next);
        const inputStyle: React.CSSProperties = {
          padding: '8px 10px', borderRadius: '6px', background: 'rgba(0,0,0,0.2)',
          border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.82rem', outline: 'none',
        };
        return (
          <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
              {param.name} {param.required && <span style={{ color: 'var(--color-error)' }}>*</span>}
            </label>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
              {rows.map((row, i) => (
                <div key={i} style={{ display: 'flex', gap: '6px', alignItems: 'center' }}>
                  <input
                    type="text" placeholder="name" value={row.name ?? ''}
                    onChange={(e) => setRows(rows.map((r, j) => j === i ? { ...r, name: e.target.value } : r))}
                    style={{ ...inputStyle, flex: '0 0 38%', fontFamily: 'monospace' }}
                  />
                  <span style={{ color: 'var(--text-muted)' }}>=</span>
                  <input
                    type="text" placeholder="value or {{ expression }}" value={row.value ?? ''}
                    onChange={(e) => setRows(rows.map((r, j) => j === i ? { ...r, value: e.target.value } : r))}
                    style={{ ...inputStyle, flex: 1 }}
                  />
                  <button
                    type="button" title="Remove" onClick={() => setRows(rows.filter((_, j) => j !== i))}
                    style={{ background: 'transparent', border: '1px solid var(--border-color)', color: 'var(--text-muted)', borderRadius: '6px', padding: '6px 9px', cursor: 'pointer', flex: 'none' }}
                  >×</button>
                </div>
              ))}
              <button
                type="button" onClick={() => setRows([...rows, { name: '', value: '' }])}
                style={{ background: 'rgba(255,255,255,0.04)', border: '1px dashed var(--border-color)', color: 'var(--text-secondary)', borderRadius: '6px', padding: '7px', cursor: 'pointer', fontSize: '0.78rem' }}
              >+ Add variable</button>
            </div>
            <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', marginTop: '2px' }}>
              Values support expressions: <code>{"{{ $node.<id>.output.<field> }}"}</code>
            </span>
          </div>
        );
      }

      case 'code': {
        const codeValue = typeof value === 'string' ? value : '';
        // Language: honor a sibling `language` property if the node exposes one, else C#.
        const lang = (typeof properties['language'] === 'string' && properties['language'])
          ? String(properties['language']).toLowerCase().replace('c#', 'csharp')
          : 'csharp';
        return (
          <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
                {param.name} {param.required && <span style={{ color: 'var(--color-error)' }}>*</span>}
              </label>
              <button
                type="button"
                onClick={() => setCodeModalField(param.name)}
                style={{ background: 'transparent', border: '1px solid var(--border-color)', borderRadius: '6px', color: 'var(--text-secondary)', fontSize: '0.7rem', padding: '3px 8px', cursor: 'pointer' }}
                title="Open full editor and test the script"
              >
                ⤢ Expand &amp; test
              </button>
            </div>
            <div style={{ borderRadius: '8px', overflow: 'hidden', border: '1px solid var(--border-color)' }}>
              <Suspense fallback={<div style={{ padding: '12px', fontSize: '0.8rem', color: 'var(--text-muted)' }}>Loading editor…</div>}>
                <CodeEditor
                  height="220px"
                  language={lang}
                  theme="vs-dark"
                  value={codeValue}
                  onChange={(val) => handleFieldChange(param.name, val ?? '')}
                  beforeMount={registerCsharpInlineCompletions}
                  options={{
                    minimap: { enabled: false },
                    fontSize: 13,
                    lineNumbers: 'on',
                    scrollBeyondLastLine: false,
                    tabSize: 4,
                    automaticLayout: true,
                  }}
                />
              </Suspense>
            </div>
            <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', marginTop: '2px' }}>
              Helpers: <code>Input.Get&lt;T&gt;(name)</code>, <code>Logger</code>, <code>Success(obj)</code>, <code>Fail(msg)</code>, <code>cancellationToken</code>
            </span>
            <InlineCodeEditorModal
              open={codeModalField === param.name}
              code={codeValue}
              language={lang}
              nodeId={nodeId}
              onSave={(val, meta) => {
                if (meta?.outputKeys) {
                  onChange({ ...properties, [param.name]: val, _outputKeys: meta.outputKeys });
                } else {
                  handleFieldChange(param.name, val);
                }
              }}
              onClose={() => setCodeModalField(null)}
            />
          </div>
        );
      }

      case 'dynamicOptions':
      case 'resourceLocator': {
        // Resolve the connection/server-config id from a sibling property. The manifest may name
        // it via loaderConfig.connectionParam; otherwise fall back to common conventions.
        const connectionParam = param.loaderConfig?.connectionParam ?? 'connectionId';
        const connectionId =
          (typeof properties[connectionParam] === 'string' && (properties[connectionParam] as string)) ||
          (typeof properties['serverConfigId'] === 'string' && (properties['serverConfigId'] as string)) ||
          null;
        return (
          <AsyncOptionsField
            key={param.name}
            param={param}
            value={properties[param.name]}
            properties={properties}
            connectionId={connectionId}
            onChange={(val) => handleFieldChange(param.name, val)}
          />
        );
      }

      case 'dynamicFields': {
        // A parameter whose value is an object keyed by field key. The loader (e.g. reactor.actionFields)
        // returns the fields — one typed sub-editor is rendered per field, with a raw-JSON escape hatch.
        const connectionParam = param.loaderConfig?.connectionParam ?? 'connectionId';
        const connectionId =
          (typeof properties[connectionParam] === 'string' && (properties[connectionParam] as string)) ||
          (typeof properties['serverConfigId'] === 'string' && (properties['serverConfigId'] as string)) ||
          null;
        return (
          <DynamicFieldsField
            key={param.name}
            param={param}
            value={properties[param.name]}
            properties={properties}
            connectionId={connectionId}
            onChange={(val) => handleFieldChange(param.name, val)}
          />
        );
      }

      case 'agentTools': {
        // A parameter whose value is an array of tool bindings (target workflow + model-facing
        // name/description + parameter contract + projected outputs) for the AI Agent node.
        return (
          <AgentToolsField
            key={param.name}
            param={param}
            value={properties[param.name]}
            onChange={(val) => handleFieldChange(param.name, val)}
          />
        );
      }

      case 'aiModel': {
        // The AI-node `model` override: an editable combo of curated per-vendor model suggestions (with an
        // optional live-load), resolving the vendor/credential from the saved global AI provider config.
        return (
          <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
              {param.name}
            </label>
            <ModelCombo
              value={typeof value === 'string' ? value : ''}
              onChange={(val) => handleFieldChange(param.name, val)}
              placeholder="Enter model…"
              style={{ padding: '10px', borderRadius: '8px', background: 'rgba(0, 0, 0, 0.2)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.85rem', boxSizing: 'border-box', outline: 'none' }}
            />
            {param.description && (
              <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', marginTop: '2px' }}>{param.description}</span>
            )}
          </div>
        );
      }

      case 'string':
      default: {
        // Render a multi-line editor for fields that commonly hold long / multi-line text, or whenever the
        // current value already spans multiple lines or is long — a single-line input truncates those badly.
        const multilineParams = new Set([
          'payload', 'body', 'message', 'headers',
          'value', 'prompt', 'systemPrompt', 'task', 'content', 'sources', 'previous', 'current', 'instructions', 'resultSchema', 'jsonSchema',
        ]);
        const isTextArea = multilineParams.has(param.name)
          || (typeof value === 'string' && (value.includes('\n') || value.length > 80));
        return (
          <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
            <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
              {param.name} {param.required && <span style={{ color: 'var(--color-error)' }}>*</span>}
            </label>
            <FieldDropWrapper workflowId={workflowId} value={value} onChange={(val) => handleFieldChange(param.name, val)}>
              <ExpressionField
                value={typeof value === 'string' ? value : ''}
                onChange={(val) => handleFieldChange(param.name, val)}
                completions={expressionCompletions}
                multiline={isTextArea}
                placeholder={`Enter ${param.name}...`}
                style={{
                  width: '100%',
                  padding: '10px',
                  borderRadius: '8px',
                  background: 'rgba(0, 0, 0, 0.2)',
                  border: '1px solid var(--border-color)',
                  color: '#fff',
                  fontSize: '0.85rem',
                  boxSizing: 'border-box',
                  ...(isTextArea
                    ? { fontFamily: param.name === 'payload' || param.name === 'headers' ? 'monospace' : 'inherit', resize: 'vertical', outline: 'none' }
                    : { outline: 'none' }),
                }}
              />
            </FieldDropWrapper>
            {param.description && (
              <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', marginTop: '2px' }}>
                {param.description}
              </span>
            )}
            {param.name === 'variableName' && typeof value === 'string' && hasVariablePath(value) && (
              <span style={{ fontSize: '0.7rem', color: 'var(--color-info)', marginTop: '2px', fontWeight: 600 }}>
                → writes into a {pathContainerKind(value) === 'array' ? 'array' : 'dictionary'} (<code>{variablePathHead(value)}</code>)
              </span>
            )}
            {param.expression && !value?.__type && (
              <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', marginTop: '2px' }}>
                Supports expressions: <code>{"{{ $node.<id>.output.<field> }}"}</code>
              </span>
            )}
          </div>
        );
      }
    }
  };

  // The Condition node replaces the generic param fields (raw logic/left/operator/right) with a
  // compact status + "Edit logic" launcher for the full-screen logic-graph editor (slice 2b).
  if (manifest.id === 'condition') {
    return <ConditionLogicField workflowId={workflowId} nodeId={nodeId} properties={properties} onChange={onChange} />;
  }

  const visibleParams = getVisibleParams(manifest.parameters ?? [], properties);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '20px' }}>
      {visibleParams.length > 0 ? (
        visibleParams.map(renderField)
      ) : (
        <p style={{ fontSize: '0.8rem', color: 'var(--text-secondary)' }}>
          This node has no configurable parameters.
        </p>
      )}
    </div>
  );
}

// Memoized: PropertiesPanel re-renders several times per selection (async fetches + store subscriptions).
// With stable props (memoized manifest + useCallback onChange in the parent), this skips those repeat
// renders so the field list is built once per selected node, not once per panel render.
export const ManifestForm = memo(ManifestFormImpl);
