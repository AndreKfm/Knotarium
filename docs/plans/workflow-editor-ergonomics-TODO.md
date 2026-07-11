# Workflow Editor Ergonomics — Continuation TODO

Companion to [workflow-editor-ergonomics.md](workflow-editor-ergonomics.md). Tracks what's done
and what's next so work can resume after a context reset.

**Working dir:** `Frontend/` · **Test runner:** `npx vitest run` (no `test` npm script) ·
**Type gate:** `npx tsc -b` · **Lint:** `npx eslint <files>`

---

## ✅ DONE (Phases 1 & 2) — landed on branch `feat/workflow-editor`

| Area | Files |
|------|-------|
| 1a geometry helpers | `src/node-editor/canvasGeometry.ts` (+`.test.ts`) |
| 1b insert-on-edge (Feature B) | `Canvas.tsx` `tryInsertOnEdge` |
| 1c proximity snap + A3 glow (Feature A) | `Canvas.tsx` `runProximityConnect`/`handleNodeDrag`; `useVariableStore.snapCandidateKeys`; `CustomNodes.tsx` `glowFor` |
| 2a Undo/Redo | `src/node-editor/undoHistory.ts` (+`.test.ts`); `Canvas.tsx` `recordUndo/doUndo/doRedo` |
| 2b Copy/Paste/Duplicate | `src/node-editor/clipboard.ts` (+`.test.ts`); `Canvas.tsx` `copySelection/pasteClipboard/duplicateSelection` |
| 2c Multi-select | `Canvas.tsx` ReactFlow `selectionOnDrag` + `SelectionMode.Partial` + `panOnDrag={[1,2]}`; `Ctrl+A` select-all |
| Integration tests | `src/node-editor/autoConnect.test.tsx` (~40 tests) |

**Test state:** node-editor suites all green; full frontend suite **184 passing, 5 failing**.
The 5 failures are **pre-existing & unrelated** (`Dashboard.test.tsx` ×4, `OpenApiImporter.test.tsx` ×1).
Lint: new files clean; `Canvas.tsx` has 2 **pre-existing** set-state-in-effect errors only.

**Not yet verified in the running app** — all verification so far is via the test suite.

### Gotchas learned (apply when writing more Canvas tests)
- jsdom drop events drop `clientX/clientY` → mock `screenToFlowPosition` to return a per-test point.
- Test mocks of `@xyflow/react` must export `SelectionMode` (`{Partial,Full}`) and a
  `getWorkflows` api mock, or Canvas render crashes.
- Don't write refs during render (react-hooks v7 errors) — sync `nodesRef/edgesRef` in an effect.
- `recordUndo()` goes at user-gesture entry points only, never inside `addConnection` (keeps a
  splice = one undo step).

---

## ✅ DONE — Phase 3 (Navigation & overview) — all landed on `feat/workflow-editor`

Full suite after Phase 3: **244 passing, 5 failing** (the same pre-existing Dashboard ×4 +
OpenApiImporter ×1). New files all lint-clean. Not yet verified in the running app.

- [x] **#4 Search / jump palette** (`Ctrl+F` or `Cmd+K`) ✅ DONE — fuzzy list of nodes by title;
      on pick `setCenter` + select. Pure matcher in `src/node-editor/nodeSearch.ts` (+`.test.ts`,
      17 tests); overlay in `src/components/NodeSearchPalette.tsx` (+`.test.tsx`, 10 tests);
      wired in `Canvas.tsx` (`searchOpen` state, `jumpToNode`, keydown Ctrl+F/Cmd+K, mount-on-open).
- [x] **#5 Auto-layout button** ✅ DONE (dagre, LR) — one-click "Tidy" over top-level `nodes/edges`,
      positions written back through `recordUndo()`. Plus **Align** (6-way) + **Distribute** (H/V) on
      multi-select. Pure helpers `src/node-editor/autoLayout.ts` (+`.test.ts`, 17 tests:
      `computeAutoLayout`/`alignNodes`/`distributeNodes`); floating toolbar in `Canvas.tsx`
      (`runAutoLayout`/`alignSelection`/`distributeSelection`, `selectedNodeCount` gate); integration
      tests `src/node-editor/autoLayout.canvas.test.tsx` (4). Dep added: `@dagrejs/dagre` v3.
      Nested container children keep relative positions (dagre flattens nesting poorly).
