// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ReplayDialog } from '../components/ExecutionDetail/ReplayDialog';
import type { ReplayResult, WorkflowVersionSummary } from '../types';

const versions: WorkflowVersionSummary[] = [
  { id: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z', createdBy: null, label: null, origin: 'Published', isActive: false, restoredFromVersionId: null, nodeCount: 0, executionCount: 0 },
  { id: 'v2', versionNumber: 2, createdAt: '2026-02-01T00:00:00Z', createdBy: null, label: null, origin: 'Published', isActive: false, restoredFromVersionId: null, nodeCount: 0, executionCount: 0 },
];

function renderDialog(overrides: Partial<React.ComponentProps<typeof ReplayDialog>> = {}) {
  const onConfirm = vi.fn();
  const onClose = vi.fn();
  const onOpenRun = vi.fn();

  render(
    <ReplayDialog
      nodeId="reader"
      originalVersionId="v1"
      versions={versions}
      busy={false}
      result={null}
      error={null}
      onConfirm={onConfirm}
      onClose={onClose}
      onOpenRun={onOpenRun}
      {...overrides}
    />,
  );

  return { onConfirm, onClose, onOpenRun };
}

describe('ReplayDialog', () => {
  it('confirms with original version and no mocking by default', () => {
    const { onConfirm } = renderDialog();

    fireEvent.click(screen.getByRole('button', { name: 'Re-run from here' }));

    expect(onConfirm).toHaveBeenCalledWith({ targetVersionId: undefined, mockSideEffects: false });
  });

  it('confirms with a selected target version and mock side effects enabled', () => {
    const { onConfirm } = renderDialog();

    fireEvent.change(screen.getByLabelText('Target version'), { target: { value: 'v2' } });
    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getByRole('button', { name: 'Re-run from here' }));

    expect(onConfirm).toHaveBeenCalledWith({ targetVersionId: 'v2', mockSideEffects: true });
  });

  it('shows side-effect warnings and routes to the new run', () => {
    const result: ReplayResult = {
      newExecutionId: 'replay-99',
      warnings: [{ nodeId: 'http', sideEffectKind: 'NonIdempotentSideEffect' }],
    };
    const { onOpenRun } = renderDialog({ result });

    expect(screen.getByText('Non-idempotent side effects will re-run')).toBeInTheDocument();
    expect(screen.getByText('http')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Open replay run' }));
    expect(onOpenRun).toHaveBeenCalledWith('replay-99');
  });
});
