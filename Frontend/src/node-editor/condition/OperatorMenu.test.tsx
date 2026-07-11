import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { OperatorMenu } from './OperatorMenu';

function setup(overrides: Partial<React.ComponentProps<typeof OperatorMenu>> = {}) {
  const onPick = vi.fn();
  const onClose = vi.fn();
  render(
    <OperatorMenu
      currentOp={overrides.currentOp ?? 'eq'}
      leftType={overrides.leftType ?? 'any'}
      rightType={overrides.rightType ?? 'any'}
      onPick={overrides.onPick ?? onPick}
      onClose={overrides.onClose ?? onClose}
    />,
  );
  return { onPick, onClose };
}

describe('OperatorMenu', () => {
  it('lists type-valid operators and hides the rest for a known left type', () => {
    setup({ leftType: 'number' });
    expect(screen.getByRole('menuitemradio', { name: /Greater than/i })).toBeInTheDocument();
    expect(screen.queryByRole('menuitemradio', { name: /Contains/i })).toBeNull();
  });

  it('marks the current operator and picks on click', () => {
    const { onPick } = setup({ currentOp: 'eq', leftType: 'number' });
    expect(screen.getByRole('menuitemradio', { name: 'Equals' })).toHaveAttribute('aria-checked', 'true');
    fireEvent.click(screen.getByRole('menuitemradio', { name: /Greater than/i }));
    expect(onPick).toHaveBeenCalledWith('gt');
  });

  it('filters by the search query', () => {
    setup({ leftType: 'any' });
    fireEvent.change(screen.getByLabelText('Search operators'), { target: { value: 'contains' } });
    expect(screen.getByRole('menuitemradio', { name: /Contains/i })).toBeInTheDocument();
    expect(screen.queryByRole('menuitemradio', { name: /Greater than/i })).toBeNull();
  });

  it('disables a cross-type ordering operator and refuses to pick it', () => {
    const { onPick } = setup({ leftType: 'number', rightType: 'string' });
    const gt = screen.getByRole('menuitemradio', { name: /Greater than/i });
    expect(gt).toBeDisabled();
    fireEvent.click(gt);
    expect(onPick).not.toHaveBeenCalled();
  });

  it('shows the ordinal-string hint when ordering over strings', () => {
    setup({ currentOp: 'lt', leftType: 'string', rightType: 'string' });
    expect(screen.getByText(/compare lexically/i)).toBeInTheDocument();
  });

  it('closes on Escape', () => {
    const { onClose } = setup();
    fireEvent.keyDown(screen.getByRole('menu'), { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});

describe('OperatorMenu — dismissal', () => {
  it('closes on a mousedown outside the menu', () => {
    const { onClose } = setup();
    fireEvent.mouseDown(document.body);
    expect(onClose).toHaveBeenCalled();
  });

  it('does not close on a mousedown inside the menu', () => {
    const { onClose } = setup();
    fireEvent.mouseDown(screen.getByRole('menu'));
    expect(onClose).not.toHaveBeenCalled();
  });

  it('does not close when the operator pill trigger is clicked (it toggles itself)', () => {
    const trigger = document.createElement('button');
    trigger.className = 'cne-op-pill';
    document.body.appendChild(trigger);
    const { onClose } = setup();
    fireEvent.mouseDown(trigger);
    expect(onClose).not.toHaveBeenCalled();
    trigger.remove();
  });
});
