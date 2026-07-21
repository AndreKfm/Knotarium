// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import { AlertTriangle, Database, ShieldCheck } from 'lucide-react';
import { api } from '../utils/api';
import type { RetentionConfigDto } from '../types';

const cardStyle: React.CSSProperties = {
  padding: '20px',
  borderRadius: '12px',
  background: 'rgba(255, 255, 255, 0.03)',
  border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))',
  marginBottom: '24px',
};

const labelStyle: React.CSSProperties = {
  fontSize: '0.78rem',
  color: 'var(--text-secondary, #94a3b8)',
  display: 'block',
  marginBottom: 4,
};

const inputStyle: React.CSSProperties = {
  width: '100%',
  padding: '6px 8px',
  borderRadius: 6,
  border: '1px solid var(--border-color, rgba(255,255,255,0.12))',
  background: 'rgba(0,0,0,0.25)',
  color: '#fff',
  fontSize: '0.82rem',
};

type Field = { key: keyof RetentionConfigDto; label: string; hint: string; min: number; max: number };

// The two levers that actually bound disk growth — run history (with its journal/logs) and how often
// the sweep runs. Presented first and prominently.
const PRIMARY_FIELDS: Field[] = [
  { key: 'runHistoryDays', label: 'Run history (days)', hint: 'Delete finished runs and their logs older than this. 0 = keep forever.', min: 0, max: 3650 },
  { key: 'sweepIntervalMinutes', label: 'Sweep interval (minutes)', hint: 'How often the cleanup runs. The first sweep is one interval after startup.', min: 1, max: 10080 },
];

// Opt-in caps for the other unbounded tables. Off (0) by default so nothing is deleted unexpectedly.
const ADVANCED_FIELDS: Field[] = [
  { key: 'maxWorkflowVersionsPerWorkflow', label: 'Max versions / workflow', hint: 'Cap saved version history per workflow. Never deletes the active or a referenced version. 0 = keep all.', min: 0, max: 100000 },
  { key: 'maxOpenApiSpecVersionsPerSpec', label: 'Max OpenAPI versions / spec', hint: 'Cap re-import history per imported spec. 0 = keep all.', min: 0, max: 100000 },
  { key: 'auditEntryDays', label: 'Audit log (days)', hint: 'Roll over audit-log entries older than this. 0 = keep forever.', min: 0, max: 3650 },
];

/**
 * Settings → Retention. Bounds database growth so the SQLite file can't fill the disk over a long
 * deployment: prune old finished runs (and their journal/logs), cap version histories, and roll over
 * the audit log. The retention worker re-reads this on every sweep, so changes apply without a restart.
 * Unset ⇒ the appsettings "Retention" section (default: keep 30 days) stays in effect.
 */
