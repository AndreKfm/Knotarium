// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

/* eslint-disable @typescript-eslint/no-explicit-any -- loose node/edge shapes in the test harness */
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { Canvas } from '../components/Canvas';
import { api } from '../utils/api';
import { DEFAULT_NODE_WIDTH } from './canvasGeometry';
import { useVariableStore } from '../stores/useVariableStore';

// ─────────────────────────────────────────────────────────────────────────────
// Integration harness for the auto-connect drop behaviours (Features A & B).
//
// React Flow is replaced with a thin mock that:
//   • renders the current nodes/edges as JSON for assertions,
//   • forwards drop events to the real handleCanvasDrop,
//   • exposes getNodes()/getInternalNode() backed by a live store, deriving
//     handle bounds from a fixed node geometry so the geometry helpers resolve
//     real port positions.
// schemaMapper is mocked so we control the loaded graph precisely.
// ─────────────────────────────────────────────────────────────────────────────

const NODE_W = 200;
const NODE_H = 80;
const HS = 8;

// Live mirror of the canvas state, kept in sync by the ReactFlow mock on render.
// `handlers` captures the latest React Flow callbacks so tests can drive them directly.
const store: { nodes: any[]; edges: any[]; handlers: Record<string, any>; setCenter: ReturnType<typeof vi.fn> } = {
  nodes: [],
  edges: [],
  handlers: {},
  setCenter: vi.fn(),
};

// jsdom doesn't transfer clientX/clientY through a synthetic drop event, so the
// mocked screenToFlowPosition returns this per-test drop point instead.
let nextDrop = { x: 0, y: 0 };

function internalFor(id: string) {
  const n = store.nodes.find((x) => x.id === id);
  if (!n) return undefined;
  let ax = n.position.x;
  let ay = n.position.y;
  if (n.parentId) {
    const p = store.nodes.find((x) => x.id === n.parentId);
    if (p) {
      ax += p.position.x;
      ay += p.position.y;
    }
  }
  const isTrigger = Boolean(n.data?.triggerOnly);
  const outId = (n.data?.outputHandles?.[0] as string) || 'result';
  const source = [{ id: outId, x: NODE_W - HS / 2, y: NODE_H / 2 - HS / 2, width: HS, height: HS }];
  const target = isTrigger ? [] : [{ id: 'in', x: -HS / 2, y: NODE_H / 2 - HS / 2, width: HS, height: HS }];
  return {
    id,
    position: n.position,
    internals: { positionAbsolute: { x: ax, y: ay }, handleBounds: { source, target } },
  };
}

vi.mock('../utils/nodeTypes', () => ({ createNodeTypes: () => ({}) }));
vi.mock('../components/SidebarPalette', () => ({ SidebarPalette: () => <div data-testid="sidebar-palette" /> }));
vi.mock('../components/PropertiesPanel', () => ({ PropertiesPanel: () => <div data-testid="properties-panel" /> }));
vi.mock('../components/VariablesPanel', () => ({ VariablesPanel: () => <div data-testid="variables-panel" /> }));

// Control exactly which nodes/edges the canvas loads.
let loadedGraph: { nodes: any[]; edges: any[] } = { nodes: [], edges: [] };
vi.mock('../utils/schemaMapper', () => ({
  schemaMapper: {
    toReactFlow: () => ({ nodes: loadedGraph.nodes, edges: loadedGraph.edges }),
    toBackend: (_id: string, name: string, nodes: any[], edges: any[]) => ({ name, nodes, edges }),
  },
  definitionHasSavedPositions: () => true,
}));

