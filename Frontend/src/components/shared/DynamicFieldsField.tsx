// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo, useRef, useState } from 'react';
import { useNodeOptions } from '../../hooks/useNodeOptions';
import type { OptionItem, ParameterDefinition } from '../../types';

interface DynamicFieldsFieldProps {
  param: ParameterDefinition;
  /** Persisted value: an object keyed by field key (or a legacy JSON string). */
  value: unknown;
  /** Sibling node properties (parent values for dependsOn — e.g. the picked action/instance). */
  properties: Record<string, unknown>;
  /** Stored connection / server-config id. */
  connectionId?: string | null;
  onChange: (value: unknown) => void;
}

const labelStyle: React.CSSProperties = {
  display: 'block', fontSize: '0.75rem', fontWeight: 700,
  color: 'var(--text-secondary)', textTransform: 'uppercase',
};

const inputStyle: React.CSSProperties = {
  width: '100%', padding: '9px 10px', borderRadius: '8px',
  background: 'rgba(0, 0, 0, 0.2)', border: '1px solid var(--border-color)',
  color: '#fff', fontSize: '0.85rem', outline: 'none',
};

// ── Value normalization ────────────────────────────────────────────────────────
// The stored value is an object keyed by field key. Legacy workflows may have stored a JSON
// string in the old free-text `data` field — parse it back to an object so it loads unchanged.

function readObject(value: unknown): Record<string, unknown> {
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  if (typeof value === 'string' && value.trim()) {
    try {
      const parsed = JSON.parse(value);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        return parsed as Record<string, unknown>;
      }
    } catch {
      // Not valid JSON — surfaced via the raw editor, nothing to migrate.
    }
  }
  return {};
}

// The generic value-kind hint carried on each field option (OptionItem.kind), lower-cased.
function kindOf(opt: OptionItem): string {
  return (opt.kind ?? 'string').toLowerCase();
}

// Field kinds that resolve to a cascaded resource picker (loaded per instance) rather than free text.
// The value stored stays the scalar id the target expects. Resource has no catalog, so it stays text.
const RESOURCE_LOADER_BY_KIND: Record<string, string> = {
  channel: 'reactor.channels',
  eventtype: 'reactor.eventTypes',
};

const fxButtonStyle: React.CSSProperties = {
  background: 'rgba(255,255,255,0.04)', border: '1px solid var(--border-color)',
  color: 'var(--text-secondary)', borderRadius: '6px', padding: '2px 8px',
  fontSize: '0.68rem', fontFamily: 'ui-monospace, Menlo, monospace', cursor: 'pointer', flex: 'none',
};

function isExpression(v: unknown): boolean {
  return typeof v === 'string' && v.includes('{{');
}

