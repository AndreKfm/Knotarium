// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { ServerConfigManager } from './ServerConfigManager';
import * as client from '../utils/serverConfigClient';
import { api } from '../utils/api';
import type { ServerConfigInfo } from '../types';

vi.mock('../utils/serverConfigClient', () => ({
  listServerConfigs: vi.fn(),
  createServerConfig: vi.fn(),
  updateServerConfig: vi.fn(),
  deleteServerConfig: vi.fn(),
}));

vi.mock('../utils/api', () => ({
  api: {
    getCredentials: vi.fn(),
  },
}));

const mockConfigs: ServerConfigInfo[] = [
  {
    id: 'cfg-1',
    name: 'Staging Server',
    baseUrl: 'https://staging.example.com',
    serverVariables: { env: 'staging' },
    securitySchemeType: 'http_bearer',
    credentialRef: 'cred-1',
    createdAt: '',
    updatedAt: '',
  },
  {
    id: 'cfg-2',
    name: 'Production Server',
    baseUrl: 'https://api.example.com',
    serverVariables: {},
    securitySchemeType: 'none',
    credentialRef: null,
    createdAt: '',
    updatedAt: '',
  },
];

const mockCredentials = [
  { id: 'cred-1', name: 'Staging Token' },
  { id: 'cred-2', name: 'Prod Token' },
];