- [x] **#6 Snap-to-grid** ✅ DONE — toggle "# Grid" button in the floating toolbar; wires
      `snapToGrid`/`snapGrid={[24,24]}` (matches Background dot gap) on `<ReactFlow>`. Default off.
      `Background offset={24}` cancels React Flow's half-cell dot shift so dots align to snap
      corners. Programmatic placement (Tidy/drop/paste) also snaps via `snapPointToGrid` +
      `snapIfEnabled` (Align/Distribute excluded). Integration tests in `autoLayout.canvas.test.tsx`.
- [x] **#7 Low-zoom LOD** ✅ DONE — below zoom 0.5 the node card drops its body and shows only the
      icon + name header (Handles live outside the body, so edges still anchor). Pure
      `isLowDetailZoom`/`LOD_ZOOM_THRESHOLD` in `CustomNodes.helpers.tsx` (+`.test.tsx`, 4); wired
      via `useStore(s => s.transform[2])` in `CustomNodes.tsx`.
- [x] **#8 Subflow drill-down affordance** ✅ DONE — explicit "↗ Open subflow" icon in the subflow
      card header (`CustomNodes.tsx`); routes through new `useSubflowOpenStore` (mirrors
      `useInlineCodeEditorStore`) → consumed by a `Canvas.tsx` subscription that reuses the
      save-before-open `openSubflowFromNode` path. Double-click still works. Tests:
      `useSubflowOpenStore.test.ts` (4) + subflow describe in `autoLayout.canvas.test.tsx` (2).

## ✅ DONE — Phase 4 (Config polish) — landed on `feat/workflow-editor-phase4`
- [x] **#9 Non-blocking diagnostics** ✅ DONE — dockable, collapsible `DiagnosticsPanel.tsx`
      (replaces the always-on error overlay) merges blocking publish/run failures with live
      edge-validation warnings; each row is clickable → centers the node/edge (`focusDiagnostic`
      reuses `getInternalNode`+`setCenter`). Pure helpers `src/utils/diagnosticsNavigation.ts`
      (normalizeNodeId/severityRank/sortDiagnostics/mergeDiagnostics/countBySeverity/
      resolveDiagnosticFocus, +`__tests__/diagnosticsNavigation.test.ts`, 12); component tests
      `DiagnosticsPanel.test.tsx` (7); integration in `autoConnect.test.tsx` (1, needed
      `MarkerType` added to the RF mock).
- [x] **#10 Invalid-connection toast** ✅ DONE — a drag ending over a node that doesn't wire up
      shows an amber toast with the reason (self-connect / non-output handle / container / no
      input); empty-pane drops stay quiet. Pure `src/node-editor/connectionFeedback.ts`
      (`connectionFailureReason`, +`.test.ts`, 7); connect-toast state generalised to
      success|error; `onConnectEnd` routes failures through it; integration in
      `autoConnect.test.tsx` (6).
- [x] **#11 Inline rename on the node card** ✅ DONE — double-click the header label to edit in
      place (Enter commit / Escape cancel), persisted to `data.displayName`; excluded for subflow
      cards (derived name) and the read-only run view. Pure `src/node-editor/nodeRename.ts`
      (canRenameNode/commitNodeName/applyNodeRename, +`.test.ts`, 10); UI in `CustomNodes.tsx`;
      integration `CustomNodes.rename.test.tsx` (5).
- [x] **#12 Keyboard-shortcut help** ✅ DONE (`?` overlay + toolbar "?" button) — documents
      Esc/Ctrl+Z/Y/C/V/D/A/F-K/Delete + Tidy/Grid/Align/Distribute + node gestures. Data-only
      `keyboardShortcuts.ts` (`SHORTCUT_GROUPS`, split out to satisfy react-refresh) rendered by
      `KeyboardShortcutsHelp.tsx` (+`.test.tsx`, 7); `?` keydown + `shortcutsOpen` state in
      `Canvas.tsx`; integration tests in `autoLayout.canvas.test.tsx` (2).

