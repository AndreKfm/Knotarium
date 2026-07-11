# Typed I/O Subflows — Implementation Plan

Branch: `feat/subflow-typed-io`

## Goal

Turn subflows from "inline-only, single anchor in/out" into reusable components with a
**declared typed interface**: one or more named inputs and outputs (variables with types).

Target UX:
- A subflow node in a parent workflow exposes **dynamic ports** matching the referenced child's interface.
- Edges bind parent values to named child inputs; named child outputs surface as the node's outputs.
- Double-click a subflow node opens the child workflow in a new canvas.
- A subflow is reusable across workflows and multiple times within one workflow, with **instance-scoped internals** (no global-variable name collisions across instances).

## What already works (free, today)

The reference-based model (`subflow` node → `subflowId` → first-class `WorkflowDefinition`) already supports:
- **Open child in new canvas** — child is a normal workflow; frontend just loads it by id.
- **Reuse across workflows** — any workflow can reference the same `subflowId`.
- **Reuse multiple times in one workflow** — compile-time inlining prefixes child node ids per instance
  (`sub-node-A/…`), and `$node.*` expressions are rewritten per prefix
  (`WorkflowCompiler.cs:558` `RewriteExpressionPrefixes`).

## The core gap: a typed I/O contract

Today:
- `StartNodeTask` passes inputs straight through — **no declared input names/types** (`StartNodeTask.cs:12`).
- `EndNodeTask` returns empty success — **no declared outputs** (`EndNodeTask.cs:11`).
- The `subflow` manifest has a fixed single `result` output + `subflowId` param
  (`InMemoryNodePackageManifestProvider.cs:158`).
- **Manifests/ports are static per node TYPE** — but a subflow instance needs ports that vary by which
  child it references. This is the central obstacle.

---

## Architecture findings (grounded)

### Domain model — `Backend\KnotGarden.Core\Domain\`
- `NodeDefinition(NodeId Id, string Type, IReadOnlyDictionary<string,object> Properties)`
- `EdgeDefinition(string Id, NodeId From, string Output, NodeId To, string Input)`
- `WorkflowDefinition(Id, Name, Nodes, Edges, WorkflowMetadata? Metadata)` + `IsEnabled`
- `NodeId` = `record struct(string Value)` — `/` prefixing is a compiler convention, not a type semantic.
- **No place to hang an interface today.** `WorkflowMetadata` is the only existing extensibility hook.

### Manifest system — `Backend\KnotGarden.Core\Domain\NodePackageManifest.cs`
- `ParameterDefinition(Name, Type, Required, Expression, Values?)`, `OutputDefinition(Name, Type, Fields?)`,
  `InputDefinition(Name, Type, Fields?)`, `FieldSchema(Name, Type, Required)`.
- Manifests keyed strictly by **node Type** via `INodePackageManifestProvider.GetManifestAsync(NodePackageId)`.
- Edge/port validation: `WorkflowCompiler.cs:137-264` (output socket 171-183, input socket 185-203,
  type checks 205-262 via `TypeCompatibility.cs`, currently `WARN_*` only).
- Validation runs on the **flattened** node list AFTER inlining — so subflow-node edges are already
  redirected to child `start`/`end`. Dynamic-port validation must run **before/within inlining**, while
  edges still reference the `subflow` node.

### Global variables — collision risk (real, unhandled)
- `NodeExecutionContext.cs` `VariableBag` wraps a single flat `GlobalVariables` dict keyed by raw name.
- `SetVariableNodeTask` / `SetVariablesNodeTask` call `context.Variables.Set(name, value)` with the
  user-typed name — **no namespacing**.
- `RewriteExpressionPrefixes` only rewrites `$node.<id>.` — it does **not** touch `$variables.<name>`.
  Two instances of the same subflow both setting `counter` collide on the single global bag.

### API persistence — workflows stored as full `WorkflowDefinition` JSON
- `IWorkflowDefinitionProvider.GetDefinitionAsync(id)` → `SqliteWorkflowDefinitionProvider` →
  `DatabaseWorkflowStore.cs`. Compiler resolves children through this (`WorkflowCompiler.cs:426`).
- Endpoints in `Backend\KnotGarden.Api\Program.cs`: CRUD `/api/workflows` (630-738), validate (844),
  publish (851), `GET /api/node-packages` (1535).
- A new `Interface` field should round-trip via JSON automatically — **verify** `DatabaseWorkflowStore`
  serialization (column-mapped vs whole-doc JSON).

### Frontend — `Frontend\src` (state-driven, no router)
- `App.tsx` switches `currentView` + `selectedWorkflowId`; `Canvas.tsx` loads by `workflowId` prop
  (line 62, `loadWorkflowDefinition` 355) via `schemaMapper.toReactFlow`.
- Ports: manifest `outputs[].name` → `outputHandles` (`utils\nodePackages.ts:49,71`); `GenericCustomNode`
  falls back to `['result']` (`CustomNodes.tsx:159`). **Input side is a single `'in'` handle** today.
- Double-click already exists: `Canvas.tsx:1083 onNodeDoubleClick` (special-cases `inlineCode`).
- **No subflow support anywhere in the frontend.**

---

## Implementation steps

