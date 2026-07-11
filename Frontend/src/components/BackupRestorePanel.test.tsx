import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { BackupRestorePanel } from './BackupRestorePanel';
import { ApiError } from '../utils/api';
import type { BackupManifest } from '../types';

vi.mock('../utils/api', async () => {
  const actual = await vi.importActual<typeof import('../utils/api')>('../utils/api');
  return {
    ...actual,
    api: {
      createBackup: vi.fn(),
      inspectBackup: vi.fn(),
      restoreBackup: vi.fn(),
    },
  };
});

import { api } from '../utils/api';

const manifest: BackupManifest = {
  formatVersion: 1,
  engineVersion: '1.0.0',
  createdAtUtc: '2026-06-18T10:00:00.0000000Z',
  databaseProvider: 'SQLite',
  includesRunHistory: false,
  counts: { 'credentials.json': 2, 'workflow-definitions.json': 3, 'workflow-versions.json': 8, workflows: 1 },
};

function uploadFile() {
  const file = new File(['x'], 'snap.kgbak', { type: 'application/octet-stream' });
  fireEvent.change(screen.getByLabelText('Backup file') as HTMLInputElement, { target: { files: [file] } });
}

// Walk a disarmed restore up to the point a verified manifest is on screen.
async function inspectAsDisarmed() {
  uploadFile();
  fireEvent.change(screen.getByLabelText('Restore passphrase'), { target: { value: 'pw' } });
  fireEvent.click(screen.getByRole('button', { name: 'Inspect backup' }));
  await screen.findByText('Backup contents');
}

