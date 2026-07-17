// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import { Plus, Edit2, Trash2, RefreshCw, Plug, Camera, Radio, Zap, Activity } from 'lucide-react';
import type {
  ProviderDescriptor,
  ExternalSystemInfo,
  ExternalTargetInfo,
  ExternalTargetEdit,
  TargetConnectivity,
} from '../types';
import { api } from '../utils/api';

const inputStyle: React.CSSProperties = {
  width: '100%',
  padding: '10px',
  borderRadius: '8px',
  background: 'rgba(0, 0, 0, 0.2)',
  border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))',
  color: '#fff',
  fontSize: '0.85rem',
  outline: 'none',
  boxSizing: 'border-box',
};

const labelStyle: React.CSSProperties = {
  fontSize: '0.75rem',
  fontWeight: 700,
  color: 'var(--text-secondary, #94a3b8)',
  textTransform: 'uppercase',
};

const STATUS_STYLE: Record<TargetConnectivity, { color: string; bg: string; border: string; label: string }> = {
  Online: { color: '#4ade80', bg: 'rgba(74,222,128,.1)', border: 'rgba(74,222,128,.25)', label: 'Online' },
  Connecting: { color: '#fbbf24', bg: 'rgba(251,191,36,.1)', border: 'rgba(251,191,36,.25)', label: 'Connecting' },
  Offline: { color: '#7a8899', bg: 'rgba(122,136,153,.1)', border: 'rgba(122,136,153,.25)', label: 'Offline' },
  Faulted: { color: '#f87171', bg: 'rgba(248,113,113,.1)', border: 'rgba(248,113,113,.25)', label: 'Faulted' },
};

// The host serializes TargetConnectivity as its numeric enum value; map it back to the label.
const CONNECTIVITY_BY_NUMBER: Record<number, TargetConnectivity> = { 0: 'Offline', 1: 'Connecting', 2: 'Online', 3: 'Faulted' };
function connectivityLabel(value: TargetConnectivity | number): TargetConnectivity {
  return typeof value === 'number' ? (CONNECTIVITY_BY_NUMBER[value] ?? 'Offline') : value;
}

