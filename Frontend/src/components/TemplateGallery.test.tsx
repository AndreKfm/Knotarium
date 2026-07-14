import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { TemplateGallery } from './TemplateGallery';
import type { GalleryTemplate, TemplateInstallResponse } from '../types';

vi.mock('../utils/api', async () => {
  const actual = await vi.importActual<typeof import('../utils/api')>('../utils/api');
  return {
    ...actual,
    api: {
      listGalleryTemplates: vi.fn(),
      listCredentials: vi.fn(),
      installGalleryTemplate: vi.fn(),
      getGroups: vi.fn(),
      getWorkflow: vi.fn(),
      updateWorkflow: vi.fn(),
      saveGroups: vi.fn(),
    },
  };
});

import { api } from '../utils/api';

const gallery: GalleryTemplate[] = [
  {
    templateId: 'tpl_starter-hello-world',
    manifest: {
      templateId: 'tpl_starter-hello-world', templateVersion: '1.0.0', schemaVersion: 1, name: 'Hello World',
      author: 'Knotarium', description: 'A minimal starter.', tags: ['starter'], category: 'starter',
      minEngineVersion: null, createdAtUtc: '2026-01-01T00:00:00Z', sourceWorkflowName: 'Hello World',
      workflowChecksum: 'abc', credentialSlots: [], parameters: [],
    },
  },
];

const installResponse: TemplateInstallResponse = {
  workflowId: 'new-id', versionNumber: 1, workflowName: 'Hello World',
  reboundSlots: [], openSlots: [], bindingErrors: [], configurationRequired: false, runnable: true, diagnostics: [],
};

describe('TemplateGallery', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.listGalleryTemplates).mockResolvedValue(gallery);
    vi.mocked(api.listCredentials).mockResolvedValue([]);
    vi.mocked(api.installGalleryTemplate).mockResolvedValue(installResponse);
    vi.mocked(api.getGroups).mockResolvedValue({ container: { version: 1, groups: [] }, etag: 'e' });
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'new-id' }, name: 'Hello World', nodes: [], edges: [] });
    vi.mocked(api.updateWorkflow).mockResolvedValue({ id: { value: 'new-id' }, name: 'Hello World', nodes: [], edges: [] });
    vi.mocked(api.saveGroups).mockResolvedValue('e2');
  });

  it('lists the built-in templates', async () => {
    render(<TemplateGallery />);
    expect(await screen.findByText('Hello World')).toBeInTheDocument();
    expect(screen.getByText('A minimal starter.')).toBeInTheDocument();
  });

  it('uses a template: expand → name → create', async () => {
    render(<TemplateGallery />);
    await screen.findByText('Hello World');
    // First click expands the config (name field), does not install yet.
    fireEvent.click(screen.getByRole('button', { name: 'Use Hello World' }));
    expect(api.installGalleryTemplate).not.toHaveBeenCalled();
    expect(screen.getByLabelText('New workflow name for Hello World')).toBeInTheDocument();
    // Confirm creates the workflow with the prefilled name.
    fireEvent.click(screen.getByRole('button', { name: 'Create workflow from Hello World' }));
    await waitFor(() => expect(api.installGalleryTemplate).toHaveBeenCalledWith('tpl_starter-hello-world', {}, 'Hello World', {}));
    expect(await screen.findByRole('status')).toHaveTextContent(/Imported “Hello World”/);
  });

  it('opens the new workflow in the editor when a navigation handler is provided', async () => {
    const onOpenWorkflow = vi.fn();
    render(<TemplateGallery onOpenWorkflow={onOpenWorkflow} />);
    await screen.findByText('Hello World');
    fireEvent.click(screen.getByRole('button', { name: 'Use Hello World' }));
    fireEvent.click(screen.getByRole('button', { name: 'Create workflow from Hello World' }));
    await waitFor(() => expect(onOpenWorkflow).toHaveBeenCalledWith('new-id'));
  });
});
