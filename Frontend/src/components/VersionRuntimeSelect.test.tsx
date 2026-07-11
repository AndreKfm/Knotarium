import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { VersionRuntimeSelect } from './VersionRuntimeSelect';
import type { WorkflowVersionSummary } from '../types';

function summary(versionNumber: number): WorkflowVersionSummary {
  return {
    id: `v${versionNumber}`,
    versionNumber,
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: null,
    label: null,
    origin: 'Published',
    isActive: false,
    restoredFromVersionId: null,
    nodeCount: 1,
    executionCount: 0,
  };
}

const versions = [summary(3), summary(2), summary(1)];

describe('VersionRuntimeSelect', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function open() {
    fireEvent.click(screen.getByRole('button', { name: 'Runtime version' }));
  }

  it('previews a version after the user lingers on it', () => {
    const onHoverPreview = vi.fn();
    render(
      <VersionRuntimeSelect
        versions={versions}
        value="v3"
        onSelect={vi.fn()}
        onHoverPreview={onHoverPreview}
        hoverPreviewDelayMs={300}
      />,
    );

    open();
    fireEvent.mouseEnter(screen.getByRole('option', { name: 'Version 2' }));

    expect(onHoverPreview).not.toHaveBeenCalled();
    vi.advanceTimersByTime(300);
    expect(onHoverPreview).toHaveBeenCalledWith('v2');
  });

  it('does not preview on a quick scroll-past', () => {
    const onHoverPreview = vi.fn();
    render(
      <VersionRuntimeSelect
        versions={versions}
        value="v3"
        onSelect={vi.fn()}
        onHoverPreview={onHoverPreview}
        hoverPreviewDelayMs={300}
      />,
    );

    open();
    fireEvent.mouseEnter(screen.getByRole('option', { name: 'Version 2' }));
    vi.advanceTimersByTime(100);
    fireEvent.mouseLeave(screen.getByRole('listbox'));
    vi.advanceTimersByTime(300);

    expect(onHoverPreview).not.toHaveBeenCalled();
  });

  it('commits (not just previews) when an option is clicked', () => {
    const onSelect = vi.fn();
    render(
      <VersionRuntimeSelect versions={versions} value="v3" onSelect={onSelect} onHoverPreview={vi.fn()} />,
    );

    open();
    fireEvent.click(screen.getByRole('option', { name: 'Version 1' }));

    expect(onSelect).toHaveBeenCalledWith('v1');
  });
});
