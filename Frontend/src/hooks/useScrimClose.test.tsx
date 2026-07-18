// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { useScrimClose } from './useScrimClose';

function Dialog({ onClose, enabled }: { onClose: () => void; enabled?: boolean }) {
  const onScrimMouseDown = useScrimClose(onClose, enabled);
  return (
    <div data-testid="scrim" onMouseDown={onScrimMouseDown}>
      <div data-testid="content">
        <textarea data-testid="field" defaultValue="select me" />
      </div>
    </div>
  );
}

describe('useScrimClose', () => {
  it('closes on a mousedown that lands on the scrim itself', () => {
    const onClose = vi.fn();
    render(<Dialog onClose={onClose} />);
    fireEvent.mouseDown(screen.getByTestId('scrim'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does NOT close when the press starts inside the content (a text selection)', () => {
    const onClose = vi.fn();
    render(<Dialog onClose={onClose} />);
    // Press begins on the field; even if the mouse is released over the scrim, this must not close.
    fireEvent.mouseDown(screen.getByTestId('field'));
    fireEvent.mouseUp(screen.getByTestId('scrim'));
    expect(onClose).not.toHaveBeenCalled();
  });

  it('closes on Escape', () => {
    const onClose = vi.fn();
    render(<Dialog onClose={onClose} />);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does nothing while disabled (e.g. a request in flight)', () => {
    const onClose = vi.fn();
    render(<Dialog onClose={onClose} enabled={false} />);
    fireEvent.mouseDown(screen.getByTestId('scrim'));
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).not.toHaveBeenCalled();
  });
});
