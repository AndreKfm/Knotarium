# Step C2: Workflow Editor UI & Properties Sidebar Separation

## Goal
Decouple parameter configuration interfaces, separating properties sidebar UI modules from the primary React Flow Canvas component.

## Proposed Changes

### Component Separation
Refactor components to isolate render responsibilities:
- **`Canvas.tsx`**: Responsible strictly for visual node rendering, drag-and-drop connections, layout staggering, and selection changes (§2).
- **`PropertiesPanel.tsx`**: Standalone sidebar panel component mounted *beside* the Canvas container. Captures selection changes from Canvas, decides if a Node or Edge is active, and houses `<ManifestForm />` (§2, §6).
- **`shared/ManifestForm.tsx`**: Independent dynamic form component rendering parameter fields directly from package manifests (§2).

### Dynamic State Sync
Hook properties panel changes into Zustand or dynamic React callbacks:
- Propagate parameter mutations back to the visual Canvas node data arrays atomically.
- Highlight edge paths in indigo/blue during connection selections.

---

## Constraints from Architecture
- **Sidebar Decoupling**: The properties layout must live completely outside the Canvas component, preventing React Flow rendering lag on parameter keystrokes (§2).
- **Dynamic Bindings**: Property updates must map exactly to parameters defined in the node's package manifest, preserving the single-source-of-truth model (§5, §7).
- **Interactive Staggering**: Horizon staggering coordinates must be calculated to prevent overlaps during dynamic element creation (§2).
