// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useState } from 'react';
import { api } from '../utils/api';
import type { ActionFieldsById, SimulatablePin } from '../node-editor/signalFieldBinding';

interface SimulateSignalDialogProps {
  workflowId: string;
  /** Wired device pins that can be fired. */
  pins: SimulatablePin[];
  /** Static field schema per action id, for sample-value inputs. */
  actionFieldsById: ActionFieldsById;
  onClose: () => void;
  onStarted: (executionId: string) => void;
}

/**
 * Simulate an inbound device signal: pick a wired action/event pin, optionally fill sample values for the
 * action's fields, and start a run seeded at that pin's downstream node(s) — exactly like a live event.
 * This is the "single step / run a device-driven workflow" path; a plain manual run would instead execute
 * the inert device block as a disconnected no-op and never flow from the pin.
 */
export function SimulateSignalDialog({ workflowId, pins, actionFieldsById, onClose, onStarted }: SimulateSignalDialogProps) {
  const [selectedIndex, setSelectedIndex] = useState(0);
  const [values, setValues] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const pin = pins[selectedIndex];
  const fields = pin?.kind === 'action' ? actionFieldsById[pin.type] ?? [] : [];

  const fire = async () => {
    if (!pin) return;
    setBusy(true);
    setError(null);
    try {
      const payload = pin.kind === 'action'
        ? Object.fromEntries(fields.map((f) => [f.key, values[f.key] ?? '']).filter(([, v]) => v !== ''))
        : {};
      const { id } = await api.simulateSignal(workflowId, { kind: pin.kind, type: pin.type, payload });
      onStarted(id);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to simulate the signal.');
      setBusy(false);
    }
  };

  const label = { display: 'block', fontSize: '0.72rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase' as const, letterSpacing: '0.04em', marginBottom: '6px' };
  const input: React.CSSProperties = { width: '100%', boxSizing: 'border-box', padding: '7px 10px', borderRadius: '6px', background: 'rgba(0,0,0,0.25)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.85rem' };

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label="Simulate device signal"
      onClick={onClose}
      style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.55)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000 }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        style={{ width: '440px', maxWidth: '92vw', maxHeight: '86vh', overflowY: 'auto', background: 'var(--bg-elevated, #131823)', border: '1px solid var(--border-color)', borderRadius: '12px', padding: '20px 22px', display: 'flex', flexDirection: 'column', gap: '16px' }}
      >
        <div>
          <h2 style={{ fontSize: '1.05rem', fontWeight: 700, color: '#fff', margin: 0 }}>Simulate signal</h2>
          <span style={{ fontSize: '0.78rem', color: 'var(--text-muted)' }}>
            Fire a wired device pin to start a run as if the device sent it.
          </span>
        </div>

        <div>
          <label style={label} htmlFor="sim-pin">Pin</label>
          <select id="sim-pin" value={selectedIndex} onChange={(e) => { setSelectedIndex(Number(e.target.value)); setValues({}); }} style={input}>
            {pins.map((p, i) => (
              <option key={`${p.kind}:${p.type}:${i}`} value={i}>
                {p.kind === 'action' ? '⚡ ' : '◉ '}{p.label} {p.kind === 'event' ? '(event)' : '(action)'}
              </option>
            ))}
          </select>
        </div>

        {pin?.kind === 'action' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
            <span style={label}>Sample fields</span>
            {fields.length === 0 ? (
              <span style={{ fontSize: '0.78rem', color: 'var(--text-muted)' }}>
                This action has no static fields; the run gets an empty payload.
              </span>
            ) : (
              fields.map((f) => (
                <div key={f.key}>
                  <label style={{ ...label, textTransform: 'none', fontWeight: 500, color: 'var(--text-secondary)' }} htmlFor={`sim-f-${f.key}`}>
                    {f.key} <span style={{ color: 'var(--text-muted)' }}>· {f.type}</span>
                  </label>
                  <input
                    id={`sim-f-${f.key}`}
                    value={values[f.key] ?? ''}
                    placeholder={`sample ${f.type}`}
                    onChange={(e) => setValues((v) => ({ ...v, [f.key]: e.target.value }))}
                    style={input}
                  />
                </div>
              ))
            )}
          </div>
        )}
        {pin?.kind === 'event' && (
          <span style={{ fontSize: '0.78rem', color: 'var(--text-muted)' }}>
            Fires the event as “started” with an empty payload.
          </span>
        )}

        {error && <div style={{ fontSize: '0.8rem', color: 'var(--color-error)' }}>{error}</div>}

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '8px' }}>
          <button type="button" onClick={onClose} disabled={busy} style={{ padding: '8px 14px', borderRadius: '7px', background: 'transparent', border: '1px solid var(--border-color)', color: 'var(--text-secondary)', cursor: 'pointer' }}>
            Cancel
          </button>
          <button type="button" onClick={fire} disabled={busy || !pin} style={{ padding: '8px 16px', borderRadius: '7px', background: 'var(--color-accent)', border: '1px solid var(--color-accent)', color: '#fff', fontWeight: 600, cursor: busy ? 'default' : 'pointer', opacity: busy || !pin ? 0.6 : 1 }}>
            {busy ? 'Firing…' : 'Fire signal'}
          </button>
        </div>
      </div>
    </div>
  );
}
