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
      onClick={onToggle}
      disabled={busy || isLoading}
      title={title}
      aria-label={`Runtime ${label}. ${isArmed ? 'Click to disarm.' : 'Click to arm.'}`}
      aria-pressed={isArmed}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: '8px',
        padding: '8px 14px',
        borderRadius: '999px',
        background: isArmed ? 'rgba(16, 185, 129, 0.12)' : 'rgba(250, 204, 21, 0.10)',
        border: `1px solid ${isArmed ? 'rgba(16, 185, 129, 0.4)' : 'rgba(250, 204, 21, 0.35)'}`,
        color,
        fontWeight: 700,
        fontSize: '0.8rem',
        letterSpacing: '0.02em',
        cursor: busy || isLoading ? 'not-allowed' : 'pointer',
        opacity: busy || isLoading ? 0.5 : 1,
        transition: 'background 0.2s, border-color 0.2s, opacity 0.2s',
      }}
    >
      <span
        style={{
          width: '8px',
          height: '8px',
          borderRadius: '50%',
          background: color,
          boxShadow: isArmed ? '0 0 8px var(--color-success)' : 'none',
          flex: 'none',
        }}
      />
      {isArmed ? <ShieldCheck size={15} /> : <ShieldOff size={15} />}
      <span>{label}</span>
    </button>
  );
}
