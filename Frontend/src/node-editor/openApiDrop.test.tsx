import { fireEvent, render, waitFor, screen } from '@testing-library/react';
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

describe('openApiDrop', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('drop_openapi_operation_creates_node', async () => {
    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Loaded Workflow',
      nodes: [],
      edges: [],
    } as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([] as never);
    vi.mocked(api.getActiveWorkflowVersion).mockResolvedValue(null as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([
      {
        id: 'openapi.petstore',
        displayName: 'Petstore API',
        category: 'Integrations',
        versions: [],
      },
    ] as never);

    render(<Canvas workflowId="wf-1" onSaved={() => {}} onTriggered={() => {}} />);

    const reactFlowElement = await screen.findByTestId('react-flow');

    // Create custom DataTransfer mock
    const dragData = {
      type: 'openapi-operation',
      specId: 'petstore',
      packageId: 'openapi.petstore',
      operationId: 'getPetById',
    };

    const dataTransfer = {
      getData: (format: string) => {
        if (format === 'application/json') {
          return JSON.stringify(dragData);
        }
        return '';
      },
    };

    // Fire the drop event
    fireEvent.drop(reactFlowElement, {
      dataTransfer,
      clientX: 200,
      clientY: 300,
    });

    // Verify node is created with properties
    await waitFor(() => {
      const nodesPre = screen.getByTestId('react-flow-nodes');
      expect(nodesPre.textContent).toContain('"type":"openapi.petstore"');
      expect(nodesPre.textContent).toContain('"operationId":"getPetById"');
    });
  });

  it('drop_unknown_type_ignored', async () => {
    vi.mocked(api.getWorkflow).mockResolvedValue({
      id: { value: 'wf-1' },
      name: 'Loaded Workflow',
      nodes: [],
      edges: [],
    } as never);
    vi.mocked(api.getWorkflowVersions).mockResolvedValue([] as never);
    vi.mocked(api.getActiveWorkflowVersion).mockResolvedValue(null as never);
    vi.mocked(api.getNodePackages).mockResolvedValue([
      {
        id: 'openapi.petstore',
        displayName: 'Petstore API',
        category: 'Integrations',
        versions: [],
      },
    ] as never);

    render(<Canvas workflowId="wf-1" onSaved={() => {}} onTriggered={() => {}} />);

    const reactFlowElement = await screen.findByTestId('react-flow');

    const dragData = {
      type: 'some-other-type',
      specId: 'petstore',
      packageId: 'openapi.petstore',
      operationId: 'getPetById',
    };

    const dataTransfer = {
      getData: (format: string) => {
        if (format === 'application/json') {
          return JSON.stringify(dragData);
        }
        return '';
      },
    };

    fireEvent.drop(reactFlowElement, {
      dataTransfer,
      clientX: 200,
      clientY: 300,
    });

    // Wait a brief moment and verify no nodes are added
    await new Promise((resolve) => setTimeout(resolve, 50));
    const nodesPre = screen.getByTestId('react-flow-nodes');
    expect(JSON.parse(nodesPre.textContent || '[]')).toHaveLength(0);
  });
});
