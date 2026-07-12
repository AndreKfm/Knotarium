import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { GuidedTour } from './GuidedTour';
import { TOUR_STEPS } from './tourSteps';

describe('TOUR_STEPS', () => {
  it('is a non-empty set with a title and body per step', () => {
    expect(TOUR_STEPS.length).toBeGreaterThan(1);
    for (const s of TOUR_STEPS) {
      expect(s.title.trim()).toBeTruthy();
      expect(s.body.trim()).toBeTruthy();
      if (s.selector) expect(s.selector).toMatch(/^\[data-tour="[a-z-]+"\]$/);
    }
  });
});

describe('GuidedTour', () => {
  it('starts on the first step with a step counter', () => {
    render(<GuidedTour onClose={vi.fn()} />);
    expect(screen.getByText(TOUR_STEPS[0].title)).toBeInTheDocument();
    expect(screen.getByText(`1 / ${TOUR_STEPS.length}`)).toBeInTheDocument();
  });

  it('advances with Next and goes back with Back', () => {
    render(<GuidedTour onClose={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /^next$/i }));
    expect(screen.getByText(TOUR_STEPS[1].title)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /^back$/i }));
    expect(screen.getByText(TOUR_STEPS[0].title)).toBeInTheDocument();
  });

  it('finishes on the last step (Done closes)', () => {
    const onClose = vi.fn();
    render(<GuidedTour onClose={onClose} />);
    for (let i = 0; i < TOUR_STEPS.length - 1; i++) {
      fireEvent.click(screen.getByRole('button', { name: /^next$/i }));
    }
    fireEvent.click(screen.getByRole('button', { name: /^done$/i }));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('skips via the ✕ and closes on Escape', () => {
    const onClose = vi.fn();
    const { unmount } = render(<GuidedTour onClose={onClose} />);
    fireEvent.click(screen.getByRole('button', { name: /skip tour/i }));
    expect(onClose).toHaveBeenCalledOnce();
    unmount();

    const onClose2 = vi.fn();
    render(<GuidedTour onClose={onClose2} />);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose2).toHaveBeenCalledOnce();
  });
});
