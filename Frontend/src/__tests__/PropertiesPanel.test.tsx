import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PropertiesPanel } from '../components/PropertiesPanel';
import { api } from '../utils/api';

vi.mock('../utils/api', () => ({
  api: {
    getNodePackages: vi.fn(),
    getWorkflowSchedules: vi.fn(),
    getLatestExecution: vi.fn().mockResolvedValue(null),
  },
}));

vi.mock('../components/shared/ManifestForm', () => ({
  ManifestForm: () => <div data-testid="manifest-form" />,
}));

vi.mock('../components/RestCallerPropertyForm', () => ({
  RestCallerPropertyForm: () => <div data-testid="rest-caller-property-form" />,
}));

describe('PropertiesPanel', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('shows next fire information for a selected scheduler node', async () => {
    vi.mocked(api.getNodePackages).mockResolvedValue([
      {
        id: 'scheduler',
        displayName: 'Scheduler',
        category: 'Trigger',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Cron Scheduler' }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
    ] as never);

    vi.mocked(api.getWorkflowSchedules).mockResolvedValue([
      {
        nodeId: 'scheduler-1',
        cronExpression: '*/5 * * * *',
        timeZoneId: 'UTC',
        nextFireAtUtc: '2026-05-31T17:55:00Z',
        isActive: true,
      },
    ] as never);

    render(
      <PropertiesPanel
        workflowId="wf-1"
        selectedNode={{
          id: 'scheduler-1',
          type: 'scheduler',
          position: { x: 0, y: 0 },
          data: { properties: {} },
        } as never}
        selectedEdge={null}
        onUpdateNodeProperties={() => {}}
        onDeleteNode={() => {}}
        onDeleteEdge={() => {}}
      />,
    );

    await waitFor(() => {
      expect(api.getWorkflowSchedules).toHaveBeenCalledWith('wf-1');
    });

    expect(await screen.findByText(/Next fire:/)).toBeInTheDocument();
    expect(screen.getByText(/\*\/5 \* \* \* \*/)).toBeInTheDocument();
    expect(screen.getByText(/Active schedule/)).toBeInTheDocument();
  });

  it('renders safely when no node or edge is selected', async () => {
    vi.mocked(api.getNodePackages).mockResolvedValue([]);

    render(
      <PropertiesPanel
        workflowId="wf-1"
        selectedNode={null}
        selectedEdge={null}
        onUpdateNodeProperties={() => {}}
        onDeleteNode={() => {}}
        onDeleteEdge={() => {}}
      />,
    );

    await waitFor(() => {
      expect(screen.getByText(/Property Inspector/)).toBeInTheDocument();
    });
    expect(screen.getByText(/Select a node on the canvas/)).toBeInTheDocument();
  });

  it('handles selecting a node, then deselecting without triggering infinite loops', async () => {
    vi.mocked(api.getNodePackages).mockResolvedValue([]);

    const { rerender } = render(
      <PropertiesPanel
        workflowId="wf-1"
        selectedNode={null}
        selectedEdge={null}
        onUpdateNodeProperties={() => {}}
        onDeleteNode={() => {}}
        onDeleteEdge={() => {}}
      />,
    );

    await waitFor(() => {
      expect(screen.getByText(/Property Inspector/)).toBeInTheDocument();
    });

    // Select a node
    rerender(
      <PropertiesPanel
        workflowId="wf-1"
        selectedNode={{
          id: 'node-1',
          type: 'log',
          position: { x: 0, y: 0 },
          data: { properties: {} },
        } as never}
        selectedEdge={null}
        onUpdateNodeProperties={() => {}}
        onDeleteNode={() => {}}
        onDeleteEdge={() => {}}
      />,
    );

    await waitFor(() => {
      expect(screen.getByText(/log Node Properties/)).toBeInTheDocument();
    });

    // Deselect the node
    rerender(
      <PropertiesPanel
        workflowId="wf-1"
        selectedNode={null}
        selectedEdge={null}
        onUpdateNodeProperties={() => {}}
        onDeleteNode={() => {}}
        onDeleteEdge={() => {}}
      />,
    );

    await waitFor(() => {
      expect(screen.getByText(/Property Inspector/)).toBeInTheDocument();
    });
  });

  it('renders_RestCallerPropertyForm_for_openapi_node', async () => {
    vi.mocked(api.getNodePackages).mockResolvedValue([]);

    render(
      <PropertiesPanel
        workflowId="wf-1"
        selectedNode={{
          id: 'openapi-node-1',
          type: 'openapi.petstore',
          position: { x: 0, y: 0 },
          data: { properties: { operationId: 'getPetById', arguments: {} } },
        } as never}
        selectedEdge={null}
        onUpdateNodeProperties={() => {}}
        onDeleteNode={() => {}}
        onDeleteEdge={() => {}}
      />
    );

    await waitFor(() => {
      expect(screen.getByTestId('rest-caller-property-form')).toBeInTheDocument();
    });
  });

  it('renders_ManifestForm_for_non_openapi_node', async () => {
    // A generic node (Log) that has no dedicated property form still falls back to ManifestForm.
    // (HttpRequest / scheduler now have their own dedicated forms, so they no longer exercise this path.)
    vi.mocked(api.getNodePackages).mockResolvedValue([
      {
        id: 'log',
        displayName: 'Log',
        category: 'Utility',
        versions: [
          {
            manifestJson: JSON.stringify({ displayName: 'Log' }),
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      },
    ] as never);

    render(
      <PropertiesPanel
        workflowId="wf-1"
        selectedNode={{
          id: 'log-node-1',
          type: 'log',
          position: { x: 0, y: 0 },
          data: { properties: {} },
        } as never}
        selectedEdge={null}
        onUpdateNodeProperties={() => {}}
        onDeleteNode={() => {}}
        onDeleteEdge={() => {}}
      />
    );

    await waitFor(() => {
      expect(screen.getByTestId('manifest-form')).toBeInTheDocument();
    });
  });
});