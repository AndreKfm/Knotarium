# OpenAPI Caller — Implementation Spec (Option C: data-manifest packages + shared interpreter)

**Status:** Draft  
**Date:** 2026-06-07

---

## Goal

Two problems with the current design:

1. **UX** — importing a spec is a separate "API Importer tab → drag operation to canvas" dance.
2. **Architecture** — each imported spec becomes a *generated C# executor compiled at runtime via Roslyn*. But that generated code is **byte-for-byte identical for every spec except one constant** (`SpecId`). There is no per-spec logic — only per-spec *data*. Compiling N copies of the same ~250 lines is pure waste: N collectible `AssemblyLoadContext`s, N JIT passes, and it widens the dynamic-compilation attack surface that `BannedApiAnalyzer` exists to police.

**Option C fixes both without losing what the package model gives us (per-spec palette nodes with pre-listed operations):**

- Keep **one `NodePackage` per imported spec** (`openapi.petstore`) → palette and operation dropdown work exactly as today.
- `OpenApiNodeGenerator` stops emitting executor C# source. It emits **only the manifest** (pure data).
- Add **one built-in, pre-compiled `OpenApiInterpreterExecutor`** — the body of today's generated template, with `SpecId` read from the manifest/node instead of a baked constant.
- **Route all `openapi.*` nodes to that single interpreter**, before the `DynamicCustomNodeTask` Roslyn-compile fallback.

Result: a workload with 5000 specs loads **one** assembly instead of 5000. The dynamic-compilation path is reserved for genuinely custom, user-authored C# nodes.

---

## Why not the alternatives

| | A: Codegen + compile (current) | B: One generic "OpenAPI Caller" node | **C: Data-manifest + shared interpreter (chosen)** |
|---|---|---|---|
| Per spec, generate… | C# source → Roslyn → assembly → ALC | nothing | a JSON manifest (data) |
| Runtime cost | JIT compile per spec, one ALC each | none | none |
| Palette shows | "Petstore", "Stripe" as distinct nodes ✅ | one generic node ❌ | "Petstore", "Stripe" as distinct nodes ✅ |
| Operation dropdown | baked into manifest ✅ | resolved live | baked into manifest ✅ |
| Security surface | widens dynamic-compilation surface ⚠️ | none | none |
| Spec update | recompile + reload | n/a | rewrite manifest JSON |

Option C is almost a *subtractive* change vs. A: delete `BuildExecutorCode`, promote the executor template to one pre-compiled class, add one registry case.

---

## Architecture

### Before (Option A)

```
ImportOpenApiSpecHandler
  → OpenApiNodeGenerator.Generate(spec)
      → NodePackage "openapi.petstore"  (manifest JSON + executor SOURCE)
DependencyInjectionNodeTaskRegistry
  → (no match) → DB lookup → DynamicCustomNodeTask
       → Roslyn compile OpenApiExecutor_petstore (SpecId = "petstore" const)
       → load into collectible ALC → run
```

### After (Option C)

```
ImportOpenApiSpecHandler
  → OpenApiNodeGenerator.Generate(spec)
      → NodePackage "openapi.petstore"  (manifest JSON only, NO source)
DependencyInjectionNodeTaskRegistry
  → nodeType starts with "openapi." → OpenApiInterpreterExecutor (pre-compiled, DI)
       → reads specId from nodeType, operationId from node inputs
       → loads ParsedSpec from IOpenApiSpecStore → builds + sends request
```

No Roslyn. No per-spec assemblies. Existing saved workflows with `openapi.<specId>` nodes dispatch to the interpreter instead of a recompiled copy of themselves.

---

## Node Data Model

Unchanged from today. The node's `type` carries the spec (`openapi.petstore`); `node.data` carries the rest:

```typescript
interface OpenApiNodeData {
  operationId?: string;     // e.g. "listPets"
  serverConfigId?: string;  // UUID
  arguments?: string;       // JSON: { path:{}, query:{}, header:{}, body:{} }
}
```

The interpreter derives `specId` from the node type by stripping the `openapi.` prefix (or, equivalently, from `manifest.Id`). No `specId` input parameter is needed — it is implicit in the package identity. This keeps existing nodes working with zero migration.

---

## Backend Changes

### B-1 — `OpenApiInterpreterExecutor` (new file)

**File:** `Backend/Knotarium.Features/OpenApi/OpenApiInterpreterExecutor.cs`

This is the body of today's generated template (`OpenApiNodeGenerator.BuildExecutorCode`, the `ExecuteAsync` logic at lines ~198–450), promoted to a real pre-compiled class. One change: `SpecId` is no longer a `const` — it is resolved at runtime.

