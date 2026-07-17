// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent } from '@testing-library/react';
import { beforeEach, describe, it, expect } from 'vitest';
import { TipOfTheDay } from './TipOfTheDay';
import { TIPS } from './tips';

describe('TIPS', () => {
  it('is a non-empty set of non-empty, single-line tips', () => {
    expect(TIPS.length).toBeGreaterThan(0);
    for (const t of TIPS) {
      expect(t.text.trim()).toBeTruthy();
      expect(t.text).not.toContain('\n');
    }
  });
});

describe('TipOfTheDay', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('renders a tip from the set', () => {
    render(<TipOfTheDay />);
    expect(screen.getByRole('note', { name: /tip of the day/i })).toBeInTheDocument();
    const shown = screen.getByRole('note').textContent ?? '';
    expect(TIPS.some((t) => shown.includes(t.text))).toBe(true);
  });

  it('shows a different tip when "Next" is clicked', () => {
    render(<TipOfTheDay />);
    const before = screen.getByRole('note').textContent;
    fireEvent.click(screen.getByRole('button', { name: /next tip/i }));
    expect(screen.getByRole('note').textContent).not.toEqual(before);
  });

  it('dismissing hides the card and persists the choice', () => {
    const { unmount } = render(<TipOfTheDay />);
    fireEvent.click(screen.getByRole('button', { name: /dismiss tips/i }));
    expect(screen.queryByRole('note')).not.toBeInTheDocument();
    expect(localStorage.getItem('kg-tips-hidden')).toBe('1');

    // Stays hidden on the next mount.
    unmount();
    render(<TipOfTheDay />);
    expect(screen.queryByRole('note')).not.toBeInTheDocument();
  });

  it('advances the persisted index on mount so the next visit rotates', () => {
    localStorage.setItem('kg-tip-index', '0');
    const { unmount } = render(<TipOfTheDay />);
    expect(localStorage.getItem('kg-tip-index')).toBe('1');
    unmount();
    render(<TipOfTheDay />);
    expect(localStorage.getItem('kg-tip-index')).toBe('2');
  });
});
