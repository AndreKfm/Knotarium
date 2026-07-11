/* eslint-disable @typescript-eslint/no-explicit-any -- loose node/edge shapes in the test harness */
import { fireEvent, render, screen, waitFor, act } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { Canvas } from '../components/Canvas';
import { api } from '../utils/api';
import { useSubflowOpenStore } from '../stores/useSubflowOpenStore';

// ─────────────────────────────────────────────────────────────────────────────
// Integration harness for the layout toolbar (Tidy / Align / Distribute).
// Mirrors the autoConnect.test.tsx mock: React Flow is replaced with a thin mock
// that renders nodes/edges as JSON and captures the latest RF callbacks so the
// test can drive selection. The toolbar JSX is a sibling of <ReactFlow>, so it
// renders whenever Canvas does.
// ─────────────────────────────────────────────────────────────────────────────

const store: { nodes: any[]; edges: any[]; handlers: Record<string, any> } = { nodes: [], edges: [], handlers: {} };

vi.mock('../utils/nodeTypes', () => ({ createNodeTypes: () => ({}) }));
vi.mock('../components/SidebarPalette', () => ({ SidebarPalette: () => <div data-testid="sidebar-palette" /> }));
vi.mock('../components/PropertiesPanel', () => ({ PropertiesPanel: () => <div data-testid="properties-panel" /> }));
vi.mock('../components/VariablesPanel', () => ({ VariablesPanel: () => <div data-testid="variables-panel" /> }));

let loadedGraph: { nodes: any[]; edges: any[] } = { nodes: [], edges: [] };
vi.mock('../utils/schemaMapper', () => ({
  schemaMapper: {
    toReactFlow: () => ({ nodes: loadedGraph.nodes, edges: loadedGraph.edges }),
    toBackend: (_id: string, name: string, nodes: any[], edges: any[]) => ({ name, nodes, edges }),
  },
}));

vi.mock('../utils/api', () => ({
  api: {
    getWorkflow: vi.fn(),
    getWorkflows: vi.fn().mockResolvedValue([]),
    getWorkflowVersions: vi.fn().mockResolvedValue([]),
    getActiveWorkflowVersion: vi.fn().mockResolvedValue(null),
    getNodePackages: vi.fn().mockResolvedValue([]),
    validateWorkflow: vi.fn().mockResolvedValue([]),
    saveWorkflow: vi.fn(),
    publishWorkflow: vi.fn(),
    activateWorkflowVersion: vi.fn(),
    triggerWorkflow: vi.fn(),
  },
}));

vi.mock('@xyflow/react', async () => {
  const React = await vi.importActual<typeof import('react')>('react');
  return {
    ReactFlowProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
    ReactFlow: ({ children, nodes, edges, ...rest }: { children?: ReactNode; nodes?: any[]; edges?: any[] } & Record<string, any>) => {
      store.nodes = nodes ?? [];
      store.edges = edges ?? [];
      store.handlers = rest;
      return (
        <div data-testid="react-flow">
          <pre data-testid="rf-nodes">{JSON.stringify(nodes ?? [])}</pre>
          {children}
        </div>
      );
    },
    MiniMap: () => null,
    Controls: () => null,
    Background: () => null,
    BackgroundVariant: { Dots: 'dots' },
    SelectionMode: { Partial: 'partial', Full: 'full' },
    addEdge: (edge: unknown, eds: unknown[]) => [...eds, edge],
    reconnectEdge: (_o: unknown, _c: unknown, eds: unknown[]) => eds,
    useReactFlow: () => ({
      screenToFlowPosition: () => ({ x: 0, y: 0 }),
      getInternalNode: () => undefined, // unmeasured -> layout falls back to default size
      getNodes: () => store.nodes,
      setCenter: vi.fn(),
      fitView: vi.fn(),
      setViewport: vi.fn(),
      getViewport: () => ({ x: 0, y: 0, zoom: 1 }),
      getZoom: () => 1,
    }),
    useStoreApi: () => ({ setState: vi.fn(), getState: () => ({}) }),
    useConnection: (selector?: (c: { inProgress: boolean }) => unknown) =>
      selector ? selector({ inProgress: false }) : { inProgress: false },
    useNodeConnections: () => [],
    useNodesState: (initial: unknown[]) => {
      const [nodes, setNodes] = React.useState(initial);
      return [nodes, setNodes, vi.fn()] as const;
    },
    useEdgesState: (initial: unknown[]) => {
      const [edges, setEdges] = React.useState(initial);
      return [edges, setEdges, vi.fn()] as const;
    },
  };
});

