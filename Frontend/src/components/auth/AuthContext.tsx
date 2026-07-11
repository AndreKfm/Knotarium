import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api } from '../../utils/api';
import type { AuthStatus } from '../../types';

interface AuthContextValue {
  /** null while the initial status check is in flight. */
  status: AuthStatus | null;
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus | null>(null);

  const refresh = useCallback(async () => {
    try {
      setStatus(await api.getAuthStatus());
    } catch {
      // If even the status probe fails (server unreachable), fall back to the login screen.
      setStatus({ authenticated: false, username: null, userId: null, setupRequired: false });
    }
  }, []);

  const logout = useCallback(async () => {
    try {
      await api.logout();
    } finally {
      await refresh();
    }
  }, [refresh]);

  useEffect(() => { void refresh(); }, [refresh]);

  // A 401 from any request means the session is gone — flip back to the login screen.
  useEffect(() => {
    const handler = () => setStatus((prev) => (prev ? { ...prev, authenticated: false } : prev));
    window.addEventListener('kg-unauthorized', handler);
    return () => window.removeEventListener('kg-unauthorized', handler);
  }, []);

  return <AuthContext.Provider value={{ status, refresh, logout }}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return ctx;
}
