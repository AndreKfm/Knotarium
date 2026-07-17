// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { UnsavedChangesDialog } from './UnsavedChangesDialog';

describe('UnsavedChangesDialog', () => {
  const handlers = () => ({ onCancel: vi.fn(), onDiscard: vi.fn(), onSave: vi.fn() });

  it('routes each button to its handler', () => {
    const h = handlers();
    render(<UnsavedChangesDialog {...h} />);

    fireEvent.click(screen.getByRole('button', { name: 'Save & leave' }));
    fireEvent.click(screen.getByRole('button', { name: 'Discard & leave' }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(h.onSave).toHaveBeenCalledTimes(1);
    expect(h.onDiscard).toHaveBeenCalledTimes(1);
    expect(h.onCancel).toHaveBeenCalledTimes(1);
  });

  it('cancels when the scrim behind the card is clicked', () => {
    const h = handlers();
    render(<UnsavedChangesDialog {...h} />);
    fireEvent.click(screen.getByRole('dialog'));
    expect(h.onCancel).toHaveBeenCalledTimes(1);
  });

  it('disables the actions and shows a saving label while saving', () => {
    const h = handlers();
    render(<UnsavedChangesDialog {...h} saving />);

    expect(screen.getByRole('button', { name: 'Saving…' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Discard & leave' })).toBeDisabled();
    // A click on the scrim is inert while saving (avoids losing the in-flight save).
    fireEvent.click(screen.getByRole('dialog'));
    expect(h.onCancel).not.toHaveBeenCalled();
  });
});