## ✅ DONE — Phase 5 (Nice-to-have) — landed on `feat/workflow-editor-phase5`
Persistence fork (decided with user): sticky notes + groups are **inert backend node types**
(`stickyNote`, `group`) registered in `InMemoryNodePackageManifestProvider` — port-less, no
required params, never reached by the executor (no incoming edges), so they compile cleanly and
round-trip through save/version/restore via the existing `_metadata` channel. Backend compiler
test `CompilerTests.Compiles_With_Inert_Annotation_Nodes` (full Knotarium.Tests: 416 green).
Frontend full suite after Phase 5: **390 passing, 5 failing** (same pre-existing Dashboard ×4 +
OpenApiImporter ×1). New files lint-clean; `tsc -b` clean. Annotation types hidden from the
SidebarPalette (created from the canvas toolbar instead).
- [x] **#13 Sticky notes / comments** ✅ DONE — editable/resizable/colourable annotation card,
      no ports. Toolbar "Note" button places one at the viewport centre. Pure helpers
      `src/node-editor/stickyNote.ts` (createStickyNoteNode/apply{Text,Color}/getters/colours,
      +`.test.ts`, 6); component `src/components/StickyNoteNode.tsx` (+`.test.tsx`, 5, mocks
      useStore/useReactFlow/NodeResizer); registered in Canvas `combinedNodeTypes`.
- [x] **#14 Node groups / collapse** ✅ DONE — "Group" toolbar button (≥2 selected) wraps the
      selection in a visual container (membership via child `parentId`); header has a collapse
      chevron (hides children + shrinks to a strip) and double-click label rename; "Ungroup"
      restores children. Pure helpers `src/node-editor/nodeGroup.ts`
      (groupNodes/ungroupNodes/toggleGroupCollapsed/applyGroupCollapseOnLoad/computeGroupBounds/
      findContainingGroupNode/…, +`.test.ts`, 11); component `src/components/GroupNode.tsx`
      (+`.test.tsx`, 5). Canvas `handleNodeDragStop` generalised: loop **and** group boxes act as
      `parentId` containers (drag in → join, drag out → detach); collapse re-derived on load.
- [x] **#15 Virtualized variables panel** ✅ DONE — fixed-height windowing kicks in past 40
      variables; renders only the visible slice + overscan, framed by spacers. Pure
      `src/node-editor/listVirtualization.ts` (computeVirtualWindow/shouldVirtualize, +`.test.ts`,
      9); wired into `VariablesPanel.tsx` via an extracted `VariablesList` (+`.test.tsx`, 3).

## ⏭️ TODO — Phase 6 (Platform expansion)
- [ ] **#16 Pre-built apps / integration library** — a curated catalogue of ready-to-use connector
      workflows (Slack, GitHub, Google Sheets, JIRA, Stripe, …). Each connector ships as a versioned
      bundle of nodes + credential template + sample workflow that users can import in one click.
      Discovery UI: searchable gallery in the sidebar or a dedicated "Integrations" screen.
- [ ] **#18 Templates / shareable workflows** — export any workflow as a portable template (JSON/YAML
      bundle with node definitions, connections, and variable stubs). Import from file or a public
      template gallery. "Share" action produces a URL or downloadable archive; recipients can import
      into their own Knotarium instance with one click.
- [ ] **#17 AI agents** — first-class agent node type that wraps an LLM call (Claude, OpenAI, etc.)
      with configurable system prompt, tool list, and memory scope. Supports tool-use loop
      (agent → tool execution → continue) natively inside the workflow engine. Companion agent-
      builder UI for prompt authoring, model selection, and output schema definition.

## Process reminder (user's standing instruction)
Implement **step by step**, and **generate unit tests for every feature** until stable. Keep the
pure-helper + integration-test split used in Phases 1–2. Ask rather than assume on UX forks.

## Memory pointers (auto-loaded each session)
`auto-connect-geometry.md`, `undo-redo-canvas.md` in the memory dir summarize the implementation.
