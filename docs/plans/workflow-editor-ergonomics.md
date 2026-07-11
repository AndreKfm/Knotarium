# Plan: Workflow Editor Ergonomics ("handlicher")

Goal: make the workflow editor faster to use, harder to lose work in, and easier to
navigate — with two new **auto-connect** behaviours as the headline features.

> **Status:** Phase 1 (Auto-connect A + B, incl. A3) and Phase 2 (Undo/Redo, Copy/Paste/Duplicate,
> Multi-select) are **DONE** and fully unit/integration-tested. Phases 3–5 remain.
> New modules: `canvasGeometry.ts` helpers, `undoHistory.ts`, `clipboard.ts` (+ matching
> `*.test.ts`), integration tests in `autoConnect.test.tsx`.

Stack recap: React 19 + `@xyflow/react` v12, Zustand, Vite, TypeScript.
Key files:
- `Frontend/src/components/Canvas.tsx` — node/edge state, drop handlers, `addConnection`, `onConnect`, `isValidConnection`.
- `Frontend/src/node-editor/nodeFactory.ts` — `buildNode`.
- `Frontend/src/node-editor/canvasGeometry.ts` — `getContainerSize`, `findContainingLoopNode`, container constants.
- `Frontend/src/components/CustomNodes.tsx` — handle/port definitions, `getNodeDataOutputs`, `getNodePrimaryInputParameter`.
- `Frontend/src/utils/schemaMapper.ts` — position persistence in `node.properties._metadata`.

State facts used throughout:
- `const [nodes, setNodes, onNodesChange] = useNodesState<RFNode>([])` (Canvas.tsx ~L93).
- `const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([])` (Canvas.tsx ~L94).
- Single wiring entry point: `addConnection(conn: Connection)` (Canvas.tsx ~L617) — already
  de-dupes single-input handles and respects fan-in (`end`, `join`).
- Drop position: `screenToFlowPosition({x,y})` (Canvas.tsx ~L976).
- Handle bounds at runtime: `getInternalNode(id)?.internals.handleBounds?.{source|target}`.
- Default input handle id is `'in'`; default output handle is `'result'`; branch nodes use
  `success/error/true/false`; loop containers use `start`/`end`/`success`.

---

## Feature A — Auto-connect on proximity (snap to a free endpoint) ✅ DONE

**Behaviour:** when a new node is dropped (or moved) so that one of its ports lands close to a
*compatible, currently-unconnected* port of another node, draw the edge automatically.

### A1. Geometry helpers (`canvasGeometry.ts`)
- Add `getPortPositions(internalNode)` → absolute flow-space coords for each source/target handle,
  derived from `node.position` + `internals.handleBounds[*].{x,y,width,height}`.
- Add `getFreePorts(nodes, edges)` → list of `{ nodeId, handleId, kind: 'source'|'target', pos }`
  filtered to ports with **no** edge attached:
  - target port free ⇔ no edge with `target===nodeId && targetHandle===handleId`
    (except fan-in ports `end`/`join`, which are never "free" for this purpose).
  - source port free ⇔ no edge with `source===nodeId && sourceHandle===handleId`
    (branch/multi-output sources may stay eligible even if one branch is wired).
- Add `findNearestCompatiblePort(newNodePorts, freePorts, threshold)`:
  - compatible = opposite kinds (new source ↔ existing target, or new target ↔ existing source),
    not same node, passes `isValidConnection`.
  - returns the closest pair within `threshold` (start ~60px in flow units; tune).

### A2. Hook into drop + drag-stop (`Canvas.tsx`)
- After `buildNode` in `handleCanvasDrop` (~L983) and after `handleNodeDragStop` (~L567):
  1. Read the new/moved node's `getInternalNode` to get its port positions (defer one frame /
     `requestAnimationFrame` so React Flow has measured handles).
  2. Run `findNearestCompatiblePort`; if found, call `addConnection(...)` with the right
     source/target orientation.
  3. Connect at most: one downstream (new.source → existing.target) **and** one upstream
     (existing.source → new.target). Skip the upstream link if the dropped node is `triggerOnly`.
- Reuse the existing container auto-wire path (~L994) — proximity should not fight it; if the node
  dropped into an empty container, keep current behaviour and skip proximity wiring.

### A3. Affordance (optional, recommended)
- While dragging an existing node, highlight the candidate free port (CSS class on the handle) so
  the snap is predictable. Use `onNodeDrag` to compute the nearest candidate and store a
  `snapCandidateRef`; clear on `onNodeDragStop`.

