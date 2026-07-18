// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import { AlertTriangle, Box, ShieldCheck } from 'lucide-react';
import { api } from '../utils/api';
import type { SandboxSettingsDto } from '../types';

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

const NUMERIC_FIELDS: { key: keyof SandboxSettingsDto; label: string; hint: string; min: number; max: number }[] = [
  { key: 'workerCount', label: 'Worker processes', hint: 'Pool size (new pools only; restart to shrink a running pool)', min: 1, max: 32 },
  { key: 'memoryLimitMb', label: 'Memory limit (MB)', hint: 'Hard cap per worker, OS-enforced', min: 64, max: 16384 },
  { key: 'cpuPercent', label: 'CPU limit (%)', hint: '100 = uncapped', min: 5, max: 100 },
  { key: 'maxRunSeconds', label: 'Max run (s)', hint: 'Ceiling when a node declares no timeout', min: 1, max: 3600 },
  { key: 'killGraceSeconds', label: 'Kill grace (s)', hint: 'After timeout, before the worker is killed', min: 1, max: 60 },
  { key: 'recycleAfterRuns', label: 'Recycle after runs', hint: 'Worker replaced after N executions', min: 1, max: 10000 },
  { key: 'maxHttpResponseMb', label: 'HTTP response cap (MB)', hint: 'Max proxied response body', min: 1, max: 100 },
];

const TOGGLES: { key: keyof SandboxSettingsDto; label: string; desc: string; processOnly: boolean }[] = [
  {
    key: 'analyzeAtRuntime',
    label: 'Screen code before running',
    desc: 'Reject scripts using banned APIs (file system, process control, …) at run time — the same check the node editor applies.',
    processOnly: false,
  },
  {
    key: 'restrictedToken',
    label: 'Restricted worker privileges (Windows)',
    desc: 'Run workers with stripped privileges and low integrity: no access to user profiles, service data or most of the file system.',
    processOnly: true,
  },
  {
    key: 'proxyCredentials',
    label: 'Keep secrets out of the sandbox',
    desc: 'Scripts receive an opaque placeholder instead of the credential value; the host injects the real secret into outgoing HTTP calls. Disable only for scripts that need the raw value (e.g. HMAC signing).',
    processOnly: true,
  },
];

/**
 * Settings → Sandbox. Where (and how confined) user-authored node C# — Inline Code and custom
 * package source — executes. "Isolated process" runs it in pooled worker processes with
 * OS-enforced memory/CPU limits and a hard kill on timeout; mode changes apply immediately.
 */