vi.mock('../utils/api', () => ({
  api: {
    getWorkflow: vi.fn(),
    getWorkflows: vi.fn().mockResolvedValue([]),
    getWorkflowVersions: vi.fn().mockResolvedValue([]),
    getActiveWorkflowVersion: vi.fn().mockResolvedValue(null),
    getNodePackages: vi.fn(),
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
          <pre data-testid="rf-edges">{JSON.stringify(edges ?? [])}</pre>
          {children}
        </div>
      );
    },
    MiniMap: () => null,
    Controls: () => null,
    Background: () => null,
    BackgroundVariant: { Dots: 'dots' },
    SelectionMode: { Partial: 'partial', Full: 'full' },
    MarkerType: { ArrowClosed: 'arrowclosed', Arrow: 'arrow' },
    addEdge: (edge: unknown, existingEdges: unknown[]) => [...existingEdges, edge],
    reconnectEdge: (_o: unknown, _c: unknown, eds: unknown[]) => eds,
    useReactFlow: () => ({
      screenToFlowPosition: () => ({ ...nextDrop }),
      getInternalNode: (id: string) => internalFor(id),
      getNodes: () => store.nodes,
      setCenter: store.setCenter,
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

function pkg(id: string, opts: { triggerOnly?: boolean; outputs?: { name: string }[] } = {}) {
  const manifest = {
    id,
    displayName: id,
    triggerOnly: opts.triggerOnly ?? false,
    outputs: opts.outputs ?? [{ name: 'result' }],
  };
  return {
    id,
    displayName: id,
    category: 'Test',
    versions: [{ id: `${id}-v1`, createdAt: '2024-01-01T00:00:00Z', manifestJson: JSON.stringify(manifest) }],
  };
}

function node(id: string, type: string, x: number, y: number, triggerOnly = false) {
  return {
    id,
    type,
    position: { x, y },
    data: { properties: {}, displayName: id, triggerOnly, outputHandles: ['result'] },
  };
}

function readNodes(): any[] {
  return JSON.parse(screen.getByTestId('rf-nodes').textContent || '[]');
}
function readEdges(): any[] {
  return JSON.parse(screen.getByTestId('rf-edges').textContent || '[]');
}

async function renderCanvas() {
  render(<Canvas workflowId="wf-1" onSaved={() => {}} onTriggered={() => {}} />);
  await screen.findByTestId('react-flow');
  // Let metadata + load effects flush.
  await waitFor(() => expect(readNodes().length).toBeGreaterThan(0));
}

function dropPackage(packageId: string, x: number, y: number) {
  nextDrop = { x, y };
  fireEvent.drop(screen.getByTestId('react-flow'), {
    dataTransfer: {
      getData: (format: string) =>
        format === 'application/knotarium-node-package' ? packageId : '',
    },
  });
}

describe('Feature B — insert-on-edge', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('splices a dropped node into the wire under the drop point (A→new→B)', async () => {
    // start(trigger) --e1--> log ; output of start at (200,40), input of log at (400,40)
    loadedGraph = {
      nodes: [node('start-1', 'start', 0, 0, true), node('log-1', 'log', 400, 0)],
      edges: [{ id: 'e-start-1-result-log-1-in', source: 'start-1', sourceHandle: 'result', target: 'log-1', targetHandle: 'in' }],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('start', { triggerOnly: true }), pkg('log'), pkg('delay')] as never);

    await renderCanvas();
    expect(readEdges()).toHaveLength(1);

    // Drop a delay node right on the midpoint (300,40).
    dropPackage('delay', 300, 40);

    await waitFor(() => {
      const edges = readEdges();
      // Old direct edge gone, two new edges in its place.
      expect(edges.some((e) => e.id === 'e-start-1-result-log-1-in')).toBe(false);
      expect(edges).toHaveLength(2);
    });

    const edges = readEdges();
    const nodes = readNodes();
    const delay = nodes.find((n) => n.type === 'delay');
    expect(delay).toBeTruthy();

    // start → delay
    const upstream = edges.find((e) => e.source === 'start-1');
    expect(upstream?.target).toBe(delay.id);
    expect(upstream?.targetHandle).toBe('in');
    // delay → log
    const downstream = edges.find((e) => e.target === 'log-1');
    expect(downstream?.source).toBe(delay.id);
    expect(downstream?.targetHandle).toBe('in');

    // B shifted right to make room.
    const logX = nodes.find((n) => n.id === 'log-1').position.x;
    expect(logX).toBeGreaterThan(400);
    // New node centered in the *expanded* gap (midpoint + half the downstream shift),
    // so the A→new and new→B wires come out balanced rather than new hugging A.
    const delta = DEFAULT_NODE_WIDTH + 80;
    expect(delay.position.x).toBeCloseTo(300 - DEFAULT_NODE_WIDTH / 2 + delta / 2);
    // Sanity: the node now sits between A's output (200) and B's shifted input.
    expect(delay.position.x).toBeGreaterThan(200);
    expect(delay.position.x + DEFAULT_NODE_WIDTH).toBeLessThan(logX);
  });

  it('does not splice a trigger-only node (no input) — plain add instead', async () => {
    loadedGraph = {
      nodes: [node('start-1', 'start', 0, 0, true), node('log-1', 'log', 400, 0)],
      edges: [{ id: 'e1', source: 'start-1', sourceHandle: 'result', target: 'log-1', targetHandle: 'in' }],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('start', { triggerOnly: true }), pkg('log'), pkg('scheduler', { triggerOnly: true })] as never);

    await renderCanvas();
    dropPackage('scheduler', 300, 40);

    await waitFor(() => expect(readNodes().some((n) => n.type === 'scheduler')).toBe(true));
    const edges = readEdges();
    expect(edges).toHaveLength(1);
    expect(edges[0].id).toBe('e1'); // untouched
  });

  it('falls through to a plain drop when the point misses every wire', async () => {
    loadedGraph = {
      nodes: [node('start-1', 'start', 0, 0, true), node('log-1', 'log', 400, 0)],
      edges: [{ id: 'e1', source: 'start-1', sourceHandle: 'result', target: 'log-1', targetHandle: 'in' }],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('start', { triggerOnly: true }), pkg('log'), pkg('delay')] as never);

    await renderCanvas();
    // Far below the wire (y=400), outside tolerance.
    dropPackage('delay', 300, 400);

    await waitFor(() => expect(readNodes().some((n) => n.type === 'delay')).toBe(true));
    const edges = readEdges();
    expect(edges).toHaveLength(1);
    expect(edges[0].id).toBe('e1');
  });

  it('keeps the fan-in target handle when splicing onto an edge into a join node', async () => {
    loadedGraph = {
      nodes: [node('start-1', 'start', 0, 0, true), node('join-1', 'join', 400, 0)],
      edges: [{ id: 'e1', source: 'start-1', sourceHandle: 'result', target: 'join-1', targetHandle: 'in' }],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('start', { triggerOnly: true }), pkg('join'), pkg('delay')] as never);

    await renderCanvas();
    dropPackage('delay', 300, 40);

    await waitFor(() => expect(readEdges()).toHaveLength(2));
    const edges = readEdges();
    const delay = readNodes().find((n) => n.type === 'delay');
    const intoJoin = edges.find((e) => e.target === 'join-1');
    expect(intoJoin?.source).toBe(delay.id);
    expect(intoJoin?.targetHandle).toBe('in');
    expect(edges.some((e) => e.id === 'e1')).toBe(false);
  });
});

