// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useRef } from 'react'
import type { Dispatch, RefObject, SetStateAction } from 'react'
import type { Connection, Edge, InternalNode, IsValidConnection, Node as RFNode } from '@xyflow/react'
import {
  getPortPositions,
  getFreePorts,
  findNearestCompatiblePort,
  findEdgeUnderRect,
  collectDownstream,
  isContainerNodeType,
  DEFAULT_NODE_WIDTH,
  DEFAULT_NODE_HEIGHT,
  EDGE_HIT_SLACK_PX,
  PROXIMITY_THRESHOLD,
  type PortPosition,
  type InternalNodeLike,
  type EdgeLike,
  type EdgeHit,
} from '../../node-editor/canvasGeometry'
import { buildNode } from '../../node-editor/nodeFactory'
import { acceptsMultipleIncoming } from '../../node-editor/connectionSemantics'
import { useVariableStore } from '../../stores/useVariableStore'
import { type NodePackageMetadata } from '../../utils/nodePackages'
import type { NodePackageSummary } from '../../types'

export interface UseAutoConnectArgs {
  getNodes: () => RFNode[]
  getInternalNode: (id: string) => InternalNode<RFNode> | undefined
  /** Current canvas zoom — turns the screen-space hit slack into flow units. */
  getZoom: () => number
  edges: Edge[]
  edgesRef: RefObject<Edge[]>
  isValidConnection: IsValidConnection
  addConnection: (conn: Connection) => void
  addRecentNode: (id: string) => void
  setNodes: Dispatch<SetStateAction<RFNode[]>>
  setEdges: Dispatch<SetStateAction<Edge[]>>
  snapIfEnabled: (p: { x: number; y: number }) => { x: number; y: number }
  /** Highlights the wire a release would splice into (owned by Canvas, read by displayEdges). */
  setSpliceTarget: (edgeId: string | null) => void
  /** Bridge ref (owned by Canvas) so handleNodeDragStop can trigger a final proximity connect. */
  scheduleProximityRef: RefObject<(nodeId: string, triggerOnly: boolean) => void>
  /** Bridge ref (owned by Canvas) so handleNodeDragStop can splice the dragged node onto a wire. */
  spliceNodeRef: RefObject<(node: RFNode) => boolean>
  /** Per-drag free-port cache (owned by Canvas; cleared by onNodeDragStart/handleNodeDragStop). */
  dragProximityRef: RefObject<{ nodeId: string; otherFree: PortPosition[] } | null>
}

export interface AutoConnect {
  /** Drag-time affordance: glow the ports that would auto-connect on release. */
  handleNodeDrag: (event: MouseEvent | TouchEvent, node: RFNode) => void
  /** After a drop/drag-stop, auto-wire the node to nearby free compatible ports (deferred 2 frames). */
  scheduleProximityConnect: (nodeId: string, triggerOnly: boolean) => void
  /** Splice a dropped node onto the wire under its footprint (A → new → B); false if none is hit. */
  tryInsertOnEdge: (nodePackage: NodePackageSummary, metadata: NodePackageMetadata | undefined, dropPosition: { x: number; y: number }) => boolean
  /** Id of the wire a node dropped at this point would splice into — for the drag-over highlight. */
  spliceTargetEdgeId: (dropPosition: { x: number; y: number }) => string | null
  /** Drop the per-drag port cache; call when a palette drag ends or leaves the canvas. */
  resetSpliceProbe: () => void
}

/**
 * Proximity auto-connect + insert-on-edge for the canvas. Extracted from Canvas.tsx. The two shared
 * refs (scheduleProximityRef, dragProximityRef) are owned by Canvas — they bridge into the drag
 * lifecycle handlers (onNodeDragStart / handleNodeDragStop) that stay there — and are passed in.
 */
