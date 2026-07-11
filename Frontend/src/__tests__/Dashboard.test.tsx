import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Dashboard } from '../components/Dashboard';
import { api } from '../utils/api';
import type { ExecutionInstance, WorkflowDefinition } from '../types';

vi.mock('../utils/api', () => ({
  api: {
    getWorkflows: vi.fn(),
    getExecutions: vi.fn(),
    triggerWorkflow: vi.fn(),
    getGroups: vi.fn().mockResolvedValue({ container: { version: 1, groups: [] }, etag: '' }),
    saveGroups: vi.fn(),
    deleteGroup: vi.fn(),
    getNotificationChannels: vi.fn().mockResolvedValue([]),
    duplicateWorkflow: vi.fn(),
    listArchivedWorkflows: vi.fn().mockResolvedValue([]),
    restoreWorkflow: vi.fn(),
    permanentlyDeleteWorkflow: vi.fn(),
    bulkDeleteWorkflows: vi.fn(),
    // Dashboard polls the external-signal provider for auto-filtered signals; default to "no provider".
    getExternalSystem: vi.fn().mockRejectedValue(new Error('no provider')),
  },
}));

function createWorkflow(id: string, name: string, hasActiveVersion = true): WorkflowDefinition {
  return {
    id: { value: id },
    name,
    nodes: [],
    edges: [],
    hasActiveVersion,
  };
}

function createExecution(overrides: Partial<ExecutionInstance> & Pick<ExecutionInstance, 'id' | 'workflowDefinitionId' | 'status' | 'createdAt' | 'updatedAt'>): ExecutionInstance {
  return {
    globalVariables: {},
    nodeStates: [],
    triggerOrigin: 'manual',
    workflowName: 'Default Workflow',
    ...overrides,
  };
}

