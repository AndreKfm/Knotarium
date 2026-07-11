# Knotarium — Architecture v2

## Goal

Design a web application for visually defining, extending, and executing automation flows via a node-based interface. v2 treats *the extensibility surface* — how new nodes are created, loaded, and run — as the central product differentiator. A developer should be able to add a new node by dropping a package directory under `./nodes/` and seeing it appear in the workflow canvas without restarting the host. The architecture is designed so an AI-assisted node generator (Phase 2+) can be added later without changing core contracts.

Where v1 was a minimal foundation explicitly deferring extensibility, v2 commits to extensibility as a first-class concern from day one — but defers AI generation, multi-tenant sandboxing (WASM), and Postgres deployment to clearly-staged later phases.

## Status

**Complete & Fully Implemented.** The entire extensibility core (v2) has been fully realized, tested, and verified as 100% green. This includes the collectible assembly load context for C# dynamic nodes, capability-injection sandbox, declarative Tier-1 interpreter, dynamic C# statement Roslyn compiler, in-app editor, package signing, and tamper-evident audit hash-chain.

---

## 1. High-Level System Overview

- **Architectural style:** Modular Monolith with Vertical Slice Architecture.
- **Three-layer model preserved from v1:** React Flow graph (presentation) → canonical Workflow JSON (storage) → compiled `ExecutionPlan` (execution).
- **Two distinct editors:**
  - **Workflow Editor** — React Flow canvas for composing flows from existing nodes.
  - **Node Editor** — in-app Monaco-based editor with live preview and test runner, for creating and modifying node packages manually. AI-assisted generation is *not* in scope but the editor is structured so an `INodePackageGenerator` can plug into it later.
- **Node packages as the unit of extension.** A node is a self-contained directory (manifest + executor + optional icon + tests), discovered from a `./nodes/` folder. Hot-reload in development; deliberate install API in production.
- **Execution engine:** Custom hosted worker walking immutable `ExecutionPlan`s, appending to a journal, publishing events via SSE.
- **Database:** SQLite by default, with provider abstraction so Postgres can be swapped in via configuration. See §3.
- **Scalability boundary:** Single machine. Distributed execution is out of scope; multi-machine requires externalizing the executor's state machine.

---

## 2. Frontend

- **Framework:** React 18 + Vite + TypeScript.
- **Workflow canvas:** React Flow.
- **Code editing inside the Node Editor:** Monaco (the VS Code editor as a web component).
- **State management:** Zustand for client state. TanStack Query for server state.
- **Styling:** CSS Modules. No design-system framework forced.
- **Dynamic UIs:** Both the workflow-editor parameter panels and the node-editor live-preview render from the same generic `<ManifestForm>` component, driven by the node manifest. Every node — built-in or custom — gets a parameter UI for free, and the node author sees in real time how their node will appear to workflow users.
- **Real-time updates:** Native `EventSource` against the backend SSE endpoint. Replay via `Last-Event-ID`.

---

## 3. Backend (.NET 10) & Database

### Provider strategy: SQLite now, Postgres later (configurable)

v2 ships with SQLite as the default. The data-access layer is built against an `IDatabaseProvider` abstraction so that switching to Postgres is a configuration change, not a code change. Concretely:

```json
// appsettings.json
{
  "Database": {
    "Provider": "SQLite",          // "SQLite" | "Postgres"
    "ConnectionString": "Data Source=Knotarium.db"
  }
}
```

A factory selects `UseSqlite(...)` or `UseNpgsql(...)` on the EF Core `DbContext`. The Postgres provider is *not implemented* in v2 initial scope but the abstraction and the configuration knob are present so that adding it later requires no architectural change. Marker comments in code (`// TODO(v1.5): Postgres provider implementation`) flag the integration points.

### Why this shape, not "Postgres day one"

Postgres would add an operational dependency (a running database server) for a product that, at this stage, ships to single-machine deployments. SQLite is sufficient for the journal write rate and the editor-side write load expected in v2. The cost of *preparing* for Postgres now (an interface and a config knob) is trivial; the cost of actually running Postgres in dev/test/CI is not.