### A4. Edge cases
- Don't connect if it would duplicate an existing edge id
  (`e-${source}-${sourceHandle}-${target}-${targetHandle}`).
- Respect `acceptsMultipleIncoming` — never auto-fill a single-input target that already has a wire.
- Honour `isValidConnection` (no self-connect) and skip when proximity result crosses container
  boundaries in an invalid way (e.g. body node → outside node).

---

## Feature B — Auto-connect "insert on edge" (drop a node onto a wire) ✅ DONE

**Behaviour:** when a new node is dropped on top of an existing edge, splice it in:
`A → B` becomes `A → new → B`, and the surrounding nodes shift to make room.

### B1. Edge hit-testing (`canvasGeometry.ts`)
- Add `findEdgeUnderPoint(edges, nodes, point, tolerance)`:
  - For each edge, compute its source/target handle positions (via `getPortPositions`).
  - Approximate distance from `point` to the edge. Start with distance-to-segment between the two
    endpoints; tolerance ~24px. (Bezier-accurate sampling can come later if the straight-line
    approximation feels off.)
  - Return the closest edge within tolerance, plus its endpoints.

### B2. Splice logic (`Canvas.tsx`, in `handleCanvasDrop`)
Order matters: edge-insert takes precedence over proximity (A) and over container auto-wire when
the drop is squarely on a wire.
1. After computing `dropPosition`, call `findEdgeUnderPoint`. If hit and the new node has a usable
   input and at least one output:
2. Determine the new node's primary input (`'in'`) and primary output:
   `outputHandles[0] ?? 'result'` (matches existing container-wire logic ~L997).
3. In a single `setEdges`/`setNodes` batch:
   - Remove the hit edge.
   - `addConnection({ source: hit.source, sourceHandle: hit.sourceHandle, target: newId, targetHandle: 'in' })`
   - `addConnection({ source: newId, sourceHandle: primaryOut, target: hit.target, targetHandle: hit.targetHandle })`
4. Place the new node centered on the hit edge midpoint (override `dropPosition`).

