// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useState } from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ModelCombo } from './ModelCombo';

vi.mock('../../utils/api', () => ({
  api: {
    getAiProviderConfig: vi.fn().mockResolvedValue({ vendor: 'openai', credentialRef: 'c1', baseUrl: null, apiVersion: null }),
    getAiProviderModels: vi.fn().mockResolvedValue({ models: ['gpt-5.6', 'gpt-5.5'] }),
  },
}));

function Harness({ initial }: { initial: string }) {
  const [value, setValue] = useState(initial);
  return (
    <>
      <ModelCombo vendor="openai" credentialRef="c1" value={value} onChange={setValue} />
      <span data-testid="value">{value}</span>
    </>
  );
}

describe('ModelCombo', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows all curated suggestions even when the typed value matches none (the bug fix)', () => {
    render(<Harness initial="gpt-5.5" />); // a custom, unlisted value
    fireEvent.click(screen.getByLabelText('Show model suggestions'));

    // The full curated list is still offered despite the unlisted current value.
    expect(screen.getByRole('option', { name: 'gpt-5.1' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'gpt-5' })).toBeInTheDocument();
  });

  it('picks a suggestion into the value', () => {
    render(<Harness initial="gpt-5.5" />);
    fireEvent.click(screen.getByLabelText('Show model suggestions'));
    fireEvent.mouseDown(screen.getByRole('option', { name: 'gpt-5.1' }));
    expect(screen.getByTestId('value').textContent).toBe('gpt-5.1');
  });

  it('keeps the typed free-text value (custom models are allowed)', () => {
    render(<Harness initial="" />);
    const input = screen.getByPlaceholderText('Model…');
    fireEvent.change(input, { target: { value: 'my-fine-tune-42' } });
    expect(screen.getByTestId('value').textContent).toBe('my-fine-tune-42');
  });

  it('filters to matching suggestions when the query matches some', () => {
    render(<Harness initial="mini" />);
    fireEvent.click(screen.getByLabelText('Show model suggestions'));
    // 'gpt-5-mini' and 'o4-mini' match "mini"; 'gpt-5' (exact) does not contain "mini".
    expect(screen.getByRole('option', { name: 'gpt-5-mini' })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'gpt-5' })).not.toBeInTheDocument();
  });
});