export function SandboxSetting() {
  const [settings, setSettings] = useState<SandboxSettingsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedFlash, setSavedFlash] = useState(false);

  useEffect(() => {
    // `loading` starts true, so the effect only transitions state when the fetch settles.
    api.getSandboxSettings()
      .then(setSettings)
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load the sandbox settings.'))
      .finally(() => setLoading(false));
  }, []);

  const update = (patch: Partial<SandboxSettingsDto>) => {
    setSettings((prev) => (prev ? { ...prev, ...patch } : prev));
    setDirty(true);
    setSavedFlash(false);
  };

  const save = async () => {
    if (!settings) return;
    setSaving(true);
    setError(null);
    try {
      const saved = await api.setSandboxSettings(settings);
      setSettings(saved);
      setDirty(false);
      setSavedFlash(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save the sandbox settings.');
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return <div style={cardStyle}><div style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)' }}>Loading…</div></div>;
  }
  if (!settings) {
    return (
      <div style={cardStyle}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: '0.85rem', color: '#f87171' }}>
          <AlertTriangle size={16} /> {error ?? 'Failed to load the sandbox settings.'}
        </div>
      </div>
    );
  }

  const isProcess = settings.mode === 'Process';

  return (
    <div style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '6px' }}>
        <Box size={16} style={{ color: '#60a5fa' }} />
        <h3 style={{ margin: 0, fontSize: '0.95rem', color: '#fff' }}>Code sandbox</h3>
      </div>
      <p style={{ margin: '0 0 16px', fontSize: '0.8rem', color: 'var(--text-secondary, #94a3b8)', maxWidth: 640 }}>
        Where user-authored C# — the Inline Code node and custom node packages — executes. Changes apply
        immediately; no restart needed.
      </p>

      {/* Mode selector */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginBottom: 16 }}>
        {([
          {
            mode: 'InProcess' as const,
            label: 'In-process (compatible)',
            desc: 'Scripts run inside the backend process with full access. Fast, but a runaway script can stall or crash the whole instance.',
          },
          {
            mode: 'Process' as const,
            label: 'Isolated process (recommended)',
            desc: 'Scripts run in pooled worker processes with OS-enforced memory/CPU limits. Endless loops and memory bombs are killed hard; the backend stays up.',
          },
        ]).map(({ mode, label, desc }) => {
          const selected = settings.mode === mode;
          return (
            <label
              key={mode}
              style={{
                display: 'flex', alignItems: 'flex-start', gap: 12, padding: '12px 14px', borderRadius: 10, cursor: 'pointer',
                background: selected ? 'rgba(96,165,250,0.08)' : 'rgba(0,0,0,0.15)',
                border: `1px solid ${selected ? 'rgba(96,165,250,0.4)' : 'var(--border-color, rgba(255,255,255,0.1))'}`,
              }}
            >
              <input
                type="radio"
                name="sandbox-mode"
                checked={selected}
                onChange={() => update({ mode })}
                style={{ marginTop: 3, accentColor: '#60a5fa' }}
              />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontSize: '0.86rem', fontWeight: 600, color: '#fff' }}>{label}</div>
                <div style={{ fontSize: '0.78rem', color: 'var(--text-secondary, #94a3b8)', marginTop: 2 }}>{desc}</div>
              </div>
            </label>
          );
        })}
      </div>

      {/* Toggles */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10, marginBottom: 16 }}>
        {TOGGLES.map(({ key, label, desc, processOnly }) => {
          const disabled = processOnly && !isProcess;
          const on = settings[key] === true;
          return (
            <div
              key={key}
              style={{
                display: 'flex', alignItems: 'flex-start', gap: 12, padding: '10px 14px', borderRadius: 10,
                background: 'rgba(0,0,0,0.15)', opacity: disabled ? 0.45 : 1,
                border: '1px solid var(--border-color, rgba(255,255,255,0.1))',
              }}
            >
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontSize: '0.84rem', fontWeight: 600, color: '#fff' }}>
                  {label}
                  {disabled && <span style={{ fontWeight: 400, color: '#94a3b8' }}> — isolated mode only</span>}
                </div>
                <div style={{ fontSize: '0.76rem', color: 'var(--text-secondary, #94a3b8)', marginTop: 2 }}>{desc}</div>
              </div>
              <input
                type="checkbox"
                checked={on}
                disabled={disabled}
                onChange={(e) => update({ [key]: e.target.checked } as Partial<SandboxSettingsDto>)}
                style={{ width: 16, height: 16, marginTop: 2, accentColor: '#60a5fa', cursor: disabled ? 'default' : 'pointer' }}
              />
            </div>
          );
        })}
      </div>

      {/* Numeric limits — only meaningful in Process mode */}
      <div style={{ opacity: isProcess ? 1 : 0.45 }}>
        <div style={{ fontSize: '0.8rem', fontWeight: 600, color: '#fff', marginBottom: 4 }}>
          Worker limits{!isProcess && <span style={{ fontWeight: 400, color: '#94a3b8' }}> — isolated mode only</span>}
        </div>
        <p style={{ margin: '0 0 10px', fontSize: '0.72rem', color: 'var(--text-secondary, #94a3b8)', maxWidth: 640, lineHeight: 1.5 }}>
          These limits apply <strong>only in isolated (Process) mode</strong>; in in-process mode they have no effect.
          Timeout and the hard kill are always enforced. Memory and CPU caps are best-effort at the OS level
          {' '}(Windows Job Object / Linux cgroup v2) — on Linux without cgroup v2 the CPU cap is not enforced.
        </p>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))', gap: 12 }}>
          {NUMERIC_FIELDS.map(({ key, label, hint, min, max }) => (
            <div key={key}>
              <label style={labelStyle} htmlFor={`sbx-${key}`}>{label}</label>
              <input
                id={`sbx-${key}`}
                type="number"
                min={min}
                max={max}
                disabled={!isProcess}
                value={settings[key] as number}
                onChange={(e) => update({ [key]: Number(e.target.value) } as Partial<SandboxSettingsDto>)}
                style={inputStyle}
              />
              <div style={{ fontSize: '0.68rem', color: 'var(--text-secondary, #64748b)', marginTop: 2 }}>{hint}</div>
            </div>
          ))}
        </div>
      </div>

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
            <ShieldCheck size={14} /> Saved — active immediately.
          </span>
        )}
        {error && <span style={{ fontSize: '0.8rem', color: '#f87171' }}>{error}</span>}
      </div>
    </div>
  );
}
