// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { RestoreVersionDialog } from './RestoreVersionDialog';
import type { RestoreVersionResult } from '../types';

function renderDialog(overrides: Partial<React.ComponentProps<typeof RestoreVersionDialog>> = {}) {
  const onConfirm = vi.fn();
  const onClose = vi.fn();
  render(
    <RestoreVersionDialog
      versionNumber={3}
      busy={false}
      result={null}
      error={null}
      onConfirm={onConfirm}
      onClose={onClose}
      {...overrides}
    />,
  );
  return { onConfirm, onClose };
}

describe('RestoreVersionDialog', () => {
  it('makes fork-forward + future-only semantics explicit', () => {
    renderDialog();
    expect(screen.getByText(/fork-forward/)).toBeTruthy();
    expect(screen.getByText(/future executions only/)).toBeTruthy();
  });

  it('restores inactive by default', () => {
    const { onConfirm } = renderDialog();
    fireEvent.click(screen.getByRole('button', { name: 'Restore (inactive)' }));
    expect(onConfirm).toHaveBeenCalledWith({ activate: false });
  });

  it('restores and activates when the checkbox is ticked', () => {
    const { onConfirm } = renderDialog();
    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getByRole('button', { name: 'Restore & activate' }));
    expect(onConfirm).toHaveBeenCalledWith({ activate: true });
  });

  it('surfaces an error message', () => {
    renderDialog({ error: 'Activation failed — fix these first: [E1] bad' });
    expect(screen.getByText(/Activation failed/)).toBeTruthy();
  });

  it('shows the success view with warnings', () => {
    const result: RestoreVersionResult = {
      versionId: 'v-new',
      versionNumber: 8,
      origin: 'Restored',
      restoredFromVersionId: 'v-3',
      activated: false,
      activatedAtUtc: null,
      warnings: ['Node type "legacy" is deprecated'],
    };
    renderDialog({ result });
    expect(screen.getByText(/v8/)).toBeTruthy();
    expect(screen.getByText(/inactive forward copy/)).toBeTruthy();
    expect(screen.getByText(/Node type "legacy" is deprecated/)).toBeTruthy();
  });
});
