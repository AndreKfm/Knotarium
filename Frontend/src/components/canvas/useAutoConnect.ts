import { useCallback, useEffect } from 'react'
import type { Dispatch, RefObject, SetStateAction } from 'react'
import type { Connection, Edge, InternalNode, IsValidConnection, Node as RFNode } from '@xyflow/react'
import {
  getPortPositions,
  getFreePorts,
  findNearestCompatiblePort,
  findEdgeUnderPoint,
  collectDownstream,
  isContainerNodeType,
  DEFAULT_NODE_WIDTH,
  EDGE_HIT_TOLERANCE,
  PROXIMITY_THRESHOLD,
  type PortPosition,
  type InternalNodeLike,
  type EdgeLike,
} from '../../node-editor/canvasGeometry'
import { buildNode } from '../../node-editor/nodeFactory'
import { acceptsMultipleIncoming } from '../../node-editor/connectionSemantics'
import { useVariableStore } from '../../stores/useVariableStore'
import { type NodePackageMetadata } from '../../utils/nodePackages'
import type { NodePackageSummary } from '../../types'

export interface UseAutoConnectArgs {
  getNodes: () => RFNode[]
  getInternalNode: (id: string) => InternalNode<RFNode> | undefined
  edges: Edge[]
  edgesRef: RefObject<Edge[]>
  isValidConnection: IsValidConnection
  addConnection: (conn: Connection) => void
  addRecentNode: (id: string) => void
  setNodes: Dispatch<SetStateAction<RFNode[]>>
  setEdges: Dispatch<SetStateAction<Edge[]>>
  snapIfEnabled: (p: { x: number; y: number }) => { x: number; y: number }
  /** Bridge ref (owned by Canvas) so handleNodeDragStop can trigger a final proximity connect. */
  scheduleProximityRef: RefObject<(nodeId: string, triggerOnly: boolean) => void>
  /** Per-drag free-port cache (owned by Canvas; cleared by onNodeDragStart/handleNodeDragStop). */
  dragProximityRef: RefObject<{ nodeId: string; otherFree: PortPosition[] } | null>
}

export interface AutoConnect {
  /** Drag-time affordance: glow the ports that would auto-connect on release. */
  handleNodeDrag: (event: React.MouseEvent, node: RFNode) => void
  /** After a drop/drag-stop, auto-wire the node to nearby free compatible ports (deferred 2 frames). */
  scheduleProximityConnect: (nodeId: string, triggerOnly: boolean) => void
  /** Splice a dropped node onto the edge under the cursor (A → new → B); false if no edge is hit. */
  tryInsertOnEdge: (nodePackage: NodePackageSummary, metadata: NodePackageMetadata | undefined, dropPosition: { x: number; y: number }) => boolean
}

/**
 * Proximity auto-connect + insert-on-edge for the canvas. Extracted from Canvas.tsx. The two shared
 * refs (scheduleProximityRef, dragProximityRef) are owned by Canvas — they bridge into the drag
 * lifecycle handlers (onNodeDragStart / handleNodeDragStop) that stay there — and are passed in.
 */
