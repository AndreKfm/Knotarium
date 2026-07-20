// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import type { ReactNode } from 'react';
import { ExecutionDetail } from '../components/ExecutionDetail/index';
import { api } from '../utils/api';

vi.mock('../utils/api', () => ({
  api: {
    getExecution: vi.fn(),
    getExecutions: vi.fn(),
    getWorkflow: vi.fn(),
    getWorkflowVersions: vi.fn(),
    getWorkflowVersionDetail: vi.fn(),
    getExecutionJournal: vi.fn(),
    getNodePackages: vi.fn(),
    getWorkflowSchedules: vi.fn(),
    fireWorkflowSchedule: vi.fn(),
    getSseUrl: vi.fn(() => '/api/executions/exec-1/events'),
    mapJournalEntry: vi.fn((entry) => entry),
    applyManualDecision: vi.fn(),
  },
}));

vi.mock('../utils/nodeTypes', () => ({
  createNodeTypes: () => ({}),
}));

vi.mock('@xyflow/react', async () => {
  return {
    ReactFlowProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
    ReactFlow: ({ children, nodes, edges }: { children?: ReactNode; nodes?: unknown[]; edges?: unknown[] }) => (
      <div data-testid="react-flow">
        <pre data-testid="react-flow-nodes">{JSON.stringify(nodes ?? [])}</pre>
        <pre data-testid="react-flow-edges">{JSON.stringify(edges ?? [])}</pre>
        {children}
      </div>
    ),
    Controls: () => null,
    Background: () => null,
    BackgroundVariant: { Dots: 'dots' },
    useReactFlow: () => ({ fitView: () => {} }),
    useNodesInitialized: () => true,
  };
});