describe('BackupRestorePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    globalThis.URL.createObjectURL = vi.fn(() => 'blob:mock');
    globalThis.URL.revokeObjectURL = vi.fn();
  });

  it('downloads a backup when the passphrases match', async () => {
    vi.mocked(api.createBackup).mockResolvedValue({
      blob: new Blob(['x']),
      filename: 'knotarium-backup-20260618.kgbak',
    });
    render(<BackupRestorePanel armed={false} />);

    fireEvent.change(screen.getByLabelText('Backup passphrase'), { target: { value: 'secret-12' } });
    fireEvent.change(screen.getByLabelText('Confirm backup passphrase'), { target: { value: 'secret-12' } });
    fireEvent.click(screen.getByRole('button', { name: 'Download backup' }));

    await waitFor(() => expect(vi.mocked(api.createBackup)).toHaveBeenCalledWith({ passphrase: 'secret-12' }));
    expect(await screen.findByRole('status')).toHaveTextContent(/Downloaded/i);
    // Last-backup row appears after a successful download.
    expect(screen.getByText(/Last backup downloaded/i)).toBeInTheDocument();
  });

  it('backs up with the server key — no passphrase needed', async () => {
    vi.mocked(api.createBackup).mockResolvedValue({
      blob: new Blob(['x']),
      filename: 'knotarium-backup-serverkey.kgbak',
    });
    render(<BackupRestorePanel armed={false} />);

    // Switch to "This server's key": the passphrase fields disappear and Download enables immediately.
    fireEvent.click(screen.getByRole('radio', { name: "This server's key" }));
    expect(screen.queryByLabelText('Backup passphrase')).not.toBeInTheDocument();

    const btn = screen.getByRole('button', { name: 'Download backup' });
    expect(btn).toBeEnabled();
    fireEvent.click(btn);

    await waitFor(() => expect(vi.mocked(api.createBackup)).toHaveBeenCalledWith({ useServerKey: true }));
    expect(await screen.findByRole('status')).toHaveTextContent(/restorable on this server only/i);
  });

  it('keeps the download button disabled until passphrases match and are long enough', () => {
    render(<BackupRestorePanel armed={false} />);
    const btn = screen.getByRole('button', { name: 'Download backup' });

    // Too short.
    fireEvent.change(screen.getByLabelText('Backup passphrase'), { target: { value: 'short' } });
    fireEvent.change(screen.getByLabelText('Confirm backup passphrase'), { target: { value: 'short' } });
    expect(btn).toBeDisabled();

    // Long enough but mismatched.
    fireEvent.change(screen.getByLabelText('Backup passphrase'), { target: { value: 'long-enough-a' } });
    fireEvent.change(screen.getByLabelText('Confirm backup passphrase'), { target: { value: 'long-enough-b' } });
    expect(btn).toBeDisabled();
    expect(screen.getByText(/don't match/i)).toBeInTheDocument();

    fireEvent.click(btn);
    expect(vi.mocked(api.createBackup)).not.toHaveBeenCalled();
  });

  it('inspects a backup and shows the verified contents preview', async () => {
    vi.mocked(api.inspectBackup).mockResolvedValue(manifest);
    render(<BackupRestorePanel armed={false} />);

    await inspectAsDisarmed();

    expect(screen.getByText(/Decrypted & verified/i)).toBeInTheDocument();
    expect(screen.getByText(/format v1/i)).toBeInTheDocument();
    // The restore button stays gated until the user types the confirm word.
    expect(screen.getByRole('button', { name: 'Restore backup' })).toBeDisabled();
  });

  it('gates restore behind the type-to-confirm word when disarmed', async () => {
    vi.mocked(api.inspectBackup).mockResolvedValue(manifest);
    vi.mocked(api.restoreBackup).mockResolvedValue({
      preRestoreBackupPath: '/tmp/pre-restore.kgbak',
      manifest,
      restored: { 'credentials.json': 2, 'workflow-definitions.json': 3 },
    });
    render(<BackupRestorePanel armed={false} />);

    await inspectAsDisarmed();

    const restoreBtn = screen.getByRole('button', { name: 'Restore backup' });
    expect(restoreBtn).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Type RESTORE to confirm'), { target: { value: 'RESTORE' } });
    expect(restoreBtn).toBeEnabled();

    fireEvent.click(restoreBtn);
    await waitFor(() => expect(vi.mocked(api.restoreBackup)).toHaveBeenCalledWith(expect.any(File), 'pw', true));
    expect(await screen.findByText(/Instance restored from backup/i)).toBeInTheDocument();
  });

  it('blocks restore while the runtime is armed and offers a disarm action', async () => {
    vi.mocked(api.inspectBackup).mockResolvedValue(manifest);
    const onDisarm = vi.fn();
    render(<BackupRestorePanel armed onDisarm={onDisarm} />);

    uploadFile();
    fireEvent.change(screen.getByLabelText('Restore passphrase'), { target: { value: 'pw' } });
    fireEvent.click(screen.getByRole('button', { name: 'Inspect backup' }));
    await screen.findByText('Backup contents');

    expect(screen.getByText(/Disarm the runtime first/i)).toBeInTheDocument();
    expect(screen.getByLabelText('Type RESTORE to confirm')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Restore backup' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Disarm the runtime' }));
    expect(onDisarm).toHaveBeenCalled();
  });

  it('surfaces an armed-runtime block returned by the server (412)', async () => {
    vi.mocked(api.inspectBackup).mockResolvedValue(manifest);
    vi.mocked(api.restoreBackup).mockRejectedValue(new ApiError('blocked', 412, { reason: 'RuntimeArmed' }));
    render(<BackupRestorePanel armed={false} />);

    await inspectAsDisarmed();
    fireEvent.change(screen.getByLabelText('Type RESTORE to confirm'), { target: { value: 'RESTORE' } });
    fireEvent.click(screen.getByRole('button', { name: 'Restore backup' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/disarm the runtime/i);
  });

  it('detects a server-key backup from the file header and skips the passphrase', async () => {
    vi.mocked(api.inspectBackup).mockResolvedValue({ ...manifest, keySource: 'ServerKey' });
    render(<BackupRestorePanel armed={false} />);

    // A .kgbak whose cleartext header declares the server-key source: magic "KGBK" | version 1 | source 2.
    const header = new Uint8Array([0x4b, 0x47, 0x42, 0x4b, 1, 2, 0, 0, 0, 0]);
    const file = new File([header], 'sk.kgbak', { type: 'application/octet-stream' });
    fireEvent.change(screen.getByLabelText('Backup file') as HTMLInputElement, { target: { files: [file] } });

    // Detected locally → no passphrase asked, and Inspect is enabled without one.
    expect(await screen.findByText(/no passphrase needed/i)).toBeInTheDocument();
    expect(screen.queryByLabelText('Restore passphrase')).not.toBeInTheDocument();
    const inspectBtn = screen.getByRole('button', { name: 'Inspect backup' });
    expect(inspectBtn).toBeEnabled();

    fireEvent.click(inspectBtn);
    await waitFor(() => expect(vi.mocked(api.inspectBackup)).toHaveBeenCalled());
    expect(await screen.findByText(/Backup contents/i)).toBeInTheDocument();
  });

  it('surfaces the server message on a bad-passphrase inspect (400)', async () => {
    vi.mocked(api.inspectBackup).mockRejectedValue(
      new ApiError('Incorrect passphrase, or the backup archive is corrupt.', 400, { message: 'Incorrect passphrase, or the backup archive is corrupt.' }),
    );
    render(<BackupRestorePanel armed={false} />);

    uploadFile();
    fireEvent.change(screen.getByLabelText('Restore passphrase'), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: 'Inspect backup' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/Incorrect passphrase/i);
  });
});
