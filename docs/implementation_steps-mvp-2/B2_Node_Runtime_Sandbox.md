# Step B2: Node Runtime Assembly Isolation & Sandbox

## Goal
Implement Collectible Assembly Load Contexts (ALC) for compiled node executors, construct the capability isolation factory, configure hot-reload watchers, and establish property-based testing.

## Proposed Changes

### Assembly Load Context Callout
> [!CAUTION]
> **ALC Security Warning**:
> An `AssemblyLoadContext` is **not a security sandbox**—it functions solely as a memory load/unload boundary. System security relies entirely on:
> Admin-managed installation + Static banned API analyzer + Restricted capability injection + Mandatory test gates + Cryptographic package signing. Complete virtualization (Tier 3 WASM) is deferred to Phase 3 (§5, DR-001).

### Capability Structural Isolation
Ensure strict isolation in the `NodeContextFactory`:
- Perform structural checks: assert that `INodeContext.Http == null` when the package `manifest.yaml` does not declare the `http` capability (§5).
- Assert `INodeContext.Credentials == null` when the manifest does not declare the `credentials` capability (§5).

### Hot-Reload Engine Mechanics
Implement dynamic hot-reloading:
- **Dev Mode**: Watch the `./nodes/` folder using `FileSystemWatcher` (with `IncludeSubdirectories = true`). Swap active assembly contexts atomically when the compiler emits updated binaries.
- **Prod Mode**: Restrict hot-reloads to the deliberate `POST /api/node-packages/install` endpoint (§5).
- **Execution Invariance**: In-flight executions must complete on their original compiled assembly version. Swaps are made atomically in the registry; active runners retain a direct, scoped reference to their resolved `INodeExecutor` for the duration of their execution lifecycle (§5).

### Property-Based Testing
Introduce property-based testing using **FsCheck** under `KnotGarden.NodeRuntime.Tests` to verify:
- ALC dynamic unloads and garbage collection invariants over random code iterations.
- Banned-API static analysis bans are verified over thousands of generated syntax trees (§15).

---

## Constraints from Architecture
- **Isolation Invariant**: Capabilities not explicitly requested in `manifest.yaml` must return structural null values, enforcing the principle of least privilege (§5).
- **Hot-Swap Atomicity**: Swapping registries must occur atomically. Active executors must pin their assembly loader instances to prevent class-unload failures mid-run (§5).
- **Unload Boundary**: Collectible `AssemblyLoadContext` allocations must be verified to have zero leaked active handles after GC sweeps (§5, §15).
