// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { ReactNode } from 'react';
import { useAuth } from './AuthContext';
import { LoginPage } from './LoginPage';

/** Renders the app only for an authenticated session; otherwise shows the login / first-run setup screen. */
export function AuthGate({ children }: { children: ReactNode }) {
  const { status } = useAuth();

  if (status === null) {
    return (
      <div style={{ display: 'grid', placeItems: 'center', height: '100vh', width: '100vw', background: 'var(--bg-main)', color: 'var(--text-muted)', fontSize: '0.9rem' }}>
        Loading…
      </div>
    );
  }

  // No-auth mode (Auth:Enabled=false): every endpoint is anonymous, so open the app without a login.
  if (status.enabled && !status.authenticated) {
    return <LoginPage />;
  }

  return <>{children}</>;
}
