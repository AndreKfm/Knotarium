import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { SettingImporter } from './SettingImporter';
import type { ImportInstallResponse, ImportPreviewResponse, ImportProviderDescriptor } from '../types';

vi.mock('../utils/api', async () => {
  const actual = await vi.importActual<typeof import('../utils/api')>('../utils/api');
  return {
    ...actual,
    api: {
      listImportProviders: vi.fn(),
      previewImport: vi.fn(),
      installImport: vi.fn(),
      getExternalSystem: vi.fn(),
      bulkDeleteWorkflows: vi.fn(),
    },
  };
});

import { api } from '../utils/api';

const provider: ImportProviderDescriptor = {
  id: 'vendor-set',
  displayName: 'Vendor Setting',
  fileExtensions: ['.set'],
  supportsGranularity: true,
  supportsTargetStrategy: true,
  defaultGranularity: 'multiple',
  description: 'Import a native vendor setting file.',
};

const previewResponse: ImportPreviewResponse = {
  granularity: 'multiple',
  workflows: [
    { id: 'workflow:event-1', name: 'Door Cam', nodes: 3, edges: 2 },
    { id: 'workflow:event-2', name: 'Motion', nodes: 2, edges: 1 },
  ],
  report: [
    { scope: 'event-1', construct: 'OnStart[1] CrossSwitch', outcome: 'Mapped', reason: null },
    { scope: 'event-1', construct: 'StartBy', outcome: 'Partial', reason: 'Source binding not yet translated.' },
    { scope: 'MappingRules', construct: 'MappingRules (58)', outcome: 'Flagged', reason: 'Not mapped yet.' },
  ],
  servers: [
    { alias: 'ZW1SRV001', host: 'zw1srv001', user: 'sysadmin', enabled: true },
    { alias: 'ZW1SRV002', host: 'zw1srv002', user: 'sysadmin', enabled: true },
  ],
  provisioned: [
    { serverAlias: 'ZW1SRV001', action: 'Create', targetId: 'zw1srv001' },
    { serverAlias: 'ZW1SRV002', action: 'Create', targetId: 'zw1srv002' },
  ],
};

const installResponse: ImportInstallResponse = {
  granularity: 'single',
  installed: [{ value: 'workflow:vendor-setting', name: 'Vendor Setting', versionNumber: 1 }],
  report: previewResponse.report,
  servers: previewResponse.servers,
  provisioned: previewResponse.provisioned,
};

function upload() {
  const file = new File(['\x00'], 'plant.set', { type: 'application/octet-stream' });
  fireEvent.change(screen.getByLabelText('Upload setting file'), { target: { files: [file] } });
}

describe('SettingImporter', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.listImportProviders).mockResolvedValue([provider]);
    vi.mocked(api.previewImport).mockResolvedValue(previewResponse);
    vi.mocked(api.installImport).mockResolvedValue(installResponse);
    vi.mocked(api.bulkDeleteWorkflows).mockResolvedValue({ deleted: 1, ids: ['workflow:vendor-setting'] });
    vi.mocked(api.getExternalSystem).mockResolvedValue({ id: 'devices', name: 'External Devices', targets: [
      { id: 'local-a', name: 'Local A', host: 'h', port: 0, user: null, hasCredential: true, channels: [], events: [], actions: [], status: { targetId: 'local-a', connectivity: 'Offline', lastConnected: null, lastSignal: null, lastError: null, failedDispatches: 0 } },
    ] });
  });

  it('previews an uploaded file with the chosen granularity and shows the report', async () => {
    render(<SettingImporter />);
    await waitFor(() => expect(api.listImportProviders).toHaveBeenCalled());

    upload();
    // The granularity toggle is offered (Configure step) because the provider supports it.
    expect(await screen.findByText('Separate workflows', { exact: false })).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText('Preview import'));

    await waitFor(() => expect(api.previewImport).toHaveBeenCalledWith('vendor-set', expect.any(File), 'multiple', 'CreateOrReuse', {}));
    // Preview lands on the report (dry-run) with the coverage table + counts.
    expect(await screen.findByText('Coverage report')).toBeInTheDocument();
    expect(screen.getByText('1 mapped', { exact: false })).toBeInTheDocument();
    expect(screen.getByText('1 partial', { exact: false })).toBeInTheDocument();
    expect(screen.getByText('1 flagged', { exact: false })).toBeInTheDocument();
  });

  it('passes the single granularity when the combined option is selected, then installs', async () => {
    render(<SettingImporter />);
    await waitFor(() => expect(api.listImportProviders).toHaveBeenCalled());

    upload();
    fireEvent.click(screen.getByRole('radio', { name: /One combined workflow/i }));
    fireEvent.click(screen.getByLabelText('Import workflows'));

    await waitFor(() => expect(api.installImport).toHaveBeenCalledWith('vendor-set', expect.any(File), 'single', 'CreateOrReuse', {}));
    expect(await screen.findByText('Imported 1 workflow', { exact: false })).toBeInTheDocument();
    expect(screen.getByText('combined into one', { exact: false })).toBeInTheDocument();
  });

  it('offers the connection strategy after preview, and maps servers to existing targets on install', async () => {
    render(<SettingImporter />);
    await waitFor(() => expect(api.listImportProviders).toHaveBeenCalled());

    upload();

    // Auto-parse on upload surfaces the discovered servers + strategy choices in Configure.
    expect(await screen.findByText('Device connections', { exact: false })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('radio', { name: /Map to existing/i }));

    // Import is blocked until every server is mapped.
    expect(screen.getByLabelText('Import workflows')).toBeDisabled();

    // Map both servers to the one existing target, then install.
    const selects = await screen.findAllByRole('combobox');
    fireEvent.change(selects[0], { target: { value: 'local-a' } });
    fireEvent.change(selects[1], { target: { value: 'local-a' } });

    fireEvent.click(screen.getByLabelText('Import workflows'));
    await waitFor(() => expect(api.installImport).toHaveBeenCalledWith(
      'vendor-set', expect.any(File), 'multiple', 'MapToExisting',
      { ZW1SRV001: 'local-a', ZW1SRV002: 'local-a' },
    ));
  });

  it("preselects the provider's default granularity (single) and installs combined", async () => {
    vi.mocked(api.listImportProviders).mockResolvedValue([{ ...provider, defaultGranularity: 'single' }]);
    render(<SettingImporter />);
    await waitFor(() => expect(api.listImportProviders).toHaveBeenCalled());

    upload();
    // Import straight away — no granularity toggle — should use the provider default 'single'.
    fireEvent.click(await screen.findByLabelText('Import workflows'));
    await waitFor(() => expect(api.installImport).toHaveBeenCalledWith('vendor-set', expect.any(File), 'single', 'CreateOrReuse', {}));
  });

  it('undoes an import by bulk-deleting the created workflows', async () => {
    render(<SettingImporter />);
    await waitFor(() => expect(api.listImportProviders).toHaveBeenCalled());

    upload();
    fireEvent.click(await screen.findByLabelText('Import workflows'));
    await screen.findByText('Imported 1 workflow', { exact: false });

    fireEvent.click(screen.getByLabelText('Undo import'));
    await waitFor(() => expect(api.bulkDeleteWorkflows).toHaveBeenCalledWith(['workflow:vendor-setting']));
    expect(await screen.findByText(/Undone/i)).toBeInTheDocument();
  });

  it('shows an empty state when no providers are registered', async () => {
    vi.mocked(api.listImportProviders).mockResolvedValue([]);
    render(<SettingImporter />);
    expect(await screen.findByText('No import providers are registered', { exact: false })).toBeInTheDocument();
  });
});
