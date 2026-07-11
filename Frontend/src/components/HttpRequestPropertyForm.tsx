import { useEffect, useState } from 'react';
import { Plus, KeyRound } from 'lucide-react';
import { api } from '../utils/api';

interface CredentialItem {
  id: string;
  name: string;
}

export interface HttpRequestPropertyFormProps {
  properties: Record<string, unknown>;
  onChange: (properties: Record<string, unknown>) => void;
}

type AuthType = 'none' | 'bearer' | 'basic' | 'apiKey';

const AUTH_OPTIONS: { value: AuthType; label: string }[] = [
  { value: 'none', label: 'No auth' },
  { value: 'bearer', label: 'Bearer token' },
  { value: 'basic', label: 'Basic auth' },
  { value: 'apiKey', label: 'API key (custom header)' },
];

const HTTP_METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'];

const fieldStyle: React.CSSProperties = {
  width: '100%',
  padding: '10px',
  borderRadius: '8px',
  background: 'rgba(0, 0, 0, 0.2)',
  border: '1px solid var(--border-color)',
  color: '#fff',
  fontSize: '0.85rem',
  outline: 'none',
  boxSizing: 'border-box',
};

const selectStyle: React.CSSProperties = { ...fieldStyle, background: 'var(--bg-surface-opaque)', colorScheme: 'dark' };
const labelStyle: React.CSSProperties = {
  display: 'block',
  fontSize: '0.75rem',
  fontWeight: 700,
  color: 'var(--text-secondary)',
  textTransform: 'uppercase',
  marginBottom: '6px',
};
const wrap: React.CSSProperties = { display: 'flex', flexDirection: 'column', gap: '6px' };
const hint: React.CSSProperties = { fontSize: '0.72rem', color: 'var(--text-muted)' };

function slugId(name: string): string {
  const slug = name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  const rand = Math.floor(Math.random() * 0xffff).toString(16).padStart(4, '0');
  return `${slug || 'cred'}-${rand}`;
}

