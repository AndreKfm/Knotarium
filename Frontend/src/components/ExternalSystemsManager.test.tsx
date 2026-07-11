import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { ExternalSystemsManager } from './ExternalSystemsManager';
import { api } from '../utils/api';
import type { ProviderDescriptor, ExternalSystemInfo, ExternalTargetInfo } from '../types';

vi.mock('../utils/api', () => ({
  api: {
    getExternalSystemsDescriptor: vi.fn(),
    getExternalSystem: vi.fn(),
    renameExternalSystem: vi.fn(),
    upsertExternalTarget: vi.fn(),
    deleteExternalTarget: vi.fn(),
    syncExternalTarget: vi.fn(),
    testExternalTarget: vi.fn(),
    setExternalSystemOption: vi.fn(),
  },
}));

const descriptor: ProviderDescriptor = {
  providerId: 'device',
  displayName: 'Device Workflow',
  systemNoun: 'system',
  targetNoun: 'Device server',
  channelNoun: 'camera',
  supportsSync: true,
  supportsTestConnection: true,
  requiresCredentials: true,
};

const target: ExternalTargetInfo = {
  id: 'device-01',
  name: 'Device 01 (Front Building)',
  host: '10.0.0.11',
  port: 0,
  user: 'sysadmin',
  hasCredential: true,
  channels: [{ channelId: '1', displayName: 'Entrance', globalCameraNumber: 101 }],
  events: [{ id: 'VehicleRecognised', displayName: 'Vehicle recognised' }],
  actions: [{ id: 'StartRecording', displayName: 'Start recording' }],
  status: { targetId: 'device-01', connectivity: 'Online', failedDispatches: 0 },
};

const system: ExternalSystemInfo = {
  id: 'site-1',
  name: 'Site 1',
  targets: [target],
  options: [
    { key: 'suppressSelfEcho', label: 'Suppress self-echo', value: true, description: 'Drop reflected outbound actions.' },
  ],
  diagnostics: {
    metrics: [{ key: 'suppressedSelfEchoes', label: 'Self-echoes auto-filtered', value: '3' }],
    recentActivity: [
      { timestamp: '2026-07-05T10:00:00Z', kind: 'self-echo-filtered', summary: "Filtered Action 'StartRecording' on 'device-01'", detail: 'correlationKey: run-42' },
    ],
  },
};

