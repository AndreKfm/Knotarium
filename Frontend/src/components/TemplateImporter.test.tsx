// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { TemplateImporter } from './TemplateImporter';
import type { TemplateInspectResponse, TemplateInstallResponse } from '../types';

vi.mock('../utils/api', async () => {
  const actual = await vi.importActual<typeof import('../utils/api')>('../utils/api');
  return {
    ...actual,
    api: {
      listCredentials: vi.fn(),
      getWorkflows: vi.fn(),
      inspectTemplate: vi.fn(),
      installTemplate: vi.fn(),
      saveArchiveToLibrary: vi.fn(),
      getGroups: vi.fn(),
      getWorkflow: vi.fn(),
      updateWorkflow: vi.fn(),
      saveGroups: vi.fn(),
    },
  };
});

import { api } from '../utils/api';

const inspectResponse: TemplateInspectResponse = {
  manifest: {
    templateId: 'tpl_wf', templateVersion: '1.0.0', schemaVersion: 1, name: 'Weather Flow',
    author: 'me', description: 'Checks the weather', tags: ['weather'], category: 'demo',
    minEngineVersion: null, createdAtUtc: '2026-01-01T00:00:00Z', sourceWorkflowName: 'Weather Flow',
    workflowChecksum: 'abc', credentialSlots: [{ slot: 'weather-api', displayName: 'Weather API', description: null, requiredCredentialType: null }], parameters: [],
  },
  credentialSlots: [{ slot: 'weather-api', displayName: 'Weather API', description: null, requiredCredentialType: null }],
  compatibility: { supported: true, warnings: [] },
  privilegedNodes: [],
};

const installResponse: TemplateInstallResponse = {
  workflowId: 'new-id', versionNumber: 1, workflowName: 'Weather Flow',
  reboundSlots: ['weather-api'], openSlots: [], bindingErrors: [],
  configurationRequired: false, runnable: true, diagnostics: [],
};

function upload() {
  const file = new File(['zip'], 'weather.kgtpl', { type: 'application/zip' });
  fireEvent.change(screen.getByLabelText('Upload template file'), { target: { files: [file] } });
}

describe('TemplateImporter', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.listCredentials).mockResolvedValue([{ id: 'cred-1', name: 'Prod Key' }]);
    vi.mocked(api.getWorkflows).mockResolvedValue([]);
    vi.mocked(api.inspectTemplate).mockResolvedValue(inspectResponse);
    vi.mocked(api.installTemplate).mockResolvedValue(installResponse);
    // ensureImportedGroup is best-effort; stub its calls so it completes quietly.
    vi.mocked(api.getGroups).mockResolvedValue({ container: { version: 1, groups: [] }, etag: 'e' });
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'new-id' }, name: 'Weather Flow', nodes: [], edges: [] });
    vi.mocked(api.updateWorkflow).mockResolvedValue({ id: { value: 'new-id' }, name: 'Weather Flow', nodes: [], edges: [] });
    vi.mocked(api.saveGroups).mockResolvedValue('e2');
  });

  it('inspects an uploaded template and shows its manifest and slots', async () => {
    render(<TemplateImporter />);
    upload();
    await waitFor(() => expect(api.inspectTemplate).toHaveBeenCalled());
    expect(await screen.findByText('Weather Flow')).toBeInTheDocument();
    expect(screen.getByLabelText('Bind credential for slot weather-api')).toBeInTheDocument();
  });

  it('binds a slot and installs, then shows the result', async () => {
    render(<TemplateImporter />);
    upload();
    await screen.findByText('Weather Flow');

    fireEvent.change(screen.getByLabelText('Bind credential for slot weather-api'), { target: { value: 'cred-1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create workflow' }));

    await waitFor(() =>
      expect(api.installTemplate).toHaveBeenCalledWith(expect.any(File), { 'weather-api': 'cred-1' }, 'Weather Flow', {}));
    expect(await screen.findByRole('status')).toHaveTextContent(/Imported “Weather Flow”/);
  });

  it('saves an uploaded template to the library without installing', async () => {
    vi.mocked(api.saveArchiveToLibrary).mockResolvedValue({ templateId: 'tpl_wf', manifest: inspectResponse.manifest });
    render(<TemplateImporter />);
    upload();
    await screen.findByText('Weather Flow');

    fireEvent.click(screen.getByRole('button', { name: 'Save to library' }));

    await waitFor(() => expect(api.saveArchiveToLibrary).toHaveBeenCalledWith(expect.any(File)));
    expect(api.installTemplate).not.toHaveBeenCalled();
    expect(await screen.findByText(/Saved “Weather Flow” to your library/)).toBeInTheDocument();
  });

  it('warns when the template is not supported on this engine', async () => {
    vi.mocked(api.inspectTemplate).mockResolvedValue({
      ...inspectResponse,
      compatibility: { supported: false, warnings: ["Node 'x' has an invalid type"] },
    });
    render(<TemplateImporter />);
    upload();
    expect(await screen.findByText(/May not run on this engine/)).toBeInTheDocument();
  });
});
