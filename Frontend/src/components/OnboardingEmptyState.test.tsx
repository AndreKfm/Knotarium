import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { OnboardingEmptyState } from './OnboardingEmptyState';
import { api } from '../utils/api';

vi.mock('../utils/api', () => ({
  api: {
    listGalleryTemplates: vi.fn(),
    installGalleryTemplate: vi.fn(),
  },
}));

const sample = {
  templateId: 'tpl_starter-hello-world',
  manifest: { name: 'Hello World', description: 'A minimal starter.' },
};

describe('OnboardingEmptyState', () => {
  beforeEach(() => {
    vi.mocked(api.listGalleryTemplates).mockReset();
    vi.mocked(api.installGalleryTemplate).mockReset();
  });

  it('offers a blank-create path', async () => {
    vi.mocked(api.listGalleryTemplates).mockResolvedValue([]);
    const onCreateBlank = vi.fn();
    render(<OnboardingEmptyState onCreateBlank={onCreateBlank} onOpenWorkflow={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: /create your first workflow/i }));
    expect(onCreateBlank).toHaveBeenCalledOnce();
  });

  it('lists gallery samples and installs + opens one', async () => {
    vi.mocked(api.listGalleryTemplates).mockResolvedValue([sample as never]);
    vi.mocked(api.installGalleryTemplate).mockResolvedValue({ workflowId: 'wf-123' } as never);
    const onOpenWorkflow = vi.fn();
    render(<OnboardingEmptyState onCreateBlank={vi.fn()} onOpenWorkflow={onOpenWorkflow} />);

    // Sample card appears once the gallery loads.
    await screen.findByText('Hello World');

    fireEvent.click(screen.getByRole('button', { name: /install & open/i }));

    await waitFor(() => expect(api.installGalleryTemplate).toHaveBeenCalledWith('tpl_starter-hello-world'));
    await waitFor(() => expect(onOpenWorkflow).toHaveBeenCalledWith('wf-123'));
  });

  it('surfaces an install error instead of navigating', async () => {
    vi.mocked(api.listGalleryTemplates).mockResolvedValue([sample as never]);
    vi.mocked(api.installGalleryTemplate).mockRejectedValue(new Error('boom'));
    const onOpenWorkflow = vi.fn();
    render(<OnboardingEmptyState onCreateBlank={vi.fn()} onOpenWorkflow={onOpenWorkflow} />);

    await screen.findByText('Hello World');
    fireEvent.click(screen.getByRole('button', { name: /install & open/i }));

    await screen.findByText('boom');
    expect(onOpenWorkflow).not.toHaveBeenCalled();
  });
});