export function useAutoConnect(args: UseAutoConnectArgs): AutoConnect {
  const {
    getNodes, getInternalNode, edges, edgesRef, isValidConnection,
    addConnection, addRecentNode, setNodes, setEdges, snapIfEnabled,
    scheduleProximityRef, dragProximityRef,
  } = args

  const collectMeasuredPorts = useCallback((): PortPosition[] => {
    const ports: PortPosition[] = [];
    for (const n of getNodes()) {
      const internal = getInternalNode(n.id);
      if (internal) ports.push(...getPortPositions(internal as unknown as InternalNodeLike));
    }
    return ports;
  }, [getNodes, getInternalNode]);

  // Fan-in predicate shared by the proximity helpers: a loop 'end' loopback or a join node's input
  // accepts many wires and is never "free".
  const isFanInTarget = useCallback(
    (nodeId: string, handleId: string) => acceptsMultipleIncoming(nodeId, handleId, getNodes()),
    [getNodes],
  );

  const tryInsertOnEdge = useCallback(
    (
      nodePackage: NodePackageSummary,
      metadata: NodePackageMetadata | undefined,
      dropPosition: { x: number; y: number },
    ): boolean => {
      // Containers manage their own body wiring; never splice one onto a wire.
      if (isContainerNodeType(nodePackage.id)) return false;
      // Trigger-only nodes have no input to receive the upstream half of the splice.
      if (metadata?.triggerOnly) return false;

      const ports = collectMeasuredPorts();
      const hit = findEdgeUnderPoint(edges as EdgeLike[], ports, dropPosition, EDGE_HIT_TOLERANCE);
      if (!hit) return false;

      const outHandles = metadata?.outputHandles;
      const primaryOut = Array.isArray(outHandles) && outHandles.length > 0 ? outHandles[0] : 'result';

      const width = DEFAULT_NODE_WIDTH;

      // Open space for the inserted node by shifting the downstream subgraph right.
      // Only top-level nodes move — children stay within their container's extent.
      const downstream = collectDownstream(hit.edge.target, edges as EdgeLike[]);
      const delta = width + 80;

      // Centre the node in the *expanded* gap (after the downstream shift), not on the
      // original edge midpoint — otherwise it hugs the upstream node with a long wire to
      // the downstream one. Adding delta/2 balances the A→new and new→B wire lengths.
      const newNode = buildNode({
        type: nodePackage.id,
        position: snapIfEnabled({ x: hit.midpoint.x - width / 2 + delta / 2, y: hit.midpoint.y - 40 }),
        metadata,
        fallbackDisplayName: nodePackage.displayName,
      });

      setNodes((nds) => [
        ...nds.map((n) =>
          downstream.has(n.id) && !n.parentId
            ? { ...n, position: { x: n.position.x + delta, y: n.position.y } }
            : n,
        ),
        newNode,
      ]);

      // Re-wire A → new → B. Removing the hit edge and the two addConnection calls all
      // compose as queued functional updates, so they land in a single render batch.
      setEdges((eds) => eds.filter((e) => e.id !== hit.edge.id));
      addConnection({
        source: hit.edge.source,
        sourceHandle: hit.edge.sourceHandle ?? null,
        target: newNode.id,
        targetHandle: 'in',
      });
      addConnection({
        source: newNode.id,
        sourceHandle: primaryOut,
        target: hit.edge.target,
        targetHandle: hit.edge.targetHandle ?? null,
      });

      addRecentNode(nodePackage.id);
      return true;
    },
    [collectMeasuredPorts, edges, setNodes, setEdges, addConnection, addRecentNode, snapIfEnabled],
  );

  // ── Feature A — proximity snap ──
  // Shared core: the nearest free, compatible downstream (self.source → other.target) and upstream
  // (other.source → self.target) matches for `nodeId`. Both the connect and the drag-time highlight
  // paths read from this.
  const findProximityMatches = useCallback(
    (nodeId: string, triggerOnly: boolean) => {
      const self = getInternalNode(nodeId) as unknown as InternalNodeLike | undefined;
      if (!self || getPortPositions(self).length === 0) return { down: null, up: null };

      const internals: InternalNodeLike[] = [];
      for (const n of getNodes()) {
        const ni = getInternalNode(n.id);
        if (ni) internals.push(ni as unknown as InternalNodeLike);
      }
      const free = getFreePorts(internals, edges as EdgeLike[], isFanInTarget);
      const selfFree = free.filter((p) => p.nodeId === nodeId);
      const otherFree = free.filter((p) => p.nodeId !== nodeId);
      if (selfFree.length === 0 || otherFree.length === 0) return { down: null, up: null };

      const valid = (c: { source: string; sourceHandle: string; target: string; targetHandle: string }) =>
        isValidConnection(c as unknown as Connection);

      const down = findNearestCompatiblePort(selfFree.filter((p) => p.kind === 'source'), otherFree, PROXIMITY_THRESHOLD, valid);
      const up = triggerOnly
        ? null
        : findNearestCompatiblePort(selfFree.filter((p) => p.kind === 'target'), otherFree, PROXIMITY_THRESHOLD, valid);
      return { down, up };
    },
    [getInternalNode, getNodes, edges, isFanInTarget, isValidConnection],
  );

  const runProximityConnect = useCallback(
    (nodeId: string, triggerOnly: boolean) => {
      const { down, up } = findProximityMatches(nodeId, triggerOnly);
      if (down) {
        addConnection({
          source: down.source.nodeId,
          sourceHandle: down.source.handleId,
          target: down.target.nodeId,
          targetHandle: down.target.handleId,
        });
      }
      if (up) {
        addConnection({
          source: up.source.nodeId,
          sourceHandle: up.source.handleId,
          target: up.target.nodeId,
          targetHandle: up.target.handleId,
        });
      }
    },
    [findProximityMatches, addConnection],
  );

  // Drag-time affordance: while a node is dragged, glow the ports that would auto-connect on release.
  const handleNodeDrag = useCallback(
    (_event: React.MouseEvent, node: RFNode) => {
      const setKeys = useVariableStore.getState().setSnapCandidateKeys;
      if (isContainerNodeType(node.type)) {
        setKeys([]);
        dragProximityRef.current = null;
        return;
      }
      // Build the other-nodes free-port cache once on the first move of this drag, then reuse it.
      let cache = dragProximityRef.current;
      if (!cache || cache.nodeId !== node.id) {
        const internals: InternalNodeLike[] = [];
        for (const n of getNodes()) {
          if (n.id === node.id) continue;
          const ni = getInternalNode(n.id);
          if (ni) internals.push(ni as unknown as InternalNodeLike);
        }
        cache = { nodeId: node.id, otherFree: getFreePorts(internals, edgesRef.current as unknown as EdgeLike[], isFanInTarget) };
        dragProximityRef.current = cache;
      }
      // Recompute only the DRAGGED node's free ports each frame; match against the cached (static)
      // other nodes' ports — so the per-mousemove cost is independent of total node count.
      const self = getInternalNode(node.id) as unknown as InternalNodeLike | undefined;
      const selfFree = self ? getFreePorts([self], edgesRef.current as unknown as EdgeLike[], isFanInTarget).filter((p) => p.nodeId === node.id) : [];
      const keys: string[] = [];
      if (selfFree.length > 0 && cache.otherFree.length > 0) {
        const valid = (c: { source: string; sourceHandle: string; target: string; targetHandle: string }) =>
          isValidConnection(c as unknown as Connection);
        const down = findNearestCompatiblePort(selfFree.filter((p) => p.kind === 'source'), cache.otherFree, PROXIMITY_THRESHOLD, valid);
        const up = node.data?.triggerOnly
          ? null
          : findNearestCompatiblePort(selfFree.filter((p) => p.kind === 'target'), cache.otherFree, PROXIMITY_THRESHOLD, valid);
        if (down) keys.push(`${down.source.nodeId} ${down.source.handleId}`, `${down.target.nodeId} ${down.target.handleId}`);
        if (up) keys.push(`${up.source.nodeId} ${up.source.handleId}`, `${up.target.nodeId} ${up.target.handleId}`);
      }
      const prev = useVariableStore.getState().snapCandidateKeys;
      // Only write when the candidate set actually changes (drag fires every mousemove).
      if (keys.length !== prev.length || keys.some((k, i) => k !== prev[i])) {
        setKeys(keys);
      }
    },
    [getNodes, getInternalNode, isFanInTarget, isValidConnection, dragProximityRef, edgesRef],
  );

  // Defer two frames so React Flow has mounted + measured the node's handle bounds before we read
  // their positions. Falls back to setTimeout where rAF is absent.
  const scheduleProximityConnect = useCallback(
    (nodeId: string, triggerOnly: boolean) => {
      const raf: (cb: () => void) => void =
        typeof requestAnimationFrame === 'function' ? (cb) => requestAnimationFrame(cb) : (cb) => { setTimeout(cb, 0); };
      raf(() => raf(() => runProximityConnect(nodeId, triggerOnly)));
    },
    [runProximityConnect],
  );
  useEffect(() => {
    scheduleProximityRef.current = scheduleProximityConnect;
  }, [scheduleProximityConnect, scheduleProximityRef]);

  return { handleNodeDrag, scheduleProximityConnect, tryInsertOnEdge };
}