When the time comes to switch (multi-user editing, higher concurrent load, or `LISTEN/NOTIFY`-based event distribution), the migration is: add the provider implementation, generate Postgres migrations, change one config value.

### Data access strategy

- **EF Core** for the relational, modelling-rich tables (`WorkflowDefinition`, `WorkflowVersion`, `NodePackage`, `NodePackageVersion`, `Credential`, `AuditEntry`).
- **Direct ADO.NET** (`Microsoft.Data.Sqlite` now, `Npgsql` later) for the hot-path journal writes. Journal inserts are append-only, schema-stable, and high-frequency; EF's change-tracking is wasted overhead here. Hidden behind `IExecutionJournalWriter` so the provider switch swaps one implementation.

### Migrations

Pragmatic stance for v2: SQLite migrations are the only ones generated and tested. When Postgres support is implemented, migrations are regenerated for that provider — not maintained in parallel from day one.

### Core tables

| Table | Purpose |
|-------|---------|
| `WorkflowDefinition` / `WorkflowVersion` | Workflow content, versioned. |
| `ExecutionInstance` / `ExecutionJournal` / `NodeState` | Execution state. Journal is append-only source of truth. |
| `NodePackage` / `NodePackageVersion` | Installed node packages. Each version stores manifest, source, compiled assembly bytes, signature, capability declarations. |
| `Credential` | Secret references and encrypted secret values. |
| `AuditEntry` | Append-only audit log: package installs, edits, capability grants, workflow publishes. Each row stores `previous_hash` and `entry_hash` (SHA-256) for tamper-evident chaining. |

---

## 4. Core Workflow Engine

Shape inherited from v1; the additions are about what the executor knows about loaded node packages.

### Execution model

- Single hosted `BackgroundService` owns the execution loop.
- Each `ExecutionInstance` runs in its own DI scope.
- All journal writes serialize through a single `IExecutionJournalWriter`. Startup guard enforces exactly one execution worker process.

### Cancellation

Cooperative cancellation with hard-kill timeout. **Default: 5 seconds cooperative, then hard cancellation token completes.** Both states recorded in the journal. Configurable per node via manifest, capped at 60s.

### Idempotency

