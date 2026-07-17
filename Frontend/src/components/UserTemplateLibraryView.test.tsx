// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { UserTemplateLibraryView } from './UserTemplateLibraryView';
import type { GalleryTemplate, TemplateInstallResponse } from '../types';

vi.mock('../utils/api', async () => {
  const actual = await vi.importActual<typeof import('../utils/api')>('../utils/api');
  return {
    ...actual,
    api: {
      listLibraryTemplates: vi.fn(),
      listCredentials: vi.fn(),
      installLibraryTemplate: vi.fn(),
      deleteLibraryTemplate: vi.fn(),
      getLibraryTemplatePayload: vi.fn(),
      getGroups: vi.fn(),
      getWorkflow: vi.fn(),
      updateWorkflow: vi.fn(),
      saveGroups: vi.fn(),
    },
  };
});

import { api } from '../utils/api';

const saved: GalleryTemplate[] = [
  {
    templateId: 'tpl_wf-1',
    manifest: {
      templateId: 'tpl_wf-1', templateVersion: '1.0.0', schemaVersion: 1, name: 'My Saved Flow',
      author: 'me', description: 'Saved earlier.', tags: [], category: 'starter',
      minEngineVersion: null, createdAtUtc: '', sourceWorkflowName: 'My Saved Flow',
      workflowChecksum: 'abc', credentialSlots: [], parameters: [],
    },
  },
];

const installResponse: TemplateInstallResponse = {
  workflowId: 'new-1', versionNumber: 1, workflowName: 'My Saved Flow', reboundSlots: [], openSlots: [],
  bindingErrors: [], configurationRequired: false, runnable: true, diagnostics: [],
};

describe('UserTemplateLibraryView', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.listLibraryTemplates).mockResolvedValue(saved);
    vi.mocked(api.listCredentials).mockResolvedValue([]);
    vi.mocked(api.installLibraryTemplate).mockResolvedValue(installResponse);
    vi.mocked(api.deleteLibraryTemplate).mockResolvedValue(undefined);
    vi.mocked(api.getGroups).mockResolvedValue({ container: { groups: [], assignments: {} }, etag: '' } as never);
  });

  it('lists, then installs a saved template', async () => {
    render(<UserTemplateLibraryView />);
    await screen.findByText('My Saved Flow');

    fireEvent.click(screen.getByRole('button', { name: 'Use My Saved Flow' }));        // expand
    fireEvent.click(screen.getByRole('button', { name: 'Create workflow from My Saved Flow' }));

    await waitFor(() => expect(api.installLibraryTemplate).toHaveBeenCalledWith('tpl_wf-1', {}, 'My Saved Flow', {}));
    expect(await screen.findByRole('status')).toHaveTextContent(/Imported “My Saved Flow”/);
  });

  it('deletes a saved template', async () => {
    render(<UserTemplateLibraryView />);
    await screen.findByText('My Saved Flow');

    fireEvent.click(screen.getByRole('button', { name: 'Delete My Saved Flow' }));

    await waitFor(() => expect(api.deleteLibraryTemplate).toHaveBeenCalledWith('tpl_wf-1'));
    await waitFor(() => expect(screen.queryByText('My Saved Flow')).not.toBeInTheDocument());
  });
});
