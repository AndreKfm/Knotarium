// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import { Plus, Edit2, Trash2, Send, Star } from 'lucide-react';
import type { NotificationChannel, NotificationChannelType } from '../types';
import { api } from '../utils/api';

const TYPE_LABELS: Record<NotificationChannelType, string> = {
  Webhook: 'Webhook',
  Slack: 'Slack',
  Email: 'E-Mail',
};

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

export function NotificationChannelManager() {
  const [channels, setChannels] = useState<NotificationChannel[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [testStatus, setTestStatus] = useState<Record<string, string>>({});

  // Form state
  const [showForm, setShowForm] = useState(false);
  const [editChannel, setEditChannel] = useState<NotificationChannel | null>(null);
  const [formName, setFormName] = useState('');
  const [formType, setFormType] = useState<NotificationChannelType>('Webhook');
  const [formIsDefault, setFormIsDefault] = useState(false);
  const [validationError, setValidationError] = useState<string | null>(null);

  // Type-specific config fields
  const [cfgUrl, setCfgUrl] = useState('');
  const [cfgWebhookUrl, setCfgWebhookUrl] = useState('');
  const [cfgHost, setCfgHost] = useState('');
  const [cfgPort, setCfgPort] = useState('587');
  const [cfgUseSsl, setCfgUseSsl] = useState(true);
  const [cfgUsername, setCfgUsername] = useState('');
  const [cfgPassword, setCfgPassword] = useState('');
  const [cfgFrom, setCfgFrom] = useState('');
  const [cfgTo, setCfgTo] = useState('');

  const [deleteConfirm, setDeleteConfirm] = useState<NotificationChannel | null>(null);

  const loadData = () => {
    setLoading(true);
    setError(null);
    api.getNotificationChannels()
      .then(setChannels)
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load notification channels.'))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    loadData();
  }, []);

  const resetConfigFields = () => {
    setCfgUrl('');
    setCfgWebhookUrl('');
    setCfgHost('');
    setCfgPort('587');
    setCfgUseSsl(true);
    setCfgUsername('');
    setCfgPassword('');
    setCfgFrom('');
    setCfgTo('');
  };

  const handleOpenCreate = () => {
    setEditChannel(null);
    setFormName('');
    setFormType('Webhook');
    setFormIsDefault(false);
    resetConfigFields();
    setValidationError(null);
    setShowForm(true);
  };

  const handleOpenEdit = (channel: NotificationChannel) => {
    setEditChannel(channel);
    setFormName(channel.name);
    setFormType(channel.type);
    setFormIsDefault(channel.isDefaultFailureAlert);
    // Secrets are never returned by the API — leave config blank to keep the stored values.
    resetConfigFields();
    setValidationError(null);
    setShowForm(true);
  };

  // Builds the config object from the form, or returns null to mean "keep the stored config".
  const buildConfig = (): Record<string, unknown> | null => {
    if (formType === 'Webhook') {
      return cfgUrl.trim() ? { url: cfgUrl.trim() } : null;
    }
    if (formType === 'Slack') {
      return cfgWebhookUrl.trim() ? { webhookUrl: cfgWebhookUrl.trim() } : null;
    }
    // Email
    if (!cfgHost.trim()) {
      return null;
    }
    return {
      host: cfgHost.trim(),
      port: Number(cfgPort) || 587,
      useSsl: cfgUseSsl,
      username: cfgUsername.trim(),
      password: cfgPassword,
      fromAddress: cfgFrom.trim() || cfgUsername.trim(),
      toAddresses: cfgTo.split(/[,;]/).map((s) => s.trim()).filter(Boolean),
    };
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setValidationError(null);

    const name = formName.trim();
    if (!name) {
      setValidationError('Name is required.');
      return;
    }

    const config = buildConfig();
    if (!editChannel && !config) {
      setValidationError('Please fill in the channel configuration.');
      return;
    }

    const id = editChannel ? editChannel.id : (globalThis.crypto?.randomUUID?.() ?? `chan_${Date.now()}`);

    api.saveNotificationChannel(id, name, formType, config, formIsDefault)
      .then(() => {
        setShowForm(false);
        loadData();
      })
      .catch((err) => setValidationError(err instanceof Error ? err.message : 'Save request failed.'));
  };

  const handleDelete = () => {
    if (!deleteConfirm) return;
    api.deleteNotificationChannel(deleteConfirm.id)
      .then(() => {
        setDeleteConfirm(null);
        loadData();
      })
      .catch((err) => {
        setError(err instanceof Error ? err.message : 'Failed to delete channel.');
        setDeleteConfirm(null);
      });
  };

  const handleTest = (channel: NotificationChannel) => {
    setTestStatus((prev) => ({ ...prev, [channel.id]: 'Sending…' }));
    api.testNotificationChannel(channel.id)
      .then((result) => {
        setTestStatus((prev) => ({
          ...prev,
          [channel.id]: result.success ? '✓ Test sent' : `✗ ${result.error ?? 'Failed'}`,
        }));
      })
      .catch((err) => {
        setTestStatus((prev) => ({ ...prev, [channel.id]: `✗ ${err instanceof Error ? err.message : 'Failed'}` }));
      });
  };

  if (loading && channels.length === 0) {
    return (
      <div style={{ padding: '40px 20px', textAlign: 'center', color: '#566173', fontSize: 13 }}>
        Loading notification channels…
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', padding: '24px', overflowY: 'auto' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '24px' }}>
        <div>
          <h2 style={{ fontSize: '1.4rem', fontWeight: 700, color: '#fff', margin: 0 }}>Notification Channels</h2>
          <p style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)', marginTop: '4px' }}>
            Destinations for failure alerts. Channels marked as default alert on any workflow failure unless the
            workflow overrides it.
          </p>
        </div>
        <button
          onClick={handleOpenCreate}
          aria-label="New Channel"
          style={{
            display: 'flex', alignItems: 'center', gap: 8, padding: '10px 16px', borderRadius: 10,
            background: 'var(--color-accent, #6366f1)', border: 'none', color: '#fff', fontSize: 13.5,
            fontWeight: 600, cursor: 'pointer', transition: 'background .15s',
          }}
          onMouseOver={(e) => (e.currentTarget.style.background = '#4f46e5')}
          onMouseOut={(e) => (e.currentTarget.style.background = 'var(--color-accent, #6366f1)')}
        >
          <Plus size={16} /> New Channel
        </button>
      </div>

      {error && (
        <div style={{ padding: '12px 16px', borderRadius: '10px', background: 'rgba(239, 68, 68, 0.1)', border: '1px solid rgba(239, 68, 68, 0.2)', color: 'var(--color-error, #f87171)', fontSize: '0.85rem', marginBottom: '20px' }}>
          {error}
        </div>
      )}

      {channels.length === 0 ? (
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', padding: '64px 24px', background: 'rgba(255, 255, 255, 0.02)', border: '1px dashed var(--border-color, rgba(255,255,255,0.1))', borderRadius: '16px', color: '#566173', textAlign: 'center' }}>
          <div style={{ fontSize: 40, marginBottom: 12, opacity: 0.3 }}>🔔</div>
          <div style={{ fontSize: 15, fontWeight: 600, color: '#9aa6b5', marginBottom: 4 }}>No notification channels yet</div>
          <div style={{ fontSize: 13 }}>Click "New Channel" to be alerted when a workflow run fails.</div>
        </div>
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))', gap: '16px' }}>
          {channels.map((channel) => (
            <div
              key={channel.id}
              style={{ background: '#0d1422', border: '1px solid #18222f', borderRadius: '16px', padding: '18px 20px', display: 'flex', flexDirection: 'column', gap: '12px' }}
            >
              {/* Identity row: name + its badges cluster together on the left (all gap-8), wrapping on a
                  long name — rather than pushing the type badge to the far edge with space-between. */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                <h3 style={{ fontSize: '1.05rem', fontWeight: 700, color: '#f4f7fb', margin: 0 }}>{channel.name}</h3>
                {channel.isDefaultFailureAlert && (
                  <span title="Default failure-alert channel" style={{ display: 'inline-flex', alignItems: 'center', gap: 3, fontSize: '0.66rem', fontWeight: 700, color: '#fbbf24', background: 'rgba(251, 191, 36, 0.1)', border: '1px solid rgba(251, 191, 36, 0.25)', padding: '2px 6px', borderRadius: '6px' }}>
                    <Star size={10} /> Default
                  </span>
                )}
                <span style={{ fontSize: '0.68rem', fontWeight: 700, padding: '2px 8px', borderRadius: '6px', background: '#121a28', border: '1px solid #1e2a3a', color: '#7a8899', textTransform: 'uppercase', letterSpacing: '.05em' }}>
                  {TYPE_LABELS[channel.type]}
                </span>
              </div>

              {testStatus[channel.id] && (
                <div style={{ fontSize: '0.75rem', color: testStatus[channel.id].startsWith('✓') ? '#4ade80' : testStatus[channel.id].startsWith('✗') ? '#f87171' : '#94a3b8' }}>
                  {testStatus[channel.id]}
                </div>
              )}

              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '8px', borderTop: '1px solid #141d29', paddingTop: '12px', marginTop: '4px' }}>
                <button
                  onClick={() => handleTest(channel)}
                  aria-label={`Test ${channel.name}`}
                  style={{ display: 'flex', alignItems: 'center', gap: 6, background: 'transparent', border: '1px solid #243245', color: '#8995a6', padding: '6px 12px', borderRadius: '8px', fontSize: '0.78rem', fontWeight: 600, cursor: 'pointer' }}
                >
                  <Send size={13} /> Test
                </button>
                <button
                  onClick={() => handleOpenEdit(channel)}
                  aria-label={`Edit ${channel.name}`}
                  style={{ display: 'flex', alignItems: 'center', gap: 6, background: 'transparent', border: '1px solid #243245', color: '#8995a6', padding: '6px 12px', borderRadius: '8px', fontSize: '0.78rem', fontWeight: 600, cursor: 'pointer' }}
                >
                  <Edit2 size={13} /> Edit
                </button>
                <button
                  onClick={() => setDeleteConfirm(channel)}
                  aria-label={`Delete ${channel.name}`}
                  style={{ display: 'flex', alignItems: 'center', gap: 6, background: 'rgba(239, 68, 68, 0.1)', border: '1px solid rgba(239, 68, 68, 0.2)', color: 'var(--color-error, #f87171)', padding: '6px 12px', borderRadius: '8px', fontSize: '0.78rem', fontWeight: 600, cursor: 'pointer' }}
                >
                  <Trash2 size={13} /> Delete
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Form Modal */}
      {showForm && (
        <div
          style={{ position: 'fixed', inset: 0, background: 'rgba(4,7,13,.85)', backdropFilter: 'blur(4px)', display: 'grid', placeItems: 'center', zIndex: 1000 }}
          onClick={() => setShowForm(false)}
        >
          <div
            style={{ background: '#0d1422', border: '1px solid #1e2a3a', borderRadius: 18, width: 520, maxWidth: '95vw', boxShadow: '0 20px 50px rgba(0,0,0,.6)', color: '#e6edf3' }}
            onClick={(e) => e.stopPropagation()}
          >
            <form onSubmit={handleSubmit}>
              <div style={{ padding: '20px 24px 16px', borderBottom: '1px solid #1a2433' }}>
                <h3 style={{ fontSize: '1.1rem', fontWeight: 700, color: '#fff', margin: 0 }}>
                  {editChannel ? 'Edit Notification Channel' : 'Create Notification Channel'}
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
                  <input type="text" placeholder="e.g. Ops Slack, On-call Email" value={formName} onChange={(e) => setFormName(e.target.value)} style={inputStyle} />
                </div>

                <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                  <label style={labelStyle}>Type</label>
                  <select
                    value={formType}
                    onChange={(e) => setFormType(e.target.value as NotificationChannelType)}
                    disabled={!!editChannel}
                    style={{ ...inputStyle, background: 'var(--bg-surface-opaque, rgba(20, 20, 20, 0.8))', opacity: editChannel ? 0.6 : 1 }}
                  >
                    <option value="Webhook">Webhook (generic JSON POST)</option>
                    <option value="Slack">Slack (incoming webhook)</option>
                    <option value="Email">E-Mail (SMTP)</option>
                  </select>
                </div>

                {editChannel && (
                  <div style={{ fontSize: '0.75rem', color: '#7a8899', background: 'rgba(255,255,255,0.03)', border: '1px solid rgba(255,255,255,0.06)', borderRadius: 8, padding: '8px 10px' }}>
                    Leave the configuration fields below empty to keep the stored secret. Fill them in to replace it.
                  </div>
                )}

                {formType === 'Webhook' && (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                    <label style={labelStyle}>Webhook URL{editChannel ? '' : ' *'}</label>
                    <input type="text" placeholder="https://example.com/hooks/incoming" value={cfgUrl} onChange={(e) => setCfgUrl(e.target.value)} style={inputStyle} />
                  </div>
                )}

                {formType === 'Slack' && (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                    <label style={labelStyle}>Slack Incoming Webhook URL{editChannel ? '' : ' *'}</label>
                    <input type="text" placeholder="https://hooks.slack.com/services/..." value={cfgWebhookUrl} onChange={(e) => setCfgWebhookUrl(e.target.value)} style={inputStyle} />
                  </div>
                )}

                {formType === 'Email' && (
                  <>
                    <div style={{ display: 'flex', gap: 12 }}>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', flex: 2 }}>
                        <label style={labelStyle}>SMTP Host{editChannel ? '' : ' *'}</label>
                        <input type="text" placeholder="smtp.example.com" value={cfgHost} onChange={(e) => setCfgHost(e.target.value)} style={inputStyle} />
                      </div>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', flex: 1 }}>
                        <label style={labelStyle}>Port</label>
                        <input type="number" placeholder="587" value={cfgPort} onChange={(e) => setCfgPort(e.target.value)} style={inputStyle} />
                      </div>
                    </div>
                    <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: '0.82rem', color: '#cdd6e2', cursor: 'pointer' }}>
                      <input type="checkbox" checked={cfgUseSsl} onChange={(e) => setCfgUseSsl(e.target.checked)} />
                      Use SSL/TLS
                    </label>
                    <div style={{ display: 'flex', gap: 12 }}>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', flex: 1 }}>
                        <label style={labelStyle}>Username</label>
                        <input type="text" placeholder="user@example.com" value={cfgUsername} onChange={(e) => setCfgUsername(e.target.value)} style={inputStyle} />
                      </div>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', flex: 1 }}>
                        <label style={labelStyle}>Password</label>
                        <input type="password" placeholder="••••••••" value={cfgPassword} onChange={(e) => setCfgPassword(e.target.value)} style={inputStyle} />
                      </div>
                    </div>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                      <label style={labelStyle}>From Address</label>
                      <input type="text" placeholder="alerts@example.com" value={cfgFrom} onChange={(e) => setCfgFrom(e.target.value)} style={inputStyle} />
                    </div>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                      <label style={labelStyle}>Recipients (comma-separated){editChannel ? '' : ' *'}</label>
                      <input type="text" placeholder="oncall@example.com, ops@example.com" value={cfgTo} onChange={(e) => setCfgTo(e.target.value)} style={inputStyle} />
                    </div>
                  </>
                )}

                <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: '0.82rem', color: '#cdd6e2', cursor: 'pointer', marginTop: 4 }}>
                  <input type="checkbox" checked={formIsDefault} onChange={(e) => setFormIsDefault(e.target.checked)} />
                  Use as default failure-alert channel (alerts every workflow unless overridden)
                </label>
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

      {/* Delete Confirmation */}
      {deleteConfirm && (
        <div
          style={{ position: 'fixed', inset: 0, background: 'rgba(4,7,13,.85)', backdropFilter: 'blur(4px)', display: 'grid', placeItems: 'center', zIndex: 1000 }}
          onClick={() => setDeleteConfirm(null)}
        >
          <div
            style={{ background: '#0d1422', border: '1px solid #1e2a3a', borderRadius: 18, padding: 24, width: '90%', maxWidth: 420, boxShadow: '0 20px 50px rgba(0,0,0,0.6)', color: '#e6edf3' }}
            onClick={(e) => e.stopPropagation()}
          >
            <div style={{ fontSize: 18, fontWeight: 700, marginBottom: 12, color: '#fff' }}>
              Delete "{deleteConfirm.name}"?
            </div>
            <div style={{ fontSize: 14.5, color: '#9aa6b5', lineHeight: 1.5, marginBottom: 24 }}>
              Workflows routing failure alerts to this channel will stop notifying it. This cannot be undone.
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
