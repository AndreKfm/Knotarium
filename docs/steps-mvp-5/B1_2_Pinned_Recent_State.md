# Step 15 — B1.2: Pinned & Recent State

## Goal
Implement a "Recent / Pinned" nodes section pinned directly to the top of the new Sidebar Palette. To prevent database bloating, this persistent visual state must live purely on the client-side, managed by **Zustand** and synchronized using browser **`localStorage`**.

---

## Invariant Alignment
* **Invariant 1.5 (Ephemeral Client State):** A "Recent / Pinned" category group lives at the very top of the sidebar palette, backed strictly by Zustand and `localStorage`. Storing this state in SQLite is prohibited.

---

## Proposed Changes

### 1. Modify or Create [useCanvasStore.ts](file:///d:/Private/Source/AknSideProjects/Automate/Frontend/src/stores/useCanvasStore.ts) [MODIFY]
* Extend the Zustand canvas state hook to track arrays of node IDs representing pinned and recent selections:
```typescript
interface CanvasState {
  pinnedNodeIds: string[];
  recentNodeIds: string[];
  togglePinNode: (nodeId: string) => void;
  addRecentNode: (nodeId: string) => void;
}
```
* Configure Zustand's middleware (`persist`) to automatically serialize and load the `pinnedNodeIds` and `recentNodeIds` from/to `localStorage` under the key `knotarium:canvas-palette`.
* Limit the `recentNodeIds` list to a maximum of 5 unique entries (evicting the oldest via FIFO queue).

### 2. Modify [SidebarPalette.tsx](file:///d:/Private/Source/AknSideProjects/Automate/Frontend/src/components/SidebarPalette.tsx) [MODIFY]
* Render a special "Recent / Pinned" collapsible category at the top of the sidebar list.
* Add a small pin/unpin icon button (e.g. `📌`) next to every node package item. Clicking it calls `togglePinNode(nodeId)`.
* When a node is successfully dragged or added to the canvas, trigger `addRecentNode(nodeId)`.

---

## Verification & Test Checklist

### 1. Unit Tests
* Write a unit test in `useCanvasStore.test.ts` to assert:
  * **Toggles**: Toggling a node's pin state adds/removes it from `pinnedNodeIds`.
  * **Queue Size**: Adding a node to `recentNodeIds` keeps it within the maximum size limit of 5.
  * **Persistence**: Assert that the state matches values in mocked local storage.

### 2. Manual Verification
* Pin several items, refresh the browser, and verify they stay pinned.