function node(id: string, x: number, y: number, selected = false) {
  return {
    id,
    type: 'log',
    position: { x, y },
    selected,
    data: { properties: {}, displayName: id, triggerOnly: false, outputHandles: ['result'] },
  };
}

function readNodes(): any[] {
  return JSON.parse(screen.getByTestId('rf-nodes').textContent || '[]');
}
function byId(nodes: any[]) {
  return Object.fromEntries(nodes.map((n) => [n.id, n]));
}

async function renderCanvas() {
  render(<Canvas workflowId="wf-1" onSaved={() => {}} onTriggered={() => {}} />);
  await screen.findByTestId('react-flow');
  await waitFor(() => expect(readNodes().length).toBeGreaterThan(0));
}

// Drive React Flow's onSelectionChange to populate the selected-count state.
function selectInStore(ids: string[]) {
  act(() => {
    store.handlers.onSelectionChange?.({ nodes: store.nodes.filter((n) => ids.includes(n.id)), edges: [] });
  });
}

describe('layout toolbar (integration)', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('Tidy re-arranges a chain left-to-right', async () => {
    // Deliberately scrambled x so left-to-right is not already satisfied.
    loadedGraph = {
      nodes: [node('a', 400, 0), node('b', 0, 0), node('c', 200, 50)],
      edges: [
        { id: 'e1', source: 'a', sourceHandle: 'result', target: 'b', targetHandle: 'in' },
        { id: 'e2', source: 'b', sourceHandle: 'result', target: 'c', targetHandle: 'in' },
      ],
    };
    await renderCanvas();

    fireEvent.click(screen.getByTitle(/Tidy layout/i));

    await waitFor(() => {
      const n = byId(readNodes());
      expect(n.a.position.x).toBeLessThan(n.b.position.x);
      expect(n.b.position.x).toBeLessThan(n.c.position.x);
    });
  });

  it('hides the align toolbar until ≥2 nodes are selected', async () => {
    loadedGraph = { nodes: [node('a', 0, 0), node('b', 0, 200)], edges: [] };
    await renderCanvas();

    expect(screen.queryByTitle('Align left')).toBeNull();
    selectInStore(['a', 'b']);
    expect(screen.getByTitle('Align left')).toBeTruthy();
  });

  it('Align left snaps selected nodes to the same x', async () => {
    loadedGraph = {
      nodes: [node('a', 10, 0, true), node('b', 90, 200, true)],
      edges: [],
    };
    await renderCanvas();
    selectInStore(['a', 'b']);

    fireEvent.click(screen.getByTitle('Align left'));

    await waitFor(() => {
      const n = byId(readNodes());
      expect(n.a.position.x).toBe(10);
      expect(n.b.position.x).toBe(10); // min left
      expect(n.b.position.y).toBe(200); // other axis untouched
    });
  });

  it('shows distribute buttons only at ≥3 selected', async () => {
    loadedGraph = { nodes: [node('a', 0, 0), node('b', 100, 0), node('c', 300, 0)], edges: [] };
    await renderCanvas();

    selectInStore(['a', 'b']);
    expect(screen.queryByTitle('Distribute horizontally')).toBeNull();
    selectInStore(['a', 'b', 'c']);
    expect(screen.getByTitle('Distribute horizontally')).toBeTruthy();
  });
});