export function HttpRequestPropertyForm({ properties, onChange }: HttpRequestPropertyFormProps) {
  const [credentials, setCredentials] = useState<CredentialItem[]>([]);
  const [adding, setAdding] = useState(false);
  const [newName, setNewName] = useState('');
  const [newValue, setNewValue] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const str = (key: string, fallback = '') => (typeof properties[key] === 'string' ? (properties[key] as string) : fallback);
  const url = str('url');
  const method = str('method', 'GET') || 'GET';
  const body = str('body');
  const headers = str('headers');
  const authType = (str('authType', 'none') || 'none') as AuthType;
  const authCredentialRef = str('authCredentialRef');
  const authUsername = str('authUsername');
  const authHeaderName = str('authHeaderName');
  const authValuePrefix = str('authValuePrefix');

  const set = (key: string, value: unknown) => onChange({ ...properties, [key]: value });

  const loadCredentials = () =>
    api.getCredentials()
      .then((res) => setCredentials((res as CredentialItem[]) ?? []))
      .catch((err) => console.error('Error loading credentials:', err));

  useEffect(() => { void loadCredentials(); }, []);

  const saveNewCredential = async () => {
    if (!newName.trim() || !newValue) {
      setError('Name and value are required.');
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const id = slugId(newName);
      await api.saveCredential(id, newName.trim(), newValue);
      await loadCredentials();
      set('authCredentialRef', id);
      setAdding(false);
      setNewName('');
      setNewValue('');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save credential.');
    } finally {
      setSaving(false);
    }
  };

  // Rendered as an inline element (NOT a nested component) so parent re-renders reconcile it in place
  // rather than remounting it — otherwise the new-credential inputs would lose focus on every keystroke.
  const credentialPicker = (
    <div style={wrap}>
      <label style={labelStyle}>Credential (secret)</label>
      {adding ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '8px', padding: '10px', borderRadius: '8px', border: '1px solid var(--border-color)', background: 'rgba(255,255,255,0.03)' }}>
          <input type="text" value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="Name (e.g. Stripe API key)" style={fieldStyle} />
          <input type="password" value={newValue} onChange={(e) => setNewValue(e.target.value)} placeholder="Secret value" style={fieldStyle} />
          {error && <span style={{ fontSize: '0.74rem', color: 'var(--color-error)' }}>{error}</span>}
          <div style={{ display: 'flex', gap: '8px' }}>
            <button type="button" onClick={saveNewCredential} disabled={saving}
              style={{ flex: 1, padding: '8px', borderRadius: '7px', border: '1px solid rgba(99,102,241,0.35)', background: 'rgba(99,102,241,0.16)', color: '#fff', fontSize: '0.8rem', fontWeight: 600, cursor: saving ? 'default' : 'pointer' }}>
              {saving ? 'Saving…' : 'Save credential'}
            </button>
            <button type="button" onClick={() => { setAdding(false); setError(null); }}
              style={{ padding: '8px 12px', borderRadius: '7px', border: '1px solid var(--border-color)', background: 'transparent', color: 'var(--text-secondary)', fontSize: '0.8rem', cursor: 'pointer' }}>
              Cancel
            </button>
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', gap: '8px' }}>
          <select value={authCredentialRef} onChange={(e) => set('authCredentialRef', e.target.value)} style={{ ...selectStyle, flex: 1 }}>
            <option value="">Select credential…</option>
            {credentials.map((c) => (
              <option key={c.id} value={c.id} style={{ background: 'var(--bg-surface-opaque)', color: '#fff' }}>{c.name} ({c.id})</option>
            ))}
          </select>
          <button type="button" title="Create a new credential" onClick={() => { setAdding(true); setError(null); }}
            style={{ flex: '0 0 auto', display: 'flex', alignItems: 'center', gap: '5px', padding: '0 12px', borderRadius: '8px', border: '1px solid var(--border-color)', background: 'rgba(255,255,255,0.04)', color: 'var(--text-secondary)', fontSize: '0.8rem', cursor: 'pointer' }}>
            <Plus size={14} /> New
          </button>
        </div>
      )}
      <span style={hint}>The secret is stored encrypted and referenced by id — it never lives in the workflow definition.</span>
    </div>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
      <div style={wrap}>
        <label style={labelStyle}>URL</label>
        <input type="text" value={url} onChange={(e) => set('url', e.target.value)} placeholder="https://api.example.com/v1/resource" style={fieldStyle} />
      </div>

      <div style={wrap}>
        <label style={labelStyle}>Method</label>
        <select value={method} onChange={(e) => set('method', e.target.value)} style={selectStyle}>
          {HTTP_METHODS.map((m) => (
            <option key={m} value={m} style={{ background: 'var(--bg-surface-opaque)', color: '#fff' }}>{m}</option>
          ))}
        </select>
      </div>

      {/* Auth section */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: '12px', padding: '14px', borderRadius: '10px', border: '1px solid var(--border-color)', background: 'rgba(255,255,255,0.03)' }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: '7px', fontSize: '0.78rem', fontWeight: 700, color: 'var(--color-accent)', textTransform: 'uppercase', letterSpacing: '0.04em' }}>
          <KeyRound size={14} /> Authentication
        </span>

        <div style={wrap}>
          <label style={labelStyle}>Type</label>
          <select value={authType} onChange={(e) => set('authType', e.target.value)} style={selectStyle}>
            {AUTH_OPTIONS.map((o) => (
              <option key={o.value} value={o.value} style={{ background: 'var(--bg-surface-opaque)', color: '#fff' }}>{o.label}</option>
            ))}
          </select>
        </div>

        {authType === 'basic' && (
          <div style={wrap}>
            <label style={labelStyle}>Username</label>
            <input type="text" value={authUsername} onChange={(e) => set('authUsername', e.target.value)} placeholder="username" style={fieldStyle} />
          </div>
        )}

        {authType === 'apiKey' && (
          <>
            <div style={wrap}>
              <label style={labelStyle}>Header name</label>
              <input type="text" value={authHeaderName} onChange={(e) => set('authHeaderName', e.target.value)} placeholder="X-API-Key" style={fieldStyle} />
            </div>
            <div style={wrap}>
              <label style={labelStyle}>Value prefix (optional)</label>
              <input type="text" value={authValuePrefix} onChange={(e) => set('authValuePrefix', e.target.value)} placeholder="e.g. &quot;Token &quot; — sent as prefix + secret" style={fieldStyle} />
            </div>
          </>
        )}

        {authType !== 'none' && credentialPicker}
      </div>

      <div style={wrap}>
        <label style={labelStyle}>Headers</label>
        <textarea value={headers} onChange={(e) => set('headers', e.target.value)} rows={3}
          placeholder={'{"Accept": "application/json"}  — or one "Key: Value" per line'}
          style={{ ...fieldStyle, resize: 'vertical', fontFamily: 'ui-monospace, Menlo, monospace' }} />
        <span style={hint}>JSON object or newline-separated <code>Key: Value</code> lines.</span>
      </div>

      <div style={wrap}>
        <label style={labelStyle}>Body</label>
        <textarea value={body} onChange={(e) => set('body', e.target.value)} rows={5}
          placeholder='{"key": "value"}'
          style={{ ...fieldStyle, resize: 'vertical', fontFamily: 'ui-monospace, Menlo, monospace' }} />
        <span style={hint}>Sent as JSON unless a <code>Content-Type</code> header says otherwise. Supports <code>{'{{ $variables.x }}'}</code>.</span>
      </div>
    </div>
  );
}
