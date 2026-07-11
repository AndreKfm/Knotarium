import { useEffect, useState } from 'react';
import { Plus, Edit2, Trash2 } from 'lucide-react';
import type { ServerConfigInfo } from '../types';
import {
  listServerConfigs,
  createServerConfig,
  updateServerConfig,
  deleteServerConfig,
} from '../utils/serverConfigClient';
import { api } from '../utils/api';

interface CredentialItem {
  id: string;
  name: string;
}

interface ServerConfigManagerProps {
  prefilledBaseUrl?: string | null;
  onClearPrefilledBaseUrl?: () => void;
}

export function ServerConfigManager({ prefilledBaseUrl, onClearPrefilledBaseUrl }: ServerConfigManagerProps) {
  const [configs, setConfigs] = useState<ServerConfigInfo[]>([]);
  const [credentials, setCredentials] = useState<CredentialItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Form states
  const [showForm, setShowForm] = useState(false);
  const [editConfig, setEditConfig] = useState<ServerConfigInfo | null>(null);
  const [formName, setFormName] = useState('');
  const [formBaseUrl, setFormBaseUrl] = useState('');
  const [formSecurityType, setFormSecurityType] = useState('none');
  const [formCredentialRef, setFormCredentialRef] = useState('');
  const [formAllowInsecure, setFormAllowInsecure] = useState(false);
  const [formVariables, setFormVariables] = useState<Array<{ key: string; value: string }>>([]);
  // Inline secret creation (the credential dropdown otherwise only lists existing secrets).
  const [showNewSecret, setShowNewSecret] = useState(false);
  const [newSecretName, setNewSecretName] = useState('');
  const [newSecretValue, setNewSecretValue] = useState('');
  const [savingSecret, setSavingSecret] = useState(false);
  const [secretError, setSecretError] = useState<string | null>(null);
  const [validationError, setValidationError] = useState<string | null>(null);

  // Delete confirmation state
  const [deleteConfirmConfig, setDeleteConfirmConfig] = useState<ServerConfigInfo | null>(null);

  const loadData = () => {
    setLoading(true);
    setError(null);
    Promise.all([listServerConfigs(), api.getCredentials()])
      .then(([configsData, credsData]) => {
        setConfigs(configsData);
        setCredentials(credsData as CredentialItem[]);
      })
      .catch((err) => {
        setError(err instanceof Error ? err.message : 'Failed to load configuration data.');
      })
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    loadData();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (prefilledBaseUrl) {
      setEditConfig(null);
      setFormName('Spec Server');
      setFormBaseUrl(prefilledBaseUrl);
      setFormSecurityType('none');
      setFormCredentialRef('');
      setFormAllowInsecure(false);
      setFormVariables([]);
      setValidationError(null);
      setShowForm(true);
      if (onClearPrefilledBaseUrl) {
        onClearPrefilledBaseUrl();
      }
    }
  }, [prefilledBaseUrl, onClearPrefilledBaseUrl]);

  const handleOpenCreate = () => {
    setEditConfig(null);
    setFormName('');
    setFormBaseUrl('');
    setFormSecurityType('none');
    setFormCredentialRef('');
    setFormAllowInsecure(false);
    setFormVariables([]);
    setShowNewSecret(false);
    setNewSecretName('');
    setNewSecretValue('');
    setSecretError(null);
    setValidationError(null);
    setShowForm(true);
  };

  const handleOpenEdit = (config: ServerConfigInfo) => {
    setEditConfig(config);
    setFormName(config.name);
    setFormBaseUrl(config.baseUrl);
    setFormSecurityType(config.securitySchemeType);
    setFormCredentialRef(config.credentialRef || '');
    setFormAllowInsecure(config.allowInsecureCertificate ?? false);
    setFormVariables(
      Object.entries(config.serverVariables || {}).map(([key, value]) => ({ key, value }))
    );
    setValidationError(null);
    setShowForm(true);
  };

  const handleCreateSecret = async () => {
    const name = newSecretName.trim();
    const value = newSecretValue.trim();
    if (!name) { setSecretError('Secret name is required.'); return; }
    if (!value) { setSecretError('Secret value is required.'); return; }
    setSecretError(null);
    setSavingSecret(true);
    try {
      const slug = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'secret';
      const id = `cred-${slug}-${Math.random().toString(36).slice(2, 7)}`;
      await api.saveCredential(id, name, value);
      const creds = await api.getCredentials();
      setCredentials(creds as CredentialItem[]);
      setFormCredentialRef(id);
      setShowNewSecret(false);
      setNewSecretName('');
      setNewSecretValue('');
    } catch (err) {
      setSecretError(err instanceof Error ? err.message : 'Failed to create secret.');
    } finally {
      setSavingSecret(false);
    }
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setValidationError(null);

    const name = formName.trim();
    const baseUrl = formBaseUrl.trim();

    if (!name) {
      setValidationError('Name is required.');
      return;
    }
    if (!baseUrl) {
      setValidationError('Base URL is required.');
      return;
    }

    // Convert variables list back to record
    const serverVariables: Record<string, string> = {};
    for (const v of formVariables) {
      const k = v.key.trim();
      const val = v.value.trim();
      if (k) {
        serverVariables[k] = val;
      }
    }

    const payload = {
      name,
      baseUrl,
      securitySchemeType: formSecurityType,
      credentialRef: formCredentialRef || null,
      allowInsecureCertificate: formAllowInsecure,
      serverVariables,
    };

    const promise = editConfig
      ? updateServerConfig(editConfig.id, payload)
      : createServerConfig(payload);

    promise
      .then(() => {
        setShowForm(false);
        loadData();
      })
      .catch((err) => {
        setValidationError(err instanceof Error ? err.message : 'Save request failed.');
      });
  };

  const handleDelete = () => {
    if (!deleteConfirmConfig) return;
    deleteServerConfig(deleteConfirmConfig.id)
      .then(() => {
        setDeleteConfirmConfig(null);
        loadData();
      })
      .catch((err) => {
        setError(err instanceof Error ? err.message : 'Failed to delete configuration.');
        setDeleteConfirmConfig(null);
      });
  };

  if (loading && configs.length === 0) {
    return (
      <div style={{ padding: '40px 20px', textAlign: 'center', color: '#566173', fontSize: 13 }}>
        Loading configurations…
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', padding: '24px', overflowY: 'auto' }}>
      {/* Title / Header */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '24px' }}>
        <div>
          <h2 style={{ fontSize: '1.4rem', fontWeight: 700, color: '#fff', margin: 0 }}>Server Configurations</h2>
          <p style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)', marginTop: '4px' }}>
            Configure target servers, base URLs, and their authentication settings for API integration.
          </p>
        </div>
        <button
          onClick={handleOpenCreate}
          aria-label="New Config"
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: 8,
            padding: '10px 16px',
            borderRadius: 10,
            background: 'var(--color-accent, #6366f1)',
            border: 'none',
            color: '#fff',
            fontSize: 13.5,
            fontWeight: 600,
            cursor: 'pointer',
            transition: 'background .15s',
          }}
          onMouseOver={(e) => (e.currentTarget.style.background = '#4f46e5')}
          onMouseOut={(e) => (e.currentTarget.style.background = 'var(--color-accent, #6366f1)')}
        >
          <Plus size={16} /> New Config
        </button>
      </div>

      {error && (
        <div style={{ padding: '12px 16px', borderRadius: '10px', background: 'rgba(239, 68, 68, 0.1)', border: '1px solid rgba(239, 68, 68, 0.2)', color: 'var(--color-error, #f87171)', fontSize: '0.85rem', marginBottom: '20px' }}>
          {error}
        </div>
      )}

      {/* Grid of server configs */}
      {configs.length === 0 ? (
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', padding: '64px 24px', background: 'rgba(255, 255, 255, 0.02)', border: '1px dashed var(--border-color, rgba(255,255,255,0.1))', borderRadius: '16px', color: '#566173', textAlign: 'center' }}>
          <div style={{ fontSize: 40, marginBottom: 12, opacity: 0.3 }}>⚙</div>
          <div style={{ fontSize: 15, fontWeight: 600, color: '#9aa6b5', marginBottom: 4 }}>No server configurations found</div>
          <div style={{ fontSize: 13 }}>Click "New Config" to create a configuration.</div>
        </div>
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))', gap: '16px' }}>
          {configs.map((config) => (
            <div
              key={config.id}
              style={{
                background: '#0d1422',
                border: '1px solid #18222f',
                borderRadius: '16px',
                padding: '18px 20px',
                display: 'flex',
                flexDirection: 'column',
                gap: '12px',
                transition: 'border-color .15s',
              }}
              onMouseOver={(e) => (e.currentTarget.style.borderColor = '#24324a')}
              onMouseOut={(e) => (e.currentTarget.style.borderColor = '#18222f')}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
                <div>
                  <h3 style={{ fontSize: '1.05rem', fontWeight: 700, color: '#f4f7fb', margin: 0 }}>
                    {config.name}
                  </h3>
                  <span style={{ fontSize: '0.72rem', color: '#4f5b6b', fontFamily: 'ui-monospace, Menlo, monospace' }}>
                    ID: {config.id}
                  </span>
                </div>
                <span
                  style={{
                    fontSize: '0.68rem',
                    fontWeight: 700,
                    padding: '2px 8px',
                    borderRadius: '6px',
                    background: '#121a28',
                    border: '1px solid #1e2a3a',
                    color: '#7a8899',
                    textTransform: 'uppercase',
                    letterSpacing: '.05em',
                  }}
                >
                  {config.securitySchemeType === 'none'
                    ? 'None'
                    : config.securitySchemeType === 'apiKey'
                    ? 'API Key'
                    : config.securitySchemeType === 'http_bearer'
                    ? 'Bearer'
                    : config.securitySchemeType === 'http_basic'
                    ? 'Basic'
                    : config.securitySchemeType === 'oauth2'
                    ? 'OAuth2'
                    : config.securitySchemeType}
                </span>
              </div>

              <div>
                <label style={{ display: 'block', fontSize: '0.68rem', fontWeight: 700, color: '#566173', textTransform: 'uppercase', marginBottom: '2px' }}>Base URL</label>
                <div style={{ fontFamily: 'ui-monospace, Menlo, monospace', fontSize: '0.85rem', color: '#aeb9c8', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={config.baseUrl}>
                  {config.baseUrl}
                </div>
              </div>

              {config.credentialRef && (
                <div>
                  <label style={{ display: 'block', fontSize: '0.68rem', fontWeight: 700, color: '#566173', textTransform: 'uppercase', marginBottom: '2px' }}>Linked Secret</label>
                  <span style={{ fontSize: '0.78rem', color: '#3b82f6', background: 'rgba(59, 130, 246, 0.1)', border: '1px solid rgba(59, 130, 246, 0.2)', padding: '2px 8px', borderRadius: '6px' }}>
                    {credentials.find((cr) => cr.id === config.credentialRef)?.name || config.credentialRef}
                  </span>
                </div>
              )}

              {Object.keys(config.serverVariables || {}).length > 0 && (
                <div>
                  <label style={{ display: 'block', fontSize: '0.68rem', fontWeight: 700, color: '#566173', textTransform: 'uppercase', marginBottom: '4px' }}>Variables</label>
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: '6px' }}>
                    {Object.entries(config.serverVariables).map(([key, value]) => (
                      <span key={key} style={{ fontSize: '0.72rem', color: '#aeb9c8', background: 'rgba(255, 255, 255, 0.04)', border: '1px solid rgba(255, 255, 255, 0.06)', padding: '2px 6px', borderRadius: '5px' }}>
                        <code>{key}</code>: <code>{value}</code>
                      </span>
                    ))}
                  </div>
                </div>
              )}

              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '8px', borderTop: '1px solid #141d29', paddingTop: '12px', marginTop: '4px' }}>
                <button
                  onClick={() => handleOpenEdit(config)}
                  aria-label={`Edit ${config.name}`}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: 6,
                    background: 'transparent',
                    border: '1px solid #243245',
                    color: '#8995a6',
                    padding: '6px 12px',
                    borderRadius: '8px',
                    fontSize: '0.78rem',
                    fontWeight: 600,
                    cursor: 'pointer',
                    transition: 'all .15s',
                  }}
                  onMouseOver={(e) => {
                    e.currentTarget.style.borderColor = '#2f3d52';
                    e.currentTarget.style.color = '#cdd6e2';
                  }}
                  onMouseOut={(e) => {
                    e.currentTarget.style.borderColor = '#243245';
                    e.currentTarget.style.color = '#8995a6';
                  }}
                >
                  <Edit2 size={13} /> Edit
                </button>
                <button
                  onClick={() => setDeleteConfirmConfig(config)}
                  aria-label={`Delete ${config.name}`}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: 6,
                    background: 'rgba(239, 68, 68, 0.1)',
                    border: '1px solid rgba(239, 68, 68, 0.2)',
                    color: 'var(--color-error, #f87171)',
                    padding: '6px 12px',
                    borderRadius: '8px',
                    fontSize: '0.78rem',
                    fontWeight: 600,
                    cursor: 'pointer',
                    transition: 'background .15s',
                  }}
                  onMouseOver={(e) => (e.currentTarget.style.background = 'rgba(239, 68, 68, 0.2)')}
                  onMouseOut={(e) => (e.currentTarget.style.background = 'rgba(239, 68, 68, 0.1)')}
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
                  {editConfig ? 'Edit Server Configuration' : 'Create Server Configuration'}
                </h3>
              </div>

              <div style={{ padding: '20px 24px', display: 'flex', flexDirection: 'column', gap: '14px', maxHeight: '65vh', overflowY: 'auto' }}>
                {validationError && (
                  <div role="alert" style={{ padding: '10px 14px', borderRadius: '8px', background: 'rgba(239, 68, 68, 0.1)', border: '1px solid rgba(239, 68, 68, 0.2)', color: 'var(--color-error, #f87171)', fontSize: '0.8rem' }}>
                    {validationError}
                  </div>
                )}

                {/* Name */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                  <label htmlFor="form-name-input" style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary, #94a3b8)', textTransform: 'uppercase' }}>
                    Name <span style={{ color: 'var(--color-error, #f87171)' }}>*</span>
                  </label>
                  <input
                    id="form-name-input"
                    type="text"
                    placeholder="Enter server configuration name..."
                    value={formName}
                    onChange={(e) => setFormName(e.target.value)}
                    style={{ width: '100%', padding: '10px', borderRadius: '8px', background: 'rgba(0, 0, 0, 0.2)', border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))', color: '#fff', fontSize: '0.85rem', outline: 'none' }}
                  />
                </div>

                {/* BaseUrl */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                  <label htmlFor="form-url-input" style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary, #94a3b8)', textTransform: 'uppercase' }}>
                    Base URL <span style={{ color: 'var(--color-error, #f87171)' }}>*</span>
                  </label>
                  <input
                    id="form-url-input"
                    type="text"
                    placeholder="https://api.example.com"
                    value={formBaseUrl}
                    onChange={(e) => setFormBaseUrl(e.target.value)}
                    style={{ width: '100%', padding: '10px', borderRadius: '8px', background: 'rgba(0, 0, 0, 0.2)', border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))', color: '#fff', fontSize: '0.85rem', outline: 'none' }}
                  />
                </div>

                {/* Allow self-signed / untrusted certificate */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
                  <label style={{ display: 'flex', alignItems: 'center', gap: '8px', fontSize: '0.85rem', color: '#cbd5e1', cursor: 'pointer' }}>
                    <input
                      type="checkbox"
                      checked={formAllowInsecure}
                      onChange={(e) => setFormAllowInsecure(e.target.checked)}
                    />
                    Allow self-signed / untrusted certificate (insecure HTTPS)
                  </label>
                  {formAllowInsecure && (
                    <span style={{ fontSize: '0.72rem', color: 'var(--color-warning, #f0b429)', lineHeight: 1.45, paddingLeft: '24px' }}>
                      ⚠ Skips TLS certificate validation for calls to this server. Use only for trusted dev/LAN servers. The egress policy still applies.
                    </span>
                  )}
                </div>

                {/* Security Scheme Type */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                  <label htmlFor="form-security-select" style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary, #94a3b8)', textTransform: 'uppercase' }}>
                    Security Scheme
                  </label>
                  <select
                    id="form-security-select"
                    value={formSecurityType}
                    onChange={(e) => setFormSecurityType(e.target.value)}
                    style={{ width: '100%', padding: '10px', borderRadius: '8px', background: 'var(--bg-surface-opaque, rgba(20, 20, 20, 0.8))', border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))', color: '#fff', fontSize: '0.85rem', outline: 'none' }}
                  >
                    <option value="none">None</option>
                    <option value="apiKey">API Key</option>
                    <option value="http_bearer">Bearer Token</option>
                    <option value="http_basic">Basic Authentication</option>
                    <option value="oauth2">OAuth2 Client Credentials</option>
                  </select>
                </div>

                {/* Credential Reference */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                    <label htmlFor="form-credential-select" style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary, #94a3b8)', textTransform: 'uppercase' }}>
                      Linked Secret / Credential
                    </label>
                    <button
                      type="button"
                      onClick={() => { setShowNewSecret((v) => !v); setSecretError(null); }}
                      style={{ background: 'rgba(99, 102, 241, 0.1)', border: '1px solid rgba(99, 102, 241, 0.2)', color: '#9d9af8', borderRadius: '6px', padding: '3px 8px', fontSize: '0.75rem', fontWeight: 600, cursor: 'pointer' }}
                    >
                      {showNewSecret ? '× Cancel' : '+ New secret'}
                    </button>
                  </div>
                  <select
                    id="form-credential-select"
                    value={formCredentialRef}
                    onChange={(e) => setFormCredentialRef(e.target.value)}
                    style={{ width: '100%', padding: '10px', borderRadius: '8px', background: 'var(--bg-surface-opaque, rgba(20, 20, 20, 0.8))', border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))', color: '#fff', fontSize: '0.85rem', outline: 'none' }}
                  >
                    <option value="">No secret selected...</option>
                    {credentials.map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.name} ({c.id})
                      </option>
                    ))}
                  </select>

                  {showNewSecret && (
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '4px', padding: '12px', borderRadius: '8px', background: 'rgba(99,102,241,0.04)', border: '1px solid var(--border-color, rgba(255,255,255,0.1))' }}>
                      <span style={{ fontSize: '0.72rem', color: 'var(--text-muted, #64748b)' }}>
                        Create a secret (e.g. your bearer token). It's encrypted at rest; only its name is shown afterwards.
                      </span>
                      <input
                        type="text"
                        placeholder="Secret name (e.g. Device Bearer)"
                        value={newSecretName}
                        onChange={(e) => setNewSecretName(e.target.value)}
                        style={{ width: '100%', padding: '9px', borderRadius: '7px', background: 'rgba(0,0,0,0.2)', border: '1px solid var(--border-color, rgba(255,255,255,0.1))', color: '#fff', fontSize: '0.82rem', outline: 'none' }}
                      />
                      <input
                        type="password"
                        placeholder="Secret value (paste the token)"
                        value={newSecretValue}
                        onChange={(e) => setNewSecretValue(e.target.value)}
                        style={{ width: '100%', padding: '9px', borderRadius: '7px', background: 'rgba(0,0,0,0.2)', border: '1px solid var(--border-color, rgba(255,255,255,0.1))', color: '#fff', fontSize: '0.82rem', fontFamily: 'monospace', outline: 'none' }}
                      />
                      {secretError && <span style={{ fontSize: '0.75rem', color: 'var(--color-error, #f87171)' }}>{secretError}</span>}
                      <button
                        type="button"
                        onClick={handleCreateSecret}
                        disabled={savingSecret}
                        style={{ alignSelf: 'flex-start', background: '#6f6cf0', border: '1px solid #5856c5', color: '#fff', borderRadius: '7px', padding: '7px 14px', fontSize: '0.8rem', fontWeight: 600, cursor: savingSecret ? 'not-allowed' : 'pointer', opacity: savingSecret ? 0.6 : 1 }}
                      >
                        {savingSecret ? 'Creating…' : 'Create & link'}
                      </button>
                    </div>
                  )}
                  {formSecurityType === 'http_bearer' && (
                    <span style={{ fontSize: '0.72rem', color: 'var(--text-muted, #64748b)' }}>
                      For Bearer auth, the secret value is the token itself (sent as <code>Authorization: Bearer &lt;value&gt;</code>).
                    </span>
                  )}
                </div>

                {/* Server Variables */}
                <div>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '8px' }}>
                    <label style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary, #94a3b8)', textTransform: 'uppercase' }}>
                      Server Variables
                    </label>
                    <button
                      type="button"
                      onClick={() => setFormVariables([...formVariables, { key: '', value: '' }])}
                      style={{ background: 'rgba(99, 102, 241, 0.1)', border: '1px solid rgba(99, 102, 241, 0.2)', color: '#9d9af8', borderRadius: '6px', padding: '3px 8px', fontSize: '0.75rem', fontWeight: 600, cursor: 'pointer' }}
                    >
                      + Add Variable
                    </button>
                  </div>

                  <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                    {formVariables.map((v, idx) => (
                      <div key={idx} style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
                        <input
                          type="text"
                          placeholder="Variable Key (e.g. env)"
                          value={v.key}
                          onChange={(e) => {
                            const newVars = [...formVariables];
                            newVars[idx].key = e.target.value;
                            setFormVariables(newVars);
                          }}
                          style={{ flex: 1, padding: '8px 10px', borderRadius: '6px', background: 'rgba(0, 0, 0, 0.2)', border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))', color: '#fff', fontSize: '0.8rem', outline: 'none' }}
                        />
                        <input
                          type="text"
                          placeholder="Value"
                          value={v.value}
                          onChange={(e) => {
                            const newVars = [...formVariables];
                            newVars[idx].value = e.target.value;
                            setFormVariables(newVars);
                          }}
                          style={{ flex: 1, padding: '8px 10px', borderRadius: '6px', background: 'rgba(0, 0, 0, 0.2)', border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))', color: '#fff', fontSize: '0.8rem', outline: 'none' }}
                        />
                        <button
                          type="button"
                          onClick={() => setFormVariables(formVariables.filter((_, i) => i !== idx))}
                          style={{ background: 'rgba(239, 68, 68, 0.1)', border: '1px solid rgba(239, 68, 68, 0.2)', color: 'var(--color-error, #f87171)', borderRadius: '6px', padding: '8px 12px', fontSize: '0.78rem', cursor: 'pointer' }}
                        >
                          Delete
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              </div>

              <div style={{ padding: '16px 24px 20px', borderTop: '1px solid #1a2433', display: 'flex', justifyContent: 'flex-end', gap: '12px' }}>
                <button
                  type="button"
                  onClick={() => setShowForm(false)}
                  style={{ padding: '9px 18px', borderRadius: 10, fontSize: 13, fontWeight: 600, cursor: 'pointer', border: '1px solid #243245', background: 'transparent', color: '#8995a6' }}
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  style={{ padding: '9px 18px', borderRadius: 10, fontSize: 13, fontWeight: 600, cursor: 'pointer', border: 'none', background: 'var(--color-accent, #6366f1)', color: '#fff' }}
                >
                  Save
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Delete Confirmation Modal */}
      {deleteConfirmConfig && (
        <div
          style={{ position: 'fixed', inset: 0, background: 'rgba(4,7,13,.85)', backdropFilter: 'blur(4px)', display: 'grid', placeItems: 'center', zIndex: 1000 }}
          onClick={() => setDeleteConfirmConfig(null)}
        >
          <div
            style={{ background: '#0d1422', border: '1px solid #1e2a3a', borderRadius: 18, padding: 24, width: '90%', maxWidth: 420, boxShadow: '0 20px 50px rgba(0,0,0,0.6)', color: '#e6edf3' }}
            onClick={(e) => e.stopPropagation()}
          >
            <div style={{ fontSize: 18, fontWeight: 700, marginBottom: 12, color: '#fff' }}>
              Delete "{deleteConfirmConfig.name}"?
            </div>
            <div style={{ fontSize: 14.5, color: '#9aa6b5', lineHeight: 1.5, marginBottom: 24 }}>
              Are you sure you want to delete the configuration "{deleteConfirmConfig.name}"? This action cannot be undone.
            </div>
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 12 }}>
              <button
                onClick={() => setDeleteConfirmConfig(null)}
                style={{ padding: '10px 18px', borderRadius: 10, fontSize: 13.5, fontWeight: 600, cursor: 'pointer', border: '1px solid #243245', background: 'transparent', color: '#8995a6' }}
              >
                Cancel
              </button>
              <button
                onClick={handleDelete}
                style={{ padding: '10px 18px', borderRadius: 10, fontSize: 13.5, fontWeight: 600, cursor: 'pointer', border: 'none', background: '#f0556d', color: '#fff' }}
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