describe('Dashboard', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('shows an activation-specific message when a workflow has no active runtime version', async () => {
    const alertSpy = vi.spyOn(window, 'alert').mockImplementation(() => {});

    vi.mocked(api.getWorkflows).mockResolvedValue([createWorkflow('wf-1', 'Activation Workflow')]);
    vi.mocked(api.getExecutions).mockResolvedValue([]);
    vi.mocked(api.triggerWorkflow).mockRejectedValue({
      status: 409,
      message: 'Workflow has no active version. Activate a version before triggering executions.',
      data: {
        message: 'Workflow has no active version. Activate a version before triggering executions.',
      },
    });

    render(
      <Dashboard
        onEditWorkflow={vi.fn()}
        onViewExecution={vi.fn()}
        onTriggeredExecution={vi.fn()}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Trigger Run' }));

    await screen.findByText('Activation Workflow');

    expect(alertSpy).toHaveBeenCalledWith(
      'Trigger failed: this workflow has no active runtime version. Open it, publish a version, and activate it before running.',
    );
  });

  it('permanently deletes an archived workflow after confirmation and removes its row', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

    vi.mocked(api.getWorkflows).mockResolvedValue([createWorkflow('wf-1', 'Live Workflow')]);
    vi.mocked(api.getExecutions).mockResolvedValue([]);
    vi.mocked(api.listArchivedWorkflows).mockResolvedValue([{ id: 'arch-1', name: 'Old Flow' }]);
    vi.mocked(api.permanentlyDeleteWorkflow).mockResolvedValue({ purged: true, id: 'arch-1' });

    render(
      <Dashboard
        onEditWorkflow={vi.fn()}
        onViewExecution={vi.fn()}
        onTriggeredExecution={vi.fn()}
      />,
    );

    // Reveal the archived panel, then permanently delete the lone archived row.
    fireEvent.click(await screen.findByRole('button', { name: 'Toggle archived workflows' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Permanently delete Old Flow' }));

    expect(confirmSpy).toHaveBeenCalled();
    expect(api.permanentlyDeleteWorkflow).toHaveBeenCalledWith('arch-1');
    await waitFor(() => expect(screen.queryByText('Old Flow')).not.toBeInTheDocument());

    confirmSpy.mockRestore();
  });

  it('does not permanently delete an archived workflow when confirmation is cancelled', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);

    vi.mocked(api.getWorkflows).mockResolvedValue([createWorkflow('wf-1', 'Live Workflow')]);
    vi.mocked(api.getExecutions).mockResolvedValue([]);
    vi.mocked(api.listArchivedWorkflows).mockResolvedValue([{ id: 'arch-1', name: 'Old Flow' }]);

    render(
      <Dashboard
        onEditWorkflow={vi.fn()}
        onViewExecution={vi.fn()}
        onTriggeredExecution={vi.fn()}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Toggle archived workflows' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Permanently delete Old Flow' }));

    expect(confirmSpy).toHaveBeenCalled();
    expect(api.permanentlyDeleteWorkflow).not.toHaveBeenCalled();
    expect(screen.getByText('Old Flow')).toBeInTheDocument();

    confirmSpy.mockRestore();
  });

  it('renders the Retrying filter and maps WaitingForRetry runs into the filtered results', async () => {
    vi.mocked(api.getWorkflows).mockResolvedValue([createWorkflow('wf-1', 'Retry Workflow')]);
    // Persistent (not chained `Once`) so the dashboard's unfiltered stat-strip poll can also read it
    // without desyncing the sequence the filtered timeline relies on.
    vi.mocked(api.getExecutions).mockResolvedValue([
      createExecution({
        id: 'exec-1',
        workflowDefinitionId: { value: 'wf-1' },
        workflowName: 'Retry Workflow',
        status: 'WaitingForRetry',
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      }),
    ]);

    render(
      <Dashboard
        onEditWorkflow={vi.fn()}
        onViewExecution={vi.fn()}
        onTriggeredExecution={vi.fn()}
      />,
    );

    const statusFilter = await screen.findByLabelText('Execution status filter');
    expect(within(statusFilter).getByRole('option', { name: 'Retrying' })).toBeInTheDocument();

    fireEvent.change(statusFilter, { target: { value: 'Retrying' } });

    const todaySection = await screen.findByLabelText('Today');

    expect(within(todaySection).getByText('Retry Workflow')).toBeInTheDocument();
    expect(within(todaySection).getByText('Retrying')).toBeInTheDocument();
    expect(api.getExecutions).toHaveBeenLastCalledWith({ status: 'Retrying', search: undefined });
  });

  it('renders runs inside Today, Yesterday, and Older timeline buckets', async () => {
    const now = new Date();
    const yesterday = new Date(now);
    yesterday.setDate(now.getDate() - 1);
    const older = new Date(now);
    older.setDate(now.getDate() - 10);

    vi.mocked(api.getWorkflows).mockResolvedValue([createWorkflow('wf-1', 'Timeline Workflow')]);
    vi.mocked(api.getExecutions).mockResolvedValue([
      createExecution({
        id: 'today-run',
        workflowDefinitionId: { value: 'wf-1' },
        workflowName: 'Today Run',
        status: 'Completed',
        createdAt: now.toISOString(),
        updatedAt: now.toISOString(),
      }),
      createExecution({
        id: 'yesterday-run',
        workflowDefinitionId: { value: 'wf-1' },
        workflowName: 'Yesterday Run',
        status: 'Suspended',
        createdAt: yesterday.toISOString(),
        updatedAt: yesterday.toISOString(),
      }),
      createExecution({
        id: 'older-run',
        workflowDefinitionId: { value: 'wf-1' },
        workflowName: 'Older Run',
        status: 'Failed',
        createdAt: older.toISOString(),
        updatedAt: older.toISOString(),
      }),
    ]);

    render(
      <Dashboard
        onEditWorkflow={vi.fn()}
        onViewExecution={vi.fn()}
        onTriggeredExecution={vi.fn()}
      />,
    );

    const todaySection = await screen.findByRole('region', { name: 'Today' }).catch(() => screen.getByLabelText('Today'));
    const yesterdaySection = screen.getByLabelText('Yesterday');
    const olderSection = screen.getByLabelText('Older');

    expect(within(todaySection).getByText('Today Run')).toBeInTheDocument();
    expect(within(yesterdaySection).getByText('Yesterday Run')).toBeInTheDocument();
    expect(within(olderSection).getByText('Older Run')).toBeInTheDocument();
  });

  it('disables the Trigger Run button when hasActiveVersion is false', async () => {
    vi.mocked(api.getWorkflows).mockResolvedValue([createWorkflow('wf-inactive', 'Inactive Workflow', false)]);
    vi.mocked(api.getExecutions).mockResolvedValue([]);

    render(
      <Dashboard
        onEditWorkflow={vi.fn()}
        onViewExecution={vi.fn()}
        onTriggeredExecution={vi.fn()}
      />,
    );

    const triggerBtn = await screen.findByRole('button', { name: 'Publish and activate this workflow before running' });
    expect(triggerBtn).toBeDisabled();
  });
});