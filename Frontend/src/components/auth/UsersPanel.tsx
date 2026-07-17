// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import { X, Trash2, UserPlus, KeyRound } from 'lucide-react';
import { api } from '../../utils/api';
import { useAuth } from './AuthContext';
import type { AuthUser } from '../../types';

const message = (err: unknown) => (err instanceof Error ? err.message : String(err));

/** Modal for managing the 1–n login accounts: list, add, delete, and change your own password. */
export function UsersPanel({ onClose }: { onClose: () => void }) {
  const { status } = useAuth();
  const [users, setUsers] = useState<AuthUser[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [newUsername, setNewUsername] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [ownPassword, setOwnPassword] = useState('');

  const load = () => api.listUsers().then(setUsers).catch((err) => setError(message(err)));
  useEffect(() => { load(); }, []);

  // Escape closes the modal (in addition to the ✕ and a backdrop click), so backing out of
  // "add user" is unambiguous from the keyboard too. Capture so it wins over background handlers.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [onClose]);

  const addUser = async () => {
    setError(null);
    try {
      await api.createUser(newUsername.trim(), newPassword, 'user');
      setNewUsername('');
      setNewPassword('');
      await load();
    } catch (err) {
      setError(message(err));
    }
  };

  const deleteUser = async (id: string) => {
    setError(null);
    try {
      await api.deleteUser(id);
      await load();
    } catch (err) {
      setError(message(err));
    }
  };

  const changeOwnPassword = async () => {
    setError(null);
    setNotice(null);
    try {
      await api.changeOwnPassword(ownPassword);
      setOwnPassword('');
      setNotice('Your password was changed.');
    } catch (err) {
      setError(message(err));
    }
  };

  const input: React.CSSProperties = { flex: 1, minWidth: 0, padding: '8px 10px', borderRadius: 7, background: 'rgba(0,0,0,0.25)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.83rem', outline: 'none' };
  const btn: React.CSSProperties = { display: 'inline-flex', alignItems: 'center', gap: 6, padding: '8px 12px', borderRadius: 7, border: '1px solid var(--border-color)', background: 'rgba(255,255,255,0.04)', color: 'var(--text-secondary)', fontSize: '0.8rem', cursor: 'pointer' };
  const sectionTitle: React.CSSProperties = { fontSize: '0.72rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: '0.05em' };

  return (
    <div onClick={onClose} style={{ position: 'fixed', inset: 0, zIndex: 100, background: 'rgba(0,0,0,0.55)', display: 'grid', placeItems: 'center' }}>
      <div onClick={(e) => e.stopPropagation()} style={{ width: 520, maxWidth: '92vw', maxHeight: '85vh', overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 18, padding: 24, borderRadius: 14, background: 'var(--bg-surface-opaque)', border: '1px solid var(--border-color)', boxShadow: '0 20px 60px rgba(0,0,0,0.5)' }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
          <h2 style={{ margin: 0, fontSize: '1.05rem', fontWeight: 700, color: '#fff' }}>Users</h2>
          <button onClick={onClose} aria-label="Close" style={{ background: 'transparent', border: 'none', color: 'var(--text-muted)', cursor: 'pointer' }}><X size={18} /></button>
        </div>

        {error && <div style={{ fontSize: '0.8rem', color: 'var(--color-error)' }}>{error}</div>}
        {notice && <div style={{ fontSize: '0.8rem', color: 'var(--color-success)' }}>{notice}</div>}

        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <span style={sectionTitle}>Accounts ({users.length})</span>
          {users.map((u) => (
            <div key={u.id} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '8px 12px', borderRadius: 8, background: 'rgba(255,255,255,0.03)', border: '1px solid var(--border-color)' }}>
              <div style={{ display: 'flex', flexDirection: 'column' }}>
                <span style={{ color: '#fff', fontSize: '0.86rem', fontWeight: 600 }}>{u.username}{u.id === status?.userId && <span style={{ color: 'var(--text-muted)', fontWeight: 400 }}> · you</span>}</span>
                <span style={{ color: 'var(--text-muted)', fontSize: '0.72rem' }}>{u.role}</span>
              </div>
              <button
                onClick={() => deleteUser(u.id)}
                disabled={u.id === status?.userId}
                title={u.id === status?.userId ? 'You cannot delete your own account' : 'Delete user'}
                style={{ ...btn, color: u.id === status?.userId ? 'var(--text-muted)' : 'var(--color-error)', cursor: u.id === status?.userId ? 'default' : 'pointer', borderColor: 'rgba(239,68,68,0.2)', background: 'rgba(239,68,68,0.08)' }}
              >
                <Trash2 size={13} />
              </button>
            </div>
          ))}
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, borderTop: '1px solid var(--border-color)', paddingTop: 16 }}>
          <span style={sectionTitle}>Add user</span>
          <div style={{ display: 'flex', gap: 8 }}>
            <input value={newUsername} onChange={(e) => setNewUsername(e.target.value)} placeholder="username" autoComplete="off" style={input} />
            <input value={newPassword} onChange={(e) => setNewPassword(e.target.value)} placeholder="password (min 8)" type="password" autoComplete="new-password" style={input} />
            <button onClick={addUser} disabled={!newUsername.trim() || newPassword.length < 8} style={{ ...btn, opacity: !newUsername.trim() || newPassword.length < 8 ? 0.5 : 1 }}><UserPlus size={14} /> Add</button>
          </div>
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, borderTop: '1px solid var(--border-color)', paddingTop: 16 }}>
          <span style={sectionTitle}>Change my password</span>
          <div style={{ display: 'flex', gap: 8 }}>
            <input value={ownPassword} onChange={(e) => setOwnPassword(e.target.value)} placeholder="new password (min 8)" type="password" autoComplete="new-password" style={input} />
            <button onClick={changeOwnPassword} disabled={ownPassword.length < 8} style={{ ...btn, opacity: ownPassword.length < 8 ? 0.5 : 1 }}><KeyRound size={14} /> Change</button>
          </div>
        </div>
      </div>
    </div>
  );
}