```csharp
public sealed class OpenApiInterpreterExecutor : INodeExecutor
{
    private readonly IOpenApiSpecStore _specStore;
    private readonly IServerConfigStore _serverConfigStore;
    private readonly IOAuthTokenCache? _oAuthTokenCache;

    public OpenApiInterpreterExecutor(
        IOpenApiSpecStore specStore,
        IServerConfigStore serverConfigStore,
        IOAuthTokenCache? oAuthTokenCache = null)
    {
        _specStore = specStore;
        _serverConfigStore = serverConfigStore;
        _oAuthTokenCache = oAuthTokenCache;
    }

    public async ValueTask<NodeResult> ExecuteAsync(
        NodeInput input, INodeContext context, CancellationToken ct)
    {
        // specId is supplied via a reserved input the dispatcher injects (see B-2),
        // OR derived from the node type. Prefer an explicit input for testability:
        var specId = GetString(input, "__specId");
        if (string.IsNullOrEmpty(specId))
            return Fail("specId not provided to interpreter.");
        // ... rest is verbatim the existing template body, using `specId`
        //     instead of the const: load spec, find operation, build URL,
        //     place path/query/header/body args, apply auth
        //     (apiKey / bearer / basic / oauth2), send, map success/error.
    }
}
```

> The auth + request-building logic is already covered by `RestCallerExecutorTests` — that test suite moves to target this class (see Test Plan).

### B-2 — Wire the interpreter into the dispatch path

`OpenApiInterpreterExecutor` is an `INodeExecutor`, but the registry produces `INodeTask`. Two options; pick **(a)** for the smallest change:

**(a) Reuse `DynamicCustomNodeTask` plumbing, skip compilation.**
`DynamicCustomNodeTask` already maps `NodeExecutionContext → NodeInput/INodeContext`, injects `IOpenApiSpecStore`/`IServerConfigStore`/`IOAuthTokenCache`, and adapts `NodeResult → LegacyNodeResult`. Add an early branch: if the manifest tier is `Interpreted` (new) or the package id starts with `openapi.`, instantiate `OpenApiInterpreterExecutor` directly instead of compiling source. Inject `__specId` (the package id minus the `openapi.` prefix) into the `NodeInput`.

**(b) New `OpenApiNodeTask : INodeTask`** that does the same context mapping standalone. Cleaner separation, more boilerplate.

Recommended: **(a)**. The change in `DynamicCustomNodeTask.ExecuteAsync` is roughly:

```csharp
INodeExecutor executor;
if (manifest.Tier == NodeTier.Interpreted)   // new enum value
{
    executor = new OpenApiInterpreterExecutor(
        _openApiSpecStore!, _serverConfigStore!, _oAuthTokenCache);
    // ensure __specId reaches the executor
    inputs["__specId"] = JsonSerializer.SerializeToElement(
        _nodeType.StartsWith("openapi.") ? _nodeType["openapi.".Length..] : _nodeType);
}
else if (manifest.Tier == NodeTier.Declarative) { executor = new DeclarativeExecutor(manifest); }
else { /* existing Roslyn-compile path — now only for genuinely custom C# nodes */ }
```

### B-3 — `OpenApiNodeGenerator`: emit manifest only

**File:** `Backend/Knotarium.Features/OpenApi/OpenApiNodeGenerator.cs`

- **Delete** `BuildExecutorCode` and the `executorCode` field of `GeneratedPackage` (or leave the field and pass `string.Empty`).
- In `BuildManifestObject` / `BuildManifestYaml`, change `tier: Compiled` → `tier: Interpreted`.
- Drop the `specVersion` parameter if it was only used to disambiguate the compiled const; the interpreter loads the latest spec version by default. (Keep it if version pinning is a desired feature — it already works through the spec store.)

`GenerateManifestJson(spec)` is otherwise unchanged — `operationId` keeps its `values:` enum so the property panel dropdown still lists operations.

### B-4 — `NodeTier.Interpreted` enum value

**File:** wherever `NodeTier` is defined (search `enum NodeTier`).

```csharp
public enum NodeTier { Declarative, Compiled, Interpreted }
```

### B-5 — `ImportOpenApiSpecHandler`: stop storing source

**File:** `Backend/Knotarium.Features/OpenApi/ImportOpenApiSpecHandler.cs`

Set `NodePackageVersion.Source = string.Empty` (or null) for OpenAPI packages. The interpreter needs no stored source. The manifest JSON is still stored as before.

### B-6 — DI registration

**File:** `Backend/Knotarium.Api/Program.cs`

`OpenApiInterpreterExecutor` is `new`-ed by `DynamicCustomNodeTask` with services it already resolves, so no new DI registration is strictly required. If you prefer DI construction, register it scoped.

