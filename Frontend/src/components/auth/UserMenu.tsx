import { useState } from 'react';
import { LogOut, Users, UserCircle } from 'lucide-react';
import { useAuth } from './AuthContext';
import { UsersPanel } from './UsersPanel';

/** Header widget: current user, a shortcut to user management, and sign-out. */
export function UserMenu() {
  const { status, logout } = useAuth();
  const [showUsers, setShowUsers] = useState(false);

  if (!status?.authenticated) {
    return null;
  }

  const iconButton: React.CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: 6, padding: '7px 10px', borderRadius: 8,
    border: '1px solid var(--border-color)', background: 'rgba(255,255,255,0.04)', color: 'var(--text-secondary)',
    fontSize: '0.78rem', fontWeight: 600, cursor: 'pointer',
  };

  return (
    <>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color: 'var(--text-secondary)', fontSize: '0.82rem', fontWeight: 600 }}>
          <UserCircle size={16} /> {status.username}
        </span>
        <button onClick={() => setShowUsers(true)} title="Manage users" style={iconButton}>
          <Users size={14} /> Users
        </button>
        <button onClick={() => void logout()} title="Sign out" style={iconButton}>
          <LogOut size={14} /> Sign out
        </button>
      </div>
      {showUsers && <UsersPanel onClose={() => setShowUsers(false)} />}
    </>
  );
}
