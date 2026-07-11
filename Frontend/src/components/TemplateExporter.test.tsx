import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { TemplateExporter } from './TemplateExporter';
import type { WorkflowDefinition } from '../types';

vi.mock('../utils/api', async () => {
  const actual = await vi.importActual<typeof import('../utils/api')>('../utils/api');
  return {
    ...actual,
    api: {
      getWorkflows: vi.fn(),
      exportTemplate: vi.fn(),
    },
  };
});

import { api } from '../utils/api';

const workflow = (id: string, name: string): WorkflowDefinition => ({
  id: { value: id },
  name,
  nodes: [],
  edges: [],
});

describe('TemplateExporter', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getWorkflows).mockResolvedValue([workflow('wf-1', 'My Flow')]);
    vi.mocked(api.exportTemplate).mockResolvedValue({
      blob: new Blob(['x'], { type: 'application/vnd.knotgarden.template+zip' }),
      filename: 'tpl_wf-1-1.0.0.kgtpl',
      report: { slots: [{ slot: 'weather-api', displayName: 'Weather API', description: null, requiredCredentialType: null }], rewrittenPaths: ['http-1.credential'] },
    });
    globalThis.URL.createObjectURL = vi.fn(() => 'blob:mock');
    globalThis.URL.revokeObjectURL = vi.fn();
  });

  it('disables export until a workflow is selected', async () => {
    render(<TemplateExporter />);
    await waitFor(() => expect(api.getWorkflows).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: 'Export template' })).toBeDisabled();
    expect(vi.mocked(api.exportTemplate)).not.toHaveBeenCalled();
  });

  it('exports the selected workflow and shows the portabilization report', async () => {
    render(<TemplateExporter />);
    // Open the custom dropdown and pick the workflow.
    const combo = await screen.findByRole('combobox', { name: 'Workflow to export' });
    fireEvent.click(combo);
    fireEvent.click(await screen.findByRole('option', { name: /My Flow/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Export template' }));

    await waitFor(() => expect(api.exportTemplate).toHaveBeenCalledWith(expect.objectContaining({ workflowId: 'wf-1' })));
    expect(globalThis.URL.createObjectURL).toHaveBeenCalled();
    const status = await screen.findByRole('status');
    // WYSIWYG: the saved name matches the preview (derived from the template name + version),
    // not the server-side Content-Disposition. "My Flow" → my-flow-1.0.0.kgtpl.
    expect(status).toHaveTextContent(/Exported my-flow-1.0.0.kgtpl/);
    expect(status).toHaveTextContent(/weather-api/);
  });

  it('preselects a workflow when given an initialWorkflowId', async () => {
    render(<TemplateExporter initialWorkflowId="wf-1" />);
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Workflow to export' })).toHaveTextContent(/My Flow/));
  });
});
