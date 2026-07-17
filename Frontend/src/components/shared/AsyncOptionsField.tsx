// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo, useRef, useState } from 'react';
import { useNodeOptions } from '../../hooks/useNodeOptions';
import type { DynamicOptionMultiValue, DynamicOptionValue, OptionItem, ParameterDefinition } from '../../types';

interface AsyncOptionsFieldProps {
  param: ParameterDefinition;
  /** Persisted value: DynamicOptionValue | DynamicOptionMultiValue | legacy string/object. */
  value: unknown;
  /** Sibling node properties (parent values for dependsOn). */
  properties: Record<string, unknown>;
  /** Stored connection / server-config id. */
  connectionId?: string | null;
  onChange: (value: unknown) => void;
}

const controlStyle: React.CSSProperties = {
  width: '100%',
  padding: '10px',
  borderRadius: '8px',
  background: 'var(--bg-surface-opaque)',
  border: '1px solid var(--border-color)',
  color: '#fff',
  fontSize: '0.85rem',
  outline: 'none',
  cursor: 'pointer',
  textAlign: 'left',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: '8px',
};

function toStringValue(value: unknown): string {
  if (value == null) return '';
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  if (typeof value === 'object' && 'value' in (value as Record<string, unknown>)) {
    return String((value as Record<string, unknown>).value ?? '');
  }
  return '';
}

// ── Value normalization ──────────────────────────────────────────────────────
// Read whatever shape is persisted (current contract or legacy) into a working form.

function readSingle(value: unknown): DynamicOptionValue | null {
  if (value == null || value === '') return null;
  if (typeof value === 'string') return { value, mode: 'manual' };
  if (typeof value === 'object') {
    const obj = value as Record<string, unknown>;
    if (typeof obj.value === 'string') {
      return {
        value: obj.value,
        label: typeof obj.label === 'string' ? obj.label : undefined,
        mode: obj.mode === 'manual' ? 'manual' : 'list',
      };
    }
  }
  return null;
}

function readMulti(value: unknown): DynamicOptionMultiValue {
  if (Array.isArray(value)) {
    // Legacy: a bare array of strings or {value,label}.
    const items = value
      .map((v) => (typeof v === 'string' ? { value: v } : readSingle(v) ? { value: readSingle(v)!.value, label: readSingle(v)!.label } : null))
      .filter((v): v is { value: string; label?: string } => v != null);
    return { mode: 'list', items };
  }
  if (value && typeof value === 'object' && Array.isArray((value as Record<string, unknown>).items)) {
    const obj = value as DynamicOptionMultiValue;
    return { mode: obj.mode === 'manual' ? 'manual' : 'list', items: obj.items ?? [] };
  }
  // Legacy single object/string → one-item list.
  const single = readSingle(value);
  return { mode: single?.mode ?? 'list', items: single ? [{ value: single.value, label: single.label }] : [] };
}

