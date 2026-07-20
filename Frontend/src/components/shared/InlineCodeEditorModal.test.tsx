// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { InlineCodeEditorModal } from './InlineCodeEditorModal';

// Monaco can't run in jsdom — swap both editors for a plain textarea keyed by language so we can type.
vi.mock('@monaco-editor/react', () => ({
  default: (props: { value: string; language?: string; onChange?: (v: string) => void }) => (
    <textarea
      data-testid={`editor-${props.language ?? 'x'}`}
      value={props.value}
      onChange={(e) => props.onChange?.(e.target.value)}
    />
  ),
}));

// Control the capability policy + spy on the test-run endpoint.
const getCapabilityPolicy = vi.fn();
const testInlineCode = vi.fn();
vi.mock('../../utils/api', () => ({
  api: {
    getCapabilityPolicy: () => getCapabilityPolicy(),
    testInlineCode: (...a: unknown[]) => testInlineCode(...a),
    generateInlineCode: vi.fn(),
  },
}));

function renderModal(over: Partial<Parameters<typeof InlineCodeEditorModal>[0]> = {}) {
  const onSave = vi.fn();
  const onClose = vi.fn();
  render(
    <InlineCodeEditorModal open code="return 1;" language="csharp" onSave={onSave} onClose={onClose} {...over} />,
  );
  return { onSave, onClose };
}

describe('InlineCodeEditorModal — save is decoupled from execution', () => {
  beforeEach(() => {
    getCapabilityPolicy.mockReset();
    testInlineCode.mockReset();
  });

  it('when code execution is DISABLED: shows a notice, disables Run test, but Save still commits without running', async () => {
    getCapabilityPolicy.mockResolvedValue({ enabledCapabilities: [] });
    const { onSave } = renderModal();

    // Non-blocking notice appears once the policy loads.
    expect(await screen.findByText(/execution is disabled/i)).toBeInTheDocument();

    // Run test is disabled (execution gated).
    expect(screen.getByRole('button', { name: /run test/i })).toBeDisabled();

    // Edit the code so there are unsaved changes.
    const editor = await screen.findByTestId('editor-csharp');
    fireEvent.change(editor, { target: { value: 'return 42;' } });

    // Save is now enabled and commits WITHOUT hitting the execution endpoint.
    const saveBtn = screen.getByRole('button', { name: /^save$/i });
    expect(saveBtn).toBeEnabled();
    fireEvent.click(saveBtn);

    expect(onSave).toHaveBeenCalledWith('return 42;');
    expect(testInlineCode).not.toHaveBeenCalled();
  });

  it('when code execution is ENABLED: no notice and Run test is enabled', async () => {
    getCapabilityPolicy.mockResolvedValue({ enabledCapabilities: ['code.execute'] });
    renderModal();

    await waitFor(() => expect(getCapabilityPolicy).toHaveBeenCalled());
    expect(screen.queryByText(/execution is disabled/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /run test/i })).toBeEnabled();
  });

  it('Save is disabled when there are no unsaved changes', async () => {
    getCapabilityPolicy.mockResolvedValue({ enabledCapabilities: [] });
    renderModal();
    await screen.findByText(/execution is disabled/i);
    expect(screen.getByRole('button', { name: /^save$/i })).toBeDisabled();
  });
});