describe('Feature A — proximity snap', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('auto-wires a dropped node whose input lands near a free output (upstream snap)', async () => {
    // Existing log node: output 'result' at (200,40), input 'in' at (0,40).
    loadedGraph = { nodes: [node('log-1', 'log', 0, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log'), pkg('delay')] as never);

    await renderCanvas();
    expect(readEdges()).toHaveLength(0);

    // Drop delay so its input (dropX,40) sits ~10px from log's output (200,40).
    dropPackage('delay', 210, 0);

    await waitFor(() => expect(readEdges()).toHaveLength(1));
    const edge = readEdges()[0];
    const delay = readNodes().find((n) => n.type === 'delay');
    expect(edge.source).toBe('log-1');
    expect(edge.sourceHandle).toBe('result');
    expect(edge.target).toBe(delay.id);
    expect(edge.targetHandle).toBe('in');
  });

  it('does not auto-wire when no free port is within the threshold', async () => {
    loadedGraph = { nodes: [node('log-1', 'log', 0, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log'), pkg('delay')] as never);

    await renderCanvas();
    // Far away — nothing within PROXIMITY_THRESHOLD.
    dropPackage('delay', 1000, 1000);

    await waitFor(() => expect(readNodes().some((n) => n.type === 'delay')).toBe(true));
    // Give the deferred proximity pass time to (not) fire.
    await new Promise((r) => setTimeout(r, 50));
    expect(readEdges()).toHaveLength(0);
  });

  it('connects a trigger-only node downstream only (no input to snap upstream)', async () => {
    // Existing log node: input 'in' at (400,40).
    loadedGraph = { nodes: [node('log-1', 'log', 400, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log'), pkg('scheduler', { triggerOnly: true })] as never);

    await renderCanvas();
    // Drop scheduler so its output (dropX+200,40) sits ~10px from log's input (400,40).
    dropPackage('scheduler', 210, 0);

    await waitFor(() => expect(readEdges()).toHaveLength(1));
    const edge = readEdges()[0];
    const scheduler = readNodes().find((n) => n.type === 'scheduler');
    expect(edge.source).toBe(scheduler.id);
    expect(edge.target).toBe('log-1');
    expect(edge.targetHandle).toBe('in');
  });

  it('snaps a moved node to a nearby free port on drag-stop', async () => {
    // Two unconnected nodes far apart; then drive onNodeDragStop with the second node
    // repositioned so its input lands next to the first node's output.
    loadedGraph = {
      nodes: [node('log-1', 'log', 0, 0), node('delay-1', 'delay', 800, 800)],
      edges: [],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log'), pkg('delay')] as never);

    await renderCanvas();
    expect(readEdges()).toHaveLength(0);

    // Simulate React Flow committing the drag: delay-1 now sits at (210,0) so its input
    // (210,40) is ~10px from log-1's output (200,40).
    const moved = { ...readNodes().find((n) => n.id === 'delay-1'), position: { x: 210, y: 0 } };
    // The drag-stop handler reads node.position to recompute placement; reflect the move
    // in the live store too so getInternalNode resolves the new port positions.
    store.nodes = store.nodes.map((n) => (n.id === 'delay-1' ? { ...n, position: { x: 210, y: 0 } } : n));
    store.handlers.onNodeDragStop({}, moved);

    await waitFor(() => expect(readEdges()).toHaveLength(1));
    const edge = readEdges()[0];
    expect(edge.source).toBe('log-1');
    expect(edge.sourceHandle).toBe('result');
    expect(edge.target).toBe('delay-1');
    expect(edge.targetHandle).toBe('in');
  });
});

describe('Feature A — drag highlight (A3)', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    useVariableStore.getState().setSnapCandidateKeys([]);
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('marks the candidate ports while dragging, and clears them on drop', async () => {
    loadedGraph = {
      nodes: [node('log-1', 'log', 0, 0), node('delay-1', 'delay', 800, 800)],
      edges: [],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log'), pkg('delay')] as never);

    await renderCanvas();
    expect(useVariableStore.getState().snapCandidateKeys).toEqual([]);

    // Drag delay-1 next to log-1's output.
    const moved = { ...readNodes().find((n) => n.id === 'delay-1'), position: { x: 210, y: 0 } };
    store.nodes = store.nodes.map((n) => (n.id === 'delay-1' ? { ...n, position: { x: 210, y: 0 } } : n));
    store.handlers.onNodeDrag({}, moved);

    const keys = useVariableStore.getState().snapCandidateKeys;
    expect(keys).toContain('log-1 result');
    expect(keys).toContain('delay-1 in');

    // Releasing clears the highlight.
    store.handlers.onNodeDragStop({}, moved);
    expect(useVariableStore.getState().snapCandidateKeys).toEqual([]);
  });

  it('clears candidates when the dragged node moves away from any free port', async () => {
    loadedGraph = {
      nodes: [node('log-1', 'log', 0, 0), node('delay-1', 'delay', 800, 800)],
      edges: [],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log'), pkg('delay')] as never);

    await renderCanvas();

    // First near → candidates populate.
    let moved = { ...readNodes().find((n) => n.id === 'delay-1'), position: { x: 210, y: 0 } };
    store.nodes = store.nodes.map((n) => (n.id === 'delay-1' ? { ...n, position: { x: 210, y: 0 } } : n));
    store.handlers.onNodeDrag({}, moved);
    expect(useVariableStore.getState().snapCandidateKeys.length).toBeGreaterThan(0);

    // Then far → candidates clear.
    moved = { ...moved, position: { x: 2000, y: 2000 } };
    store.nodes = store.nodes.map((n) => (n.id === 'delay-1' ? { ...n, position: { x: 2000, y: 2000 } } : n));
    store.handlers.onNodeDrag({}, moved);
    expect(useVariableStore.getState().snapCandidateKeys).toEqual([]);
  });
});

function ctrlZ(shift = false) {
  window.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, shiftKey: shift, bubbles: true }));
}
function pressDelete() {
  window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }));
}
function ctrlKey(key: string) {
  window.dispatchEvent(new KeyboardEvent('keydown', { key, ctrlKey: true, bubbles: true }));
}

describe('Phase 2 — Undo/Redo', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('undoes and redoes a dropped node', async () => {
    loadedGraph = { nodes: [node('log-1', 'log', 0, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log'), pkg('delay')] as never);

    await renderCanvas();
    expect(readNodes()).toHaveLength(1);

    dropPackage('delay', 1000, 1000); // far → plain add, no snap
    await waitFor(() => expect(readNodes()).toHaveLength(2));

    ctrlZ();
    await waitFor(() => expect(readNodes()).toHaveLength(1));
    expect(readNodes().some((n) => n.type === 'delay')).toBe(false);

    ctrlZ(true); // redo
    await waitFor(() => expect(readNodes()).toHaveLength(2));
    expect(readNodes().some((n) => n.type === 'delay')).toBe(true);
  });

  it('undoes and redoes a connection', async () => {
    loadedGraph = { nodes: [node('a-1', 'log', 0, 0), node('b-1', 'log', 600, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);

    await renderCanvas();
    expect(readEdges()).toHaveLength(0);

    store.handlers.onConnect({ source: 'a-1', sourceHandle: 'result', target: 'b-1', targetHandle: 'in' });
    await waitFor(() => expect(readEdges()).toHaveLength(1));

    ctrlZ();
    await waitFor(() => expect(readEdges()).toHaveLength(0));

    ctrlZ(true);
    await waitFor(() => expect(readEdges()).toHaveLength(1));
  });

  it('undoes a delete (selected node + its edges), restoring node and wire', async () => {
    loadedGraph = {
      nodes: [node('a-1', 'log', 0, 0), { ...node('b-1', 'log', 600, 0), selected: true }],
      edges: [{ id: 'e1', source: 'a-1', sourceHandle: 'result', target: 'b-1', targetHandle: 'in' }],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);

    await renderCanvas();
    expect(readNodes()).toHaveLength(2);
    expect(readEdges()).toHaveLength(1);

    pressDelete();
    await waitFor(() => expect(readNodes()).toHaveLength(1));
    expect(readEdges()).toHaveLength(0); // connected edge removed too

    ctrlZ();
    await waitFor(() => expect(readNodes()).toHaveLength(2));
    expect(readEdges()).toHaveLength(1);
  });

  it('undoes a node move (reparent into a container)', async () => {
    const loop = {
      id: 'loop-1',
      type: 'forLoop',
      position: { x: 0, y: 0 },
      style: { width: 500, height: 280 },
      data: { properties: {}, displayName: 'Loop', triggerOnly: false, outputHandles: ['result'] },
    };
    loadedGraph = { nodes: [loop, node('b-1', 'log', 800, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('forLoop'), pkg('log')] as never);

    await renderCanvas();
    expect(readNodes().find((n) => n.id === 'b-1').parentId).toBeUndefined();

    // Drag b-1 to a point inside the container → reparented on drop.
    store.handlers.onNodeDragStart({}, readNodes().find((n) => n.id === 'b-1'));
    store.handlers.onNodeDragStop({}, { ...readNodes().find((n) => n.id === 'b-1'), position: { x: 100, y: 100 } });

    await waitFor(() => expect(readNodes().find((n) => n.id === 'b-1').parentId).toBe('loop-1'));

    ctrlZ();
    await waitFor(() => expect(readNodes().find((n) => n.id === 'b-1').parentId).toBeUndefined());
    expect(readNodes().find((n) => n.id === 'b-1').position.x).toBe(800);
  });
});

describe('Phase 2 — Copy/Paste/Duplicate', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('duplicates the selected subgraph, cloning internal edges with new ids', async () => {
    loadedGraph = {
      nodes: [
        { ...node('a-1', 'log', 0, 0), selected: true },
        { ...node('b-1', 'log', 300, 0), selected: true },
      ],
      edges: [{ id: 'e1', source: 'a-1', sourceHandle: 'result', target: 'b-1', targetHandle: 'in' }],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);

    await renderCanvas();
    ctrlKey('d');

    await waitFor(() => expect(readNodes()).toHaveLength(4));
    const edges = readEdges();
    expect(edges).toHaveLength(2); // original + cloned internal edge

    const clones = readNodes().filter((n) => n.id !== 'a-1' && n.id !== 'b-1');
    expect(clones).toHaveLength(2);
    // Clones are offset, selected, and have brand-new ids.
    expect(clones.every((c) => c.selected === true)).toBe(true);
    expect(clones.some((c) => c.position.x === 40)).toBe(true);
    // The cloned edge points only at clones, not the originals.
    const clonedEdge = edges.find((e) => e.id !== 'e1')!;
    expect(['a-1', 'b-1']).not.toContain(clonedEdge.source);
    expect(['a-1', 'b-1']).not.toContain(clonedEdge.target);
    // Originals deselected.
    expect(readNodes().find((n) => n.id === 'a-1').selected).toBe(false);
  });

  it('copies then pastes, with repeated pastes offsetting further', async () => {
    loadedGraph = { nodes: [{ ...node('a-1', 'log', 0, 0), selected: true }], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);

    await renderCanvas();
    ctrlKey('c');
    ctrlKey('v');
    await waitFor(() => expect(readNodes()).toHaveLength(2));
    ctrlKey('v');
    await waitFor(() => expect(readNodes()).toHaveLength(3));

    const pasted = readNodes().filter((n) => n.id !== 'a-1');
    const xs = pasted.map((n) => n.position.x).sort((p, q) => p - q);
    expect(xs).toEqual([40, 80]); // first paste +40, second +80
  });

  it('paste is a no-op when the clipboard is empty', async () => {
    loadedGraph = { nodes: [node('a-1', 'log', 0, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);

    await renderCanvas();
    ctrlKey('v');
    await new Promise((r) => setTimeout(r, 30));
    expect(readNodes()).toHaveLength(1);
  });

  it('undoes a duplicate in one step', async () => {
    loadedGraph = { nodes: [{ ...node('a-1', 'log', 0, 0), selected: true }], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);

    await renderCanvas();
    ctrlKey('d');
    await waitFor(() => expect(readNodes()).toHaveLength(2));
    ctrlZ();
    await waitFor(() => expect(readNodes()).toHaveLength(1));
  });
});

describe('Phase 2 — Multi-select', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('Ctrl+A selects every node', async () => {
    loadedGraph = { nodes: [node('a-1', 'log', 0, 0), node('b-1', 'log', 300, 0), node('c-1', 'log', 600, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);

    await renderCanvas();
    ctrlKey('a');
    await waitFor(() => expect(readNodes().every((n) => n.selected)).toBe(true));
  });

  it('batch-deletes the whole selection and undoes it in one step', async () => {
    loadedGraph = {
      nodes: [node('a-1', 'log', 0, 0), node('b-1', 'log', 300, 0), node('c-1', 'log', 600, 0)],
      edges: [{ id: 'e1', source: 'a-1', sourceHandle: 'result', target: 'b-1', targetHandle: 'in' }],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);

    await renderCanvas();
    ctrlKey('a');
    await waitFor(() => expect(readNodes().every((n) => n.selected)).toBe(true));

    pressDelete();
    await waitFor(() => expect(readNodes()).toHaveLength(0));
    expect(readEdges()).toHaveLength(0);

    ctrlZ();
    await waitFor(() => expect(readNodes()).toHaveLength(3));
    expect(readEdges()).toHaveLength(1);
  });

  it('duplicates a multi-node selection together', async () => {
    loadedGraph = { nodes: [node('a-1', 'log', 0, 0), node('b-1', 'log', 300, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);

    await renderCanvas();
    ctrlKey('a');
    await waitFor(() => expect(readNodes().every((n) => n.selected)).toBe(true));
    ctrlKey('d');
    await waitFor(() => expect(readNodes()).toHaveLength(4));
  });
});

// Feature #10 — surface *why* a connection drop failed via a toast. Drives the
// captured onConnectEnd handler with fabricated connection states.
describe('Feature #10 — invalid-connection toast', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  // A target node carrying handle bounds, as React Flow hands them to onConnectEnd.
  function toNodeWith(id: string, type: string, hasInput: boolean) {
    return {
      id,
      type,
      internals: { handleBounds: { target: hasInput ? [{ id: 'in' }] : [] } },
    };
  }

  async function fireConnectEnd(state: Record<string, any>) {
    await act(async () => {
      store.handlers.onConnectEnd?.(new MouseEvent('mouseup'), state);
    });
  }

  async function setup() {
    loadedGraph = { nodes: [node('log-1', 'log', 0, 0), node('log-2', 'log', 300, 0)], edges: [] };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log'), pkg('forLoop')] as never);
    await renderCanvas();
  }

  it('explains a self-connection', async () => {
    await setup();
    await fireConnectEnd({
      isValid: false,
      fromHandle: { type: 'source', id: 'result' },
      fromNode: { id: 'log-1' },
      toNode: toNodeWith('log-1', 'log', true),
    });
    expect(await screen.findByRole('alert')).toHaveTextContent("A node can't connect to itself.");
  });

  it('explains dragging from a non-output handle', async () => {
    await setup();
    await fireConnectEnd({
      isValid: false,
      fromHandle: { type: 'target', id: 'in' },
      fromNode: { id: 'log-1' },
      toNode: toNodeWith('log-2', 'log', true),
    });
    expect(await screen.findByRole('alert')).toHaveTextContent('Start the connection from an output port.');
  });

  it('explains a drop on a container node', async () => {
    await setup();
    await fireConnectEnd({
      isValid: false,
      fromHandle: { type: 'source', id: 'result' },
      fromNode: { id: 'log-1' },
      toNode: toNodeWith('loop-1', 'forLoop', true),
    });
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Drop onto a node inside the container, not the container itself.',
    );
  });

  it('explains a target node with no input port', async () => {
    await setup();
    await fireConnectEnd({
      isValid: false,
      fromHandle: { type: 'source', id: 'result' },
      fromNode: { id: 'log-1' },
      toNode: toNodeWith('log-2', 'log', false),
    });
    expect(await screen.findByRole('alert')).toHaveTextContent('That node has no input port to connect to.');
  });

  it('stays silent when released on empty canvas', async () => {
    await setup();
    await fireConnectEnd({ isValid: false, fromHandle: { type: 'source', id: 'result' }, fromNode: { id: 'log-1' }, toNode: null });
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('does not show an error for a valid output → node drop (wires up instead)', async () => {
    await setup();
    await fireConnectEnd({
      isValid: false,
      fromHandle: { type: 'source', id: 'result' },
      fromNode: { id: 'log-1' },
      toNode: toNodeWith('log-2', 'log', true),
    });
    expect(screen.queryByRole('alert')).toBeNull();
    // The wire was created and the success status toast shown.
    await waitFor(() => expect(readEdges()).toHaveLength(1));
    expect(screen.getByRole('status')).toHaveTextContent('Connected ✓');
  });
});

// Feature #9 — dockable diagnostics panel: live edge-validation warnings show
// up as clickable rows that centre the canvas on the offending edge / node.
describe('Feature #9 — diagnostics panel', () => {
  beforeEach(() => {
    store.nodes = [];
    store.edges = [];
    vi.mocked(api.getWorkflow).mockResolvedValue({ id: { value: 'wf-1' }, name: 'WF', nodes: [], edges: [] } as never);
  });
  afterEach(() => vi.clearAllMocks());

  it('lists validation diagnostics and centres on the edge when a row is clicked', async () => {
    loadedGraph = {
      nodes: [node('log-1', 'log', 0, 0), node('log-2', 'log', 300, 0)],
      edges: [{ id: 'e1', source: 'log-1', sourceHandle: 'result', target: 'log-2', targetHandle: 'in' }],
    };
    vi.mocked(api.getNodePackages).mockResolvedValue([pkg('log')] as never);
    vi.mocked(api.validateWorkflow).mockResolvedValue([
      { severity: 'Warning', code: 'WARN_TYPE_MISMATCH', message: 'type mismatch', edgeId: 'e1' },
    ] as never);

    await renderCanvas();

    // Debounced validate pass surfaces the diagnostic as a clickable panel row.
    const row = await screen.findByTitle('Click to locate on the canvas', undefined, { timeout: 3000 });
    expect(row).toHaveTextContent('type mismatch');
    expect(screen.getByRole('region', { name: 'Diagnostics' })).toBeInTheDocument();

    fireEvent.click(row);
    // Edge focus centres on the midpoint of its two endpoints.
    await waitFor(() => expect(store.setCenter).toHaveBeenCalled());
  });
});
