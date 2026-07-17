// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { BundleExporter } from './BundleExporter';
import type { NodePackageSummary, WorkflowDefinition } from '../types';

vi.mock('../utils/api', async () => {
  const actual = await vi.importActual<typeof import('../utils/api')>('../utils/api');
  return {
    ...actual,
    api: {
      getWorkflows: vi.fn(),
      getNodePackages: vi.fn(),
      exportBundle: vi.fn(),
    },
  };
});

import { api } from '../utils/api';

const workflow = (id: string, name: string, nodeTypes: string[] = []): WorkflowDefinition => ({
  id: { value: id },
  name,
  nodes: nodeTypes.map((t, i) => ({ id: { value: `${id}-n${i}` }, type: t, properties: {} })),
  edges: [],
});

// Custom package: versions come back latest-first (descending CreatedAt) from the API.
const customPkg = (id: string): NodePackageSummary => ({
  id,
  displayName: id,
  category: 'misc',
  versions: [
    { id: 'v2', nodePackageId: id, version: '2.0.0', manifestJson: '{}', source: 'local', capabilities: [], createdAt: '' },
    { id: 'v1', nodePackageId: id, version: '1.0.0', manifestJson: '{}', source: 'local', capabilities: [], createdAt: '' },
  ],
});

// Built-in package: single version with a "Built-in …" source — must never be bundled.
const builtInPkg = (id: string): NodePackageSummary => ({
  id,
  displayName: id,
  category: 'core',
  versions: [
    { id: '00000000-0000-0000-0000-000000000000', nodePackageId: id, version: '1.0.0', manifestJson: '{}', source: 'Built-in Core', capabilities: [], createdAt: '' },
  ],
});

describe('BundleExporter', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getWorkflows).mockResolvedValue([
      workflow('wf-1', 'My Flow', ['acme.http', 'errorTrigger']),
    ]);
    vi.mocked(api.getNodePackages).mockResolvedValue([customPkg('acme.http'), builtInPkg('errorTrigger')]);
    vi.mocked(api.exportBundle).mockResolvedValue(new Blob(['x'], { type: 'application/zip' }));
    // jsdom doesn't implement object URLs.
    globalThis.URL.createObjectURL = vi.fn(() => 'blob:mock');
    globalThis.URL.revokeObjectURL = vi.fn();
  });

  it('requires_a_bundle_id', async () => {
    render(<BundleExporter />);
    fireEvent.click(screen.getByRole('button', { name: 'Export bundle' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/bundle id is required/i);
    expect(vi.mocked(api.exportBundle)).not.toHaveBeenCalled();
  });

  it('requires_at_least_one_workflow', async () => {
    render(<BundleExporter />);
    fireEvent.change(screen.getByLabelText('Bundle id'), { target: { value: 'com.test' } });
    fireEvent.click(screen.getByRole('button', { name: 'Export bundle' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/at least one workflow/i);
  });

  it('auto_derives_packages_from_selected_workflows_and_excludes_built_ins', async () => {
    render(<BundleExporter />);
    await screen.findByLabelText('Include workflow My Flow');

    fireEvent.change(screen.getByLabelText('Bundle id'), { target: { value: 'com.example.demo' } });
    fireEvent.change(screen.getByLabelText('Tags'), { target: { value: 'a, b' } });
    fireEvent.click(screen.getByLabelText('Include workflow My Flow'));

    fireEvent.click(screen.getByRole('button', { name: 'Export bundle' }));

    await waitFor(() => expect(vi.mocked(api.exportBundle)).toHaveBeenCalledTimes(1));
    const manifest = vi.mocked(api.exportBundle).mock.calls[0][0];
    expect(manifest.bundleId).toBe('com.example.demo');
    expect(manifest.tags).toEqual(['a', 'b']);
    expect(manifest.workflows).toEqual([{ key: 'wf-1', role: 'primary', ref: 'wf-1.json' }]);
    // acme.http is derived + pinned to its latest version; the built-in errorTrigger is dropped.
    expect(manifest.packages).toEqual([{ id: 'acme.http', versionConstraintOrPin: '2.0.0', source: 'local' }]);
    expect(await screen.findByText(/Exported com\.example\.demo-1\.0\.0\.kgbundle/)).toBeInTheDocument();
  });

  it('select_all_toggles_every_workflow', async () => {
    vi.mocked(api.getWorkflows).mockResolvedValue([
      workflow('wf-1', 'One', ['acme.http']),
      workflow('wf-2', 'Two', []),
    ]);
    render(<BundleExporter />);
    await screen.findByLabelText('Include workflow One');

    fireEvent.click(screen.getByRole('button', { name: 'Select all workflows' }));
    fireEvent.change(screen.getByLabelText('Bundle id'), { target: { value: 'com.test' } });
    fireEvent.click(screen.getByRole('button', { name: 'Export bundle' }));

    await waitFor(() => expect(vi.mocked(api.exportBundle)).toHaveBeenCalled());
    const manifest = vi.mocked(api.exportBundle).mock.calls[0][0];
    expect(manifest.workflows.map((w) => w.key)).toEqual(['wf-1', 'wf-2']);
  });

  it('includes_declared_credential_slots', async () => {
    render(<BundleExporter />);
    await screen.findByLabelText('Include workflow My Flow');
    fireEvent.change(screen.getByLabelText('Bundle id'), { target: { value: 'com.test' } });
    fireEvent.click(screen.getByLabelText('Include workflow My Flow'));

    fireEvent.click(screen.getByRole('button', { name: 'Add credential slot' }));
    fireEvent.change(screen.getByLabelText('Slot name 0'), { target: { value: 'apiToken' } });
    fireEvent.change(screen.getByLabelText('Slot label 0'), { target: { value: 'Service API key' } });

    fireEvent.click(screen.getByRole('button', { name: 'Export bundle' }));

    await waitFor(() => expect(vi.mocked(api.exportBundle)).toHaveBeenCalled());
    const manifest = vi.mocked(api.exportBundle).mock.calls[0][0];
    expect(manifest.credentialSlots).toEqual([
      { slot: 'apiToken', type: '', displayName: 'Service API key', description: null, checklist: [] },
    ]);
  });

  it('surfaces_export_errors', async () => {
    vi.mocked(api.exportBundle).mockRejectedValueOnce(new Error("No available package satisfies 'acme.http'"));
    render(<BundleExporter />);
    await screen.findByLabelText('Include workflow My Flow');
    fireEvent.change(screen.getByLabelText('Bundle id'), { target: { value: 'com.test' } });
    fireEvent.click(screen.getByLabelText('Include workflow My Flow'));
    fireEvent.click(screen.getByRole('button', { name: 'Export bundle' }));
    expect(await screen.findByText(/No available package satisfies/)).toBeInTheDocument();
  });
});
