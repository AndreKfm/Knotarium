import { useEffect, useState } from 'react';
import { Sparkles } from 'lucide-react';
import { api } from '../utils/api';
import type { CredentialSummary, AiProviderTestResponse } from '../types';
import { ModelCombo } from './shared/ModelCombo';

const inputStyle: React.CSSProperties = {
  width: '100%',
  maxWidth: '420px',
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
  display: 'block', fontSize: '0.78rem', color: 'var(--text-secondary, #94a3b8)', margin: '14px 0 6px',
};

interface VendorMeta {
  label: string;
  modelLabel: string;
  modelPlaceholder: string;
  baseUrlRequired?: boolean;
  baseUrlPlaceholder: string;
  showApiVersion?: boolean;
  apiVersionLabel?: string;
  apiVersionPlaceholder?: string;
}

const VENDOR_META: Record<string, VendorMeta> = {
  anthropic: {
    label: 'Anthropic (Claude)', modelLabel: 'Model', modelPlaceholder: 'claude-opus-4-8',
    baseUrlPlaceholder: 'https://api.anthropic.com (optional)',
    showApiVersion: true, apiVersionLabel: 'anthropic-version (optional)', apiVersionPlaceholder: '2023-06-01',
  },
  openai: {
    label: 'OpenAI (ChatGPT)', modelLabel: 'Model', modelPlaceholder: 'gpt-4o',
    baseUrlPlaceholder: 'https://api.openai.com (optional)',
  },
  azure: {
    label: 'Azure OpenAI / Microsoft Copilot', modelLabel: 'Deployment name', modelPlaceholder: 'my-gpt4o-deployment',
    baseUrlRequired: true, baseUrlPlaceholder: 'https://<resource>.openai.azure.com',
    showApiVersion: true, apiVersionLabel: 'api-version', apiVersionPlaceholder: '2024-06-01',
  },
  gemini: {
    label: 'Google Gemini', modelLabel: 'Model', modelPlaceholder: 'gemini-2.0-flash',
    baseUrlPlaceholder: 'https://generativelanguage.googleapis.com (optional)',
  },
};

/**
 * Global AI provider configuration for workflow generation: pick a vendor + model, and the encrypted
 * credential holding the API key. Persists via PUT /api/settings/ai-provider; the key lives in the
 * credential store (never in this config), resolved server-side when a workflow is generated.
 */
