import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { BundleInstaller } from './BundleInstaller';
import type { BundleInstallResponse } from '../types';

vi.mock('../utils/api', async () => {
  const actual = await vi.importActual<typeof import('../utils/api')>('../utils/api');
  return {
    ...actual,
    api: {
      listCredentials: vi.fn().mockResolvedValue([{ id: 'cred-1', name: 'SMTP' }]),
      installBundle: vi.fn(),
    },
  };
});

import { api } from '../utils/api';

const pkg = (over: Partial<BundleInstallResponse['verification'][number]> = {}) => ({
  packageId: 'acme.http',
  expectedSha256: 'abc',
  actualSha256: 'abc',
  hashMatches: true,
  signatureVerified: true,
  signatureStatus: 2,
  trustLevel: 2,
  status: 4,
  installable: true,
  ...over,
});

const baseResponse = (over: Partial<BundleInstallResponse> = {}): BundleInstallResponse => ({
  installed: false,
  installedPackages: [],
  skippedPackages: [],
  importedWorkflows: [],
  requiredCredentialSlots: [],
  reboundCredentialSlots: [],
  unboundCredentialSlots: [],
  conflictingPackages: [],
  verification: [],
  blocking: [],
  privilegedNodes: [],
  privilegedAcknowledgementRequired: false,
  ...over,
});

function uploadFile() {
  const file = new File(['zip-bytes'], 'demo.kgbundle', { type: 'application/zip' });
  const input = screen.getByLabelText('Upload bundle file') as HTMLInputElement;
  fireEvent.change(input, { target: { files: [file] } });
  return file;
}

describe('BundleInstaller', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.listCredentials).mockResolvedValue([{ id: 'cred-1', name: 'SMTP' }]);
  });

  it('install_button_disabled_until_a_file_is_chosen', () => {
    render(<BundleInstaller />);
    expect(screen.getByRole('button', { name: 'Install bundle' })).toBeDisabled();
    uploadFile();
    expect(screen.getByRole('button', { name: 'Install bundle' })).toBeEnabled();
  });

  it('200_shows_install_success_summary', async () => {
    vi.mocked(api.installBundle).mockResolvedValueOnce({
      status: 200,
      result: baseResponse({
        installed: true,
        installedPackages: ['acme.http@1.0.0'],
        importedWorkflows: [{ key: 'wf', workflowId: 'id', versionNumber: 1 }],
        verification: [pkg()],
      }),
    });
    render(<BundleInstaller />);
    uploadFile();
    fireEvent.click(screen.getByRole('button', { name: 'Install bundle' }));
    expect(await screen.findByText(/Bundle installed/)).toBeInTheDocument();
    expect(screen.getByText(/acme\.http@1\.0\.0/)).toBeInTheDocument();
  });

  it('422_shows_verification_rejected_and_keeps_distinct_axes', async () => {
    vi.mocked(api.installBundle).mockResolvedValueOnce({
      status: 422,
      result: baseResponse({
        verification: [pkg({ hashMatches: false, signatureStatus: 0, trustLevel: 0, status: 1, installable: false })],
        blocking: [pkg({ hashMatches: false, installable: false })],
      }),
    });
    render(<BundleInstaller />);
    uploadFile();
    fireEvent.click(screen.getByRole('button', { name: 'Install bundle' }));
    expect(await screen.findByText(/Verification rejected/)).toBeInTheDocument();
    // Hash tampering surfaces distinctly — not flattened into a generic "not ok".
    expect(screen.getByText('MISMATCH')).toBeInTheDocument();
    expect(screen.getByText('Tampered (hash mismatch)')).toBeInTheDocument();
  });

  it('409_shows_version_conflict_with_offending_packages', async () => {
    vi.mocked(api.installBundle).mockResolvedValueOnce({
      status: 409,
      result: baseResponse({ conflictingPackages: ['acme.http@1.0.0'] }),
    });
    render(<BundleInstaller />);
    uploadFile();
    fireEvent.click(screen.getByRole('button', { name: 'Install bundle' }));
    expect(await screen.findByText(/Version conflict/)).toBeInTheDocument();
    expect(screen.getByText('acme.http@1.0.0')).toBeInTheDocument();
  });

  it('credential_slots_render_and_feed_bindings', async () => {
    vi.mocked(api.installBundle)
      .mockResolvedValueOnce({
        status: 422,
        result: baseResponse({
          requiredCredentialSlots: [{ slot: 'smtp', type: 'smtp', displayName: 'Mail server', description: null, checklist: [] }],
          verification: [pkg({ trustLevel: 1, signatureStatus: 0, status: 3, installable: false })],
          blocking: [pkg({ installable: false })],
        }),
      })
      .mockResolvedValueOnce({ status: 200, result: baseResponse({ installed: true }) });

    render(<BundleInstaller />);
    uploadFile();
    fireEvent.click(screen.getByRole('button', { name: 'Install bundle' }));

    const select = await screen.findByLabelText('Bind credential for slot smtp');
    await waitFor(() => expect(screen.getByRole('option', { name: /SMTP/ })).toBeInTheDocument());
    fireEvent.change(select, { target: { value: 'cred-1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Install bundle' }));

    await waitFor(() => expect(vi.mocked(api.installBundle).mock.calls[1][1]).toEqual({
      allowProvisional: false,
      credentialBindings: { smtp: 'cred-1' },
      acknowledgePrivileged: false,
    }));
  });

  it('api_error_is_surfaced', async () => {
    vi.mocked(api.installBundle).mockRejectedValueOnce(new Error('Malformed archive'));
    render(<BundleInstaller />);
    uploadFile();
    fireEvent.click(screen.getByRole('button', { name: 'Install bundle' }));
    expect(await screen.findByText(/Malformed archive/)).toBeInTheDocument();
  });
});