describe('ExternalSystemsManager', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getExternalSystemsDescriptor).mockResolvedValue(descriptor);
    vi.mocked(api.getExternalSystem).mockResolvedValue(system);
  });

  it('renders provider branding and the configured target', async () => {
    render(<ExternalSystemsManager />);
    expect(await screen.findByText('Device Workflow')).toBeInTheDocument();
    expect(screen.getByText('Device 01 (Front Building)')).toBeInTheDocument();
    expect(screen.getByText('10.0.0.11 · sysadmin')).toBeInTheDocument();
    expect(screen.getByText('Online')).toBeInTheDocument();
  });

  it('shows the unavailable state when no provider supports admin', async () => {
    vi.mocked(api.getExternalSystemsDescriptor).mockResolvedValueOnce(null);
    render(<ExternalSystemsManager />);
    expect(await screen.findByText('No configurable integration installed')).toBeInTheDocument();
  });

  it('creates a target via the form (password sent, no id)', async () => {
    vi.mocked(api.upsertExternalTarget).mockResolvedValue(target);
    render(<ExternalSystemsManager />);
    await screen.findByText('Device Workflow');

    fireEvent.click(screen.getByRole('button', { name: /Add Device server/i }));
    fireEvent.change(screen.getByPlaceholderText(/Front Building/i), { target: { value: 'New Box' } });
    fireEvent.change(screen.getByPlaceholderText('10.0.0.11'), { target: { value: '10.0.0.99' } });
    fireEvent.change(screen.getByPlaceholderText(/No password set/i), { target: { value: 'pw' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(api.upsertExternalTarget).toHaveBeenCalled());
    const arg = vi.mocked(api.upsertExternalTarget).mock.calls[0][0];
    expect(arg).toMatchObject({ id: null, name: 'New Box', host: '10.0.0.99', password: 'pw' });
  });

  it('keeps the stored secret on edit when password left blank', async () => {
    vi.mocked(api.upsertExternalTarget).mockResolvedValue(target);
    render(<ExternalSystemsManager />);
    await screen.findByText('Device Workflow');

    fireEvent.click(screen.getByRole('button', { name: /Edit Device 01/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(api.upsertExternalTarget).toHaveBeenCalled());
    const arg = vi.mocked(api.upsertExternalTarget).mock.calls[0][0];
    expect(arg.id).toBe('device-01');
    expect(arg.password).toBeNull(); // blank => keep stored secret
  });

  it('treats a numeric Online connectivity from Test Connection as success', async () => {
    // The host serializes the enum as a number (2 = Online); the UI must not show "✗ 2".
    vi.mocked(api.testExternalTarget).mockResolvedValue({ targetId: 'device-01', connectivity: 2, failedDispatches: 0 } as any);
    render(<ExternalSystemsManager />);
    await screen.findByText('Device Workflow');

    fireEvent.click(screen.getByRole('button', { name: /Edit Device 01/i }));
    fireEvent.click(screen.getByRole('button', { name: /Test connection/i }));

    expect(await screen.findByText('✓ Connection OK')).toBeInTheDocument();
  });

  it('sends the per-target suppress-self-echo flag from the edit form', async () => {
    vi.mocked(api.upsertExternalTarget).mockResolvedValue(target);
    render(<ExternalSystemsManager />);
    await screen.findByText('Device Workflow');

    fireEvent.click(screen.getByRole('button', { name: /Edit Device 01/i }));
    // Defaults on for the target; uncheck it and save.
    fireEvent.click(screen.getByRole('checkbox', { name: /Suppress self-echo/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(api.upsertExternalTarget).toHaveBeenCalled());
    const arg = vi.mocked(api.upsertExternalTarget).mock.calls[0][0];
    expect(arg.suppressSelfEcho).toBe(false);
  });

  it('renders the system option toggle and the diagnostics readout', async () => {
    render(<ExternalSystemsManager />);
    await screen.findByText('Device Workflow');

    const toggle = screen.getByRole('switch', { name: 'Suppress self-echo' });
    expect(toggle).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByText('Self-echoes auto-filtered')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
    expect(screen.getByText(/Filtered Action 'StartRecording' on 'device-01'/)).toBeInTheDocument();
  });

  it('flips a system option and calls the api with the negated value', async () => {
    vi.mocked(api.setExternalSystemOption).mockResolvedValue({
      ...system,
      options: [{ ...system.options![0], value: false }],
    });
    render(<ExternalSystemsManager />);
    await screen.findByText('Device Workflow');

    fireEvent.click(screen.getByRole('switch', { name: 'Suppress self-echo' }));

    await waitFor(() => expect(api.setExternalSystemOption).toHaveBeenCalledWith('suppressSelfEcho', false));
    await waitFor(() =>
      expect(screen.getByRole('switch', { name: 'Suppress self-echo' })).toHaveAttribute('aria-checked', 'false'),
    );
  });

  it('syncs a target and reports the pulled catalog counts', async () => {
    vi.mocked(api.syncExternalTarget).mockResolvedValue({
      ...target,
      channels: [target.channels[0], { channelId: '2', displayName: 'Parking', globalCameraNumber: 102 }],
    });
    render(<ExternalSystemsManager />);
    await screen.findByText('Device Workflow');

    fireEvent.click(screen.getByRole('button', { name: /Sync Device 01/i }));
    await waitFor(() => expect(api.syncExternalTarget).toHaveBeenCalledWith('device-01'));
    expect(await screen.findByText(/2 cameras, 1 events, 1 actions/i)).toBeInTheDocument();
  });
});