Inherited verbatim from v1 — `NodeSideEffectKind` and `RecoveryMode` are part of the manifest now (not only the C# interface), so the workflow editor and (later) the AI generator both have access to them.

### State ownership

- `ExecutionJournal` is append-only, absolute source of truth.
- `NodeState` is a projection updated in the same transaction as the journal append.

---

## 5. Node Packages — Extensibility Core

The largest architectural difference from v1. A node is a **package directory**, not a class in the main project.

### Package layout

```
./nodes/
  HttpRequest/
    manifest.yaml          # Metadata, parameters, capabilities, side-effect kind
    Executor.cs            # INodeExecutor (compiled tier only)
    icon.svg               # Optional
    tests/
      cases.yaml           # Declarative input/output test cases
```

### Two execution tiers in v2 initial scope

**Tier 1 — Declarative nodes (target: 70% of all nodes).** No C# code. The manifest fully describes behaviour: an HTTP template, a JSONPath transform, a conditional. A generic `DeclarativeExecutor` interprets the manifest at runtime. Examples: most "Send X to Y" connectors, simple transforms, conditions.

**Tier 2 — Trusted compiled nodes (target: 30%).** A C# class implementing `INodeExecutor`. Roslyn compiles it; the resulting assembly is loaded into a collectible `AssemblyLoadContext`. Signed by the host on publish, admin-only installation in production. Capabilities (HTTP, credentials, logging) are injected via `INodeContext` — *no* node can reach beyond its declared capabilities. Examples: nodes with branching logic, custom retry strategies, format-specific parsers.

**Tier 3 — Sandboxed plugins (WASM via SDK).** Explicitly **not in v2 initial scope**. See the "Future: Tier 3 (WASM)" decision record at the end of this document for the committed direction. v2 leaves architectural room (capability model, manifest format) so this tier can be added without rework.

### Capability model

Every node declares in its manifest exactly what it needs:

```yaml
capabilities:
  - http             # IHttpClient injected
  - credentials      # ICredentialAccessor injected (scoped to declared refs)
  - logging          # ILogger injected (always granted)
  - workflowState    # IWorkflowState injected (read variables, set outputs)
```

What is not declared is not injected, and `INodeContext` exposes only the granted capabilities. There is no escape hatch — no `IServiceProvider`, no static singletons. Enforced by:

- A Roslyn analyzer at compile time banning `System.IO`, `System.Diagnostics.Process`, `System.Net.Sockets`, `System.Reflection.Emit`, and static mutable state outside the executor class. **No override flag.**
- Absence of registrations at runtime — capabilities not declared in the manifest are not present on the `INodeContext` instance handed to the executor.

This is the strongest enforcement Tier 2 can offer in-process. It is "trust through review and analyzer", not "trust through isolation". The Tier-3 WASM path (Phase 3) is what provides isolation-grade trust for untrusted code.

### Hot-reload

`AssemblyLoadContext` with `isCollectible: true`, combined with `FileSystemWatcher` on `./nodes/` in development and a deliberate install API in production.

- **Dev mode:** edit `Executor.cs`, save, watcher fires, old ALC unloaded, new ALC loaded, registry swapped atomically. In-flight executions on the old version complete on the old assembly (executor holds a reference to the resolved `INodeExecutor` for the duration of the run).
- **Production:** changes go through `POST /api/node-packages/install`, which writes the new version row, triggers the same ALC swap, but adds: signature verification, capability-grant confirmation by an admin, and an audit entry. No `FileSystemWatcher` in production.

### Package discovery and versioning

- `NodeDiscoveryService` scans `./nodes/` at startup and registers every package version row found.
- Workflows pin to specific package versions in their `WorkflowVersion` row — a newer node package never silently changes the behaviour of a saved workflow.
- The Node Editor surfaces the diff between the currently-published version and the in-progress draft.

---

## 6. The In-App Node Editor (Manual)

Headline feature of v2. The editor is built *without* AI in initial scope — AI generation is a Phase 2+ addition that plugs into the same editor surface.

### User flow

1. Developer opens the Node Editor and clicks "New Node" (or "Edit" on an existing package).
2. Center panel: Monaco editor with three tabs — `manifest.yaml`, `Executor.cs` (Tier 2 only), `tests/cases.yaml`.
3. Right panel: live preview of how the node will render in the workflow canvas, driven by the current manifest. Renders via the same `<ManifestForm>` component the workflow editor uses.
4. Bottom panel: test runner. Developer fills mock inputs, clicks Run, sees output / logs / exceptions from a sandboxed test execution.
5. When satisfied: "Publish v1" — package is signed, audit-logged, hot-loaded, appears in the workflow palette.

### Test-before-publish (mandatory gate)

`POST /api/node-editor/test` is the only path to a `publish` call. It:

1. Validates the manifest against the JSON Schema.
2. For Tier 2: runs the banned-API Roslyn analyzer. Hard-fails on any violation.
3. Compiles the executor into a temporary collectible ALC.
4. Runs each declared test case with a mock `INodeContext` that records all capability calls.
5. Returns: pass/fail per case, captured logs, exceptions with stack traces, and a summary of capabilities actually invoked (cross-checked against the manifest declaration — invoking an undeclared capability fails the test).

`POST /api/node-packages/publish` refuses any package that has not passed `test` in the current editor session.

### Versioning and rollback

Every publish creates a new `NodePackageVersion`. Workflows can pin or float (default: float to latest minor on the same major). One-click rollback to any prior version. Audit log records who published what and when.

### Future AI integration (architectural placeholder)

The editor is structured so AI generation can be added later without refactoring:

- `INodePackageGenerator` interface lives in `Knotarium.Core` with the signature `Task<GeneratedPackage> GenerateAsync(GenerationRequest request, CancellationToken ct)`. No implementation in v2.
- The Node Editor UI has a designated panel slot where a "Generate" button will appear once the interface is implemented. In v2 the slot is hidden behind a feature flag (`Features:AiGeneration = false`).
- An MCP server integration is the likely Phase 2 path, so the user can connect their own AI provider rather than the platform managing API keys.

No AI-specific tables, conversation persistence, or telemetry are in v2.

---

## 7. Contract Definitions

These contracts are foundational and load-bearing for every feature.

### Manifest schema (excerpt)

```yaml
id: http.request
version: 1.2.0
displayName: HTTP Request
category: Network
tier: declarative   # or "compiled"
sideEffectKind: IdempotentSideEffect
recoveryMode: RetryAutomatically
defaultTimeoutSeconds: 30

capabilities:
  - http
  - credentials

parameters:
  - name: url
    type: string
    required: true
    expression: true   # supports {{ $node.X.output.field }}
  - name: method
    type: enum
    values: [GET, POST, PUT, PATCH, DELETE]
    default: GET
  - name: apiKeySecretRef
    type: credentialRef
    required: false

outputs:
  - name: success
  - name: error

retryPolicy:
  maxRetries: 3
  backoff: exponential
```

The manifest is the **single source** that drives: workflow-canvas parameter UI, node-editor live preview, executor parameter binding, JSON-Schema validation, and (future) AI generation context.

### Strongly-typed IDs

```csharp
public readonly record struct NodeId(string Value);
public readonly record struct WorkflowDefinitionId(string Value);
public readonly record struct WorkflowVersionId(Guid Value);
public readonly record struct ExecutionInstanceId(Guid Value);
public readonly record struct NodePackageId(string Value);          // e.g., "http.request"
public readonly record struct NodePackageVersionId(Guid Value);
```

### Node interface (Tier 2)

```csharp
public interface INodeExecutor
{
    ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken);
}

public sealed record NodeInput(IReadOnlyDictionary<string, JsonElement> Parameters);

public sealed record NodeResult(
    string OutputName,
    JsonElement? Payload,
    NodeExecutionStatus Status);

public interface INodeContext
{
    // Always present
    ILogger Logger { get; }
    IWorkflowState State { get; }

    // Conditionally present based on manifest capabilities
    IHttpClient? Http { get; }
    ICredentialAccessor? Credentials { get; }
}
```

`NodeInput` uses dynamic dictionaries rather than typed records. Rationale: keeping the manifest as the single contract avoids forcing two stubs (manifest + record) to stay in sync — important for both manual editing and future AI generation.

### Compiler output

Unchanged from v1 — `ExecutionPlan`, `CompilationResult`, `CompilationDiagnostic`. Subflow inlining preserved.

---

## 8. Variables, Data Flow, and Expressions

A point left underspecified in v1 and worth nailing down in v2.

- **Primary data flow:** through edges, as `NodeResult.Payload`. Downstream nodes receive their direct upstream's payload as `input.UpstreamPayload`.
- **Cross-node references:** the expression engine supports `{{ $node.<nodeId>.output.<path> }}` to read any prior node's output in the current execution. All outputs are retained in `WorkflowState` for the duration of the execution (cleared on completion).
- **Workflow-scoped variables:** `context.State.Variables.Get<T>(name)` and `Set(name, value)` for explicit variable storage (set via the `SetVariable` node or programmatically inside a Tier-2 node).
- **Secrets in expressions:** secret references resolve at the last possible moment, inside the capability accessor — never substituted into a logged or journaled expression string.

The expression engine is a small handwritten evaluator: identifier paths, basic operators (`==`, `!=`, `&&`, `||`, `+`, `-`, `*`, `/`), a fixed function set (`now()`, `uuid()`, `coalesce()`, `length()`, JSONPath segments). Deliberately restricted — expressions execute in the host process and must not become an exfiltration vector. This is the "small controlled DSL" referenced in the Tier-3 decision record below.

---

## 9. Real-Time Transport: SSE

Preserved from v1. Rationales unchanged:

- W3C standard, native `EventSource`.
- Mirrors journal sequence for `Last-Event-ID` replay.
- Inspectable via `curl -N`.
- Polyglot-friendly.

**Notification mechanism:** short polling on the journal table (configurable interval, default 100ms) in v2 initial scope. When the Postgres provider is implemented, `LISTEN/NOTIFY` becomes available as an optional faster path; the SSE publisher abstracts over both behind `IExecutionEventSource`.

If bidirectional features are later required (collaborative editing of workflows or nodes), a WebSocket endpoint is added alongside SSE.

---

## 10. Built-in Nodes (Day-One Scope)

Larger than v1 because the extensibility infrastructure makes additional nodes cheap. Each is itself a node package, dogfooding the package system.

| Node | Tier | Category |
|------|------|----------|
| Start | Declarative | Trigger |
| Manual Trigger | Declarative | Trigger |
| Webhook Trigger | Compiled | Trigger |
| Condition (If/Else) | Declarative | Logic |
| Switch | Declarative | Logic |
| SetVariable | Declarative | Data |
| Transform (JSONPath) | Declarative | Data |
| Merge | Compiled | Data |
| HTTP Request | Compiled | Network |
| Delay | Compiled | Utility |
| Log | Declarative | Utility |
| End | Declarative | Utility |

Deferred to Phase 2: Database nodes (security review), File I/O nodes (security review), Scheduled Trigger (requires `PeriodicTimer` scheduler — small addition, not architectural), Email send (third-party SDK choice pending).

---

## 11. Secrets and Credentials

Promoted from a subsection in v1 to its own concern in v2.

- **`Credential`** is a first-class entity, encrypted at rest using a key managed by the host (key from env var or platform keystore, never in DB).
- Nodes never see raw secret values in parameters. They request access via `INodeContext.Credentials.GetAsync(credentialRef)`, which returns a `SecretValue` wrapper that:
  - Implicitly converts to a string only inside the capability accessor (e.g., when set as an HTTP header by the `IHttpClient` capability).
  - Has a `ToString()` that returns `"***"`.
  - Is filtered out of any structured logging by a global `ILogger` enricher.
- Audit log records every credential access: which node, which execution, which workflow.

---

## 12. Observability

First-class in v2 (absent in v1).

- **Logs:** Serilog with structured-property enricher, default JSON sink to stdout, file sink in dev.
- **Metrics:** OpenTelemetry. Counters: `executions_started_total`, `executions_completed_total`, `executions_failed_total`, `journal_writes_total`. Histograms: `node_execution_duration_seconds{node_type}`, `journal_write_latency_seconds`. Gauges: `running_executions`, `loaded_node_packages`.
- **Traces:** OpenTelemetry. Each `ExecutionInstance` is a trace; each node execution a span; each capability call a child span.

---

## 13. Security Posture

- **No untrusted user code at runtime** in v2 initial scope. Tier 2 packages are admin-installed and signed. Tier 3 (sandboxed customer code) is deferred to Phase 3.
- **Capability-only execution** as defined in §5. No node has ambient authority.
- **Roslyn analyzer ban list** enforced at compile time for Tier 2 packages. No override flag.
- **Signature verification** for production-installed packages. Self-built packages signed by the host's key on `publish`; externally distributed packages require a configured trusted key.
- **Audit trail** for every package mutation, capability grant, credential access, and workflow publication. Append-only with hash-chain entries (`previous_hash` + `entry_hash` per row, SHA-256 over canonical serialization) for tamper evidence — see DR-004.
- **Network egress** from compiled nodes is funnelled through the `http` capability, which enforces a configurable allowlist/blocklist of destination domains per package.

---

## 14. Project Structure

### Backend

```
/Backend
  /src
    /Knotarium.Api                       (Host, Minimal APIs, SSE endpoints, Node Editor endpoints)
    /Knotarium.Core                      (IDs, INodeExecutor, manifests, ExecutionPlan, IExecutionEventPublisher, INodePackageGenerator placeholder)
    /Knotarium.Infrastructure            (EF Core + provider abstraction, direct-ADO.NET journal writer, secret resolver, audit, OpenTelemetry)
    /Knotarium.NodeRuntime               (Collectible ALC loader, capability injection, banned-API analyzer, test sandbox, declarative executor)
    /Knotarium.Features
      /Definitions                      (Workflow CRUD, versioning)
      /Compiler                         (Graph → ExecutionPlan; subflow inlining; diagnostics)
      /Execution                        (Hosted worker, DAG traversal, journal writer, event publisher impl)
      /NodePackages                     (Discovery, install, publish, version management)
      /NodeEditor                       (Test endpoint, publish endpoint; AI integration slot — empty in v2)
      /Credentials                      (CRUD, encryption, access control)
      /Audit                            (Append + query)
  /nodes                                (Built-in node packages, dogfooding the package system)
  /tests
    /Knotarium.Core.Tests
    /Knotarium.Compiler.Tests
    /Knotarium.Execution.Tests
    /Knotarium.NodeRuntime.Tests         (ALC unload, capability isolation, analyzer rules)
    /Knotarium.Api.IntegrationTests
```

### Frontend

```
/Frontend
  /src
    /workflow-editor                    (React Flow canvas, properties panel, execution viewer)
    /node-editor                        (Monaco, manifest editor, live preview, test runner)
    /components/shared                  (ManifestForm — used by both editors)
    /api                                (HTTP client, EventSource wrapper)
    /schemas                            (Generated TS types from backend JSON schemas)
    /stores                             (Zustand)
  /tests
    /unit                               (Vitest)
    /e2e                                (Playwright)
```

---

## 15. Implementation Plan (Staged)

### Phase A — Foundations (parallel-blocked: nothing else starts until A is done)

| # | Step | Realises |
|---|------|----------|
| A1 | **Core contracts & schemas** | §7 (all contracts), §5 (manifest schema), idempotency enums, `INodePackageGenerator` placeholder |
| A2 | **Workflow Compiler** | §1, §7 (`CompilationResult`), subflow inlining |
| A3 | **Database & persistence (SQLite, provider abstraction)** | §3, §4 (journal semantics), §11 (credential storage) |

### Phase B — Execution

| # | Step | Realises |
|---|------|----------|
| B1 | **Custom execution engine** | §4 |
| B2 | **Node runtime: capability injection + collectible ALC** | §5 (compiled tier, capabilities, hot-reload mechanics) |
| B3 | **Declarative executor** (Tier 1) | §5 (declarative tier) |
| B4 | **Built-in node packages** | §10 |

### Phase C — API & Frontend Foundations

| # | Step | Realises |
|---|------|----------|
| C1 | **Minimal API + SSE publisher (polling-based)** | §9 |
| C2 | **Workflow Editor (React Flow + ManifestForm)** | §2 |

### Phase D — Node Editor

| # | Step | Realises |
|---|------|----------|
| D1 | **Node Editor shell: Monaco + live preview + test runner** | §6 |
| D2 | **Banned-API analyzer + test sandbox + publish gate** | §6 (mandatory gate), §13 |
| D3 | **Package signing, version pinning, audit log** | §5 (versioning), §13 (audit, signatures) |

### Phase E — Production Readiness

| # | Step | Realises |
|---|------|----------|
| E1 | **Observability: Serilog, OpenTelemetry, dashboards** | §12 |
| E2 | **Secrets hardening: key management, log filtering, egress allowlist** | §11, §13 |
| E3 | **End-to-end: Playwright suite covering create-node → use-in-workflow → execute** | All |

### Cross-cutting requirements

- Unit tests per step. Integration tests where the step crosses a process or storage boundary.
- Node Runtime work (B2) and the banned-API analyzer (D2) get **property-based tests** (FsCheck) — the failure modes are subtle and example-based testing catches the obvious cases only.
- Phase B and Phase C2 can run in parallel against the contracts from Phase A. Phase D depends on B and C being complete.

---

## 16. Key Differences from v1

| Aspect | v1 | v2 |
|--------|----|----|
| Database | SQLite | SQLite (default) with provider abstraction; Postgres swappable via config |
| Data access | EF Core | EF Core + direct ADO.NET for journal |
| Plugin loading | `AssemblyLoadContext`, host restart required | Collectible ALC, hot-reload via `FileSystemWatcher` (dev) and install API (prod) |
| Node definition | C# class inside main project | Self-contained package directory (manifest + executor + tests) |
| Node creation | Code in IDE, rebuild | In-app Node Editor (Monaco + live preview + test runner), manual |
| AI assistance | Not considered | Not in scope; `INodePackageGenerator` interface placeholder and UI slot reserved for Phase 2+ |
| Built-in node count | 7 | 12 |
| Secrets | Subsection of Extensibility | First-class concern with its own section |
| Observability | Not specified | First-class section: Serilog + OpenTelemetry |
| Security | Trusted plugins, host restart | Capability model, Roslyn ban list, signature verification, audit log, egress allowlist |
| Expression engine | Not specified | Small controlled DSL (handwritten evaluator with explicit function set) |
| Cancellation timeout | TBD | 5s cooperative default, configurable per node, capped at 60s |
| Realistic build time | Weeks | Several months |

---

## 17. Decision Records

### DR-001: Tier-3 (untrusted code) runtime — WebAssembly via SDK, deferred to Phase 3

Captured verbatim from the architectural conversation so the reasoning survives future re-litigation.

**Decision:** Scripting and plugin runtime for an n8n-like automation tool.

For an n8n-like automation tool, WebAssembly should not be understood as a user interface or primary customer-facing format, but as an *internal security and execution boundary for advanced extensions*.

The central question is therefore not simply "Jint or Wasmtime?" but: **which layer of the system needs which runtime?**

**Principle.** For simple workflows, mappings, and conditions, usage must not become complicated. A user should not have to build WebAssembly modules just to transform data or formulate a condition. At the same time, a JavaScript interpreter like Jint in the host process is not a particularly robust security story when foreign or customer-side code is to be executed. Jint can be constrained and controlled, but remains a configured scripting engine rather than a hard isolation boundary. Wasmtime/WebAssembly makes the stronger argument here: code runs in an isolated sandbox and receives only explicitly granted host capabilities.

**Recommended architecture:**

```
Workflow Engine
  ├─ Built-in Nodes              C#/.NET
  ├─ Visual Mapping              no code
  ├─ Expression Engine           small controlled DSL
  ├─ Trusted Extensions          C# plugins, signed, admin-only
  └─ Isolated Extensions         WASM via SDK/CLI and Wasmtime
```

**Role of Jint.** Jint is suitable for: prototyping, internal/trusted automations, simple JavaScript transformations, local admin scripts. Jint is *less* suitable as the primary solution for: multi-tenant systems, marketplace plugins, untrusted customer code, third-party nodes, security-critical extensions. The reason: the security argument remains vague. One has to explain that the interpreter is correctly configured, that no dangerous host objects are exposed, that limits apply, and that no bypass is possible. That *can* work, but is a weaker enterprise story.

**Role of Wasmtime.** Wasmtime should be used for isolated extensions: customer-specific custom nodes, third-party plugins, marketplace extensions, code with a clear trust boundary. The argument is cleaner: *"Custom code runs as WebAssembly in an isolated runtime. It has no ambient access to file system, network, secrets, database, or .NET objects. Every external operation must go through an explicitly granted host capability."* That is substantially more robust than: *"JavaScript runs in an interpreter that we carefully configure."*

**Important: customers should not have to write WASM directly.** The customer should not work with WASM memory layout, imports, exports, pointer passing, or Wasmtime details. Instead: the customer writes a custom node with an SDK. The SDK builds it into a plugin package. The platform runs that package internally as WASM.

Example from the customer's perspective:

```typescript
import { NodeContext } from "@yourtool/sdk";

export function run(ctx: NodeContext) {
  return {
    fullName: ctx.input.firstName + " " + ctx.input.lastName
  };
}
```

Build:

```
yourtool plugin build
```

Resulting package internally:

```
my-custom-node/
  ├─ manifest.json
  ├─ node.wasm
  ├─ input.schema.json
  ├─ output.schema.json
  ├─ permissions.json
  └─ README.md
```

The customer thus thinks in: *write node → run build → upload plugin*. Not in: *program WebAssembly directly*.

**Capability model.** The most important part of the WASM architecture is not WASM itself, but the capability model. A plugin must not have automatic access to network, file system, secrets, or databases. Everything must be explicitly permitted.

Example permissions:

```json
{
  "network": false,
  "filesystem": false,
  "secrets": [],
  "maxMemoryMb": 32,
  "maxExecutionMs": 5000,
  "maxOutputSizeKb": 512
}
```

Host capabilities could be: `log.write`, `http.request`, `secrets.read`, `storage.read`, `storage.write`, `emit.event`. Each access goes through the host: `Plugin → host.http.request(...) → Policy Check → execute or deny`. This makes controllable: may this workflow do this operation? May this node? May this user? May this URL be called? May this secret be read?

**Product-phase recommendation:**

- **MVP (this v2):** built-in nodes in C#, visual mapping, simple conditions, small controlled expression DSL. No WASM requirement for normal users.
- **Phase 2:** trusted C# plugin model — signed internal/enterprise extensions, admin-only installation. Good for integrators or internal project teams. (This is what v2 Tier 2 already delivers.)
- **Phase 3:** WASM plugin SDK — CLI build tool, plugin packages with manifest and permissions, execution via Wasmtime. For isolated custom nodes, third parties, and marketplace scenarios.

**Clear decision.** Jint is not used as a long-term security boundary for this tool. The split is:

- Simple user logic → visual mapping or small DSL
- Trusted/internal extension → C# plugin
- Untrusted/marketplace/customer code → Wasmtime/WASM via SDK

**Short form:** Jint is convenient but weak as a security argument. Wasmtime is more robust but must not directly be the customer's UX. Therefore: **WASM is the internal runtime format. The SDK is the customer surface.** That gives the best balance of usability, extensibility, and a robust security argument.

**v2 commitment:** in this architectural step, only the small controlled DSL is implemented. The Tier-3 capability model fields are reserved in the manifest schema (`network`, `filesystem`, `maxMemoryMb`, `maxExecutionMs`) but are *only* honored for Tier 2 today; for Tier 3 they become the enforcement contract when WASM is added.

### DR-002: AI generation deferred, interface reserved

`INodePackageGenerator` lives in `Knotarium.Core` from day one with no implementation. The Node Editor reserves a UI slot for the future "Generate" button, hidden behind `Features:AiGeneration = false`. Likely Phase 2 integration path is MCP, allowing users to connect their own AI provider rather than the platform managing API keys.

### DR-003: SQLite default, Postgres provider-ready

Database access goes through `IDatabaseProvider` with EF Core provider factory selection from `appsettings.json`. SQLite is the only implemented provider in v2; Postgres is config-ready with code marker `// TODO(v1.5): Postgres provider implementation` at integration points. Migrations are generated for SQLite only and regenerated when Postgres is added.

### DR-004: Audit log with hash-chain from day one

Each `AuditEntry` row contains `previous_hash` (the `entry_hash` of the prior row) and `entry_hash` (SHA-256 over `previous_hash || canonical_serialization_of_this_entry`). The first row has `previous_hash = 0x00…00`. Tamper evidence is verified by re-walking the chain from the first row and recomputing each hash. Implementation in Phase D3 alongside package signing — both rely on the same SHA-256 + canonical-serialization primitives, so building them together amortizes the cost.

The canonical serialization is JSON with sorted keys and no insignificant whitespace; the exact serializer is fixed in code (not configurable) so the hash is reproducible across versions. Schema changes to `AuditEntry` require a versioned canonical serializer (`v1`, `v2`, etc.) with the version recorded per row.

### DR-005: Package signing primitive

To ensure cryptographically verifiable extensibility and prevent supply chain attacks, v2 defines a formal package signing primitive:
- **Digest**: SHA-256 over the canonical, deterministic byte sequence of the package contents (manifest + compiled assembly or declarative files).
- **Signature Algorithm**: Ed25519, providing high-performance, compact signatures.
- **Verification**: Verified at load/install time against a set of configured trusted public keys managed by the host. 
This provides a unified signature verification process for both locally developed packages (signed by the host key) and third-party plugins (verified against trusted certificates).

---

## Confirmed Scope Decisions

The following were explicitly confirmed during architecture review and are now binding for the implementation plan:

1. **Node Editor (manual, no AI) is in v2 initial scope.** Phase D as listed in §15 stands. The editor is built alongside the package system, not after it.
2. **12 built-in nodes ship day-one** as listed in §10. The five additions over v1 (Manual Trigger, Webhook Trigger, Switch, Transform, Merge) are part of Phase B4.
3. **Audit log uses hash-chain entries from the start.** Each `AuditEntry` row stores the SHA-256 hash of `(previous_entry_hash || canonical_serialization_of_this_entry)`. Tamper evidence is verifiable by re-walking the chain. Implementation in Phase D3 alongside package signing.
4. **Build-time estimate of "several months" accepted.** No scope reduction; phases proceed as planned.