### Migration of existing packages

Existing `openapi.*` packages in dev/test DBs have `tier: Compiled` and stored source. Two paths:
- **No-op:** they keep compiling via the old path and still work. New imports use `Interpreted`.
- **Clean:** a one-line startup migration flips `tier` to `Interpreted` for `id LIKE 'openapi.%'`. Stored source becomes dead weight but is harmless.

Recommend the no-op for safety; the dev DB is regenerated anyway (see PROGRESS.md Step 03).

---

## Frontend Changes

Smaller than Option B — the canvas node type, palette, and property routing are **unchanged**, because each spec is still its own package/node type. The only UX addition is making import reachable without leaving the canvas.

### F-1 — Keep `RestCallerPropertyForm` routing as-is

**File:** `Frontend/src/components/PropertiesPanel.tsx`

The existing `type.startsWith('openapi.')` → `RestCallerPropertyForm` route is correct and unchanged.

### F-2 — In-canvas import affordance (the UX fix)

Add an "Import OpenAPI…" entry to the node palette (or a canvas context-menu item) that opens the existing `<OpenApiImporter>` modal. On success, the new `openapi.<specId>` package is registered and immediately appears as a palette node the user can drag. This removes the need to switch to the "API Importer" tab to import, while keeping that tab for browsing/management.

No new property-panel component is needed (that was Option B's cost).

### F-3 — Demote the API Importer tab (optional)

**File:** `Frontend/src/App.tsx`

Keep the tab for spec/server-config management; it's no longer the only way to get a node onto the canvas. Optional "(Manage)" relabel.

---

## Implementation Order

| # | What | Risk |
|---|------|------|
| 1 | `NodeTier.Interpreted` enum value | Trivial |
| 2 | `OpenApiInterpreterExecutor.cs` (promote template body) | Medium |
| 3 | `DynamicCustomNodeTask` interpreter branch + `__specId` injection | Medium |
| 4 | `OpenApiNodeGenerator`: drop source, `tier: Interpreted` | Low |
| 5 | `ImportOpenApiSpecHandler`: stop storing source | Low |
| 6 | Repoint `RestCallerExecutorTests` at the interpreter | Low |
| 7 | FE: in-canvas "Import OpenAPI…" affordance | Low |

Steps 1–6 are one backend PR. Step 7 is a small frontend PR.

---

## Test Plan

### Backend — unit

`RestCallerExecutorTests.cs` already exercises URL building, path/query/header/body placement, omit-optional-args, and apiKey/bearer/basic/oauth2 auth against mock `IHttpClient`/`ICredentialAccessor`. **Repoint it** from the Roslyn-compiled generated class to `OpenApiInterpreterExecutor` (construct directly, pass `__specId` via `NodeInput`). All existing assertions should pass unchanged — same logic, no longer compiled.

Add:
- `Execute_MissingSpecId_ReturnsError`
- `Execute_SpecIdFromNodeType_ResolvesSpec` (verify the `openapi.` prefix stripping in the dispatcher)

`OpenApiNodeGeneratorTests` — update to assert:
- emitted manifest has `tier == Interpreted`
- `GeneratedPackage.ExecutorCode` is empty/absent
- `operationId` parameter still carries the `values:` enum

### Backend — integration

One execution-engine test (analogous to `ExecutionEngineTests`): a workflow with an `openapi.petstore` node runs end-to-end through the registry → `DynamicCustomNodeTask` interpreter branch → mock HTTP, asserting **no Roslyn compilation occurs** (e.g., assert the compile cache stays empty, or that no `CSharpCompilation` is invoked).

### Frontend — Vitest

- "Import OpenAPI…" affordance opens the importer modal
- After a successful import, the new `openapi.<specId>` node appears in the palette

### E2E — Playwright (PROGRESS.md Step 12)

Unchanged in intent: import Petstore (all three dialects), drop the resulting node, configure an operation, run against a mock server, assert `success`.

---

## Open Questions

1. **`__specId` reserved input vs. deriving from node type** — Using a reserved input keeps `OpenApiInterpreterExecutor` unit-testable in isolation; the dispatcher is the only place that knows about the `openapi.` prefix convention. Preferred over the executor parsing its own node type.

2. **Version pinning** — The spec store already supports `GetVersionAsync`. If pinning is wanted, keep the `specVersion` manifest parameter and forward it; otherwise the interpreter uses `GetLatestAsync`.

3. **Should the Roslyn `Compiled` tier remain at all?** — Yes. It still serves genuinely custom, user-authored C# nodes (`NodeEditorSandboxService`). Option C only removes OpenAPI from that path.