describe('snap to grid (integration)', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('passes snapToGrid off to React Flow by default, with a square grid', async () => {
    loadedGraph = { nodes: [node('a', 0, 0)], edges: [] };
    await renderCanvas();

    expect(store.handlers.snapToGrid).toBe(false);
    expect(store.handlers.snapGrid).toEqual([24, 24]);
  });

  it('toggles snapToGrid on and off via the Grid button', async () => {
    loadedGraph = { nodes: [node('a', 0, 0)], edges: [] };
    await renderCanvas();

    fireEvent.click(screen.getByTitle('Snap to grid: off'));
    await waitFor(() => expect(store.handlers.snapToGrid).toBe(true));
    expect(screen.getByTitle('Snap to grid: on')).toHaveProperty('ariaPressed', 'true');

    fireEvent.click(screen.getByTitle('Snap to grid: on'));
    await waitFor(() => expect(store.handlers.snapToGrid).toBe(false));
  });

  it('Tidy snaps node positions to the 24px grid when snap is enabled', async () => {
    loadedGraph = {
      nodes: [node('a', 7, 0), node('b', 113, 41), node('c', 219, 88)],
      edges: [
        { id: 'e1', source: 'a', sourceHandle: 'result', target: 'b', targetHandle: 'in' },
        { id: 'e2', source: 'b', sourceHandle: 'result', target: 'c', targetHandle: 'in' },
      ],
    };
    await renderCanvas();

    fireEvent.click(screen.getByTitle('Snap to grid: off')); // enable snap
    fireEvent.click(screen.getByTitle(/Tidy layout/i));

    await waitFor(() => {
      const ns = readNodes();
      expect(ns.length).toBe(3);
      for (const n of ns) {
        expect(n.position.x % 24).toBe(0);
        expect(n.position.y % 24).toBe(0);
      }
    });
  });

  it('Tidy leaves off-grid positions when snap is disabled', async () => {
    loadedGraph = {
      nodes: [node('a', 7, 0), node('b', 113, 41)],
      edges: [{ id: 'e1', source: 'a', sourceHandle: 'result', target: 'b', targetHandle: 'in' }],
    };
    await renderCanvas();

    fireEvent.click(screen.getByTitle(/Tidy layout/i)); // snap stays off

    await waitFor(() => {
      const ns = readNodes();
      // At least one coordinate is not a grid multiple (dagre output is not grid-aligned).
      const allOnGrid = ns.every((n) => n.position.x % 24 === 0 && n.position.y % 24 === 0);
      expect(allOnGrid).toBe(false);
    });
  });
});

describe('keyboard-shortcut help (integration)', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('opens the help overlay from the toolbar "?" button and closes on Escape', async () => {
    loadedGraph = { nodes: [node('a', 0, 0)], edges: [] };
    await renderCanvas();

    expect(screen.queryByRole('dialog', { name: 'Keyboard shortcuts' })).toBeNull();
    fireEvent.click(screen.getByTitle('Keyboard shortcuts (?)'));
    expect(screen.getByRole('dialog', { name: 'Keyboard shortcuts' })).toBeTruthy();

    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Keyboard shortcuts' })).toBeNull());
  });

  it('toggles the help overlay with the "?" key', async () => {
    loadedGraph = { nodes: [node('a', 0, 0)], edges: [] };
    await renderCanvas();

    fireEvent.keyDown(window, { key: '?' });
    expect(screen.getByRole('dialog', { name: 'Keyboard shortcuts' })).toBeTruthy();
  });
});

describe('subflow drill-down (integration)', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    useSubflowOpenStore.getState().clearRequest();
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
    vi.mocked(api.saveWorkflow).mockResolvedValue(undefined as never);
  });
  afterEach(() => vi.clearAllMocks());

  function subflowNode(id: string, subflowId: string) {
    return {
      id,
      type: 'subflow',
      position: { x: 0, y: 0 },
      data: { properties: { subflowId }, displayName: id, triggerOnly: false, outputHandles: ['result'] },
    };
  }

  it('consumes an open request from the store and navigates via onOpenSubflow', async () => {
    loadedGraph = { nodes: [subflowNode('s1', 'child-wf')], edges: [] };
    const onOpenSubflow = vi.fn();
    render(<Canvas workflowId="wf-1" onSaved={() => {}} onTriggered={() => {}} onOpenSubflow={onOpenSubflow} />);
    await screen.findByTestId('react-flow');
    await waitFor(() => expect(readNodes().length).toBeGreaterThan(0));

    act(() => {
      useSubflowOpenStore.getState().requestOpen('s1');
    });

    await waitFor(() => expect(onOpenSubflow).toHaveBeenCalledWith('child-wf'));
    // The request is cleared after being consumed.
    expect(useSubflowOpenStore.getState().requestNodeId).toBeNull();
  });

  it('ignores a request for an unknown node id', async () => {
    loadedGraph = { nodes: [subflowNode('s1', 'child-wf')], edges: [] };
    const onOpenSubflow = vi.fn();
    render(<Canvas workflowId="wf-1" onSaved={() => {}} onTriggered={() => {}} onOpenSubflow={onOpenSubflow} />);
    await screen.findByTestId('react-flow');
    await waitFor(() => expect(readNodes().length).toBeGreaterThan(0));

    act(() => {
      useSubflowOpenStore.getState().requestOpen('does-not-exist');
    });

    await waitFor(() => expect(useSubflowOpenStore.getState().requestNodeId).toBeNull());
    expect(onOpenSubflow).not.toHaveBeenCalled();
  });
});
