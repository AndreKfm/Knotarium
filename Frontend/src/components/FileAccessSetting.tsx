// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import { AlertTriangle, FolderLock, Plus, ShieldAlert, Trash2 } from 'lucide-react';
import type { FileAccessPolicyDto, FileAccessRuleMode } from '../types';
import { api } from '../utils/api';
import { usePendingFileAccessGrantStore } from '../stores/usePendingFileAccessGrantStore';

const cardStyle: React.CSSProperties = {
  padding: '20px',
  borderRadius: '12px',
  background: 'rgba(255, 255, 255, 0.03)',
  border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))',
  marginBottom: '24px',
};

const inputStyle: React.CSSProperties = {
  padding: '9px 10px',
  borderRadius: '8px',
  background: 'rgba(0, 0, 0, 0.2)',
  border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))',
  color: '#fff',
  fontSize: '0.85rem',
  outline: 'none',
  boxSizing: 'border-box',
};

const MB = 1024 * 1024;

const MODE_LABELS: Record<FileAccessRuleMode, string> = {
  read: 'Read',
  write: 'Write',
  both: 'Read & write',
};

/**
 * Settings → File Access. Configures the instance-global policy the file nodes enforce before any IO:
 * which directory subtrees are readable/writable, the free-space reserve for writes, and the escape
 * hatch of unrestricted "total access" (which must be confirmed explicitly). Deny-by-default: with no
 * grants and total access off, every file operation is blocked.
 */
