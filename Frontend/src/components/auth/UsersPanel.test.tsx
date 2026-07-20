// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { UsersPanel } from './UsersPanel';

vi.mock('../../utils/api', () => ({
  api: {
    listUsers: vi.fn().mockResolvedValue([]),
    createUser: vi.fn(),
    deleteUser: vi.fn(),
    changeOwnPassword: vi.fn(),
  },
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ status: { userId: 'me' } }),
}));

describe('UsersPanel — closing', () => {
  beforeEach(() => vi.clearAllMocks());

  it('closes on Escape', () => {
    const onClose = vi.fn();
    render(<UsersPanel onClose={onClose} />);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('closes via the ✕ button', () => {
    const onClose = vi.fn();
    render(<UsersPanel onClose={onClose} />);
    fireEvent.click(screen.getByRole('button', { name: /close/i }));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('closes on a backdrop mousedown but not when the press lands inside the dialog', () => {
    const onClose = vi.fn();
    const { container } = render(<UsersPanel onClose={onClose} />);
    // Dismissal keys off mousedown on the scrim (see useScrimClose): a press that lands on the
    // heading (inside the dialog) must not close, even though a click could start there and end
    // out over the backdrop.
    fireEvent.mouseDown(screen.getByText('Users'));
    expect(onClose).not.toHaveBeenCalled();
    // A press that lands directly on the backdrop (the outermost overlay) closes.
    fireEvent.mouseDown(container.firstChild as HTMLElement);
    expect(onClose).toHaveBeenCalledOnce();
  });
});
