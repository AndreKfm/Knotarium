// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo } from 'react';
import { SHORTCUT_GROUPS, formatShortcutKeys, isMacPlatform } from './keyboardShortcuts';

export interface KeyboardShortcutsHelpProps {
  onClose: () => void;
}

/**
 * Keyboard-shortcut cheat sheet, opened with "?". Mounted only while open
 * (parent gates rendering). Closes on Escape or backdrop click.
 */
export function KeyboardShortcutsHelp({ onClose }: KeyboardShortcutsHelpProps) {
  // Close on Escape — captured here so it doesn't fall through to canvas shortcuts.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' || e.key === '?') {
        e.preventDefault();
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [onClose]);

  // ⌘ on macOS, Ctrl on Windows/Linux. Computed once per open.
  const isMac = useMemo(() => isMacPlatform(), []);

  return (
    <div
      role="dialog"
      aria-label="Keyboard shortcuts"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
      style={{
        position: 'absolute',
        inset: 0,
        zIndex: 2000,
        display: 'flex',
        justifyContent: 'center',
        alignItems: 'flex-start',
        paddingTop: '8vh',
        background: 'rgba(0,0,0,0.4)',
        backdropFilter: 'blur(2px)',
      }}
    >
      <div
        style={{
          width: 'min(620px, 92%)',
          maxHeight: '80vh',
          overflowY: 'auto',
          background: 'var(--bg-surface-opaque, #101625)',
          border: '1px solid var(--border-color)',
          borderRadius: '12px',
          boxShadow: '0 20px 60px rgba(0,0,0,0.5)',
        }}
      >
        <div
          style={{
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            padding: '14px 20px',
            borderBottom: '1px solid var(--border-color)',
          }}
        >
          <strong style={{ color: 'var(--text-primary, #e5e7eb)', fontSize: '0.95rem' }}>
            Keyboard shortcuts
          </strong>
          <button
            type="button"
            aria-label="Close"
            onClick={onClose}
            style={{
              border: 'none',
              background: 'transparent',
              color: 'var(--text-secondary)',
              fontSize: '1.1rem',
              cursor: 'pointer',
              lineHeight: 1,
            }}
          >
            ✕
          </button>
        </div>

        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))',
            gap: '8px 24px',
            padding: '16px 20px',
          }}
        >
          {SHORTCUT_GROUPS.map((group) => (
            <div key={group.title}>
              <div
                style={{
                  color: 'var(--color-accent, #818cf8)',
                  fontSize: '0.72rem',
                  fontWeight: 700,
                  textTransform: 'uppercase',
                  letterSpacing: '0.04em',
                  margin: '6px 0',
                }}
              >
                {group.title}
              </div>
              {group.items.map((item) => (
                <div
                  key={item.keys}
                  style={{
                    display: 'flex',
                    justifyContent: 'space-between',
                    gap: '12px',
                    padding: '4px 0',
                    fontSize: '0.82rem',
                    color: 'var(--text-primary, #e5e7eb)',
                  }}
                >
                  <span style={{ color: 'var(--text-secondary)' }}>{item.description}</span>
                  <kbd
                    style={{
                      flexShrink: 0,
                      fontFamily: 'var(--font-mono, monospace)',
                      fontSize: '0.74rem',
                      color: 'var(--text-primary, #e5e7eb)',
                      whiteSpace: 'nowrap',
                    }}
                  >
                    {formatShortcutKeys(item.keys, isMac)}
                  </kbd>
                </div>
              ))}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