export function AsyncOptionsField({ param, value, properties, connectionId, onChange }: AsyncOptionsFieldProps) {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const [manualEntry, setManualEntry] = useState('');
  const containerRef = useRef<HTMLDivElement>(null);

  const multiple = !!param.multiple;
  const { state, reload } = useNodeOptions({ param, properties, connectionId, enabled: open, search });

  const single = useMemo(() => readSingle(value), [value]);
  const multi = useMemo(() => readMulti(value), [value]);
  const hasSelection = multiple ? multi.items.length > 0 : !!single;

  // Cascading: when a dependsOn parent value changes, the current child selection may no longer be
  // valid against the new parent's resource list — clear it. Skip the initial render so a saved
  // selection survives reload.
  const parentKey = useMemo(
    () => JSON.stringify((param.dependsOn ?? []).map((name) => toStringValue(properties[name]))),
    [param.dependsOn, properties],
  );
  const prevParentKey = useRef<string | null>(null);
  useEffect(() => {
    if (prevParentKey.current === null) {
      prevParentKey.current = parentKey;
      return;
    }
    if (prevParentKey.current !== parentKey) {
      prevParentKey.current = parentKey;
      if (hasSelection) {
        onChange(multiple ? { mode: 'list', items: [] } : undefined);
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [parentKey]);

  const liveOptions: OptionItem[] = state.status === 'ready' ? state.options : [];
  const filtered = useMemo(() => {
    if (!search) return liveOptions;
    const q = search.toLowerCase();
    return liveOptions.filter((o) => o.label.toLowerCase().includes(q) || o.value.toLowerCase().includes(q));
  }, [liveOptions, search]);

  const selectedValues = useMemo(() => new Set(multi.items.map((i) => i.value)), [multi.items]);

  // ── Mutations ──────────────────────────────────────────────────────────────

  const pickSingle = (opt: OptionItem) => {
    const next: DynamicOptionValue = { value: opt.value, label: opt.label, mode: 'list' };
    onChange(next);
    setOpen(false);
  };

  const toggleMulti = (opt: OptionItem) => {
    const exists = selectedValues.has(opt.value);
    const items = exists
      ? multi.items.filter((i) => i.value !== opt.value)
      : [...multi.items, { value: opt.value, label: opt.label }];
    const next: DynamicOptionMultiValue = { mode: 'list', items };
    onChange(next);
  };

  const removeChip = (val: string) => {
    const next: DynamicOptionMultiValue = { mode: 'list', items: multi.items.filter((i) => i.value !== val) };
    onChange(next);
  };

  const commitManual = () => {
    const trimmed = manualEntry.trim();
    if (!trimmed) return;
    if (multiple) {
      if (!selectedValues.has(trimmed)) {
        onChange({ mode: 'manual', items: [...multi.items, { value: trimmed }] } as DynamicOptionMultiValue);
      }
    } else {
      onChange({ value: trimmed, mode: 'manual' } as DynamicOptionValue);
      setOpen(false);
    }
    setManualEntry('');
  };

  // ── Display ──────────────────────────────────────────────────────────────────

  const summaryLabel = multiple
    ? multi.items.length === 0
      ? 'Select…'
      : `${multi.items.length} selected`
    : single
      ? single.label ?? single.value
      : 'Select…';

  const showManualFallback = state.status === 'error' && !!param.allowManualEntry;

  return (
    <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '6px' }} ref={containerRef}>
      <label style={{ display: 'block', fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
        {param.name} {param.required && <span style={{ color: 'var(--color-error)' }}>*</span>}
      </label>

      {/* Multi-select chips */}
      {multiple && multi.items.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '6px', marginBottom: '2px' }}>
          {multi.items.map((item) => (
            <span
              key={item.value}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: '6px',
                background: 'rgba(255,255,255,0.06)', border: '1px solid var(--border-color)',
                borderRadius: '14px', padding: '3px 8px', fontSize: '0.78rem', color: '#fff',
              }}
            >
              {item.label ?? item.value}
              <button
                type="button" title="Remove" onClick={() => removeChip(item.value)}
                style={{ background: 'transparent', border: 'none', color: 'var(--text-muted)', cursor: 'pointer', fontSize: '0.9rem', lineHeight: 1, padding: 0 }}
              >×</button>
            </span>
          ))}
        </div>
      )}

      <button type="button" style={controlStyle} onClick={() => setOpen((o) => !o)}>
        <span style={{ color: (multiple ? multi.items.length > 0 : !!single) ? '#fff' : 'var(--text-muted)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {summaryLabel}
        </span>
        <span style={{ color: 'var(--text-muted)', fontSize: '0.7rem' }}>{open ? '▲' : '▼'}</span>
      </button>

      {open && (
        <div style={{ border: '1px solid var(--border-color)', borderRadius: '8px', background: 'var(--bg-surface-opaque)', overflow: 'hidden' }}>
          <div style={{ display: 'flex', gap: '6px', padding: '8px', borderBottom: '1px solid var(--border-color)' }}>
            <input
              type="text" autoFocus value={search} placeholder="Search…"
              onChange={(e) => setSearch(e.target.value)}
              style={{ flex: 1, padding: '7px 9px', borderRadius: '6px', background: 'rgba(0,0,0,0.2)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.82rem', outline: 'none' }}
            />
            <button
              type="button" title="Refresh" onClick={() => void reload()}
              style={{ background: 'transparent', border: '1px solid var(--border-color)', color: 'var(--text-secondary)', borderRadius: '6px', padding: '0 10px', cursor: 'pointer' }}
            >⟳</button>
          </div>

          <div style={{ maxHeight: '220px', overflowY: 'auto' }}>
            {state.status === 'loading' && (
              <div style={{ padding: '12px', fontSize: '0.82rem', color: 'var(--text-muted)' }}>Loading…</div>
            )}
            {state.status === 'empty' && (
              <div style={{ padding: '12px', fontSize: '0.82rem', color: 'var(--text-muted)' }}>No options available.</div>
            )}
            {state.status === 'error' && (
              <div style={{ padding: '12px', fontSize: '0.82rem', color: 'var(--color-error)' }}>{state.message}</div>
            )}
            {state.status === 'ready' && filtered.map((opt) => {
              const isSelected = multiple ? selectedValues.has(opt.value) : single?.value === opt.value;
              return (
                <button
                  key={opt.value}
                  type="button"
                  onClick={() => (multiple ? toggleMulti(opt) : pickSingle(opt))}
                  style={{
                    width: '100%', textAlign: 'left', padding: '9px 12px', cursor: 'pointer',
                    background: isSelected ? 'rgba(255,255,255,0.08)' : 'transparent',
                    border: 'none', color: '#fff', fontSize: '0.83rem',
                    display: 'flex', alignItems: 'center', gap: '8px',
                  }}
                >
                  {multiple && (
                    <input type="checkbox" readOnly checked={isSelected} style={{ pointerEvents: 'none' }} />
                  )}
                  <span style={{ display: 'flex', flexDirection: 'column' }}>
                    <span>{opt.label}</span>
                    {opt.description && <span style={{ fontSize: '0.72rem', color: 'var(--text-muted)' }}>{opt.description}</span>}
                  </span>
                </button>
              );
            })}
          </div>

          {showManualFallback && (
            <div style={{ display: 'flex', gap: '6px', padding: '8px', borderTop: '1px solid var(--border-color)' }}>
              <input
                type="text" value={manualEntry} placeholder="Enter value manually…"
                onChange={(e) => setManualEntry(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); commitManual(); } }}
                style={{ flex: 1, padding: '7px 9px', borderRadius: '6px', background: 'rgba(0,0,0,0.2)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.82rem', outline: 'none' }}
              />
              <button
                type="button" onClick={commitManual}
                style={{ background: 'rgba(255,255,255,0.06)', border: '1px solid var(--border-color)', color: 'var(--text-secondary)', borderRadius: '6px', padding: '0 12px', cursor: 'pointer', fontSize: '0.8rem' }}
              >Add</button>
            </div>
          )}
        </div>
      )}

      {single?.mode === 'manual' && !multiple && (
        <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>Manually entered value.</span>
      )}
    </div>
  );
}
