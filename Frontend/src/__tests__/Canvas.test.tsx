// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { fireEvent, render, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { Canvas } from '../components/Canvas';
import { api } from '../utils/api';

vi.mock('../utils/api', () => ({
  api: {
    getWorkflow: vi.fn(),
    getWorkflows: vi.fn().mockResolvedValue([]),
    getWorkflowVersions: vi.fn(),
    getActiveWorkflowVersion: vi.fn(),
    getNodePackages: vi.fn(),
    saveWorkflow: vi.fn(),
    publishWorkflow: vi.fn(),
    activateWorkflowVersion: vi.fn(),
    triggerWorkflow: vi.fn(),
  },
}));

vi.mock('../utils/nodeTypes', () => ({
  createNodeTypes: () => ({}),
}));

vi.mock('../components/SidebarPalette', () => ({
  SidebarPalette: () => <div data-testid="sidebar-palette" />,
}));

vi.mock('../components/PropertiesPanel', () => ({
  PropertiesPanel: () => <div data-testid="properties-panel" />,
}));

vi.mock('@xyflow/react', async () => {
  const React = await vi.importActual<typeof import('react')>('react');

  return {
    ReactFlowProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
    ReactFlow: ({ children, nodes }: { children?: ReactNode; nodes?: unknown[] }) => (
      <div data-testid="react-flow">
        <pre data-testid="react-flow-nodes">{JSON.stringify(nodes ?? [])}</pre>
        {children}
      </div>
    ),
    MiniMap: () => null,
    Controls: () => null,
    Background: () => null,
    BackgroundVariant: { Dots: 'dots' },
    SelectionMode: { Partial: 'partial', Full: 'full' },
    addEdge: (edge: unknown, existingEdges: unknown[]) => [...existingEdges, edge],
    useReactFlow: () => ({
      screenToFlowPosition: ({ x, y }: { x: number; y: number }) => ({ x, y }),
      getInternalNode: () => undefined,
      getNodes: () => [],
      setCenter: vi.fn(),
      fitView: vi.fn(),
      setViewport: vi.fn(),
      getViewport: () => ({ x: 0, y: 0, zoom: 1 }),
      getZoom: () => 1,
    }),
    useStoreApi: () => ({ setState: vi.fn(), getState: () => ({}) }),
    useNodesInitialized: () => false,
    useConnection: (selector?: (c: { inProgress: boolean }) => unknown) =>
      selector ? selector({ inProgress: false }) : { inProgress: false },
    useNodeConnections: () => [],
    useNodesState: (initialNodes: unknown[]) => {
      const [nodes, setNodes] = React.useState(initialNodes);
      return [nodes, setNodes, vi.fn()] as const;
    },
    useEdgesState: (initialEdges: unknown[]) => {
      const [edges, setEdges] = React.useState(initialEdges);
      return [edges, setEdges, vi.fn()] as const;
    },
  };
});