export function DynamicFieldsField({ param, value, properties, connectionId, onChange }: DynamicFieldsFieldProps) {
  // Inline form: load the field schema as soon as the panel shows this parameter.
  const { state, reload } = useNodeOptions({ param, properties, connectionId, enabled: true });

  const obj = useMemo(() => readObject(value), [value]);
  const fields: OptionItem[] = state.status === 'ready' ? state.options : [];
  const hasSchema = fields.length > 0;

  // Raw JSON escape hatch. Auto-forced when there's no schema to drive a typed form (unknown/
  // manually-typed action, or the resource system is unreachable at design time).
  const [rawMode, setRawMode] = useState(false);
  const showRaw = rawMode || !hasSchema;

  // Keys present on the stored object that the schema doesn't describe — preserved on save, shown
  // so the author knows they're still there (only editable via the raw view).
  const knownKeys = useMemo(() => new Set(fields.map((f) => f.value)), [fields]);
  const extraKeys = useMemo(() => Object.keys(obj).filter((k) => !knownKeys.has(k)), [obj, knownKeys]);

  const setField = (key: string, next: unknown) => {
    const merged = { ...obj };
    if (next === undefined || next === '' || (typeof next === 'number' && Number.isNaN(next))) {
      delete merged[key];
    } else {
      merged[key] = next;
    }
    onChange(Object.keys(merged).length > 0 ? merged : undefined);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <label style={labelStyle}>
          {param.name} {param.required && <span style={{ color: 'var(--color-error)' }}>*</span>}
        </label>
        <div style={{ display: 'flex', gap: '6px' }}>
          {hasSchema && (
            <button
              type="button"
              onClick={() => setRawMode((m) => !m)}
              title={showRaw ? 'Edit each field individually' : 'Edit the raw JSON object'}
              style={{
                background: 'transparent', border: '1px solid var(--border-color)',
                color: 'var(--text-secondary)', borderRadius: '6px', padding: '3px 8px',
                fontSize: '0.7rem', cursor: 'pointer',
              }}
            >
              {showRaw ? 'Fields' : 'Raw JSON'}
            </button>
          )}
          <button
            type="button" title="Refresh fields" onClick={() => void reload()}
            style={{
              background: 'transparent', border: '1px solid var(--border-color)',
              color: 'var(--text-secondary)', borderRadius: '6px', padding: '3px 8px',
              fontSize: '0.7rem', cursor: 'pointer',
            }}
          >⟳</button>
        </div>
      </div>

      {param.description && (
        <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>{param.description}</span>
      )}

      {state.status === 'loading' && (
        <span style={{ fontSize: '0.8rem', color: 'var(--text-muted)' }}>Loading fields…</span>
      )}
      {state.status === 'error' && (
        <span style={{ fontSize: '0.78rem', color: 'var(--color-error)' }}>{state.message}</span>
      )}
      {state.status === 'empty' && !showRaw && (
        <span style={{ fontSize: '0.78rem', color: 'var(--text-muted)' }}>
          Pick an action first, or edit the raw JSON below.
        </span>
      )}

      {showRaw ? (
        <RawJsonEditor obj={obj} onChange={onChange} />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
          {fields.map((f) => (
            <FieldEditor
              key={f.value}
              field={f}
              value={obj[f.value]}
              properties={properties}
              connectionId={connectionId}
              integrationType={param.integrationType}
              onChange={(v) => setField(f.value, v)}
            />
          ))}
        </div>
      )}

      {!showRaw && extraKeys.length > 0 && (
        <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>
          Extra keys preserved: <code>{extraKeys.join(', ')}</code> (edit via Raw JSON).
        </span>
      )}
    </div>
  );
}

interface FieldEditorProps {
  field: OptionItem;
  value: unknown;
  properties: Record<string, unknown>;
  connectionId?: string | null;
  integrationType?: string;
  onChange: (v: unknown) => void;
}

// One editor per field: a label + an "fx" toggle (bind the field to an expression/variable instead of
// a literal), then the typed control chosen by the generic kind hint. Channel/EventType resolve to a
// cascaded resource picker (loaded per instance); Resource/DateTime/other fall through to free text.
function FieldEditor({ field, value, properties, connectionId, integrationType, onChange }: FieldEditorProps) {
  const kind = kindOf(field);
  const label = field.label;
  // Start in expression mode when the stored value already looks like one, so a saved binding reopens
  // as an expression rather than raw text.
  const [expr, setExpr] = useState(() => isExpression(value));

  const control = (() => {
    if (expr) {
      return (
        <input
          type="text"
          value={typeof value === 'string' ? value : value == null ? '' : String(value)}
          onChange={(e) => onChange(e.target.value || undefined)}
          placeholder="{{ $node.<id>.output.<field> }}"
          style={{ ...inputStyle, fontFamily: 'ui-monospace, Menlo, monospace' }}
        />
      );
    }
    if (kind === 'boolean') {
      return (
        <input
          type="checkbox"
          checked={value === true || value === 'true'}
          onChange={(e) => onChange(e.target.checked ? true : undefined)}
        />
      );
    }
    if (kind === 'enum') {
      const options = field.enumValues ?? [];
      return (
        <select
          value={typeof value === 'string' ? value : ''}
          onChange={(e) => onChange(e.target.value || undefined)}
          style={{ ...inputStyle, cursor: 'pointer', background: 'var(--bg-surface-opaque)' }}
        >
          <option value="">Select…</option>
          {options.map((v) => (
            <option key={v} value={v}>{v}</option>
          ))}
        </select>
      );
    }
    if (kind === 'integer' || kind === 'number') {
      return (
        <input
          type="number"
          step={kind === 'integer' ? 1 : 'any'}
          value={typeof value === 'number' ? value : typeof value === 'string' ? value : ''}
          onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))}
          placeholder="Enter number…"
          style={inputStyle}
        />
      );
    }
    const loader = RESOURCE_LOADER_BY_KIND[kind];
    if (loader) {
      return (
        <ResourcePickerField
          field={field}
          value={value}
          loaderName={loader}
          integrationType={integrationType}
          properties={properties}
          connectionId={connectionId}
          onChange={onChange}
        />
      );
    }
    // string / guid / datetime / resource / other → free text.
    return (
      <input
        type="text"
        value={typeof value === 'string' ? value : value == null ? '' : String(value)}
        onChange={(e) => onChange(e.target.value || undefined)}
        placeholder={`Enter ${label}…`}
        style={inputStyle}
      />
    );
  })();

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '5px' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px' }}>
        <label style={labelStyle}>{label}</label>
        <button
          type="button"
          onClick={() => setExpr((e) => !e)}
          title={expr ? 'Enter a literal value' : 'Bind to an expression / variable, e.g. {{ … }}'}
          style={{ ...fxButtonStyle, color: expr ? 'var(--color-accent)' : 'var(--text-secondary)' }}
        >
          {expr ? 'value' : 'fx'}
        </button>
      </div>
      {control}
    </div>
  );
}