export function RetentionSetting() {
  const [config, setConfig] = useState<RetentionConfigDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedFlash, setSavedFlash] = useState(false);

  useEffect(() => {
    api.getRetentionConfig()
      .then(setConfig)
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load the retention settings.'))
      .finally(() => setLoading(false));
  }, []);

  const update = (patch: Partial<RetentionConfigDto>) => {
    setConfig((prev) => (prev ? { ...prev, ...patch } : prev));
    setDirty(true);
    setSavedFlash(false);
  };

  const save = async () => {
    if (!config) return;
    setSaving(true);
    setError(null);
    try {
      const saved = await api.updateRetentionConfig(config);
      setConfig(saved);
      setDirty(false);
      setSavedFlash(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save the retention settings.');
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return <div style={cardStyle}><div style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)' }}>Loading…</div></div>;
  }
  if (!config) {
    return (
      <div style={cardStyle}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: '0.85rem', color: '#f87171' }}>
          <AlertTriangle size={16} /> {error ?? 'Failed to load the retention settings.'}
        </div>
      </div>
    );
  }

  const pruningOff = config.runHistoryDays <= 0;

  const renderField = ({ key, label, hint, min, max }: Field) => (
    <div key={key}>
      <label style={labelStyle} htmlFor={`ret-${key}`}>{label}</label>
      <input
        id={`ret-${key}`}
        type="number"
        min={min}
        max={max}
        value={config[key]}
        onChange={(e) => update({ [key]: Number(e.target.value) } as Partial<RetentionConfigDto>)}
        style={inputStyle}
      />
      <div style={{ fontSize: '0.68rem', color: 'var(--text-secondary, #64748b)', marginTop: 2 }}>{hint}</div>
    </div>
  );

  return (
    <div style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '6px' }}>
        <Database size={16} style={{ color: '#60a5fa' }} />
        <h3 style={{ margin: 0, fontSize: '0.95rem', color: '#fff' }}>Data retention</h3>
      </div>
      <p style={{ margin: '0 0 16px', fontSize: '0.8rem', color: 'var(--text-secondary, #94a3b8)', maxWidth: 640, lineHeight: 1.5 }}>
        Bounds how large the database can grow so it can't fill the disk. Old finished runs are deleted
        along with their logs; in-flight runs are never touched. Changes apply on the next sweep — no
        restart needed.
      </p>

      {/* Primary disk controls */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 12, marginBottom: 14 }}>
        {PRIMARY_FIELDS.map(renderField)}
      </div>

      {pruningOff && (
        <div
          style={{
            display: 'flex', alignItems: 'flex-start', gap: 8, padding: '10px 12px', borderRadius: 10, marginBottom: 16,
            background: 'rgba(240,180,41,0.08)', border: '1px solid rgba(240,180,41,0.3)',
          }}
        >
          <AlertTriangle size={15} style={{ color: '#f0b429', marginTop: 1, flex: 'none' }} />
          <div style={{ fontSize: '0.78rem', color: '#f6e6bf', lineHeight: 1.5 }}>
            Run history is set to <strong>keep forever</strong> (0 days). The database will grow without
            bound as runs accumulate. Set a positive number of days to enable pruning.
          </div>
        </div>
      )}

      {/* Advanced caps */}
      <div style={{ fontSize: '0.8rem', fontWeight: 600, color: '#fff', marginBottom: 4 }}>Version history &amp; audit caps</div>
      <p style={{ margin: '0 0 10px', fontSize: '0.72rem', color: 'var(--text-secondary, #94a3b8)', maxWidth: 640, lineHeight: 1.5 }}>
        Optional caps on the other tables that grow over time. Left at 0, nothing here is deleted.
      </p>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 12 }}>
        {ADVANCED_FIELDS.map(renderField)}
      </div>

      {config.auditEntryDays > 0 && (
        <div
          style={{
            display: 'flex', alignItems: 'flex-start', gap: 8, padding: '10px 12px', borderRadius: 10, marginTop: 12,
            background: 'rgba(248,113,113,0.08)', border: '1px solid rgba(248,113,113,0.28)',
          }}
        >
          <AlertTriangle size={15} style={{ color: '#f87171', marginTop: 1, flex: 'none' }} />
          <div style={{ fontSize: '0.78rem', color: '#fecaca', lineHeight: 1.5 }}>
            Rolling over the audit log rewrites its tamper-evident hash chain to re-anchor the surviving
            entries. Enable only if trimming the audit trail at this boundary is acceptable for your
            compliance needs.
          </div>
        </div>
      )}

      {/* Save row */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 16 }}>
        <button
          onClick={save}
          disabled={!dirty || saving}
          style={{
            padding: '8px 18px', borderRadius: 8, border: 'none', cursor: dirty && !saving ? 'pointer' : 'default',
            background: dirty ? '#3b82f6' : 'rgba(255,255,255,0.08)', color: dirty ? '#fff' : '#94a3b8',
            fontSize: '0.84rem', fontWeight: 600,
          }}
        >
          {saving ? 'Saving…' : 'Save'}
        </button>
        {savedFlash && !dirty && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: '0.78rem', color: '#4ade80' }}>
            <ShieldCheck size={14} /> Saved — applies on the next sweep.
          </span>
        )}
        {error && <span style={{ fontSize: '0.8rem', color: '#f87171' }}>{error}</span>}
      </div>
    </div>
  );
}