export function useAutoConnect(args: UseAutoConnectArgs): AutoConnect {
  const {
    getNodes, getInternalNode, getZoom, edges, edgesRef, isValidConnection,
    addConnection, addRecentNode, setNodes, setEdges, snapIfEnabled, setSpliceTarget,
    scheduleProximityRef, spliceNodeRef, dragProximityRef,
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

  // Footprint a freshly dropped node will occupy, taken from a node already on the canvas so it
  // follows the density setting instead of a hard-coded guess. Containers are skipped — they are
  // far larger than an ordinary node and would inflate the hit box.
  const typicalNodeSize = useCallback((): { width: number; height: number } => {
    for (const n of getNodes()) {
      if (isContainerNodeType(n.type)) continue;
      const measured = (getInternalNode(n.id) as unknown as InternalNodeLike | undefined)?.measured;
      if (measured?.width && measured?.height) return { width: measured.width, height: measured.height };
    }
    return { width: DEFAULT_NODE_WIDTH, height: DEFAULT_NODE_HEIGHT };
  }, [getNodes, getInternalNode]);

  // Per-gesture cache of the measured ports. The hover probes (palette drag-over, node drag) fire on
  // every mouse move, and rebuilding the port list from every node each time would scale with graph
  // size; the graph cannot change mid-gesture, so one snapshot per gesture is enough. The key is the
  // gesture identity — 'palette' for a palette drag, the node id for a node drag — so switching
  // gestures rebuilds it even without an explicit reset.
  const spliceProbeRef = useRef<{ key: string; ports: PortPosition[] } | null>(null);
  const resetSpliceProbe = useCallback(() => { spliceProbeRef.current = null; }, []);
  const cachedPorts = useCallback((key: string): PortPosition[] => {
    const cache = spliceProbeRef.current;
    if (cache && cache.key === key) return cache.ports;
    const ports = collectMeasuredPorts();
    spliceProbeRef.current = { key, ports };
    return ports;
  }, [collectMeasuredPorts]);

  /**
   * The wire a node dropped at `dropPosition` would be spliced into: the one crossing (or coming
   * within a small screen-space margin of) the box the node would occupy, centred on the cursor.
   * Testing the node's FOOTPRINT rather than the bare cursor point is what makes this hittable —
   * the box scales with the canvas exactly like the wire does, so the target no longer shrinks as
   * you zoom out, and the user aims with the thing they can see.
   */
  const findSpliceTarget = useCallback(
    (dropPosition: { x: number; y: number }, ports?: PortPosition[]): EdgeHit | null => {
      const { width, height } = typicalNodeSize();
      const rect = { x: dropPosition.x - width / 2, y: dropPosition.y - height / 2, width, height };
      const zoom = getZoom() || 1;
      return findEdgeUnderRect(
        edges as EdgeLike[],
        ports ?? collectMeasuredPorts(),
        rect,
        EDGE_HIT_SLACK_PX / zoom,
      );
    },
    [typicalNodeSize, getZoom, edges, collectMeasuredPorts],
  );

  const spliceTargetEdgeId = useCallback(
    (dropPosition: { x: number; y: number }): string | null =>
      findSpliceTarget(dropPosition, cachedPorts('palette'))?.edge.id ?? null,
    [findSpliceTarget, cachedPorts],
  );

  /**
   * Rewire `hit.edge` as A → `newNodeId` → B and open room for the node by pushing the downstream
   * subgraph right. Returns where the node belongs: centred in the *expanded* gap, so the A→new and
   * new→B wires come out balanced instead of the node hugging A. Shared by both splice paths.
   */
  const applySplice = useCallback(
    (
      hit: EdgeHit,
      newNodeId: string,
      primaryOut: string,
      size: { width: number; height: number },
    ): { x: number; y: number } => {
      // Only top-level nodes move — children stay within their container's extent.
      const downstream = collectDownstream(hit.edge.target, edges as EdgeLike[]);
      const delta = size.width + 80;

      setNodes((nds) =>
        nds.map((n) =>
          downstream.has(n.id) && !n.parentId
            ? { ...n, position: { x: n.position.x + delta, y: n.position.y } }
            : n,
        ),
      );

      // Removing the hit edge and the two addConnection calls all compose as queued functional
      // updates, so they land in a single render batch.
      setEdges((eds) => eds.filter((e) => e.id !== hit.edge.id));
      addConnection({
        source: hit.edge.source,
        sourceHandle: hit.edge.sourceHandle ?? null,
        target: newNodeId,
        targetHandle: 'in',
      });
      addConnection({
        source: newNodeId,
        sourceHandle: primaryOut,
        target: hit.edge.target,
        targetHandle: hit.edge.targetHandle ?? null,
      });

      return snapIfEnabled({
        x: hit.midpoint.x - size.width / 2 + delta / 2,
        y: hit.midpoint.y - size.height / 2,
      });
    },
    [edges, setNodes, setEdges, addConnection, snapIfEnabled],
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

      const hit = findSpliceTarget(dropPosition);
      if (!hit) return false;

      const outHandles = metadata?.outputHandles;
      const primaryOut = Array.isArray(outHandles) && outHandles.length > 0 ? outHandles[0] : 'result';

      const size = typicalNodeSize();
      const newNode = buildNode({
        type: nodePackage.id,
        position: dropPosition, // the splice below overrides this with the centre of the opened gap
        metadata,
        fallbackDisplayName: nodePackage.displayName,
      });
      const position = applySplice(hit, newNode.id, primaryOut, size);
      setNodes((nds) => [...nds, { ...newNode, position }]);

      addRecentNode(nodePackage.id);
      return true;
    },
    [findSpliceTarget, typicalNodeSize, applySplice, setNodes, addRecentNode],
  );

  /**
   * Feature B for a node that is ALREADY on the canvas: dragging it onto a wire splices it in, the
   * same as dropping a fresh one from the palette. The extra condition is that the node must still
   * be UNWIRED — splicing one that already has connections would silently reroute the graph around
   * it, so a wired node keeps the plain proximity behaviour.
   */
  const canSpliceNode = useCallback(
    (node: RFNode): boolean => {
      // Containers own their body wiring; a container child is positioned by its parent's extent,
      // and the downstream shift only moves top-level nodes — so neither is spliced.
      if (isContainerNodeType(node.type) || node.parentId) return false;
      if (node.data?.triggerOnly) return false;
      if ((edges as EdgeLike[]).some((e) => e.source === node.id || e.target === node.id)) return false;
      // Needs both halves of the splice: something to receive A and something to drive B. This also
      // rules out an 'end' node (no output) without special-casing node types.
      const internal = getInternalNode(node.id) as unknown as InternalNodeLike | undefined;
      const ports = internal ? getPortPositions(internal) : [];
      return ports.some((p) => p.kind === 'target') && ports.some((p) => p.kind === 'source');
    },
    [edges, getInternalNode],
  );

  /** The wire the dragged node currently covers, hit-tested against its real measured footprint. */
  const findSpliceTargetForNode = useCallback(
    (node: RFNode): EdgeHit | null => {
      if (!canSpliceNode(node)) return null;
      const internal = getInternalNode(node.id) as unknown as InternalNodeLike | undefined;
      const base = internal?.internals?.positionAbsolute ?? node.position;
      const width = internal?.measured?.width;
      const height = internal?.measured?.height;
      if (!base || !width || !height) return null;
      return findEdgeUnderRect(
        edges as EdgeLike[],
        cachedPorts(node.id),
        { x: base.x, y: base.y, width, height },
        EDGE_HIT_SLACK_PX / (getZoom() || 1),
      );
    },
    [canSpliceNode, getInternalNode, edges, cachedPorts, getZoom],
  );

  const spliceTargetEdgeIdForNode = useCallback(
    (node: RFNode): string | null => findSpliceTargetForNode(node)?.edge.id ?? null,
    [findSpliceTargetForNode],
  );

  const tryInsertNodeOnEdge = useCallback(
    (node: RFNode): boolean => {
      const hit = findSpliceTargetForNode(node);
      if (!hit) return false;

      const internal = getInternalNode(node.id) as unknown as InternalNodeLike | undefined;
      const size = {
        width: internal?.measured?.width ?? DEFAULT_NODE_WIDTH,
        height: internal?.measured?.height ?? DEFAULT_NODE_HEIGHT,
      };
      const outHandles = node.data?.outputHandles;
      const sourcePort = (internal ? getPortPositions(internal) : []).find((p) => p.kind === 'source');
      const primaryOut = Array.isArray(outHandles) && outHandles.length > 0
        ? String(outHandles[0])
        : sourcePort?.handleId ?? 'result';

      const position = applySplice(hit, node.id, primaryOut, size);
      // The node is dropped where the user let go; move it into the gap the splice just opened so
      // it doesn't sit on top of the wire's own nodes.
      setNodes((nds) => nds.map((n) => (n.id === node.id ? { ...n, position } : n)));
      return true;
    },
    [findSpliceTargetForNode, getInternalNode, applySplice, setNodes],
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
    (_event: MouseEvent | TouchEvent, node: RFNode) => {
      const setKeys = useVariableStore.getState().setSnapCandidateKeys;
      if (isContainerNodeType(node.type)) {
        setKeys([]);
        setSpliceTarget(null);
        dragProximityRef.current = null;
        return;
      }
      // An unwired node held over a wire will splice into it on release — show that wire instead of
      // the port glow, since the splice supersedes the proximity snap (see handleNodeDragStop).
      const spliceId = spliceTargetEdgeIdForNode(node);
      setSpliceTarget(spliceId);
      if (spliceId) {
        if (useVariableStore.getState().snapCandidateKeys.length > 0) setKeys([]);
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
    [getNodes, getInternalNode, isFanInTarget, isValidConnection, dragProximityRef, edgesRef, spliceTargetEdgeIdForNode, setSpliceTarget],
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
  useEffect(() => {
    spliceNodeRef.current = tryInsertNodeOnEdge;
  }, [tryInsertNodeOnEdge, spliceNodeRef]);

  return { handleNodeDrag, scheduleProximityConnect, tryInsertOnEdge, spliceTargetEdgeId, resetSpliceProbe };
}
