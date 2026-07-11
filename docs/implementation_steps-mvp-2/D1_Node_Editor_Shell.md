# Step D1: Monaco-Based Node Editor Shell

## Goal
Build the in-app Monaco-based node authoring workspace, implementing the three-panel visual layout configuration.

## Proposed Changes

### Three-Panel Workspace Layout
Create and configure `NodeEditorShell.tsx` using standard flex rows and columns:
1. **Center Panel (Monaco Script Space)**: Main script window featuring the multi-tab Monaco editor. Tabs manage: `manifest.yaml` (package manifest properties), `Executor.cs` (C# compiled executor logic), and `tests/cases.yaml` (declarative test suite) (§6).
2. **Right Panel (Live UI Preview)**: Sub-sidebar container mounting `<ManifestForm />` in real-time. Driven dynamically by the manifest tab contents, mirroring parameter edits as they are written in Monaco (§6).
3. **Bottom Panel (Sandbox Test Terminal)**: Terminal panel housing standard execution buttons ("Run Tests"), parameter mock inputs, and output run results (§6).

---

## Constraints from Architecture
- **Three-Panel Layout**: The editor workspace must strictly preserve the Center = Monaco, Right = Preview, Bottom = Test Runner three-panel layout schema (§6).
- **Component Reusability**: The Live UI Preview panel must utilize the exact same `<ManifestForm />` component as the workflow editor sidebar, guaranteeing UI rendering fidelity across editor surfaces (§2, §6).
- **Hot-Reload Isolation**: Editing code drafts in Monaco must never affect active system executors until a formal package publish occurs (§5, §6).