export function AiProviderSetting() {
  const [vendor, setVendor] = useState('anthropic');
  const [vendors, setVendors] = useState<string[]>(Object.keys(VENDOR_META));
  const [model, setModel] = useState('');
  const [baseUrl, setBaseUrl] = useState('');
  const [apiVersion, setApiVersion] = useState('');
  const [credentialRef, setCredentialRef] = useState('');
  const [credentials, setCredentials] = useState<CredentialSummary[]>([]);

  const [addingCred, setAddingCred] = useState(false);
  const [newCredName, setNewCredName] = useState('');
  const [newCredValue, setNewCredValue] = useState('');

  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<AiProviderTestResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [savedNote, setSavedNote] = useState<string | null>(null);
  // Snapshot of the last loaded/saved config — Save stays disabled until the form diverges from it.
  const [baseline, setBaseline] = useState<string>('');

  const snapshot = (): string => JSON.stringify({
    vendor,
    model: model.trim(),
    credentialRef,
    baseUrl: baseUrl.trim() || null,
    apiVersion: apiVersion.trim() || null,
  });

  useEffect(() => {
    setLoading(true);
    Promise.all([api.getAiProviderConfig(), api.listCredentials()])
      .then(([cfg, creds]) => {
        setCredentials(creds);
        if (cfg.availableVendors?.length) setVendors(cfg.availableVendors);
        if (cfg.vendor) setVendor(cfg.vendor);
        setModel(cfg.model ?? '');
        setBaseUrl(cfg.baseUrl ?? '');
        setApiVersion(cfg.apiVersion ?? '');
        setCredentialRef(cfg.credentialRef ?? '');
        setBaseline(JSON.stringify({
          vendor: cfg.vendor || 'anthropic',
          model: (cfg.model ?? '').trim(),
          credentialRef: cfg.credentialRef ?? '',
          baseUrl: (cfg.baseUrl ?? '').trim() || null,
          apiVersion: (cfg.apiVersion ?? '').trim() || null,
        }));
      })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load AI provider settings.'))
      .finally(() => setLoading(false));
  }, []);

  const meta = VENDOR_META[vendor] ?? VENDOR_META.anthropic;
  const dirty = snapshot() !== baseline;
  const canSave = !!vendor && model.trim().length > 0 && credentialRef.length > 0
    && (!meta.baseUrlRequired || baseUrl.trim().length > 0);

  const addCredential = async () => {
    if (!newCredName.trim() || !newCredValue.trim()) return;
    setError(null);
    try {
      const id = crypto.randomUUID();
      await api.saveCredential(id, newCredName.trim(), newCredValue.trim());
      const creds = await api.listCredentials();
      setCredentials(creds);
      setCredentialRef(id);
      setAddingCred(false);
      setNewCredName('');
      setNewCredValue('');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save credential.');
    }
  };

  const testConnection = async () => {
    setTesting(true);
    setTestResult(null);
    setError(null);
    try {
      const result = await api.testAiProvider({
        vendor,
        model: model.trim(),
        credentialRef,
        baseUrl: baseUrl.trim() || null,
        apiVersion: apiVersion.trim() || null,
      });
      setTestResult(result);
    } catch (err) {
      setTestResult({ ok: false, message: err instanceof Error ? err.message : 'Test failed.', latencyMs: null, model: model.trim() });
    } finally {
      setTesting(false);
    }
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    setSavedNote(null);
    try {
      await api.setAiProviderConfig({
        vendor,
        model: model.trim(),
        credentialRef,
        baseUrl: baseUrl.trim() || null,
        apiVersion: apiVersion.trim() || null,
      });
      setBaseline(snapshot());
      setSavedNote('Saved.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save AI provider settings.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div
      style={{
        padding: '20px', borderRadius: '12px', background: 'rgba(255, 255, 255, 0.03)',
        border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))', marginBottom: '24px',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '6px' }}>
        <Sparkles size={16} style={{ color: '#a78bfa' }} />
        <h3 style={{ margin: 0, fontSize: '0.95rem', color: '#fff' }}>AI Provider</h3>
      </div>
      <p style={{ margin: '0 0 6px', fontSize: '0.8rem', color: 'var(--text-secondary, #94a3b8)' }}>
        The model used by “Generate with AI”. The API key is stored as an encrypted credential — pick an
        existing one or add a new key below.
      </p>

      {loading ? (
        <div style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)' }}>Loading…</div>
      ) : (
        <>
          <label style={labelStyle}>Provider</label>
          <select value={vendor} disabled={saving} onChange={(e) => setVendor(e.target.value)} style={inputStyle}>
            {vendors.map((v) => (
              <option key={v} value={v}>{VENDOR_META[v]?.label ?? v}</option>
            ))}
          </select>

          <label style={labelStyle}>{meta.modelLabel}</label>
          <div style={{ maxWidth: '420px' }}>
            <ModelCombo
              vendor={vendor}
              value={model}
              onChange={setModel}
              credentialRef={credentialRef}
              baseUrl={baseUrl}
              apiVersion={apiVersion}
              placeholder={meta.modelPlaceholder}
              disabled={saving}
              style={inputStyle}
            />
          </div>

          <label style={labelStyle}>Base URL{meta.baseUrlRequired ? '' : ' (optional)'}</label>
          <input
            value={baseUrl} disabled={saving} onChange={(e) => setBaseUrl(e.target.value)}
            placeholder={meta.baseUrlPlaceholder} style={inputStyle}
          />

          {meta.showApiVersion && (
            <>
              <label style={labelStyle}>{meta.apiVersionLabel}</label>
              <input
                value={apiVersion} disabled={saving} onChange={(e) => setApiVersion(e.target.value)}
                placeholder={meta.apiVersionPlaceholder} style={inputStyle}
              />
            </>
          )}

          <label style={labelStyle}>API key credential</label>
          {!addingCred ? (
            <div style={{ display: 'flex', gap: '8px', maxWidth: '420px' }}>
              <select
                value={credentialRef} disabled={saving}
                onChange={(e) => setCredentialRef(e.target.value)}
                style={{ ...inputStyle, flex: 1 }}
              >
                <option value="">— Choose credential —</option>
                {credentials.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>
              <button
                type="button" onClick={() => setAddingCred(true)}
                style={{ padding: '0 12px', borderRadius: '8px', border: '1px solid var(--border-color, rgba(255,255,255,0.1))', background: 'transparent', color: '#a78bfa', cursor: 'pointer', fontSize: '0.8rem', whiteSpace: 'nowrap' }}
              >
                + Add key
              </button>
            </div>
          ) : (
            <div style={{ maxWidth: '420px', display: 'grid', gap: '8px' }}>
              <input value={newCredName} onChange={(e) => setNewCredName(e.target.value)} placeholder="Name (e.g. OpenAI key)" style={inputStyle} />
              <input value={newCredValue} onChange={(e) => setNewCredValue(e.target.value)} placeholder="API key (secret)" type="password" style={inputStyle} />
              <div style={{ display: 'flex', gap: '8px' }}>
                <button
                  type="button" onClick={addCredential} disabled={!newCredName.trim() || !newCredValue.trim()}
                  style={{ padding: '8px 14px', borderRadius: '8px', border: 'none', background: '#7c3aed', color: '#fff', cursor: 'pointer', fontSize: '0.8rem', fontWeight: 600 }}
                >
                  Save credential
                </button>
                <button
                  type="button" onClick={() => { setAddingCred(false); setNewCredName(''); setNewCredValue(''); }}
                  style={{ padding: '8px 14px', borderRadius: '8px', border: '1px solid var(--border-color, rgba(255,255,255,0.1))', background: 'transparent', color: '#94a3b8', cursor: 'pointer', fontSize: '0.8rem' }}
                >
                  Cancel
                </button>
              </div>
            </div>
          )}

          <div style={{ marginTop: '20px', display: 'flex', alignItems: 'center', gap: '10px' }}>
            {(() => {
              const enabled = canSave && !saving && dirty;
              return (
                <button
                  type="button" onClick={save} disabled={!enabled}
                  style={{ padding: '9px 18px', borderRadius: '8px', border: 'none', background: enabled ? '#7c3aed' : '#3b2f57', color: '#fff', cursor: enabled ? 'pointer' : 'default', fontSize: '0.85rem', fontWeight: 600, opacity: enabled ? 1 : 0.6 }}
                >
                  {saving ? 'Saving…' : 'Save provider'}
                </button>
              );
            })()}
            {(() => {
              const canTest = !!vendor && model.trim().length > 0 && credentialRef.length > 0
                && (!meta.baseUrlRequired || baseUrl.trim().length > 0) && !testing && !saving;
              return (
                <button
                  type="button" onClick={testConnection} disabled={!canTest}
                  style={{ padding: '9px 16px', borderRadius: '8px', border: '1px solid var(--border-color, rgba(255,255,255,0.1))', background: 'transparent', color: canTest ? '#a78bfa' : '#6b7280', cursor: canTest ? 'pointer' : 'default', fontSize: '0.85rem', fontWeight: 600 }}
                >
                  {testing ? 'Testing…' : 'Test connection'}
                </button>
              );
            })()}
            {!dirty && !savedNote && <span style={{ fontSize: '0.78rem', color: 'var(--text-secondary, #94a3b8)' }}>No changes.</span>}
            {savedNote && !dirty && <span style={{ fontSize: '0.78rem', color: '#34d399' }}>{savedNote}</span>}
          </div>

          {testResult && (
            <div style={{ marginTop: '10px', fontSize: '0.8rem', color: testResult.ok ? '#34d399' : '#f87171' }}>
              {testResult.ok ? '✓ ' : '✗ '}{testResult.message}
              {typeof testResult.latencyMs === 'number' && <span style={{ color: 'var(--text-secondary, #94a3b8)' }}> ({testResult.latencyMs} ms)</span>}
            </div>
          )}
        </>
      )}

      {error && <div style={{ marginTop: '10px', fontSize: '0.8rem', color: '#f87171' }}>{error}</div>}
    </div>
  );
}