describe('Canvas', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('loads workflow versions and active version alongside the workflow', async () => {
    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Loaded Workflow',
      nodes: [],
      edges: [],
    } as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([
      {
        id: 'ver-2',
        workflowDefinitionId: { value: 'wf-1' },
        versionNumber: 2,
        nodes: [],
        edges: [],
        createdAt: '2026-06-04T00:00:00Z',
      },
    ] as never);
    vi.mocked(api.getActiveWorkflowVersion).mockResolvedValue({
      workflowDefinitionId: { value: 'wf-1' },
      workflowVersionId: 'ver-2',
      activatedAtUtc: '2026-06-04T00:00:00Z',
    } as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([] as never);

    render(<Canvas workflowId="wf-1" onSaved={() => {}} onTriggered={() => {}} />);

    await waitFor(() => {
      expect(api.getWorkflowVersions).toHaveBeenCalledWith('wf-1');
      expect(api.getActiveWorkflowVersion).toHaveBeenCalledWith('wf-1');
    });
  });

  it('run activates the selected workflow version instead of always using the latest', async () => {
    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Loaded Workflow',
      nodes: [],
      edges: [],
    } as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([
      {
        id: 'ver-3',
        workflowDefinitionId: { value: 'wf-1' },
        versionNumber: 3,
        nodes: [],
        edges: [],
        createdAt: '2026-06-04T00:00:00Z',
      },
      {
        id: 'ver-2',
        workflowDefinitionId: { value: 'wf-1' },
        versionNumber: 2,
        nodes: [],
        edges: [],
        createdAt: '2026-06-03T00:00:00Z',
      },
    ] as never);
    vi.mocked(api.getActiveWorkflowVersion).mockResolvedValue({
      workflowDefinitionId: { value: 'wf-1' },
      workflowVersionId: 'ver-3',
      activatedAtUtc: '2026-06-04T00:00:00Z',
    } as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([] as never);
    vi.mocked(api.activateWorkflowVersion).mockResolvedValue({
      workflowDefinitionId: { value: 'wf-1' },
      workflowVersionId: 'ver-2',
      activatedAtUtc: '2026-06-04T01:00:00Z',
    } as never);
    vi.mocked(api.triggerWorkflow).mockResolvedValue({
      id: 'exec-1',
      workflowDefinitionId: { value: 'wf-1' },
      status: 'Pending',
      createdAt: '2026-06-04T00:00:00Z',
      updatedAt: '2026-06-04T00:00:00Z',
      globalVariables: {},
      nodeStates: [],
      triggerOrigin: 'manual',
    } as never);

    const { getByRole } = render(
      <Canvas
        workflowId="wf-1"
        onSaved={() => {}}
        onTriggered={() => {}}
      />,
    );

    await waitFor(() => {
      expect(api.getWorkflowVersions).toHaveBeenCalledWith('wf-1');
    });

    // Open the runtime-version combobox and pick v2 (selecting previews it); Run then activates it.
    fireEvent.click(getByRole('button', { name: 'Runtime version' }));
    fireEvent.click(getByRole('option', { name: 'Version 2' }));
    fireEvent.click(getByRole('button', { name: 'Run selected version' }));

    await waitFor(() => {
      expect(api.activateWorkflowVersion).toHaveBeenCalledWith('wf-1', 'ver-2');
      expect(api.triggerWorkflow).toHaveBeenCalledWith('wf-1');
    });
  });

  it('runs the selected workflow version without saving the current draft again', async () => {
    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Loaded Workflow',
      nodes: [],
      edges: [],
    } as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([
      {
        id: 'ver-3',
        workflowDefinitionId: { value: 'wf-1' },
        versionNumber: 3,
        nodes: [],
        edges: [],
        createdAt: '2026-06-04T00:00:00Z',
      },
    ] as never);
    vi.mocked(api.getActiveWorkflowVersion).mockResolvedValue({
      workflowDefinitionId: { value: 'wf-1' },
      workflowVersionId: 'ver-3',
      activatedAtUtc: '2026-06-04T00:00:00Z',
    } as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([] as never);
    vi.mocked(api.triggerWorkflow).mockResolvedValue({
      id: 'exec-1',
      workflowDefinitionId: { value: 'wf-1' },
      status: 'Pending',
      createdAt: '2026-06-04T00:00:00Z',
      updatedAt: '2026-06-04T00:00:00Z',
      globalVariables: {},
      nodeStates: [],
      triggerOrigin: 'manual',
    } as never);

    const onTriggered = vi.fn();
    const { getByLabelText } = render(
      <Canvas workflowId="wf-1" onSaved={() => {}} onTriggered={onTriggered} />,
    );

    fireEvent.click(await waitFor(() => getByLabelText('Run selected version')));

    await waitFor(() => {
      expect(api.activateWorkflowVersion).toHaveBeenCalledWith('wf-1', 'ver-3');
      expect(api.triggerWorkflow).toHaveBeenCalledWith('wf-1');
      expect(api.saveWorkflow).not.toHaveBeenCalled();
      expect(onTriggered).toHaveBeenCalledWith('exec-1');
    });
  });

  it('loads an existing workflow once even after node package metadata arrives', async () => {
    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Loaded Workflow',
      nodes: [
        {
          id: { value: 'start-1' },
          type: 'start',
          properties: { _metadata: { x: 150, y: 200 } },
        },
      ],
      edges: [],
    } as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([] as never);
    vi.mocked(api.getActiveWorkflowVersion).mockResolvedValue(null as never);

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

    render(
      <Canvas
        workflowId="wf-1"
        onSaved={() => {}}
        onTriggered={() => {}}
      />,
    );

    await waitFor(() => {
      expect(api.getWorkflow).toHaveBeenCalledTimes(1);
    });

    await waitFor(() => {
      expect(api.getNodePackages).toHaveBeenCalledTimes(1);
    });

    await waitFor(() => {
      expect(api.getWorkflow).toHaveBeenCalledTimes(1);
    });
  });

  it('applies trigger metadata when node packages load before the workflow resolves', async () => {
    let resolveWorkflow: ((value: unknown) => void) | undefined;
    const workflowPromise = new Promise((resolve) => {
      resolveWorkflow = resolve;
    });

    vi.mocked(api.getWorkflow).mockReturnValue(workflowPromise as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([] as never);
    vi.mocked(api.getActiveWorkflowVersion).mockResolvedValue(null as never);

    vi.mocked(api.getNodePackages).mockResolvedValue([
      {
        id: 'scheduler',
        displayName: 'Scheduler',
        category: 'Trigger',
        versions: [
          {
            manifestJson: JSON.stringify({ DisplayName: 'Cron Scheduler', TriggerOnly: true, Outputs: [{ Name: 'triggeredAt' }] }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
      {
        id: 'log',
        displayName: 'Log',
        category: 'Utility',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Log', outputs: [{ name: 'success' }] }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
    ] as never);

    const { getByTestId } = render(
      <Canvas
        workflowId="wf-1"
        onSaved={() => {}}
        onTriggered={() => {}}
      />,
    );

    await waitFor(() => {
      expect(api.getNodePackages).toHaveBeenCalledTimes(1);
      expect(api.getWorkflow).toHaveBeenCalledTimes(1);
    });

    resolveWorkflow?.({
      id: { value: 'wf-1' },
      name: 'Loaded Workflow',
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
    });

    await waitFor(() => {
      expect(getByTestId('react-flow-nodes').textContent).toContain('"triggerOnly":true');
      expect(getByTestId('react-flow-nodes').textContent).toContain('"outputHandles":["triggeredAt"]');
    });
  });
});