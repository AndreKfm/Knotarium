import { useState, type FormEvent } from 'react';
import { Activity, Lock } from 'lucide-react';
import { api } from '../../utils/api';
import { useAuth } from './AuthContext';

/**
 * Full-screen sign-in. Doubles as the first-run "create admin" screen when the instance has no users
 * yet (status.setupRequired), so an unconfigured instance is reachable but immediately secured.
 */
export function LoginPage() {
  const { status, refresh } = useAuth();
  const isSetup = status?.setupRequired === true;

  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    if (isSetup && password !== confirm) {
      setError('Passwords do not match.');
      return;
    }
    setBusy(true);
    try {
      if (isSetup) {
        await api.setupFirstAdmin(username, password);
      } else {
        await api.login(username, password);
      }
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Sign-in failed.');
    } finally {
      setBusy(false);
    }
  };

  const inputStyle: React.CSSProperties = {
    width: '100%', boxSizing: 'border-box', padding: '11px 13px', borderRadius: 9,
    background: 'rgba(0,0,0,0.25)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.9rem', outline: 'none',
  };

  return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100vh', width: '100vw', background: 'var(--bg-main)' }}>
      <form
        onSubmit={submit}
        style={{ width: 360, maxWidth: '90vw', display: 'flex', flexDirection: 'column', gap: 16, padding: 28, borderRadius: 16, background: 'rgba(16, 22, 37, 0.85)', border: '1px solid var(--border-color)', boxShadow: '0 20px 60px rgba(0,0,0,0.5)' }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <div style={{ background: 'linear-gradient(135deg, var(--color-accent), var(--color-info))', width: 40, height: 40, borderRadius: 11, display: 'grid', placeItems: 'center', boxShadow: '0 0 15px var(--color-accent-glow)' }}>
            <Activity size={20} color="#fff" />
          </div>
          <div>
            <div style={{ fontWeight: 800, fontSize: '1.15rem', letterSpacing: '0.04em', color: '#fff' }}>KNOT<span style={{ color: 'var(--text-secondary)', fontWeight: 600 }}>ARIUM</span><span style={{ color: 'var(--color-accent)' }}>.</span></div>
            <div style={{ fontSize: '0.72rem', color: 'var(--text-secondary)' }}>{isSetup ? 'Create the first administrator' : 'Sign in to continue'}</div>
          </div>
        </div>

        {isSetup && (
          <div style={{ fontSize: '0.76rem', color: 'var(--text-muted)', lineHeight: 1.4 }}>
            This instance has no users yet. Create an admin account to secure it — you'll use these credentials to sign in.
          </div>
        )}

        <label style={{ display: 'flex', flexDirection: 'column', gap: 6, fontSize: '0.72rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
          Username
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoFocus autoComplete="username" style={inputStyle} />
        </label>
        <label style={{ display: 'flex', flexDirection: 'column', gap: 6, fontSize: '0.72rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
          Password
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete={isSetup ? 'new-password' : 'current-password'} style={inputStyle} />
        </label>
        {isSetup && (
          <label style={{ display: 'flex', flexDirection: 'column', gap: 6, fontSize: '0.72rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
            Confirm password
            <input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" style={inputStyle} />
          </label>
        )}

        {error && <div style={{ fontSize: '0.8rem', color: 'var(--color-error)' }}>{error}</div>}

        <button
          type="submit"
          disabled={busy || !username || !password}
          style={{ display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 8, padding: '11px', borderRadius: 9, border: 'none', background: busy || !username || !password ? 'rgba(255,255,255,0.08)' : 'linear-gradient(135deg, var(--color-accent), var(--color-info))', color: '#fff', fontWeight: 700, fontSize: '0.9rem', cursor: busy || !username || !password ? 'default' : 'pointer' }}
        >
          <Lock size={15} /> {busy ? 'Please wait…' : isSetup ? 'Create admin & continue' : 'Sign in'}
        </button>
      </form>
    </div>
  );
}
