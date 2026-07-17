// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useMemo, useState } from 'react';
import { Pin, PinOff } from 'lucide-react';

interface PinOutputEditorProps {
  properties: Record<string, unknown>;
  onChange: (properties: Record<string, unknown>) => void;
}

interface PinnedOutput {
  enabled: boolean;
  payload?: unknown;
  port?: string;
}

function readPin(properties: Record<string, unknown>): PinnedOutput | null {
  const raw = properties.__pinnedOutput;
  return raw && typeof raw === 'object' ? (raw as PinnedOutput) : null;
}

/**
 * Design-time "pin output" editor. Pins a node's output to a sample so downstream nodes can be built
 * and re-run without re-executing upstream. Stored on the node as `__pinnedOutput` (rides the draft/
 * published version). The backend honors it only on manual runs — automated runs ignore pins — and
 * publishing warns while a pin is set.
 */
export function PinOutputEditor({ properties, onChange }: PinOutputEditorProps) {
  const pin = readPin(properties);
  const enabled = !!pin?.enabled;

  const [draft, setDraft] = useState<string>(() =>
    pin?.payload !== undefined ? JSON.stringify(pin.payload, null, 2) : '{\n  \n}');
  const [error, setError] = useState<string | null>(null);

  const port = pin?.port ?? 'result';

  const writePin = (next: Partial<PinnedOutput> | null) => {
    if (next === null) {
      const rest = { ...properties };
      delete rest.__pinnedOutput;
      onChange(rest);
      return;
    }
    onChange({ ...properties, __pinnedOutput: { enabled, payload: pin?.payload, port, ...next } });
  };

  const applyPayload = (text: string) => {
    setDraft(text);
    try {
      const parsed = text.trim() === '' ? null : JSON.parse(text);
      setError(null);
      writePin({ enabled: true, payload: parsed, port });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Invalid JSON');
    }
  };

  const badge = useMemo(() => (enabled ? '#f59e0b' : 'var(--text-muted)'), [enabled]);

  return (
    <div style={{ marginTop: 16, borderTop: '1px solid var(--border-color)', paddingTop: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: '0.72rem', fontWeight: 700, color: badge, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
          <Pin size={13} /> Pinned output {enabled && '(active)'}
        </span>
        <button
          onClick={() => (enabled ? writePin(null) : writePin({ enabled: true }))}
          style={{ display: 'inline-flex', alignItems: 'center', gap: 5, padding: '3px 8px', borderRadius: 6, background: enabled ? 'rgba(245,158,11,0.12)' : 'rgba(255,255,255,0.04)', border: `1px solid ${enabled ? 'rgba(245,158,11,0.32)' : 'var(--border-color)'}`, color: enabled ? '#f59e0b' : 'var(--text-secondary)', fontSize: '0.72rem', cursor: 'pointer' }}
        >
          {enabled ? <><PinOff size={12} /> Clear pin</> : <><Pin size={12} /> Pin output</>}
        </button>
      </div>

      {enabled && (
        <>
          <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>
            On a manual run this node returns the sample below on port <code>{port}</code> instead of executing.
            Automated runs ignore it.
          </span>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <label style={{ fontSize: '0.72rem', color: 'var(--text-secondary)' }}>Port</label>
            <input
              type="text"
              value={port}
              onChange={(e) => writePin({ enabled: true, port: e.target.value || 'result' })}
              style={{ flex: '0 0 120px', padding: '6px 8px', borderRadius: 6, background: 'rgba(0,0,0,0.2)', border: '1px solid var(--border-color)', color: '#fff', fontSize: '0.8rem', fontFamily: 'monospace' }}
            />
          </div>
          <textarea
            value={draft}
            onChange={(e) => applyPayload(e.target.value)}
            spellCheck={false}
            rows={6}
            style={{ width: '100%', boxSizing: 'border-box', padding: 10, borderRadius: 8, background: '#030712', border: `1px solid ${error ? 'var(--color-error)' : 'var(--border-color)'}`, color: '#e2e8f0', fontSize: '0.8rem', fontFamily: 'ui-monospace, Menlo, monospace', resize: 'vertical', outline: 'none' }}
          />
          {error && <span style={{ fontSize: '0.72rem', color: 'var(--color-error)' }}>Invalid JSON: {error}</span>}
        </>
      )}
    </div>
  );
}
