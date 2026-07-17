// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AiGenerateModal } from './AiGenerateModal';
import { api } from '../utils/api';
import type { WorkflowDefinition } from '../types';

vi.mock('../utils/api', () => ({
  api: { generateWorkflow: vi.fn(), getGenerationJob: vi.fn() },
}));

const workflow: WorkflowDefinition = {
  id: { value: 'w1' },
  name: 'Generated',
  nodes: [{ id: { value: 't' }, type: 'manualTrigger', properties: {} }],
  edges: [],
};

describe('AiGenerateModal', () => {
  beforeEach(() => vi.clearAllMocks());

  it('disables Generate until an intent is entered', () => {
    render(<AiGenerateModal open onClose={() => {}} onGenerated={() => {}} />);
    const btn = screen.getByRole('button', { name: 'Generate' });
    expect(btn).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Workflow description'), { target: { value: 'do a thing' } });
    expect(btn).not.toBeDisabled();
  });

  it('switches to refine mode and sends the current workflow when one is provided', async () => {
    (api.generateWorkflow as ReturnType<typeof vi.fn>).mockResolvedValue({ jobId: 'j1' });
    (api.getGenerationJob as ReturnType<typeof vi.fn>).mockResolvedValue({
      jobId: 'j1', status: 'Succeeded', workflow, openSlots: [], diagnostics: [], attempts: 1, error: null,
    });
    render(<AiGenerateModal open onClose={() => {}} onGenerated={() => {}} currentWorkflow={workflow} />);

    // Refine-mode copy + entry point.
    expect(screen.getByText('Refine workflow with AI')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow change description'), { target: { value: 'add a log node' } });
    fireEvent.click(screen.getByRole('button', { name: 'Refine' }));

    await waitFor(() => expect(api.generateWorkflow).toHaveBeenCalledWith('add a log node', workflow));
  });

  it('calls onGenerated with the workflow and open slots when the job succeeds', async () => {
    (api.generateWorkflow as ReturnType<typeof vi.fn>).mockResolvedValue({ jobId: 'j1' });
    (api.getGenerationJob as ReturnType<typeof vi.fn>).mockResolvedValue({
      jobId: 'j1', status: 'Succeeded', workflow, openSlots: ['weather-api'], diagnostics: [], attempts: 1, error: null,
    });
    const onGenerated = vi.fn();
    render(<AiGenerateModal open onClose={() => {}} onGenerated={onGenerated} />);

    fireEvent.change(screen.getByLabelText('Workflow description'), { target: { value: 'do a thing' } });
    fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

    await waitFor(() => expect(onGenerated).toHaveBeenCalledWith(workflow, ['weather-api']), { timeout: 3000 });
  });

  it('shows diagnostics when the job fails', async () => {
    (api.generateWorkflow as ReturnType<typeof vi.fn>).mockResolvedValue({ jobId: 'j1' });
    (api.getGenerationJob as ReturnType<typeof vi.fn>).mockResolvedValue({
      jobId: 'j1', status: 'Failed', workflow: null, openSlots: [], diagnostics: ['ERR_INVALID_NODE_TYPE: bad'], attempts: 3, error: null,
    });
    render(<AiGenerateModal open onClose={() => {}} onGenerated={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow description'), { target: { value: 'do a thing' } });
    fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

    await waitFor(() => expect(screen.getByText(/ERR_INVALID_NODE_TYPE/)).toBeInTheDocument(), { timeout: 3000 });
  });
});