describe('ServerConfigManager', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getCredentials).mockResolvedValue(mockCredentials as any);
  });

  it('renders_empty_list', async () => {
    vi.mocked(client.listServerConfigs).mockResolvedValueOnce([]);

    render(<ServerConfigManager />);

    expect(await screen.findByText('No server configurations found')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /New Config/i })).toBeInTheDocument();
  });

  it('renders_existing_configs', async () => {
    vi.mocked(client.listServerConfigs).mockResolvedValueOnce(mockConfigs);

    render(<ServerConfigManager />);

    expect(await screen.findByText('Staging Server')).toBeInTheDocument();
    expect(screen.getByText('Production Server')).toBeInTheDocument();
    expect(screen.getByText('https://staging.example.com')).toBeInTheDocument();
    expect(screen.getByText('https://api.example.com')).toBeInTheDocument();
  });

  it('create_config_calls_api', async () => {
    vi.mocked(client.listServerConfigs).mockResolvedValue(mockConfigs);
    vi.mocked(client.createServerConfig).mockResolvedValueOnce({} as any);

    render(<ServerConfigManager />);

    // Wait for list to load
    await screen.findByText('Staging Server');

    const newBtn = screen.getByRole('button', { name: /New Config/i });
    fireEvent.click(newBtn);

    // Form modal opens
    expect(screen.getByText('Create Server Configuration')).toBeInTheDocument();

    const nameInput = screen.getByLabelText(/Name/i);
    const urlInput = screen.getByLabelText(/Base URL/i);
    const saveBtn = screen.getByRole('button', { name: /Save/i });

    fireEvent.change(nameInput, { target: { value: 'Dev Server' } });
    fireEvent.change(urlInput, { target: { value: 'https://dev.example.com' } });
    fireEvent.click(saveBtn);

    await waitFor(() => {
      expect(client.createServerConfig).toHaveBeenCalledWith({
        name: 'Dev Server',
        baseUrl: 'https://dev.example.com',
        securitySchemeType: 'none',
        credentialRef: null,
        allowInsecureCertificate: false,
        serverVariables: {},
      });
    });
  });

  it('create_success_shows_new_entry', async () => {
    const updatedConfigs = [
      ...mockConfigs,
      {
        id: 'cfg-3',
        name: 'Dev Server',
        baseUrl: 'https://dev.example.com',
        serverVariables: {},
        securitySchemeType: 'none',
        credentialRef: null,
        createdAt: '',
        updatedAt: '',
      },
    ];

    vi.mocked(client.listServerConfigs)
      .mockResolvedValueOnce(mockConfigs)
      .mockResolvedValueOnce(updatedConfigs);
    vi.mocked(client.createServerConfig).mockResolvedValueOnce({} as any);

    render(<ServerConfigManager />);

    // Wait for list to load
    await screen.findByText('Staging Server');

    const newBtn = screen.getByRole('button', { name: /New Config/i });
    fireEvent.click(newBtn);

    const nameInput = screen.getByLabelText(/Name/i);
    const urlInput = screen.getByLabelText(/Base URL/i);
    const saveBtn = screen.getByRole('button', { name: /Save/i });

    fireEvent.change(nameInput, { target: { value: 'Dev Server' } });
    fireEvent.change(urlInput, { target: { value: 'https://dev.example.com' } });
    fireEvent.click(saveBtn);

    // Verify it reloaded list showing new item
    expect(await screen.findByText('Dev Server')).toBeInTheDocument();
  });

  it('create_validation_error_empty_name', async () => {
    vi.mocked(client.listServerConfigs).mockResolvedValueOnce(mockConfigs);

    render(<ServerConfigManager />);
    await screen.findByText('Staging Server');

    fireEvent.click(screen.getByRole('button', { name: /New Config/i }));

    const urlInput = screen.getByLabelText(/Base URL/i);
    fireEvent.change(urlInput, { target: { value: 'https://dev.example.com' } });
    fireEvent.click(screen.getByRole('button', { name: /Save/i }));

    expect(await screen.findByText('Name is required.')).toBeInTheDocument();
    expect(client.createServerConfig).not.toHaveBeenCalled();
  });

  it('create_validation_error_empty_baseUrl', async () => {
    vi.mocked(client.listServerConfigs).mockResolvedValueOnce(mockConfigs);

    render(<ServerConfigManager />);
    await screen.findByText('Staging Server');

    fireEvent.click(screen.getByRole('button', { name: /New Config/i }));

    const nameInput = screen.getByLabelText(/Name/i);
    fireEvent.change(nameInput, { target: { value: 'Dev Server' } });
    fireEvent.click(screen.getByRole('button', { name: /Save/i }));

    expect(await screen.findByText('Base URL is required.')).toBeInTheDocument();
    expect(client.createServerConfig).not.toHaveBeenCalled();
  });

  it('delete_calls_api_after_confirm', async () => {
    vi.mocked(client.listServerConfigs).mockResolvedValue(mockConfigs);
    vi.mocked(client.deleteServerConfig).mockResolvedValueOnce();

    render(<ServerConfigManager />);
    await screen.findByText('Staging Server');

    // Click delete on staging config
    const deleteBtn = screen.getByRole('button', { name: /Delete Staging Server/i });
    fireEvent.click(deleteBtn);

    // Verify confirm modal shown
    expect(screen.getByText('Delete "Staging Server"?')).toBeInTheDocument();

    const confirmBtn = screen.getByRole('button', { name: 'Delete' });
    fireEvent.click(confirmBtn);

    await waitFor(() => {
      expect(client.deleteServerConfig).toHaveBeenCalledWith('cfg-1');
    });
  });

  it('delete_cancel_does_not_call_api', async () => {
    vi.mocked(client.listServerConfigs).mockResolvedValue(mockConfigs);

    render(<ServerConfigManager />);
    await screen.findByText('Staging Server');

    const deleteBtn = screen.getByRole('button', { name: /Delete Staging Server/i });
    fireEvent.click(deleteBtn);

    const cancelBtn = screen.getByRole('button', { name: /Cancel/i });
    fireEvent.click(cancelBtn);

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(client.deleteServerConfig).not.toHaveBeenCalled();
    expect(screen.queryByText('Delete "Staging Server"?')).not.toBeInTheDocument();
  });

  it('edit_prefills_form', async () => {
    vi.mocked(client.listServerConfigs).mockResolvedValue(mockConfigs);

    render(<ServerConfigManager />);
    await screen.findByText('Staging Server');

    const editBtn = screen.getByRole('button', { name: /Edit Staging Server/i });
    fireEvent.click(editBtn);

    expect(screen.getByText('Edit Server Configuration')).toBeInTheDocument();

    const nameInput = screen.getByLabelText(/Name/i) as HTMLInputElement;
    const urlInput = screen.getByLabelText(/Base URL/i) as HTMLInputElement;

    expect(nameInput.value).toBe('Staging Server');
    expect(urlInput.value).toBe('https://staging.example.com');
  });

  it('edit_submit_calls_updateServerConfig', async () => {
    vi.mocked(client.listServerConfigs).mockResolvedValue(mockConfigs);
    vi.mocked(client.updateServerConfig).mockResolvedValueOnce({} as any);

    render(<ServerConfigManager />);
    await screen.findByText('Staging Server');

    const editBtn = screen.getByRole('button', { name: /Edit Staging Server/i });
    fireEvent.click(editBtn);

    const nameInput = screen.getByLabelText(/Name/i);
    fireEvent.change(nameInput, { target: { value: 'Staging New' } });

    fireEvent.click(screen.getByRole('button', { name: /Save/i }));

    await waitFor(() => {
      expect(client.updateServerConfig).toHaveBeenCalledWith('cfg-1', {
        name: 'Staging New',
        baseUrl: 'https://staging.example.com',
        securitySchemeType: 'http_bearer',
        credentialRef: 'cred-1',
        allowInsecureCertificate: false,
        serverVariables: { env: 'staging' },
      });
    });
  });
});
