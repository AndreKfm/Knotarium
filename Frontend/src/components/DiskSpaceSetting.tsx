// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import { AlertTriangle, HardDriveDownload, ShieldCheck } from 'lucide-react';
import { api } from '../utils/api';
import type { DiskSpaceConfigDto } from '../types';

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

type Field = { key: keyof DiskSpaceConfigDto; label: string; hint: string; min: number; max: number };

const FIELDS: Field[] = [
  { key: 'minFreeSpaceMb', label: 'Minimum free space (MB)', hint: 'Disarm the runtime when free space drops below this. 0 = disable the guard.', min: 0, max: 10000000 },
  { key: 'freeSpaceCheckSeconds', label: 'Check interval (seconds)', hint: 'How often free space is checked. Minimum 30.', min: 30, max: 86400 },
];

/**
 * Settings → Retention (disk-space guard card). The hard backstop against filling the disk: when free
 * space on the data volume falls below the floor, the runtime is disarmed so no new runs start (each run
 * writes many journal rows). It never auto-rearms — recovering space and re-arming is a deliberate act.
 * The guard re-reads this on every check, so changes apply without a restart.
 */
export function DiskSpaceSetting() {
  const [config, setConfig] = useState<DiskSpaceConfigDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedFlash, setSavedFlash] = useState(false);

  useEffect(() => {
    api.getDiskSpaceConfig()
      .then(setConfig)
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load the disk-space settings.'))
      .finally(() => setLoading(false));
  }, []);

  const update = (patch: Partial<DiskSpaceConfigDto>) => {
    setConfig((prev) => (prev ? { ...prev, ...patch } : prev));
    setDirty(true);
    setSavedFlash(false);
  };

  const save = async () => {
    if (!config) return;
    setSaving(true);
    setError(null);
    try {
      const saved = await api.updateDiskSpaceConfig(config);
      setConfig(saved);
      setDirty(false);
      setSavedFlash(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save the disk-space settings.');
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
          <AlertTriangle size={16} /> {error ?? 'Failed to load the disk-space settings.'}
        </div>
      </div>
    );
  }

  const guardOff = config.minFreeSpaceMb <= 0;

  return (
    <div style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '6px' }}>
        <HardDriveDownload size={16} style={{ color: '#f0b429' }} />
        <h3 style={{ margin: 0, fontSize: '0.95rem', color: '#fff' }}>Disk-space guard</h3>
      </div>
      <p style={{ margin: '0 0 16px', fontSize: '0.8rem', color: 'var(--text-secondary, #94a3b8)', maxWidth: 640, lineHeight: 1.5 }}>
        The hard backstop: when free space on the data volume falls below the floor, the runtime is
        <strong> disarmed</strong> so no new runs start. It does <strong>not</strong> auto-rearm — free up
        space and re-arm from the top bar. Complements retention, which only prunes on a schedule.
      </p>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 12 }}>
        {FIELDS.map(({ key, label, hint, min, max }) => (
          <div key={key}>
            <label style={labelStyle} htmlFor={`ds-${key}`}>{label}</label>
            <input
              id={`ds-${key}`}
              type="number"
              min={min}
              max={max}
              value={config[key]}
              onChange={(e) => update({ [key]: Number(e.target.value) } as Partial<DiskSpaceConfigDto>)}
              style={inputStyle}
            />
            <div style={{ fontSize: '0.68rem', color: 'var(--text-secondary, #64748b)', marginTop: 2 }}>{hint}</div>
          </div>
        ))}
      </div>

      {guardOff && (
        <div
          style={{
            display: 'flex', alignItems: 'flex-start', gap: 8, padding: '10px 12px', borderRadius: 10, marginTop: 14,
            background: 'rgba(248,113,113,0.08)', border: '1px solid rgba(248,113,113,0.28)',
          }}
        >
          <AlertTriangle size={15} style={{ color: '#f87171', marginTop: 1, flex: 'none' }} />
          <div style={{ fontSize: '0.78rem', color: '#fecaca', lineHeight: 1.5 }}>
            The guard is <strong>disabled</strong> (0 MB). Nothing will stop new runs as the disk approaches
            full — SQLite will start failing writes. Set a positive floor (e.g. 512 MB) to re-enable it.
          </div>
        </div>
      )}

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
            <ShieldCheck size={14} /> Saved — applies on the next check.
          </span>
        )}
        {error && <span style={{ fontSize: '0.8rem', color: '#f87171' }}>{error}</span>}
      </div>
    </div>
  );
}
