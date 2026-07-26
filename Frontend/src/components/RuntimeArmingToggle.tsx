// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { ShieldCheck, ShieldOff } from 'lucide-react';

interface RuntimeArmingToggleProps {
  /** Current armed state; null while the initial state is still loading. */
  armed: boolean | null;
  /** Whether a toggle request is in flight (button disabled). */
  busy?: boolean;
  onToggle: () => void;
}

/**
 * Global runtime arming switch shown in the app header.
 * - Armed: the scheduler fires active workflows automatically (run-time / live).
 * - Disarmed: automatic execution is paused; only manual "Run" executes (design-time / editing).
 *
 * Layout lives in topbar.css (.tb-pill) because the top bar's degradation
 * ladder collapses this pill to its dot at the narrowest widths — it may lose
 * its label but is never removed, since it carries safety state. Only the
 * state colours stay inline: they are derived, not themeable.
 */
export function RuntimeArmingToggle({ armed, busy, onToggle }: RuntimeArmingToggleProps) {
  const isLoading = armed === null;
  const isArmed = armed === true;

  const color = isLoading ? 'var(--text-muted)' : isArmed ? 'var(--color-success)' : 'var(--color-warning)';
  const label = isLoading ? 'Runtime…' : isArmed ? 'Armed' : 'Disarmed';
  const title = isArmed
    ? 'Runtime is ARMED — scheduled workflows run automatically. Click to disarm (pause automatic execution).'
    : 'Runtime is DISARMED — only manual runs execute (safe for editing). Click to arm (enable scheduled execution).';

  return (
    <button
      type="button"
      className="tb-pill"
      onClick={onToggle}
      disabled={busy || isLoading}
      title={title}
      aria-label={`Runtime ${label}. ${isArmed ? 'Click to disarm.' : 'Click to arm.'}`}
      aria-pressed={isArmed}
      style={{
        background: isArmed ? 'rgba(16, 185, 129, 0.12)' : 'rgba(250, 204, 21, 0.10)',
        border: `1px solid ${isArmed ? 'rgba(16, 185, 129, 0.4)' : 'rgba(250, 204, 21, 0.35)'}`,
        color,
      }}
    >
      <span
        className="tb-pill-dot"
        style={{ background: color, boxShadow: isArmed ? '0 0 8px var(--color-success)' : 'none' }}
      />
      <span className="tb-pill-icon">{isArmed ? <ShieldCheck size={15} /> : <ShieldOff size={15} />}</span>
      <span className="tb-pill-label">{label}</span>
    </button>
  );
}
