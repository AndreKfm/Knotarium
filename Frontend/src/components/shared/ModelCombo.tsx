// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../../utils/api';
import { curatedModelsFor, mergeModelSuggestions } from '../../node-editor/aiModels';

interface ModelComboProps {
  value: string;
  onChange: (value: string) => void;
  /** The provider vendor. When omitted, it is read from the global AI provider config (node inspector use). */
  vendor?: string | null;
  credentialRef?: string | null;
  baseUrl?: string | null;
  apiVersion?: string | null;
  placeholder?: string;
  disabled?: boolean;
  style?: React.CSSProperties;
}

/**
 * Editable model picker: a free-text input plus a dropdown of curated per-vendor suggestions, with an
 * optional "live" button that merges the provider's actual model list on top. Unlike a native &lt;datalist&gt;,
 * the dropdown always offers every suggestion regardless of what has been typed — so entering a custom /
 * unlisted model (e.g. a brand-new release) never leaves the combo unusable. Free text is always allowed.
 * Used in the global AI provider settings (vendor + credential passed from the form) and on AI nodes'
 * `model` override field (vendor + credential resolved from the saved global config).
 */
export function ModelCombo({
  value, onChange, vendor, credentialRef, baseUrl, apiVersion, placeholder, disabled, style,
}: ModelComboProps) {
  const [resolved, setResolved] = useState<{ vendor: string | null; credentialRef: string | null; baseUrl: string | null; apiVersion: string | null }>(
    { vendor: vendor ?? null, credentialRef: credentialRef ?? null, baseUrl: baseUrl ?? null, apiVersion: apiVersion ?? null },
  );
  const [live, setLive] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);
  const explicitVendor = vendor !== undefined;

  // Keep resolved config in sync with props when the vendor is supplied explicitly (settings form).
  useEffect(() => {
    if (explicitVendor) {
      setResolved({ vendor: vendor ?? null, credentialRef: credentialRef ?? null, baseUrl: baseUrl ?? null, apiVersion: apiVersion ?? null });
      setLive([]); // vendor/credential changed → prior live list no longer applies
      setNote(null);
    }
  }, [explicitVendor, vendor, credentialRef, baseUrl, apiVersion]);

  // Node inspector use: no vendor prop → read it from the saved global AI config once.
  const fetchedOnce = useRef(false);
  useEffect(() => {
    if (explicitVendor || fetchedOnce.current) return;
    fetchedOnce.current = true;
    api.getAiProviderConfig()
      .then((cfg) => setResolved({ vendor: cfg.vendor, credentialRef: cfg.credentialRef, baseUrl: cfg.baseUrl, apiVersion: cfg.apiVersion }))
      .catch(() => { /* leave curated-only */ });
  }, [explicitVendor]);

  // Close the dropdown on an outside click.
  useEffect(() => {
    if (!open) return;
    const onDocMouseDown = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDocMouseDown);
    return () => document.removeEventListener('mousedown', onDocMouseDown);
  }, [open]);

  const suggestions = useMemo(
    () => mergeModelSuggestions(curatedModelsFor(resolved.vendor), live),
    [resolved.vendor, live],
  );

  // Show all suggestions by default; if the typed text matches a subset, surface those first, but never
  // collapse to an empty list just because the current value is a custom/unlisted model.
  const shown = useMemo(() => {
    const q = value.trim().toLowerCase();
    if (!q) return suggestions;
    const matches = suggestions.filter((m) => m.toLowerCase().includes(q));
    return matches.length > 0 ? matches : suggestions;
  }, [suggestions, value]);

  const loadLive = async () => {
    if (!resolved.vendor || !resolved.credentialRef) {
      setNote('Configure a provider and API-key credential first.');
      return;
    }
    setLoading(true);
    setNote(null);
    try {
      const res = await api.getAiProviderModels({
        vendor: resolved.vendor,
        model: value || 'x',
        credentialRef: resolved.credentialRef,
        baseUrl: resolved.baseUrl,
        apiVersion: resolved.apiVersion,
      });
      setLive(res.models);
      setNote(res.models.length > 0 ? `Loaded ${res.models.length} live model(s).` : 'No live models returned — using curated suggestions.');
      if (res.models.length > 0) setOpen(true);
    } catch {
      setNote('Could not load live models — using curated suggestions.');
    } finally {
      setLoading(false);
    }
  };

  const pick = (m: string) => {
    onChange(m);
    setOpen(false);
  };

  return (
    <div ref={wrapRef} style={{ display: 'flex', flexDirection: 'column', gap: '4px', position: 'relative' }}>
      <div style={{ display: 'flex', gap: '6px', alignItems: 'stretch' }}>
        <div style={{ position: 'relative', flex: 1 }}>
          <input
            value={value}
            disabled={disabled}
            placeholder={placeholder ?? 'Model…'}
            spellCheck={false}
            autoComplete="off"
            onChange={(e) => { onChange(e.target.value); setOpen(true); }}
            onFocus={() => setOpen(true)}
            style={{ width: '100%', paddingRight: '30px', boxSizing: 'border-box', ...style }}
          />
          {suggestions.length > 0 && (
            <button
              type="button"
              tabIndex={-1}
              aria-label="Show model suggestions"
              disabled={disabled}
              onClick={() => setOpen((o) => !o)}
              style={{
                position: 'absolute', right: '2px', top: '2px', bottom: '2px', width: '26px',
                background: 'transparent', border: 'none', color: 'var(--text-secondary, #94a3b8)',
                cursor: disabled ? 'default' : 'pointer', fontSize: '0.7rem', lineHeight: 1,
              }}
            >
              {open ? '▲' : '▼'}
            </button>
          )}
          {open && shown.length > 0 && (
            <ul
              role="listbox"
              style={{
                position: 'absolute', top: 'calc(100% + 4px)', left: 0, right: 0, zIndex: 50,
                margin: 0, padding: '4px', listStyle: 'none', maxHeight: '220px', overflowY: 'auto',
                background: 'var(--panel-bg, #0b1220)', border: '1px solid var(--border-color, rgba(255,255,255,0.14))',
                borderRadius: '8px', boxShadow: '0 8px 24px rgba(0,0,0,0.45)',
              }}
            >
              {shown.map((m) => {
                const selected = m === value;
                return (
                  <li key={m}>
                    <button
                      type="button"
                      role="option"
                      aria-selected={selected}
                      // onMouseDown (not onClick) so the pick fires before the input blur closes the panel.
                      onMouseDown={(e) => { e.preventDefault(); pick(m); }}
                      style={{
                        width: '100%', textAlign: 'left', padding: '6px 8px', borderRadius: '6px',
                        border: 'none', cursor: 'pointer', fontSize: '0.82rem',
                        background: selected ? 'rgba(124,108,240,0.22)' : 'transparent',
                        color: 'var(--text-primary, #f8fafc)',
                      }}
                      onMouseEnter={(e) => { if (!selected) (e.currentTarget.style.background = 'rgba(148,163,184,0.14)'); }}
                      onMouseLeave={(e) => { if (!selected) (e.currentTarget.style.background = 'transparent'); }}
                    >
                      {m}
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
        <button
          type="button"
          onClick={loadLive}
          disabled={disabled || loading || !resolved.credentialRef}
          title={resolved.credentialRef ? 'Load the provider’s live model list' : 'Set a provider + credential first'}
          style={{
            background: 'transparent', border: '1px solid var(--border-color, rgba(255,255,255,0.1))',
            color: 'var(--text-secondary, #94a3b8)', borderRadius: '8px', padding: '0 10px',
            fontSize: '0.75rem', cursor: (disabled || !resolved.credentialRef) ? 'default' : 'pointer', whiteSpace: 'nowrap',
            opacity: (disabled || !resolved.credentialRef) ? 0.5 : 1,
          }}
        >
          {loading ? '…' : '↻ live'}
        </button>
      </div>
      {note && <span style={{ fontSize: '0.7rem', color: 'var(--text-muted, #64748b)' }}>{note}</span>}
    </div>
  );
}
