// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { EmptyCanvasHint } from './EmptyCanvasHint';

describe('EmptyCanvasHint', () => {
  it('guides the user to add a first node', () => {
    render(<EmptyCanvasHint />);
    expect(screen.getByText(/your canvas is empty/i)).toBeInTheDocument();
    expect(screen.getByText(/drag a node from the palette/i)).toBeInTheDocument();
  });

  it('is non-interactive so it never blocks the canvas', () => {
    const { container } = render(<EmptyCanvasHint />);
    const overlay = container.firstElementChild as HTMLElement;
    expect(overlay).toHaveStyle({ pointerEvents: 'none' });
  });
});