// Compact local time for the diagnostics activity feed; falls back to the raw string if unparseable.
function formatActivityTime(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

interface FormState {
  id: string | null;
  name: string;
  host: string;
  port: string;
  user: string;
  password: string;
  hasCredential: boolean;
  suppressSelfEcho: boolean;
}

const EMPTY_FORM: FormState = { id: null, name: '', host: '', port: '0', user: '', password: '', hasCredential: false, suppressSelfEcho: true };

/**
 * In-app config editor for an external-signal provider that supports administration (e.g. the Device
 * Workflow connection manager). Turns "edit a JSON file and restart" into add/edit/sync of targets from
 * the UI — edits apply live. The component is vendor-neutral: all branding (titles, nouns) comes from the
 * provider's runtime descriptor, so this file names no specific system.
 */
export function ExternalSystemsManager() {
  const [descriptor, setDescriptor] = useState<ProviderDescriptor | null>(null);
  const [system, setSystem] = useState<ExternalSystemInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<Record<string, string>>({}); // targetId -> transient status text

  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const [validationError, setValidationError] = useState<string | null>(null);
  const [testStatus, setTestStatus] = useState<string | null>(null);
  const [deleteConfirm, setDeleteConfirm] = useState<ExternalTargetInfo | null>(null);

  const targetNoun = descriptor?.targetNoun ?? 'server';
  const channelNoun = descriptor?.channelNoun ?? 'channel';

  const loadData = () => {
    setLoading(true);
    setError(null);
    api.getExternalSystemsDescriptor()
      .then((desc) => {
        if (!desc) {
          setUnavailable(true);
          return null;
        }
        setDescriptor(desc);
        return api.getExternalSystem().then(setSystem);
      })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load external systems.'))
      .finally(() => setLoading(false));
  };

  useEffect(loadData, []);

  const openCreate = () => {
    setForm(EMPTY_FORM);
    setValidationError(null);
    setTestStatus(null);
    setShowForm(true);
  };

  const openEdit = (t: ExternalTargetInfo) => {
    setForm({ id: t.id, name: t.name, host: t.host, port: String(t.port), user: t.user ?? '', password: '', hasCredential: t.hasCredential, suppressSelfEcho: t.suppressSelfEcho ?? true });
    setValidationError(null);
    setTestStatus(null);
    setShowForm(true);
  };

  const buildEdit = (): ExternalTargetEdit => ({
    id: form.id,
    name: form.name.trim(),
    host: form.host.trim(),
    port: Number(form.port) || 0,
    user: form.user.trim() || null,
    // null keeps the stored secret; a value replaces it.
    password: form.password ? form.password : null,
    suppressSelfEcho: form.suppressSelfEcho,
  });

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setValidationError(null);
    if (!form.name.trim() || !form.host.trim()) {
      setValidationError('Name and host are required.');
      return;
    }
    api.upsertExternalTarget(buildEdit())
      .then(() => {
        setShowForm(false);
        loadData();
      })
      .catch((err) => setValidationError(err instanceof Error ? err.message : 'Save failed.'));
  };

  const handleTest = () => {
    setTestStatus('Testing…');
    api.testExternalTarget(buildEdit())
      .then((status) => {
        const label = connectivityLabel(status.connectivity);
        setTestStatus(label === 'Online' ? '✓ Connection OK' : `✗ ${status.lastError ?? label}`);
      })
      .catch((err) => setTestStatus(`✗ ${err instanceof Error ? err.message : 'Failed'}`));
  };

  const handleSync = (t: ExternalTargetInfo) => {
    setBusy((p) => ({ ...p, [t.id]: 'Syncing…' }));
    api.syncExternalTarget(t.id)
      .then((updated) => {
        setBusy((p) => ({ ...p, [t.id]: `✓ ${updated.channels.length} ${channelNoun}s, ${updated.events.length} events, ${updated.actions.length} actions` }));
        loadData();
      })
      .catch((err) => setBusy((p) => ({ ...p, [t.id]: `✗ ${err instanceof Error ? err.message : 'Sync failed'}` })));
  };

  const handleToggleOption = (key: string, value: boolean) => {
    setError(null);
    // Optimistic flip so the switch responds immediately; reconcile with the server's returned state.
    setSystem((prev) =>
      prev ? { ...prev, options: prev.options?.map((o) => (o.key === key ? { ...o, value } : o)) } : prev,
    );
    api.setExternalSystemOption(key, value)
      .then(setSystem)
      .catch((err) => {
        setError(err instanceof Error ? err.message : 'Failed to update option.');
        loadData(); // roll back the optimistic flip to the true server state
      });
  };

  const handleDelete = () => {
    if (!deleteConfirm) return;
    const id = deleteConfirm.id;
    api.deleteExternalTarget(id)
      .then(() => {
        setDeleteConfirm(null);
        loadData();
      })
      .catch((err) => {
        setError(err instanceof Error ? err.message : 'Delete failed.');
        setDeleteConfirm(null);
      });
  };

  if (loading && !system) {
    return <div style={{ padding: '40px 20px', textAlign: 'center', color: '#566173', fontSize: 13 }}>Loading…</div>;
  }

  if (unavailable) {
    return (
      <div style={{ padding: '48px 24px', textAlign: 'center', color: '#7a8899' }}>
        <Plug size={36} style={{ opacity: 0.3, marginBottom: 12 }} />
        <div style={{ fontSize: 15, fontWeight: 600, color: '#9aa6b5', marginBottom: 6 }}>No configurable integration installed</div>
        <div style={{ fontSize: 13 }}>Install a connection-manager plugin to configure external systems here.</div>
      </div>
    );
  }

  const title = descriptor?.displayName ?? 'External Systems';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', padding: '24px', overflowY: 'auto' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '8px' }}>
        <div>
          <h2 style={{ fontSize: '1.4rem', fontWeight: 700, color: '#fff', margin: 0 }}>{title}</h2>
          <p style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)', marginTop: '4px' }}>
            Configure your {targetNoun}s — connection, credentials, and the live catalog used by the editor. Changes
            apply immediately; no restart needed.
          </p>
        </div>
        <button
          onClick={openCreate}
          aria-label={`Add ${targetNoun}`}
          style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '10px 16px', borderRadius: 10, background: 'var(--color-accent, #6366f1)', border: 'none', color: '#fff', fontSize: 13.5, fontWeight: 600, cursor: 'pointer', whiteSpace: 'nowrap' }}
        >
          <Plus size={16} /> Add {targetNoun}
        </button>
      </div>

      {system && (
        <div style={{ fontSize: '0.75rem', color: '#566173', marginBottom: 20 }}>
          System: <span style={{ color: '#8995a6', fontWeight: 600 }}>{system.name}</span>
        </div>
      )}

      {error && (
        <div style={{ padding: '12px 16px', borderRadius: '10px', background: 'rgba(239, 68, 68, 0.1)', border: '1px solid rgba(239, 68, 68, 0.2)', color: 'var(--color-error, #f87171)', fontSize: '0.85rem', marginBottom: '20px' }}>
          {error}
        </div>
      )}

      {system?.options && system.options.length > 0 && (
        <section aria-label="System options" style={{ display: 'flex', flexDirection: 'column', gap: 10, marginBottom: 20 }}>
          {system.options.map((opt) => (
            <div key={opt.key} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 16, background: 'rgba(255,255,255,0.02)', border: '1px solid rgba(255,255,255,0.06)', borderRadius: 12, padding: '14px 16px' }}>
              <div style={{ minWidth: 0 }}>
                <div style={{ fontSize: '0.9rem', fontWeight: 600, color: '#e6edf3' }}>{opt.label}</div>
                {opt.description && (
                  <div style={{ fontSize: '0.76rem', color: '#7a8899', marginTop: 3, lineHeight: 1.45 }}>{opt.description}</div>
                )}
              </div>
              <button
                type="button"
                role="switch"
                aria-checked={opt.value}
                aria-label={opt.label}
                onClick={() => handleToggleOption(opt.key, !opt.value)}
                style={{
                  flexShrink: 0, width: 44, height: 24, borderRadius: 999, border: 'none', cursor: 'pointer', position: 'relative',
                  background: opt.value ? 'var(--color-accent, #6366f1)' : '#2b3648', transition: 'background .15s',
                }}
              >
                <span style={{ position: 'absolute', top: 3, left: opt.value ? 23 : 3, width: 18, height: 18, borderRadius: '50%', background: '#fff', transition: 'left .15s' }} />
              </button>
            </div>
          ))}
        </section>
      )}

      {system?.diagnostics && (
        <section aria-label="Diagnostics" style={{ marginBottom: 20, background: '#0d1422', border: '1px solid #18222f', borderRadius: 14, padding: '16px 18px' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12 }}>
            <Activity size={14} style={{ color: '#8995a6' }} />
            <span style={{ fontSize: '0.75rem', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '.04em', color: '#8995a6' }}>Diagnostics</span>
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 20, marginBottom: system.diagnostics.recentActivity.length > 0 ? 14 : 0 }}>
            {system.diagnostics.metrics.map((m) => (
              <div key={m.key}>
                <div style={{ fontSize: '1.3rem', fontWeight: 700, color: '#f4f7fb' }}>{m.value}</div>
                <div style={{ fontSize: '0.72rem', color: '#7a8899' }}>{m.label}</div>
              </div>
            ))}
          </div>
          {system.diagnostics.recentActivity.length > 0 && (
            <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 6, borderTop: '1px solid #141d29', paddingTop: 12 }}>
              {system.diagnostics.recentActivity.map((a, i) => (
                <li key={`${a.timestamp}-${i}`} style={{ display: 'flex', gap: 10, fontSize: '0.76rem', color: '#9aa6b5' }}>
                  <span style={{ color: '#566173', fontVariantNumeric: 'tabular-nums', flexShrink: 0 }}>{formatActivityTime(a.timestamp)}</span>
                  <span title={a.detail ?? undefined}>{a.summary}</span>
                </li>
              ))}
            </ul>
          )}
        </section>
      )}

      {system && system.targets.length === 0 ? (
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', padding: '64px 24px', background: 'rgba(255, 255, 255, 0.02)', border: '1px dashed var(--border-color, rgba(255,255,255,0.1))', borderRadius: '16px', color: '#566173', textAlign: 'center' }}>
          <Plug size={36} style={{ opacity: 0.3, marginBottom: 12 }} />
          <div style={{ fontSize: 15, fontWeight: 600, color: '#9aa6b5', marginBottom: 4 }}>No {targetNoun}s configured yet</div>
          <div style={{ fontSize: 13 }}>Click "Add {targetNoun}" to connect one — then drop a workflow block and pick it.</div>
        </div>
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(360px, 1fr))', gap: '16px' }}>
          {system?.targets.map((t) => {
            const st = STATUS_STYLE[connectivityLabel(t.status.connectivity)] ?? STATUS_STYLE.Offline;
            return (
              <div key={t.id} style={{ background: '#0d1422', border: '1px solid #18222f', borderRadius: '16px', padding: '18px 20px', display: 'flex', flexDirection: 'column', gap: '12px' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
                  <div>
                    <h3 style={{ fontSize: '1.05rem', fontWeight: 700, color: '#f4f7fb', margin: 0 }}>{t.name}</h3>
                    <div style={{ fontSize: '0.78rem', color: '#7a8899', marginTop: 3 }}>
                      {t.host}{t.port ? `:${t.port}` : ''}{t.user ? ` · ${t.user}` : ''}
                    </div>
                  </div>
                  <span title={t.status.lastError ?? undefined} style={{ display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: '0.66rem', fontWeight: 700, color: st.color, background: st.bg, border: `1px solid ${st.border}`, padding: '3px 8px', borderRadius: '6px' }}>
                    <span style={{ width: 6, height: 6, borderRadius: '50%', background: st.color }} /> {st.label}
                  </span>
                </div>

                <div style={{ display: 'flex', gap: 14, fontSize: '0.78rem', color: '#8995a6' }}>
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }} title={`${channelNoun}s`}><Camera size={13} /> {t.channels.length}</span>
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }} title="events"><Radio size={13} /> {t.events.length}</span>
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }} title="actions"><Zap size={13} /> {t.actions.length}</span>
                  {!t.hasCredential && descriptor?.requiresCredentials && (
                    <span style={{ color: '#fbbf24' }} title="No credential stored">⚠ no password</span>
                  )}
                </div>

                {busy[t.id] && (
                  <div style={{ fontSize: '0.75rem', color: busy[t.id].startsWith('✓') ? '#4ade80' : busy[t.id].startsWith('✗') ? '#f87171' : '#94a3b8' }}>
                    {busy[t.id]}
                  </div>
                )}

                <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '8px', borderTop: '1px solid #141d29', paddingTop: '12px', marginTop: '4px' }}>
                  {descriptor?.supportsSync && (
                    <button onClick={() => handleSync(t)} aria-label={`Sync ${t.name}`} style={ghostBtn}>
                      <RefreshCw size={13} /> Sync
                    </button>
                  )}
                  <button onClick={() => openEdit(t)} aria-label={`Edit ${t.name}`} style={ghostBtn}>
                    <Edit2 size={13} /> Edit
                  </button>
                  <button onClick={() => setDeleteConfirm(t)} aria-label={`Delete ${t.name}`} style={dangerBtn}>
                    <Trash2 size={13} /> Delete
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {showForm && (
        <div style={modalBackdrop} onClick={() => setShowForm(false)}>
          <div style={{ ...modalCard, width: 520 }} onClick={(e) => e.stopPropagation()}>
            <form onSubmit={handleSubmit}>
              <div style={{ padding: '20px 24px 16px', borderBottom: '1px solid #1a2433' }}>
                <h3 style={{ fontSize: '1.1rem', fontWeight: 700, color: '#fff', margin: 0 }}>
                  {form.id ? `Edit ${targetNoun}` : `Add ${targetNoun}`}
                </h3>
              </div>

              <div style={{ padding: '20px 24px', display: 'flex', flexDirection: 'column', gap: '14px', maxHeight: '65vh', overflowY: 'auto' }}>
                {validationError && (
                  <div role="alert" style={{ padding: '10px 14px', borderRadius: '8px', background: 'rgba(239, 68, 68, 0.1)', border: '1px solid rgba(239, 68, 68, 0.2)', color: 'var(--color-error, #f87171)', fontSize: '0.8rem' }}>
                    {validationError}
                  </div>
                )}

                <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                  <label style={labelStyle}>Name <span style={{ color: '#f87171' }}>*</span></label>
                  <input type="text" placeholder="e.g. Device 01 (Front Building)" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} style={inputStyle} />
                </div>

                <div style={{ display: 'flex', gap: 12 }}>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', flex: 2 }}>
                    <label style={labelStyle}>Host / IP <span style={{ color: '#f87171' }}>*</span></label>
                    <input type="text" placeholder="10.0.0.11" value={form.host} onChange={(e) => setForm({ ...form, host: e.target.value })} style={inputStyle} />
                  </div>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', flex: 1 }}>
                    <label style={labelStyle}>Port</label>
                    <input type="number" placeholder="0" value={form.port} onChange={(e) => setForm({ ...form, port: e.target.value })} style={inputStyle} />
                  </div>
                </div>

                <div style={{ display: 'flex', gap: 12 }}>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', flex: 1 }}>
                    <label style={labelStyle}>Username</label>
                    <input type="text" placeholder="sysadmin" value={form.user} onChange={(e) => setForm({ ...form, user: e.target.value })} style={inputStyle} />
                  </div>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', flex: 1 }}>
                    <label style={labelStyle}>Password</label>
                    <input
                      type="password"
                      placeholder={form.hasCredential ? '•••••••• (stored)' : 'No password set — type one'}
                      value={form.password}
                      onChange={(e) => setForm({ ...form, password: e.target.value })}
                      style={inputStyle}
                    />
                  </div>
                </div>

                {form.id && (form.hasCredential ? (
                  <div style={{ fontSize: '0.74rem', color: '#7a8899', background: 'rgba(255,255,255,0.03)', border: '1px solid rgba(255,255,255,0.06)', borderRadius: 8, padding: '8px 10px' }}>
                    A password is stored (encrypted). Leave the field empty to keep it; type a new one to replace it.
                  </div>
                ) : (
                  <div style={{ fontSize: '0.74rem', color: '#fbbf24', background: 'rgba(240,180,41,0.06)', border: '1px solid rgba(240,180,41,0.25)', borderRadius: 8, padding: '8px 10px' }}>
                    No password is set for this target — it can't connect until you enter one (and a real host/port). Created-on-import targets start blank.
                  </div>
                ))}

                <label style={{ display: 'flex', alignItems: 'flex-start', gap: 10, cursor: 'pointer', background: 'rgba(255,255,255,0.02)', border: '1px solid rgba(255,255,255,0.06)', borderRadius: 8, padding: '10px 12px' }}>
                  <input
                    type="checkbox"
                    checked={form.suppressSelfEcho}
                    onChange={(e) => setForm({ ...form, suppressSelfEcho: e.target.checked })}
                    style={{ marginTop: 2, width: 16, height: 16, accentColor: 'var(--color-accent, #6366f1)' }}
                  />
                  <span>
                    <span style={{ fontSize: '0.85rem', fontWeight: 600, color: '#e6edf3' }}>Suppress self-echo</span>
                    <span style={{ display: 'block', fontSize: '0.74rem', color: '#7a8899', marginTop: 2, lineHeight: 1.45 }}>
                      Drop this {targetNoun}'s reflection of its own outbound actions, so a workflow that fires an action
                      <b> and</b> subscribes to that action type on this {targetNoun} doesn't re-trigger itself. On by default.
                    </span>
                  </span>
                </label>

                {descriptor?.supportsTestConnection && (
                  <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                    <button type="button" onClick={handleTest} style={ghostBtn}>
                      <Plug size={13} /> Test connection
                    </button>
                    {testStatus && (
                      <span style={{ fontSize: '0.78rem', color: testStatus.startsWith('✓') ? '#4ade80' : testStatus.startsWith('✗') ? '#f87171' : '#94a3b8' }}>
                        {testStatus}
                      </span>
                    )}
                  </div>
                )}

                <div style={{ fontSize: '0.74rem', color: '#566173' }}>
                  After saving, use <b>Sync</b> on the card to pull this {targetNoun}'s live {channelNoun}s, events and
                  actions into the editor pickers.
                </div>
              </div>

              <div style={{ padding: '16px 24px 20px', borderTop: '1px solid #1a2433', display: 'flex', justifyContent: 'flex-end', gap: '12px' }}>
                <button type="button" onClick={() => setShowForm(false)} style={{ padding: '9px 18px', borderRadius: 10, fontSize: 13, fontWeight: 600, cursor: 'pointer', border: '1px solid #243245', background: 'transparent', color: '#8995a6' }}>
                  Cancel
                </button>
                <button type="submit" style={{ padding: '9px 18px', borderRadius: 10, fontSize: 13, fontWeight: 600, cursor: 'pointer', border: 'none', background: 'var(--color-accent, #6366f1)', color: '#fff' }}>
                  Save
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {deleteConfirm && (
        <div style={modalBackdrop} onClick={() => setDeleteConfirm(null)}>
          <div style={{ ...modalCard, padding: 24, width: '90%', maxWidth: 420 }} onClick={(e) => e.stopPropagation()}>
            <div style={{ fontSize: 18, fontWeight: 700, marginBottom: 12, color: '#fff' }}>Delete "{deleteConfirm.name}"?</div>
            <div style={{ fontSize: 14.5, color: '#9aa6b5', lineHeight: 1.5, marginBottom: 24 }}>
              Workflow blocks pointing at this {targetNoun} will stop resolving. Its stored credential is removed too.
              This cannot be undone.
            </div>
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 12 }}>
              <button onClick={() => setDeleteConfirm(null)} style={{ padding: '10px 18px', borderRadius: 10, fontSize: 13.5, fontWeight: 600, cursor: 'pointer', border: '1px solid #243245', background: 'transparent', color: '#8995a6' }}>
                Cancel
              </button>
              <button onClick={handleDelete} style={{ padding: '10px 18px', borderRadius: 10, fontSize: 13.5, fontWeight: 600, cursor: 'pointer', border: 'none', background: '#f0556d', color: '#fff' }}>
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

const ghostBtn: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, background: 'transparent', border: '1px solid #243245',
  color: '#8995a6', padding: '6px 12px', borderRadius: '8px', fontSize: '0.78rem', fontWeight: 600, cursor: 'pointer',
};

const dangerBtn: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, background: 'rgba(239, 68, 68, 0.1)', border: '1px solid rgba(239, 68, 68, 0.2)',
  color: 'var(--color-error, #f87171)', padding: '6px 12px', borderRadius: '8px', fontSize: '0.78rem', fontWeight: 600, cursor: 'pointer',
};

const modalBackdrop: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(4,7,13,.85)', backdropFilter: 'blur(4px)',
  display: 'grid', placeItems: 'center', zIndex: 1000,
};

const modalCard: React.CSSProperties = {
  background: '#0d1422', border: '1px solid #1e2a3a', borderRadius: 18, maxWidth: '95vw',
  boxShadow: '0 20px 50px rgba(0,0,0,.6)', color: '#e6edf3',
};