### B3. Make room (shift surrounding nodes)
- Compute the new node's width (measured via `getInternalNode`, fallback ~220px) plus a gap (~80px).
- Shift the **downstream** subgraph right by `delta` to open space:
  - Walk edges from `hit.target` forward (BFS over `source→target`) to collect the downstream set;
    add `delta` to each node's `position.x`. Cheaper alternative for v1: shift only `hit.target`
    (and its descendants) — start simple, expand if overlaps remain.
  - Keep within container extent when nodes have a `parentId` (don't push children outside the box).
- Persist new positions through the same `_metadata` path used on save (`schemaMapper.ts`).

### B4. Edge cases
- Skip splice when the new node is `triggerOnly` (no input) or has no outputs.
- If the hit edge targets a fan-in port (`end`/`join`), still works: keep `hit.targetHandle`.
- If `findEdgeUnderPoint` returns nothing, fall through to Feature A proximity, then to plain drop.

---

## Supporting ergonomics (the full list)

Grouped exactly as discussed. A/B above depend on none of these but pair well with multi-select.

### Biggest lever — power-user basics missing today

1. **Undo/Redo** ✅ DONE — snapshot stack of `{nodes, edges}` in a small Zustand store or `useRef` ring
   buffer; push on structural changes (add/delete/connect/move-stop), bind `Ctrl+Z` /
   `Ctrl+Shift+Z`. Highest value: today deletes are unrecoverable until reload.
   *(Implemented in `undoHistory.ts` + `recordUndo()` per gesture in `Canvas.tsx`; also `Ctrl+Y`.)*
2. **Copy / Paste / Duplicate** ✅ DONE — `Ctrl+C/V`, `Ctrl+D`. Clone node(s) via `buildNode` with cloned
   `data.properties`, new ids, paste offset; remap internal edges for multi-node paste. Saves
   reconfiguring complex nodes/subflows from scratch. *(Implemented in `clipboard.ts`.)*
3. **Multi-select** ✅ DONE (`Shift`/`Ctrl` click + box-select) — today only single selection. Enables
   batch delete, group move, and later grouping. Shared base for align/distribute.
   *(Left-drag box-select via `selectionOnDrag` + `SelectionMode.Partial`, pan on middle/right;
   `Ctrl+A` select-all.)*
4. **Search / jump palette** ✅ DONE (`Ctrl+F` or `Cmd+K`) — fuzzy list of nodes by title; on pick,
   `setCenter` on the node and select it. Stops endless panning/zooming in large workflows.
   *(Pure matcher `nodeSearch.ts`; overlay `NodeSearchPalette.tsx`; wired in `Canvas.tsx`.)*

### Orientation & overview

5. **Auto-layout button** ✅ DONE (dagre, LR) — one-click "tidy" over the top-level graph, positions
   written back via `recordUndo()`. Plus **Align** (left/centreX/right/top/centreY/bottom) and
   **Distribute** (H/V) on the current multi-select (#3). *(Pure `autoLayout.ts`; floating toolbar
   in `Canvas.tsx`; dep `@dagrejs/dagre`. Children inside containers keep relative positions.)*
6. **Snap-to-grid** ✅ DONE — toggle "# Grid" button wires `snapToGrid` / `snapGrid={[24,24]}` on
   `<ReactFlow>` (24px = the Background dot gap, so nodes land on dots). Default off so fine
   positioning still works. *(State `snapEnabled` in `Canvas.tsx`.)*
7. **Low-zoom LOD** ✅ DONE — below zoom 0.5 the card drops its body, showing icon + name only.
   *(Pure `isLowDetailZoom` in `CustomNodes.helpers.tsx`; `useStore(s=>s.transform[2])` in card.)*
8. **Subflow drill-down affordance** ✅ DONE — explicit "↗ Open subflow" icon in the subflow card
   header; posts to `useSubflowOpenStore`, consumed by Canvas (reuses `openSubflowFromNode`).
   Double-click still works. *(New store mirrors `useInlineCodeEditorStore`.)*

### Configuration more pleasant

9. **Non-blocking diagnostics** — replace the modal error overlay with a dockable panel; clicking a
   diagnostic centers the offending node/edge (reuse `decorateEdgesWithDiagnostics`). Canvas stays
   interactive.
10. **Invalid-connection toast** — surface *why* a drop failed (type mismatch); failed connections
    vanish silently today. Extends the existing `triggerConnectToast`.
11. **Inline rename on the node card** — double-click the label to rename in place, instead of the
    detour through the Properties panel.
12. **Keyboard-shortcut help** ✅ DONE (`?` overlay + a toolbar "?" button) — all bindings
    discoverable. *(Data in `keyboardShortcuts.ts`; overlay `KeyboardShortcutsHelp.tsx`.)*

### Nice-to-have

13. **Sticky notes / comments** on the canvas for documentation (free-floating annotation nodes).
14. **Node groups / collapse** — visually group and collapse clusters for large graphs.
15. **Virtualized variables panel** — virtualize long variable lists to keep the panel responsive.

---

## Suggested sequencing

1. ✅ **Phase 1 — Auto-connect (A + B). DONE.** Land the two requested features first; they are
   self-contained in `canvasGeometry.ts` + `Canvas.tsx` and give the biggest "handlicher" feel.
   - ✅ 1a: geometry helpers (`getPortPositions`, `getFreePorts`, `findEdgeUnderPoint`) + unit tests.
   - ✅ 1b: Feature B (insert-on-edge) — most visible.
   - ✅ 1c: Feature A (proximity snap) + drag highlight.
2. ✅ **Phase 2 — Safety net. DONE.** Undo/Redo (#1), then Copy/Paste/Duplicate (#2), then Multi-select (#3).
3. **Phase 3 — Navigation & overview.** Search palette (#4), auto-layout + align/distribute (#5),
   snap-to-grid (#6), low-zoom LOD (#7), subflow drill-down affordance (#8).
4. **Phase 4 — Config polish.** Non-blocking diagnostics (#9), invalid-connection toast (#10),
   inline rename (#11), shortcut-help overlay (#12).
5. **Phase 5 — Nice-to-have.** Sticky notes (#13), node groups/collapse (#14), virtualized
   variables panel (#15).

## Testing notes
- Pure geometry helpers are unit-testable without React Flow (feed plain node/edge/handle data).
- For A/B integration, assert: dropped node near a free port creates exactly one expected edge;
  dropped node on `A→B` yields `A→new` + `new→B`, old edge gone, downstream shifted, no overlap.
- Verify fan-in (`end`/`join`) and `triggerOnly` nodes are never mis-wired.
- Re-run `validateWorkflow` after auto-wire so diagnostics stay accurate.
