# Step 12 — B1.3: Unified Node Registry

## Goal
Unify the discovery and retrieval of both built-in and dynamic custom C# node packages under a single backend-sourced registry endpoint (`api.getNodePackages()`). All frontend components must consume this registry, completely eliminating separate, hardcoded list definitions of built-in nodes.

---

## Invariant Alignment
* **Invariant 1.4 (Single-Source Registry):** The UI loads all nodes—built-in and custom dynamic packages—from a single unified api query (`api.getNodePackages()`). No duplicate or hardcoded frontend lists are permitted.

---

## Proposed Changes

### 1. Update [DbNodePackageManifestProvider.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/KnotGarden.Api/DbNodePackageManifestProvider.cs) [MODIFY]
* Ensure the registry provider fetches both the built-in system nodes (e.g. `Start`, `Stop`, `Webhook`, `Delay`, and `Scheduler`) and all custom dynamic C# node packages stored in the SQLite database.
* Return their standardized manifests (display name, categories, icon, inputs/outputs, and parameters) in a single unified array.

### 2. Modify [api.ts](file:///d:/Private/Source/AknSideProjects/Automate/Frontend/src/api.ts) [MODIFY]
* Declare the API method `getNodePackages()` to query the backend route `/api/node-packages`.

### 3. Modify [Canvas.tsx](file:///d:/Private/Source/AknSideProjects/Automate/Frontend/src/components/Canvas.tsx) [MODIFY]
* Refactor Canvas rendering to invoke `api.getNodePackages()` on mount.
* Set the fetched manifest array in Zustand canvas state store as `availableNodes`.
* Delete all duplicate front-end definitions of built-in node types and static toolbar objects.

---

## Verification & Test Checklist

### 1. Integration Tests
* Write a backend integration test in `NodePackagesControllerTests.cs` to verify that:
  * A call to `/api/node-packages` returns a collection containing both the system-standard `Webhook`/`Scheduler` trigger nodes and any registered custom dynamic C# nodes.
  * The schema format matches standard front-end node models perfectly.

### 2. Manual Verification
* Launch KnotGarden (`.\run.ps1`). Confirm no duplicate node listings appear in the palette layout and that custom nodes render successfully.