### (a) Declare the typed interface
Add an explicit declaration (don't infer from start/end), so validation + frontend have a stable schema
without compiling.
1. Add `WorkflowInterface? Interface = null` to `WorkflowDefinition` (`WorkflowDefinition.cs`):
   ```csharp
   public record WorkflowInterface(IReadOnlyList<InterfacePort> Inputs, IReadOnlyList<InterfacePort> Outputs);
   public record InterfacePort(string Name, string Type = "any", bool Required = false);
   ```
2. Bind interface to runtime data flow via start/end: declared inputs surface as `StartNodeTask` outputs;
   declared outputs are captured from `end`-node inputs.
3. Verify `DatabaseWorkflowStore` round-trips the new field.

### (b) Compiler — dynamic ports + inline-time binding (`WorkflowCompiler.cs`)
1. **Per-instance manifest** for `subflow` nodes: load child via `_definitionProvider`, synthesize a
   `NodePackageManifest` whose `Inputs`/`Outputs` come from the child `Interface`. Keep a local
   `Dictionary<NodeId, NodePackageManifest>` of instance manifests consulted FIRST in socket validation
   (161-203). **Do not** route through type-keyed `GetManifestAsync`. *(Key architectural change.)*
2. **Validate parent edges against child interface** in the parent frame, before redirecting edges to
   start/end (redirect at 493-519).
3. **Input binding**: when redirecting the incoming edge to child `start` (498-503), carry parent
   `edge.Input` (child input name) so `StartNodeTask` exposes it as a named output (synthetic binding
   property, or rewrite `PlannedEdge.Input` to the child input name instead of collapsing to `"in"`).
4. **Output binding**: symmetric for the outgoing edge from `end` (507-518); extend `EndNodeTask` to
   record named inputs as the subflow node's outputs.
5. Replace `ERR_SUBFLOW_MISSING_START/END` to also require each declared interface port is wired inside
   the child (warn/error on divergence).

### (c) Manifest / validation
1. Keep the static `subflow` manifest (`InMemoryNodePackageManifestProvider.cs:158`) as a fallback;
   real validation uses the per-instance synthesized manifest.
2. Extend socket validation (185-203) to treat subflow instance inputs as first-class.
3. Reuse `TypeCompatibility` for parent→child-input and child-output→parent checks; decide ERR vs WARN.

### (d) Instance-scoped variables (riskiest correctness item)
- **Preferred:** namespace variable names during inlining — extend the rewriter
  (`WorkflowCompiler.cs:533-583`) to also rewrite the `name` property of `setVariable`/`setVariables`
  nodes AND all `$variables.<name>` reads with the instance prefix. Limitation: runtime-computed names
  can't be statically rewritten — document, or restrict subflow-internal vars to static names.
- **Alternative:** give `VariableBag`/`NodeExecutionContext` a scope prefix and have the Set tasks write
  under the executing node's subflow-instance scope. Heavier (touches runtime contract + executor) but
  avoids brittle string rewriting.

### (e) Frontend
1. **Dynamic ports**: extend `schemaMapper.toReactFlow` to attach `outputHandles` + new `inputHandles`
   for `subflow` nodes from the child interface (not just from existing edges). Render multiple input
   handles in `GenericCustomNode` (`CustomNodes.tsx`, today single `'in'`). Source the child interface
   via the node-package list or a new `GET /api/workflows/{id}/interface`.
2. **Open in new canvas**: in `Canvas.tsx:1083 onNodeDoubleClick`, add a `subflow` branch reading
   `properties.subflowId` and navigating `App.tsx` to a new editor view (extend `NavigationState` with a
   back-stack for nested subflows — no router exists).
3. **Subflow picker** in `PropertiesPanel.tsx`: select `subflowId` from `api.getWorkflows()`, display the
   resolved interface ports.

---

## Riskiest / most uncertain
1. **Per-instance manifest resolution** — the whole port/validation system assumes type-keyed manifests.
2. **Output binding through `end`** — `EndNodeTask` returns empty; surfacing named child outputs needs a
   runtime data-capture mechanism that must survive id-prefix inlining.
3. **Instance-scoped variables** — static rewriting can't cover runtime-computed names; fully-correct fix
   is a scoped `VariableBag` (changes runtime contract + executor).
4. **Interface ⇄ start/end consistency** — keeping the declared interface in sync with actual wiring and
   erroring helpfully on divergence.

## Suggested sequencing
1. Frontend "open subflow in new canvas" (high value, ~zero backend risk) — proves the reference model.
2. Interface declaration + dynamic ports + inline-time binding (core feature).
3. Instance-scoped variables (correctness hardening for multi-use).

## Critical files
- `Backend\KnotGarden.Features\Compiler\WorkflowCompiler.cs`
- `Backend\KnotGarden.Core\Domain\WorkflowDefinition.cs`
- `Backend\KnotGarden.Features\Compiler\InMemoryNodePackageManifestProvider.cs`
- `Backend\KnotGarden.Core\Contracts\NodeExecutionContext.cs`
- `Backend\KnotGarden.Features\Nodes\StartNodeTask.cs`, `EndNodeTask.cs`
- `Frontend\src\utils\schemaMapper.ts`, `components\CustomNodes.tsx`, `components\Canvas.tsx`,
  `components\PropertiesPanel.tsx`, `App.tsx`
