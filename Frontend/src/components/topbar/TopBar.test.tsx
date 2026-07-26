// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { TopBar } from './TopBar';

// jsdom has no layout engine, so the degradation LADDER itself cannot be
// asserted here (every element measures 0). What is layout-independent — and
// what the ladder depends on — is asserted instead: that every destination is
// present with a full aria-label, that the active one is excluded from the
// shed order, and that ⌘K reaches everything by name.

vi.mock('../auth/AuthContext', () => ({
  useAuth: () => ({
    status: { enabled: true, authenticated: true, username: 'andre', userId: 'u1', setupRequired: false },
    refresh: vi.fn(),
    logout: vi.fn(),
  }),
}));

vi.mock('../../utils/api', () => ({
  api: {
    getWorkflows: vi.fn().mockResolvedValue([{ id: { value: 'wf-1' }, name: 'Nightly Sync', nodes: [], edges: [] }]),
    getExecutions: vi.fn().mockResolvedValue([]),
  },
}));

const DESTINATIONS = [
  'Dashboard', 'Canvas Editor', 'AI Generate', 'Node Editor',
  'Execution Visualizer', 'Dead Letter',
  'Bundles', 'Templates', 'API Importer', 'Import',
  'Settings', 'Users',
];

function renderBar(view = 'dashboard', overrides: Partial<Parameters<typeof TopBar>[0]> = {}) {
  const props = {
    view,
    onSelect: vi.fn(),
    onAiGenerate: vi.fn(),
    onOpenWorkflow: vi.fn(),
    onOpenRun: vi.fn(),
    onOpenLatestRun: vi.fn(),
    onOpenTour: vi.fn(),
    armed: false,
    armingBusy: false,
    onSetArmed: vi.fn(),
    version: null,
    onGoHome: vi.fn(),
    ...overrides,
  };
  render(<TopBar {...props} />);
  return props;
}

describe('TopBar', () => {
  it('keeps every destination in the bar, each with its full accessible name', () => {
    renderBar();
    for (const label of DESTINATIONS) {
      expect(screen.getByRole('button', { name: label })).toBeInTheDocument();
    }
  });

  it('excludes the active destination from the shed order and marks it as the current page', () => {
    renderBar('templates');

    const active = screen.getByRole('button', { name: 'Templates' });
    expect(active).toHaveAttribute('aria-current', 'page');
    expect(active).toHaveAttribute('data-shed-rank', '0');

    // The remaining eleven get a contiguous 1..11 order — the ladder's steps 2–12.
    const ranks = DESTINATIONS
      .filter((label) => label !== 'Templates')
      .map((label) => Number(screen.getByRole('button', { name: label }).getAttribute('data-shed-rank')))
      .sort((a, b) => a - b);
    expect(ranks).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
  });

  it('sheds occasional destinations before daily ones', () => {
    renderBar('dashboard');
    const rank = (label: string) => Number(screen.getByRole('button', { name: label }).getAttribute('data-shed-rank'));

    expect(rank('Users')).toBeLessThan(rank('Settings'));
    expect(rank('Settings')).toBeLessThan(rank('Dead Letter'));
    expect(rank('Dead Letter')).toBeLessThan(rank('Canvas Editor'));
    expect(rank('Canvas Editor')).toBeLessThan(rank('Execution Visualizer'));
  });

  it('navigates on click even though the item also carries tooltip handlers', () => {
    const props = renderBar();
    fireEvent.click(screen.getByRole('button', { name: 'Dead Letter' }));
    expect(props.onSelect).toHaveBeenCalledWith('dead-letter');
  });

  it('opens the palette on Ctrl+K and finds a destination by synonym', async () => {
    const props = renderBar();

    fireEvent.keyDown(window, { key: 'k', ctrlKey: true });
    const input = await screen.findByRole('dialog', { name: 'Command palette' });
    expect(input).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Search destinations, workflows, runs and actions'), {
      target: { value: 'logs' },
    });
    const hits = screen.getAllByRole('option');
    expect(hits).toHaveLength(1);

    fireEvent.click(hits[0]);
    expect(props.onSelect).toHaveBeenCalledWith('execution');
  });

  it('offers loaded workflows in the palette', async () => {
    const props = renderBar();
    fireEvent.keyDown(window, { key: 'k', ctrlKey: true });

    await waitFor(() => expect(screen.getByText('Nightly Sync')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Nightly Sync'));
    expect(props.onOpenWorkflow).toHaveBeenCalledWith('wf-1');
  });
});