// Cascaded resource picker for a Channel/EventType field: loads the instance's resources via the given
// loader and offers them as a native dropdown, storing the scalar id. Falls back to a free-text box when
// no catalog is available at design time (offline / dev), preserving any already-entered value.
function ResourcePickerField({
  field, value, loaderName, integrationType, properties, connectionId, onChange,
}: {
  field: OptionItem;
  value: unknown;
  loaderName: string;
  integrationType?: string;
  properties: Record<string, unknown>;
  connectionId?: string | null;
  onChange: (v: unknown) => void;
}) {
  const param: ParameterDefinition = useMemo(() => ({
    name: field.value,
    type: 'resourceLocator',
    optionsLoader: loaderName,
    integrationType,
    dependsOn: ['instance'],
  }), [field.value, loaderName, integrationType]);

  const { state } = useNodeOptions({ param, properties, connectionId, enabled: true });
  const options = state.status === 'ready' ? state.options : [];
  const current = typeof value === 'string' ? value : value == null ? '' : String(value);

  if (options.length === 0) {
    // No catalog to pick from (system unreachable / dev) — don't trap the author, let them type the id.
    return (
      <input
        type="text"
        value={current}
        onChange={(e) => onChange(e.target.value || undefined)}
        placeholder="Enter id…"
        style={inputStyle}
      />
    );
  }

  const known = options.some((o) => o.value === current);
  return (
    <select
      value={current}
      onChange={(e) => onChange(e.target.value || undefined)}
      style={{ ...inputStyle, cursor: 'pointer', background: 'var(--bg-surface-opaque)' }}
    >
      <option value="">Select…</option>
      {/* Keep a saved-but-unlisted id visible instead of silently blanking it. */}
      {!known && current && <option value={current}>{current}</option>}
      {options.map((o) => (
        <option key={o.value} value={o.value}>{o.label}</option>
      ))}
    </select>
  );
}

// Whole-object JSON editor. Keeps a local text buffer so partial/invalid edits don't clobber the
// stored value; commits the parsed object on valid JSON, shows a parse error otherwise.
function RawJsonEditor({ obj, onChange }: { obj: Record<string, unknown>; onChange: (v: unknown) => void }) {
  const serialized = useMemo(() => (Object.keys(obj).length ? JSON.stringify(obj, null, 2) : ''), [obj]);
  const [text, setText] = useState(serialized);
  const [error, setError] = useState<string | null>(null);
  // Re-sync when the stored object changes from outside (e.g. switching nodes), unless the user is
  // mid-edit with the same underlying value.
  const lastSerialized = useRef(serialized);
  useEffect(() => {
    if (serialized !== lastSerialized.current) {
      lastSerialized.current = serialized;
      setText(serialized);
      setError(null);
    }
  }, [serialized]);

  const commit = (next: string) => {
    setText(next);
    const trimmed = next.trim();
    if (!trimmed) {
      setError(null);
      onChange(undefined);
      return;
    }
    try {
      const parsed = JSON.parse(trimmed);
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
        setError('Expected a JSON object, e.g. { "Key": "value" }.');
        return;
      }
      setError(null);
      lastSerialized.current = JSON.stringify(parsed, null, 2);
      onChange(parsed);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Invalid JSON.');
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '5px' }}>
      <textarea
        value={text}
        onChange={(e) => commit(e.target.value)}
        placeholder={'{\n  "Key": "value"\n}'}
        rows={6}
        spellCheck={false}
        style={{ ...inputStyle, fontFamily: 'monospace', resize: 'vertical' }}
      />
      {error && <span style={{ fontSize: '0.72rem', color: 'var(--color-error)' }}>{error}</span>}
      <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>
        A JSON object of parameters, keyed by field name.
      </span>
    </div>
  );
}