describe('ExecutionDetail', () => {
  beforeEach(() => {
    vi.stubGlobal('EventSource', class {
      addEventListener() {}
      close() {}
    });

    Object.defineProperty(window.HTMLElement.prototype, 'scrollIntoView', {
      configurable: true,
      value: vi.fn(),
    });
  });

  afterEach(() => {
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  it('loads execution data once even after node package metadata arrives', async () => {
    vi.mocked(api.getExecution).mockResolvedValue({
      id: 'exec-1',
      status: 'Running',
      workflowDefinitionId: { value: 'wf-1' },
      nodeStates: [],
      globalVariables: {},
    } as never);

    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Workflow',
      nodes: [
        {
          id: { value: 'start-1' },
          type: 'start',
          properties: { _metadata: { x: 150, y: 200 } },
        },
      ],
      edges: [],
    } as never);

    vi.mocked(api.getExecutionJournal).mockResolvedValue([] as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([
      {
        id: 'start',
        displayName: 'Start',
        category: 'Trigger',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Start', triggerOnly: true, outputs: [{ name: 'success' }] }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
    ] as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowSchedules).mockResolvedValue([] as never);

    render(<ExecutionDetail executionId="exec-1" onBack={() => {}} onTriggeredExecution={() => {}} />);

    await waitFor(() => {
      expect(api.getExecution).toHaveBeenCalledTimes(1);
      expect(api.getWorkflow).toHaveBeenCalledTimes(1);
      expect(api.getWorkflowVersions).toHaveBeenCalledTimes(0);
      expect(api.getExecutionJournal).toHaveBeenCalledTimes(1);
      expect(api.getNodePackages).toHaveBeenCalledTimes(1);
      expect(api.getWorkflowSchedules).toHaveBeenCalledTimes(1);
    });

    await waitFor(() => {
      expect(api.getExecution).toHaveBeenCalledTimes(1);
      expect(api.getWorkflow).toHaveBeenCalledTimes(1);
      expect(api.getWorkflowVersions).toHaveBeenCalledTimes(0);
      expect(api.getExecutionJournal).toHaveBeenCalledTimes(1);
      expect(api.getNodePackages).toHaveBeenCalledTimes(1);
      expect(api.getWorkflowSchedules).toHaveBeenCalledTimes(1);
    });
  });

  it('renders even when execution status is returned in an unexpected shape', async () => {
    vi.mocked(api.getExecution).mockResolvedValue({
      id: 'exec-1',
      status: { value: 'Completed' },
      workflowDefinitionId: { value: 'wf-1' },
      nodeStates: [],
      globalVariables: {},
    } as never);

    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Workflow',
      nodes: [],
      edges: [],
    } as never);

    vi.mocked(api.getExecutionJournal).mockResolvedValue([] as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([] as never);
    vi.mocked(api.getExecutions).mockResolvedValue([] as never);
    vi.mocked(api.getExecutions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowSchedules).mockResolvedValue([] as never);

    render(<ExecutionDetail executionId="exec-1" onBack={() => {}} onTriggeredExecution={() => {}} />);

    await waitFor(() => {
      expect(screen.getAllByText('Completed').length).toBeGreaterThan(0);
    });
  });

  it('keeps scheduler connections in the execution view before the run starts even without package metadata', async () => {
    vi.mocked(api.getExecution).mockResolvedValue({
      id: 'exec-1',
      status: 'Pending',
      workflowDefinitionId: { value: 'wf-1' },
      nodeStates: [],
      globalVariables: {},
      triggerOrigin: 'schedule',
    } as never);

    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Workflow',
      nodes: [
        {
          id: { value: 'scheduler-1' },
          type: 'scheduler',
          properties: { _metadata: { x: 150, y: 200 } },
        },
        {
          id: { value: 'log-1' },
          type: 'log',
          properties: { _metadata: { x: 450, y: 200 }, message: 'hi' },
        },
      ],
      edges: [
        {
          id: 'edge-1',
          from: { value: 'scheduler-1' },
          output: 'triggeredAt',
          to: { value: 'log-1' },
          input: 'in',
        },
      ],
    } as never);

    vi.mocked(api.getExecutionJournal).mockResolvedValue([] as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([] as never);
    vi.mocked(api.getExecutions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowSchedules).mockResolvedValue([] as never);

    render(<ExecutionDetail executionId="exec-1" onBack={() => {}} onTriggeredExecution={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('react-flow-nodes').textContent).toContain('"triggerOnly":true');
      expect(screen.getByTestId('react-flow-nodes').textContent).toContain('"outputHandles":["triggeredAt"]');
      expect(screen.getByTestId('react-flow-edges').textContent).toContain('"sourceHandle":"triggeredAt"');
    });
  });

  it('renders the persisted workflow version snapshot layout for an execution', async () => {
    vi.mocked(api.getExecution).mockResolvedValue({
      id: 'exec-1',
      status: 'Completed',
      workflowDefinitionId: { value: 'wf-1' },
      workflowVersionId: 'version-2',
      nodeStates: [],
      globalVariables: {},
      triggerOrigin: 'schedule',
    } as never);

    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Workflow',
      nodes: [
        {
          id: { value: 'end-1' },
          type: 'end',
          properties: { _metadata: { x: 100, y: 200 } },
        },
        {
          id: { value: 'scheduler-1' },
          type: 'scheduler',
          properties: { _metadata: { x: 900, y: 200 } },
        },
      ],
      edges: [],
    } as never);

    vi.mocked(api.getWorkflowVersions).mockResolvedValue([
      {
        id: 'version-2',
        versionNumber: 2,
        createdAt: '2026-05-31T18:08:45Z',
        createdBy: null,
        label: null,
        origin: 'Published',
        isActive: true,
        restoredFromVersionId: null,
        nodeCount: 2,
        executionCount: 0,
      },
    ] as never);

    vi.mocked(api.getWorkflowVersionDetail).mockResolvedValue({
      id: 'version-2',
      workflowDefinitionId: { value: 'wf-1' },
      versionNumber: 2,
      createdAt: '2026-05-31T18:08:45Z',
      nodes: [
        {
          id: { value: 'scheduler-1' },
          type: 'scheduler',
          properties: { _metadata: { x: 150, y: 200 } },
        },
        {
          id: { value: 'end-1' },
          type: 'end',
          properties: { _metadata: { x: 700, y: 200 } },
        },
      ],
      edges: [],
    } as never);

    vi.mocked(api.getExecutionJournal).mockResolvedValue([] as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([] as never);
    vi.mocked(api.getExecutions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowSchedules).mockResolvedValue([] as never);

    render(<ExecutionDetail executionId="exec-1" onBack={() => {}} onTriggeredExecution={() => {}} />);

    await waitFor(() => {
      const nodes = screen.getByTestId('react-flow-nodes').textContent ?? '';

      expect(api.getWorkflowVersions).toHaveBeenCalledWith('wf-1');
      expect(nodes).toContain('"id":"scheduler-1"');
      expect(nodes).toContain('"x":150');
      expect(nodes).toContain('"id":"end-1"');
      expect(nodes).toContain('"x":700');
      expect(nodes).not.toContain('"x":900');
    });
  });

  it('follows the next scheduled execution for the same workflow', async () => {
    const intervalCallbacks: Array<() => void | Promise<void>> = [];
    vi.spyOn(globalThis, 'setInterval').mockImplementation(((callback: TimerHandler) => {
      if (typeof callback === 'function') {
        intervalCallbacks.push(callback as () => void | Promise<void>);
      }

      return 1 as unknown as ReturnType<typeof setInterval>;
    }) as typeof setInterval);
    vi.spyOn(globalThis, 'clearInterval').mockImplementation(() => {});

    vi.mocked(api.getExecution).mockResolvedValue({
      id: 'exec-1',
      status: 'Completed',
      workflowDefinitionId: { value: 'wf-1' },
      createdAt: '2026-05-31T18:07:45Z',
      nodeStates: [],
      globalVariables: {},
      triggerOrigin: 'schedule',
    } as never);

    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Workflow',
      nodes: [],
      edges: [],
    } as never);

    vi.mocked(api.getExecutionJournal).mockResolvedValue([] as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowSchedules).mockResolvedValue([] as never);
    vi.mocked(api.getExecutions).mockResolvedValue([
      {
        id: 'exec-2',
        status: 'Running',
        workflowDefinitionId: { value: 'wf-1' },
        createdAt: '2026-05-31T18:08:45Z',
        updatedAt: '2026-05-31T18:08:46Z',
        globalVariables: {},
        nodeStates: [],
        triggerOrigin: 'schedule',
      },
    ] as never);

    const onTriggeredExecution = vi.fn();

    render(<ExecutionDetail executionId="exec-1" onBack={() => {}} onTriggeredExecution={onTriggeredExecution} />);

    await waitFor(() => {
      expect(api.getExecution).toHaveBeenCalledTimes(1);
    });

    await waitFor(() => {
      expect(intervalCallbacks.length).toBeGreaterThanOrEqual(2);
    });

    await act(async () => {
      await Promise.all(intervalCallbacks.map(async (callback) => {
        await callback();
      }));
    });

    await waitFor(() => {
      expect(api.getExecutions).toHaveBeenCalledTimes(1);
      expect(onTriggeredExecution).toHaveBeenCalledWith('exec-2');
    });
  });

  it('renders a grouped execution timeline overview for journal entries', async () => {
    vi.mocked(api.getExecution).mockResolvedValue({
      id: 'exec-1',
      status: 'Completed',
      workflowDefinitionId: { value: 'wf-1' },
      createdAt: '2026-05-31T17:57:13.000Z',
      updatedAt: '2026-05-31T17:57:13.021Z',
      nodeStates: [],
      globalVariables: {},
      triggerOrigin: 'schedule',
    } as never);

    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Workflow',
      nodes: [
        {
          id: { value: 'scheduler-1' },
          type: 'scheduler',
          properties: { _metadata: { x: 150, y: 200 }, cronExpression: '*/5 * * * *', timeZoneId: 'Europe/Berlin' },
        },
        {
          id: { value: 'log-1' },
          type: 'log',
          properties: { _metadata: { x: 450, y: 200 }, message: 'log message' },
        },
        {
          id: { value: 'end-1' },
          type: 'end',
          properties: { _metadata: { x: 700, y: 200 } },
        },
      ],
      edges: [],
    } as never);

    vi.mocked(api.getExecutionJournal).mockResolvedValue([
      {
        id: 'j-1',
        executionInstanceId: 'exec-1',
        nodeId: { value: 'scheduler-1' },
        timestamp: '2026-05-31T17:57:13.000Z',
        eventType: 'NodeExecutionCompleted',
        message: 'Scheduler fired.',
        data: {},
      },
      {
        id: 'j-2',
        executionInstanceId: 'exec-1',
        nodeId: { value: 'log-1' },
        timestamp: '2026-05-31T17:57:13.009Z',
        eventType: 'NodeExecutionStarted',
        message: 'Executing node (type `log`).',
        data: {},
      },
      {
        id: 'j-3',
        executionInstanceId: 'exec-1',
        nodeId: { value: 'log-1' },
        timestamp: '2026-05-31T17:57:13.013Z',
        eventType: 'NodeExecutionCompleted',
        message: 'Emitted log line.',
        data: { message: 'log message' },
      },
      {
        id: 'j-4',
        executionInstanceId: 'exec-1',
        nodeId: { value: 'end-1' },
        timestamp: '2026-05-31T17:57:13.021Z',
        eventType: 'NodeExecutionCompleted',
        message: 'Reached end node.',
        data: {},
      },
    ] as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([
      {
        id: 'scheduler',
        displayName: 'Cron Scheduler',
        category: 'Trigger',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Cron Scheduler', triggerOnly: true, outputs: [{ name: 'triggeredAt' }] }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
      {
        id: 'log',
        displayName: 'Log',
        category: 'Action',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Log' }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
      {
        id: 'end',
        displayName: 'End',
        category: 'Flow',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'End' }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
    ] as never);
    vi.mocked(api.getExecutions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowSchedules).mockResolvedValue([] as never);

    render(<ExecutionDetail executionId="exec-1" onBack={() => {}} onTriggeredExecution={() => {}} />);

    await waitFor(() => {
      expect(screen.getByText('Execution Timeline')).toBeInTheDocument();
      expect(screen.getByText(/3 nodes/i)).toBeInTheDocument();
      expect(screen.queryByText('Node Results')).not.toBeInTheDocument();
      expect(screen.getAllByText('AUTO').length).toBeGreaterThanOrEqual(1);
      expect(screen.getByText('Success')).toBeInTheDocument();
      expect(screen.getByText(/Triggered by schedule/i)).toBeInTheDocument();
      expect(screen.getAllByText(/\*\/5 \* \* \* \*/i).length).toBeGreaterThanOrEqual(1);
      expect(screen.getByText('Cron Scheduler')).toBeInTheDocument();
      expect(screen.getByText('scheduler-1')).toBeInTheDocument();
      expect(screen.getByText(/Schedule \*\/5 \* \* \* \* · Europe\/Berlin/i)).toBeInTheDocument();
      expect(screen.getByText('Log')).toBeInTheDocument();
      expect(screen.getByTestId('journal-group-log-1')).toHaveTextContent('"log message"');
      expect(screen.queryByText('Output')).not.toBeInTheDocument();
      expect(screen.getByText('+0ms')).toBeInTheDocument();
      expect(screen.getByText('Workflow run completed successfully.')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('journal-toggle-log-1'));

    await waitFor(() => {
      expect(screen.getByText('Output')).toBeInTheDocument();
      expect(screen.getByText('MESSAGE')).toBeInTheDocument();
    });

    const logGroup = screen.getByTestId('journal-group-log-1');
    const logGroupText = logGroup.textContent ?? '';
    expect(logGroupText.indexOf('STARTED')).toBeGreaterThan(-1);
    expect(logGroupText.indexOf('DONE')).toBeGreaterThan(-1);
    expect(logGroupText.indexOf('STARTED')).toBeLessThan(logGroupText.indexOf('Output'));
  });

  it('renders skipped and pending nodes in the timeline without the legacy results card', async () => {
    vi.mocked(api.getExecution).mockResolvedValue({
      id: 'exec-2',
      status: 'Running',
      workflowDefinitionId: { value: 'wf-1' },
      createdAt: '2026-05-31T17:57:13.000Z',
      updatedAt: '2026-05-31T17:57:13.021Z',
      globalVariables: {},
      triggerOrigin: 'manual',
      nodeStates: [
        {
          id: 'state-1',
          executionInstanceId: 'exec-2',
          nodeId: { value: 'scheduler-1' },
          status: 'Completed',
          inputs: {},
          outputs: {},
          executionCount: 1,
        },
        {
          id: 'state-2',
          executionInstanceId: 'exec-2',
          nodeId: { value: 'log-1' },
          status: 'Completed',
          inputs: {},
          outputs: { skipped: true, manualDecision: 'Skip' },
          executionCount: 1,
        },
        {
          id: 'state-3',
          executionInstanceId: 'exec-2',
          nodeId: { value: 'end-1' },
          status: 'Pending',
          inputs: {},
          outputs: {},
          executionCount: 0,
        },
      ],
    } as never);

    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Workflow',
      nodes: [
        {
          id: { value: 'scheduler-1' },
          type: 'scheduler',
          properties: { _metadata: { x: 150, y: 200 }, cronExpression: '*/5 * * * *', timeZoneId: 'Europe/Berlin' },
        },
        {
          id: { value: 'log-1' },
          type: 'log',
          properties: { _metadata: { x: 450, y: 200 }, message: 'log message' },
        },
        {
          id: { value: 'end-1' },
          type: 'end',
          properties: { _metadata: { x: 700, y: 200 } },
        },
      ],
      edges: [],
    } as never);

    vi.mocked(api.getExecutionJournal).mockResolvedValue([
      {
        id: 'j-1',
        executionInstanceId: 'exec-2',
        nodeId: { value: 'scheduler-1' },
        timestamp: '2026-05-31T17:57:13.000Z',
        eventType: 'NodeExecutionCompleted',
        message: 'Trigger node activated.',
        data: { triggeredAt: '2026-05-31T17:57:13.000Z' },
      },
      {
        id: 'j-2',
        executionInstanceId: 'exec-2',
        nodeId: { value: 'log-1' },
        timestamp: '2026-05-31T17:57:13.009Z',
        eventType: 'NodeExecutionCompleted',
        message: 'Node was manually skipped by an operator.',
        data: { skipped: true, manualDecision: 'Skip' },
      },
    ] as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([
      {
        id: 'scheduler',
        displayName: 'Cron Scheduler',
        category: 'Trigger',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Cron Scheduler', triggerOnly: true, outputs: [{ name: 'triggeredAt' }] }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
      {
        id: 'log',
        displayName: 'Log',
        category: 'Action',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Log' }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
      {
        id: 'end',
        displayName: 'End',
        category: 'Flow',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'End' }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
    ] as never);
    vi.mocked(api.getExecutions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowSchedules).mockResolvedValue([
      {
        nodeId: 'scheduler-1',
        cronExpression: '*/5 * * * *',
        timeZoneId: 'Europe/Berlin',
        nextFireAtUtc: '2026-05-31T18:00:00Z',
        isActive: true,
      },
    ] as never);

    render(<ExecutionDetail executionId="exec-2" onBack={() => {}} onTriggeredExecution={() => {}} />);

    await waitFor(() => {
      expect(screen.queryByText('Node Results')).not.toBeInTheDocument();
      expect(screen.getAllByText('MANUAL').length).toBeGreaterThanOrEqual(1);
      expect(screen.getByText(/Fired manually - "Fire now"/i)).toBeInTheDocument();
      expect(screen.getByTestId('journal-group-log-1')).toHaveTextContent('Skipped');
      expect(screen.getByTestId('journal-group-end-1')).toHaveTextContent('Pending');
      expect(screen.queryByText('2026-05-31T17:57:13.000Z')).not.toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('journal-toggle-scheduler-1'));

    await waitFor(() => {
      expect(screen.getByText('2026-05-31T17:57:13.000Z')).toBeInTheDocument();
    });
  });

  it('uses the timeline state for the footer when the execution record still says pending', async () => {
    vi.mocked(api.getExecution).mockResolvedValue({
      id: 'exec-3',
      status: 'Pending',
      workflowDefinitionId: { value: 'wf-1' },
      createdAt: '2026-05-31T17:57:13.000Z',
      updatedAt: '2026-05-31T17:57:13.021Z',
      nodeStates: [],
      globalVariables: {},
      triggerOrigin: 'schedule',
    } as never);

    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Workflow',
      nodes: [
        {
          id: { value: 'scheduler-1' },
          type: 'scheduler',
          properties: { _metadata: { x: 150, y: 200 }, cronExpression: '*/5 * * * *', timeZoneId: 'Europe/Berlin' },
        },
        {
          id: { value: 'log-1' },
          type: 'log',
          properties: { _metadata: { x: 450, y: 200 }, message: 'log message' },
        },
      ],
      edges: [],
    } as never);

    vi.mocked(api.getExecutionJournal).mockResolvedValue([
      {
        id: 'j-1',
        executionInstanceId: 'exec-3',
        nodeId: { value: 'scheduler-1' },
        timestamp: '2026-05-31T17:57:13.000Z',
        eventType: 'NodeExecutionCompleted',
        message: 'Trigger node activated.',
        data: { triggeredAt: '2026-05-31T17:57:13.000Z' },
      },
      {
        id: 'j-2',
        executionInstanceId: 'exec-3',
        nodeId: { value: 'log-1' },
        timestamp: '2026-05-31T17:57:13.021Z',
        eventType: 'NodeExecutionCompleted',
        message: 'Emitted log line.',
        data: { message: 'log message' },
      },
    ] as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([
      {
        id: 'scheduler',
        displayName: 'Cron Scheduler',
        category: 'Trigger',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Cron Scheduler', triggerOnly: true, outputs: [{ name: 'triggeredAt' }] }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
      {
        id: 'log',
        displayName: 'Log',
        category: 'Action',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Log' }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
    ] as never);
    vi.mocked(api.getExecutions).mockResolvedValue([] as never);
    vi.mocked(api.getWorkflowSchedules).mockResolvedValue([
      {
        nodeId: 'scheduler-1',
        cronExpression: '*/5 * * * *',
        timeZoneId: 'Europe/Berlin',
        nextFireAtUtc: '2026-05-31T18:00:00Z',
        isActive: true,
      },
    ] as never);

    render(<ExecutionDetail executionId="exec-3" onBack={() => {}} onTriggeredExecution={() => {}} />);

    await waitFor(() => {
      expect(screen.getByText('Success')).toBeInTheDocument();
      expect(screen.getByText('Workflow run completed successfully.')).toBeInTheDocument();
    });
  });
});