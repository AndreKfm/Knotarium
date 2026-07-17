// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent } from '@testing-library/react';
import { vi, describe, it, expect } from 'vitest';
import { KeyboardShortcutsHelp } from './KeyboardShortcutsHelp';
import { SHORTCUT_GROUPS, formatShortcutKeys, isMacPlatform } from './keyboardShortcuts';

describe('SHORTCUT_GROUPS', () => {
  it('has non-empty groups with non-empty items', () => {
    expect(SHORTCUT_GROUPS.length).toBeGreaterThan(0);
    for (const g of SHORTCUT_GROUPS) {
      expect(g.title).toBeTruthy();
      expect(g.items.length).toBeGreaterThan(0);
      for (const i of g.items) {
        expect(i.keys).toBeTruthy();
        expect(i.description).toBeTruthy();
      }
    }
  });

  it('documents the core editor bindings (with the Mod token)', () => {
    const keys = SHORTCUT_GROUPS.flatMap((g) => g.items.map((i) => i.keys)).join(' | ');
    expect(keys).toMatch(/Mod \+ Z/); // undo
    expect(keys).toMatch(/Mod \+ C/); // copy
    expect(keys).toMatch(/Mod \+ F/); // search
    expect(keys).toContain('?'); // this help
    expect(keys).toMatch(/Delete/); // delete
  });
});

describe('formatShortcutKeys', () => {
  it('renders Ctrl off-Mac', () => {
    expect(formatShortcutKeys('Mod + Z', false)).toBe('Ctrl + Z');
  });

  it('renders ⌘ on Mac', () => {
    expect(formatShortcutKeys('Mod + Z', true)).toBe('⌘ + Z');
  });

  it('substitutes every occurrence', () => {
    expect(formatShortcutKeys('Mod + Shift + Z  ·  Mod + Y', false)).toBe('Ctrl + Shift + Z  ·  Ctrl + Y');
    expect(formatShortcutKeys('Mod + Shift + Z  ·  Mod + Y', true)).toBe('⌘ + Shift + Z  ·  ⌘ + Y');
  });

  it('leaves non-Mod keys untouched', () => {
    expect(formatShortcutKeys('Delete · Backspace', true)).toBe('Delete · Backspace');
  });
});

describe('isMacPlatform', () => {
  it('detects Mac via userAgentData.platform', () => {
    expect(isMacPlatform({ userAgentData: { platform: 'macOS' } })).toBe(true);
  });

  it('detects Mac via navigator.platform', () => {
    expect(isMacPlatform({ platform: 'MacIntel' })).toBe(true);
  });

  it('detects Mac via the UA string when platform is absent', () => {
    expect(isMacPlatform({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)' })).toBe(true);
  });

  it('is false on Windows', () => {
    expect(isMacPlatform({ platform: 'Win32', userAgent: 'Windows NT 10.0' })).toBe(false);
  });

  it('is false with no navigator', () => {
    expect(isMacPlatform(undefined)).toBe(false);
  });
});

describe('KeyboardShortcutsHelp', () => {
  it('renders all group titles and the bindings', () => {
    render(<KeyboardShortcutsHelp onClose={vi.fn()} />);
    for (const g of SHORTCUT_GROUPS) {
      expect(screen.getByText(g.title)).toBeTruthy();
    }
    expect(screen.getByText('Undo')).toBeTruthy();
    expect(screen.getByText('Search / jump to a node')).toBeTruthy();
    // jsdom is non-Mac, so the Mod token renders as Ctrl (not ⌘).
    expect(screen.getByText('Ctrl + Z')).toBeTruthy();
    expect(screen.queryByText(/⌘/)).toBeNull();
  });

  it('closes via the close button', () => {
    const onClose = vi.fn();
    render(<KeyboardShortcutsHelp onClose={onClose} />);
    fireEvent.click(screen.getByLabelText('Close'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closes on backdrop click', () => {
    const onClose = vi.fn();
    render(<KeyboardShortcutsHelp onClose={onClose} />);
    fireEvent.mouseDown(screen.getByRole('dialog'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closes on Escape', () => {
    const onClose = vi.fn();
    render(<KeyboardShortcutsHelp onClose={onClose} />);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closes on "?" (toggle off)', () => {
    const onClose = vi.fn();
    render(<KeyboardShortcutsHelp onClose={onClose} />);
    fireEvent.keyDown(window, { key: '?' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