export function FileAccessSetting() {
  const [policy, setPolicy] = useState<FileAccessPolicyDto | null>(null);
  // Snapshot of the last loaded/saved policy — Save is disabled until the form diverges from it.
  const [baseline, setBaseline] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedNote, setSavedNote] = useState<string | null>(null);
  const [confirmTotal, setConfirmTotal] = useState(false);

  useEffect(() => {
    setLoading(true);
    api.getFileAccessPolicy()
      .then((p) => {
        const n = normalize(p);
        setBaseline(canonical(n));
        // Pre-fill a write grant when we arrived from a run's "Grant this path" CTA. Appended AFTER the
        // baseline snapshot so the form reads as dirty (Save enabled) with the suggested path ready to review.
        const pending = usePendingFileAccessGrantStore.getState().pendingPath;
        if (pending && !n.rules.some((r) => r.path.trim() === pending.trim())) {
          n.rules = [...n.rules, { path: pending, mode: 'write' }];
        }
        usePendingFileAccessGrantStore.getState().clear();
        setPolicy(n);
      })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load file-access policy.'))
      .finally(() => setLoading(false));
  }, []);

  function normalize(p: FileAccessPolicyDto): FileAccessPolicyDto {
    return {
      totalAccess: !!p.totalAccess,
      rules: Array.isArray(p.rules) ? p.rules : [],
      minFreeBytes: p.minFreeBytes ?? null,
      minFreePercent: p.minFreePercent ?? null,
    };
  }

  // A stable string of exactly what a save would persist (blank-path rows dropped), for the dirty check.
  function canonical(p: FileAccessPolicyDto): string {
    return JSON.stringify({
      totalAccess: p.totalAccess,
      rules: p.rules.filter((r) => r.path.trim().length > 0).map((r) => ({ path: r.path.trim(), mode: r.mode })),
      minFreeBytes: p.minFreeBytes ?? null,
      minFreePercent: p.minFreePercent ?? null,
    });
  }

  const patch = (next: Partial<FileAccessPolicyDto>) => {
    setSavedNote(null);
    setPolicy((prev) => (prev ? { ...prev, ...next } : prev));
  };

  const addRule = () => policy && patch({ rules: [...policy.rules, { path: '', mode: 'read' }] });
  const updateRule = (i: number, next: Partial<{ path: string; mode: FileAccessRuleMode }>) =>
    policy && patch({ rules: policy.rules.map((r, idx) => (idx === i ? { ...r, ...next } : r)) });
  const removeRule = (i: number) => policy && patch({ rules: policy.rules.filter((_, idx) => idx !== i) });

  const handleSave = async () => {
    if (!policy) return;
    setSaving(true);
    setError(null);
    setSavedNote(null);
    try {
      const saved = await api.setFileAccessPolicy({
        ...policy,
        rules: policy.rules.filter((r) => r.path.trim().length > 0),
      });
      const n = normalize(saved);
      setPolicy(n);
      setBaseline(canonical(n));
      setSavedNote('Saved.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save file-access policy.');
    } finally {
      setSaving(false);
    }
  };

  // Turning total access ON is gated behind an explicit confirm; turning it OFF is immediate.
  const onToggleTotal = (next: boolean) => {
    if (next) setConfirmTotal(true);
    else patch({ totalAccess: false });
  };

  if (loading) {
    return (
      <div style={cardStyle}>
        <div style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)' }}>Loading…</div>
      </div>
    );
  }

  if (!policy) {
    return (
      <div style={cardStyle}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: '0.85rem', color: '#f87171' }}>
          <AlertTriangle size={16} /> {error ?? 'Failed to load the file-access policy.'}
        </div>
      </div>
    );
  }

  const minFreeMb = policy.minFreeBytes != null ? Math.round(policy.minFreeBytes / MB) : '';
  const denyByDefault = !policy.totalAccess && policy.rules.filter((r) => r.path.trim()).length === 0;
  const dirty = canonical(policy) !== baseline;

  return (
    <div style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '6px' }}>
        <FolderLock size={16} style={{ color: '#22d3ee' }} />
        <h3 style={{ margin: 0, fontSize: '0.95rem', color: '#fff' }}>File Access</h3>
      </div>
      <p style={{ margin: '0 0 16px', fontSize: '0.8rem', color: 'var(--text-secondary, #94a3b8)', maxWidth: 640 }}>
        The File Read / File Write nodes may only touch paths you allow here. Access is denied by default —
        grant one or more directories (subfolders are included). All path traversal and symlink escapes are
        blocked server-side, so a workflow can never reach outside a granted directory.
      </p>

      {/* Total access escape hatch */}
      <label style={{ display: 'flex', alignItems: 'flex-start', gap: '10px', cursor: 'pointer', marginBottom: policy.totalAccess ? 12 : 20 }}>
        <input
          type="checkbox"
          checked={policy.totalAccess}
          onChange={(e) => onToggleTotal(e.target.checked)}
          style={{ marginTop: 2, width: 16, height: 16, accentColor: '#f0b429', cursor: 'pointer' }}
        />
        <span>
          <span style={{ fontSize: '0.85rem', fontWeight: 600, color: '#fff' }}>Total access (unrestricted)</span>
          <span style={{ display: 'block', fontSize: '0.78rem', color: 'var(--text-secondary, #94a3b8)' }}>
            Let file nodes read and write anywhere on the host. Overrides the path grants below.
          </span>
        </span>
      </label>

      {policy.totalAccess && (
        <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start', padding: '10px 12px', borderRadius: 8, background: 'rgba(240,180,41,0.08)', border: '1px solid rgba(240,180,41,0.3)', marginBottom: 20 }}>
          <ShieldAlert size={16} style={{ color: '#f0b429', flex: 'none', marginTop: 1 }} />
          <span style={{ fontSize: '0.78rem', color: '#e8cf9a' }}>
            Total access is on. Any workflow — including imported or AI-generated ones — can read and write
            every file the host process can reach. Only leave this on for a trusted single-operator instance.
          </span>
        </div>
      )}

      {/* Path grants (only meaningful when total access is off) */}
      {!policy.totalAccess && (
        <div style={{ marginBottom: 20 }}>
          <div style={{ fontSize: '0.82rem', fontWeight: 600, color: '#cbd5e1', marginBottom: 8 }}>Permitted paths</div>

          {policy.rules.length === 0 ? (
            <div style={{ fontSize: '0.8rem', color: 'var(--text-secondary, #94a3b8)', padding: '10px 0' }}>
              No paths granted — all file access is denied.
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {policy.rules.map((rule, i) => (
                <div key={i} style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                  <input
                    value={rule.path}
                    onChange={(e) => updateRule(i, { path: e.target.value })}
                    placeholder="C:\\data\\workflows  (absolute path)"
                    spellCheck={false}
                    style={{ ...inputStyle, flex: 1, fontFamily: 'ui-monospace, Menlo, monospace' }}
                  />
                  <select
                    value={rule.mode}
                    onChange={(e) => updateRule(i, { mode: e.target.value as FileAccessRuleMode })}
                    style={{ ...inputStyle, flex: '0 0 130px', cursor: 'pointer' }}
                  >
                    <option value="read">{MODE_LABELS.read}</option>
                    <option value="write">{MODE_LABELS.write}</option>
                    <option value="both">{MODE_LABELS.both}</option>
                  </select>
                  <button
                    type="button"
                    onClick={() => removeRule(i)}
                    aria-label="Remove path"
                    title="Remove path"
                    style={{ ...inputStyle, flex: 'none', width: 38, display: 'grid', placeItems: 'center', cursor: 'pointer', color: '#f0556d', background: 'rgba(240,85,109,0.08)', borderColor: 'rgba(240,85,109,0.25)' }}
                  >
                    <Trash2 size={15} />
                  </button>
                </div>
              ))}
            </div>
          )}

          <button
            type="button"
            onClick={addRule}
            style={{ marginTop: 10, display: 'inline-flex', alignItems: 'center', gap: 6, padding: '8px 12px', borderRadius: 8, background: 'rgba(34,211,238,0.1)', border: '1px solid rgba(34,211,238,0.3)', color: '#22d3ee', fontSize: '0.82rem', fontWeight: 600, cursor: 'pointer' }}
          >
            <Plus size={14} /> Add path
          </button>
        </div>
      )}

      {/* Free-space reserve for writes */}
      <div style={{ marginBottom: 20 }}>
        <div style={{ fontSize: '0.82rem', fontWeight: 600, color: '#cbd5e1', marginBottom: 4 }}>Free-space reserve (writes)</div>
        <p style={{ margin: '0 0 10px', fontSize: '0.78rem', color: 'var(--text-secondary, #94a3b8)' }}>
          Block a write if it would drop the target drive below this reserve. Both limits apply — whichever is
          stricter wins. Leave blank for no reserve.
        </p>
        <div style={{ display: 'flex', gap: 14, flexWrap: 'wrap' }}>
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4, fontSize: '0.75rem', color: 'var(--text-secondary, #94a3b8)' }}>
            Minimum free (MB)
            <input
              type="number"
              min={0}
              value={minFreeMb}
              onChange={(e) => patch({ minFreeBytes: e.target.value === '' ? null : Math.max(0, Number(e.target.value)) * MB })}
              placeholder="—"
              style={{ ...inputStyle, width: 160 }}
            />
          </label>
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4, fontSize: '0.75rem', color: 'var(--text-secondary, #94a3b8)' }}>
            Minimum free (%)
            <input
              type="number"
              min={0}
              max={100}
              value={policy.minFreePercent ?? ''}
              onChange={(e) => patch({ minFreePercent: e.target.value === '' ? null : Math.min(100, Math.max(0, Number(e.target.value))) })}
              placeholder="—"
              style={{ ...inputStyle, width: 160 }}
            />
          </label>
        </div>
      </div>

      {denyByDefault && (
        <div style={{ display: 'flex', gap: 8, alignItems: 'center', padding: '9px 12px', borderRadius: 8, background: 'rgba(148,163,184,0.08)', border: '1px solid rgba(148,163,184,0.2)', marginBottom: 16 }}>
          <AlertTriangle size={15} style={{ color: '#94a3b8', flex: 'none' }} />
          <span style={{ fontSize: '0.78rem', color: '#94a3b8' }}>
            No paths granted and total access off — every File Read / File Write will fail until you allow a path.
          </span>
        </div>
      )}

      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <button
          type="button"
          onClick={handleSave}
          disabled={saving || !dirty}
          style={{ padding: '9px 18px', borderRadius: 8, background: 'var(--color-accent, #22d3ee)', border: 'none', color: '#06121a', fontWeight: 700, fontSize: '0.83rem', cursor: saving || !dirty ? 'default' : 'pointer', opacity: saving || !dirty ? 0.5 : 1 }}
        >
          {saving ? 'Saving…' : 'Save policy'}
        </button>
        {!dirty && !savedNote && <span style={{ fontSize: '0.78rem', color: 'var(--text-secondary, #94a3b8)' }}>No changes.</span>}
        {savedNote && <span style={{ fontSize: '0.78rem', color: '#34d399' }}>{savedNote}</span>}
        {error && <span style={{ fontSize: '0.8rem', color: '#f87171' }}>{error}</span>}
      </div>

      {/* Explicit confirmation for enabling total access */}
      {confirmTotal && (
        <div
          onClick={() => setConfirmTotal(false)}
          style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.55)', display: 'grid', placeItems: 'center', zIndex: 100 }}
        >
          <div
            onClick={(e) => e.stopPropagation()}
            style={{ width: 'min(460px, 92vw)', padding: 22, borderRadius: 14, background: '#0d121b', border: '1px solid rgba(240,180,41,0.4)', boxShadow: '0 24px 60px -20px rgba(0,0,0,0.8)' }}
          >
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 10 }}>
              <ShieldAlert size={20} style={{ color: '#f0b429' }} />
              <h3 style={{ margin: 0, fontSize: '1rem', color: '#fff' }}>Grant total file access?</h3>
            </div>
            <p style={{ margin: '0 0 18px', fontSize: '0.84rem', lineHeight: 1.6, color: '#c7d2dc' }}>
              Every workflow — including imported and AI-generated ones — will be able to read and write any
              file the host can reach, bypassing the path grants. Only do this on a trusted, single-operator
              instance. You can turn it off again at any time.
            </p>
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10 }}>
              <button
                type="button"
                onClick={() => setConfirmTotal(false)}
                style={{ padding: '8px 14px', borderRadius: 8, background: 'transparent', border: '1px solid var(--border-color, rgba(255,255,255,0.15))', color: '#cbd5e1', fontSize: '0.83rem', fontWeight: 600, cursor: 'pointer' }}
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => { patch({ totalAccess: true }); setConfirmTotal(false); }}
                style={{ padding: '8px 14px', borderRadius: 8, background: '#f0b429', border: 'none', color: '#1a1206', fontSize: '0.83rem', fontWeight: 700, cursor: 'pointer' }}
              >
                Enable total access
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
