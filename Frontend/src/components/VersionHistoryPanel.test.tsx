// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { VersionHistoryPanel } from './VersionHistoryPanel';
import type { WorkflowVersionSummary } from '../types';

function makeVersion(overrides: Partial<WorkflowVersionSummary> = {}): WorkflowVersionSummary {
  return {
    id: 'ver-1',
    versionNumber: 1,
    createdAt: '2026-06-04T10:00:00Z',
    createdBy: null,
    label: null,
    origin: 'Published',
    isActive: false,
    restoredFromVersionId: null,
    nodeCount: 3,
    executionCount: 0,
    ...overrides,
  };
}

describe('VersionHistoryPanel', () => {
  it('renders nothing when closed', () => {
    const { container } = render(
      <VersionHistoryPanel open={false} versions={[]} loading={false} error={null} onClose={vi.fn()} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it('shows version metadata, the active badge and the origin', () => {
    const versions = [
      makeVersion({ id: 'ver-2', versionNumber: 2, isActive: true, createdBy: 'alice', label: 'hotfix', origin: 'Restored' }),
      makeVersion({ id: 'ver-1', versionNumber: 1 }),
    ];
    render(
      <VersionHistoryPanel open versions={versions} loading={false} error={null} onClose={vi.fn()} />,
    );

    expect(screen.getByText('v2')).toBeTruthy();
    expect(screen.getByText('v1')).toBeTruthy();
    expect(screen.getByText('ACTIVE')).toBeTruthy();
    expect(screen.getByText('Restored')).toBeTruthy();
    expect(screen.getByText('hotfix')).toBeTruthy();
    expect(screen.getByText(/alice/)).toBeTruthy();
  });

  it('forwards the version id on row click', () => {
    const onPreview = vi.fn();
    render(
      <VersionHistoryPanel
        open
        versions={[makeVersion({ id: 'ver-9', versionNumber: 9 })]}
        loading={false}
        error={null}
        onClose={vi.fn()}
        onPreview={onPreview}
      />,
    );

    fireEvent.click(screen.getByText('v9'));
    expect(onPreview).toHaveBeenCalledWith('ver-9');
  });

  it('treats the active-version id as active even without the isActive flag', () => {
    render(
      <VersionHistoryPanel
        open
        versions={[makeVersion({ id: 'ver-5', versionNumber: 5, isActive: false })]}
        loading={false}
        error={null}
        activeVersionId="ver-5"
        onClose={vi.fn()}
      />,
    );
    expect(screen.getByText('ACTIVE')).toBeTruthy();
  });

  it('renders the loading and empty states', () => {
    const { rerender } = render(
      <VersionHistoryPanel open versions={[]} loading error={null} onClose={vi.fn()} />,
    );
    expect(screen.getByText(/Loading versions/)).toBeTruthy();

    rerender(<VersionHistoryPanel open versions={[]} loading={false} error={null} onClose={vi.fn()} />);
    expect(screen.getByText(/No published versions/)).toBeTruthy();
  });

  it('closes via the close button', () => {
    const onClose = vi.fn();
    render(<VersionHistoryPanel open versions={[]} loading={false} error={null} onClose={onClose} />);
    fireEvent.click(screen.getByLabelText('Close version history'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('shows no per-row Diff button when onDiffVersion is not provided; the row still opens', () => {
    const onPreview = vi.fn();
    render(
      <VersionHistoryPanel
        open
        versions={[makeVersion({ id: 'ver-7', versionNumber: 7 })]}
        loading={false}
        error={null}
        onClose={vi.fn()}
        onPreview={onPreview}
      />,
    );

    expect(screen.queryByRole('button', { name: 'Restore' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Diff version/ })).toBeNull();
    // The whole row still opens the version.
    fireEvent.click(screen.getByText('v7'));
    expect(onPreview).toHaveBeenCalledWith('ver-7');
  });

  it('diffs a specific version against the draft from a per-row Diff button, without previewing it', () => {
    const onPreview = vi.fn();
    const onDiffVersion = vi.fn();
    render(
      <VersionHistoryPanel
        open
        versions={[makeVersion({ id: 'ver-7', versionNumber: 7 })]}
        loading={false}
        error={null}
        onClose={vi.fn()}
        onPreview={onPreview}
        onDiffVersion={onDiffVersion}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Diff version 7/ }));
    expect(onDiffVersion).toHaveBeenCalledWith('ver-7');
    // The Diff shortcut must not also open the read-only preview (stopPropagation).
    expect(onPreview).not.toHaveBeenCalled();
  });

  it('offers the first-class draft-vs-active diff in the footer', () => {
    const onDiffDraftVsActive = vi.fn();
    render(
      <VersionHistoryPanel
        open
        versions={[makeVersion()]}
        loading={false}
        error={null}
        onClose={vi.fn()}
        onDiffDraftVsActive={onDiffDraftVsActive}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: /Diff draft vs active/ }));
    expect(onDiffDraftVsActive).toHaveBeenCalledTimes(1);
  });
});
