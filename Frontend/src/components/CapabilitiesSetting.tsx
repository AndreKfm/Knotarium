import { useEffect, useState } from 'react';
import { AlertTriangle, Code, Database, ShieldAlert } from 'lucide-react';
import { api } from '../utils/api';

const cardStyle: React.CSSProperties = {
  padding: '20px',
  borderRadius: '12px',
  background: 'rgba(255, 255, 255, 0.03)',
  border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))',
  marginBottom: '24px',
};

// The switchable privileged capabilities (must match CapabilityPolicyStore.Switchable on the backend).
const CAPS: { key: string; label: string; icon: typeof Code; desc: string }[] = [
  {
    key: 'code.execute',
    label: 'Inline code execution',
    icon: Code,
    desc: 'Let the Inline Code node — and its design-time “Test run” — compile and run arbitrary C# on the host.',
  },
  {
    key: 'database',
    label: 'Database access',
    icon: Database,
    desc: 'Let the Database Query node open connections and run SQL against your configured databases.',
  },
];

/**
 * Settings → Capabilities. Master on/off switches for privileged node capabilities that have no finer
 * policy of their own (unlike the filesystem). Off by default — an Inline Code or Database Query node
 * fails until an admin turns its capability on here. Toggles auto-save.
 */
export function CapabilitiesSetting() {
  const [enabled, setEnabled] = useState<string[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [savingKey, setSavingKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    api.getCapabilityPolicy()
      .then((p) => setEnabled(p.enabledCapabilities ?? []))
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load the capability policy.'))
      .finally(() => setLoading(false));
  }, []);

  const toggle = async (key: string, next: boolean) => {
    if (!enabled) return;
    const nextList = next ? [...new Set([...enabled, key])] : enabled.filter((c) => c !== key);
    setSavingKey(key);
    setError(null);
    try {
      const saved = await api.setCapabilityPolicy({ enabledCapabilities: nextList });
      setEnabled(saved.enabledCapabilities ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save the capability policy.');
    } finally {
      setSavingKey(null);
    }
  };

  if (loading) {
    return <div style={cardStyle}><div style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)' }}>Loading…</div></div>;
  }
  if (!enabled) {
    return (
      <div style={cardStyle}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: '0.85rem', color: '#f87171' }}>
          <AlertTriangle size={16} /> {error ?? 'Failed to load the capability policy.'}
        </div>
      </div>
    );
  }

  const anyOn = CAPS.some((c) => enabled.includes(c.key));

  return (
    <div style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '6px' }}>
        <ShieldAlert size={16} style={{ color: '#f0b429' }} />
        <h3 style={{ margin: 0, fontSize: '0.95rem', color: '#fff' }}>Privileged capabilities</h3>
      </div>
      <p style={{ margin: '0 0 16px', fontSize: '0.8rem', color: 'var(--text-secondary, #94a3b8)', maxWidth: 640 }}>
        These node capabilities are <b>off by default</b>. A node that needs one fails until you enable it here.
        Leave them off unless you trust every workflow on this instance — imported and AI-generated ones included.
      </p>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        {CAPS.map(({ key, label, icon: Icon, desc }) => {
          const on = enabled.includes(key);
          return (
            <div
              key={key}
              style={{
                display: 'flex', alignItems: 'flex-start', gap: 12, padding: '12px 14px', borderRadius: 10,
                background: on ? 'rgba(240,180,41,0.06)' : 'rgba(0,0,0,0.15)',
                border: `1px solid ${on ? 'rgba(240,180,41,0.28)' : 'var(--border-color, rgba(255,255,255,0.1))'}`,
              }}
            >
              <span style={{ flex: 'none', marginTop: 2, color: on ? '#f0b429' : '#94a3b8' }}><Icon size={18} /></span>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontSize: '0.86rem', fontWeight: 600, color: '#fff' }}>{label}</div>
                <div style={{ fontSize: '0.78rem', color: 'var(--text-secondary, #94a3b8)', marginTop: 2 }}>{desc}</div>
              </div>
              <label style={{ flex: 'none', display: 'inline-flex', alignItems: 'center', gap: 8, cursor: savingKey === key ? 'default' : 'pointer' }}>
                <span style={{ fontSize: '0.75rem', fontWeight: 600, color: on ? '#f0b429' : '#94a3b8' }}>
                  {savingKey === key ? '…' : on ? 'On' : 'Off'}
                </span>
                <input
                  type="checkbox"
                  checked={on}
                  disabled={savingKey === key}
                  onChange={(e) => toggle(key, e.target.checked)}
                  style={{ width: 16, height: 16, accentColor: '#f0b429', cursor: 'pointer' }}
                />
              </label>
            </div>
          );
        })}
      </div>

      {anyOn && (
        <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start', padding: '10px 12px', borderRadius: 8, background: 'rgba(240,180,41,0.08)', border: '1px solid rgba(240,180,41,0.3)', marginTop: 14 }}>
          <ShieldAlert size={16} style={{ color: '#f0b429', flex: 'none', marginTop: 1 }} />
          <span style={{ fontSize: '0.78rem', color: '#e8cf9a' }}>
            A privileged capability is enabled. Any workflow on this instance can now use it — turn it back off
            when you no longer need it.
          </span>
        </div>
      )}
      {error && <div style={{ marginTop: 10, fontSize: '0.8rem', color: '#f87171' }}>{error}</div>}
    </div>
  );
}
