# KnotGarden MVP — Baseline Architecture

## Goal

Design and scaffold an MVP for a web application that allows users to visually define, extend, and execute automation flows via a node-based interface. The backend is a C# .NET 10 Modular Monolith using Vertical Slice Architecture, single-machine SQLite, with a custom DAG executor and an SSE event stream to the React frontend.

## Status

**Complete & Fully Implemented.** All 8 ordered steps of the MVP baseline implementation plan have been fully built, integrated, and verified (100% test coverage passed). The custom DAG executor, SQLite persistence, SSE event transport, and visual React Flow canvas are fully active.

---

## 1. High-Level System Overview

- **Architectural style:** Modular Monolith using Vertical Slice Architecture.
- **UI vs. execution separation:** The React Flow graph is a presentation model only. The canonical workflow definition is stored separately and is the source from which the Compiler produces an immutable `ExecutionPlan`.
- **Workflow Compiler:** A dedicated feature slice that validates node types and schemas, checks for missing inputs and invalid edges, rejects cycles, resolves subflow references at compile time by **inlining** them into the plan, and accumulates diagnostics in a `CompilationResult`.
- **Execution Engine:** A custom worker that walks the `ExecutionPlan`, executes nodes, appends to the journal, and publishes events via an SSE stream.
- **Real-time transport:** Server-Sent Events (W3C standard). The decision against SignalR is recorded in Section 9.
- **Scalability boundary:** Single-machine modular monolith on SQLite. Distributed execution requires replacing or externalizing the execution-state store and is out of scope.

---

## 2. Frontend (Visual Node Editor)

- **Framework:** React + Vite + TypeScript.
- **Node editor:** React Flow, used purely as a presentation model.
- **Dynamic UI:** Node property panels are generated from strict JSON schemas served by the backend.
- **Real-time updates:** Native `EventSource` against the backend SSE endpoint. No external client library required.

---

## 3. Backend (.NET 10) & Database (SQLite)

- **Data access:** Entity Framework Core against SQLite (per Step 3).
- **Schema style:** Relational for state and concurrency-critical tables; SQLite JSON columns are used for flexible node configuration where schema enforcement happens at the Compiler level rather than the DB level.
- **Core tables:** `WorkflowDefinition`, `WorkflowVersion`, `ExecutionInstance`, `ExecutionJournal`, `NodeState`.
- **Performance posture:** Correctness, clear execution semantics, and testability come first. Hot paths are optimized only after benchmarks identify them.

---

## 4. Core Workflow Engine (Custom DAG Executor)

### Execution model

- **Pure custom executor — no Hangfire.** A single hosted `BackgroundService` owns the execution loop and polls `ExecutionInstance` for runnable work. This keeps a single source of truth for execution state.
- **No scheduled triggers in the MVP.** A `PeriodicTimer`-based scheduler may be added later when scheduled triggers are introduced; until then, all executions are started by explicit API call.

### Concurrency

- The hosted worker processes multiple `ExecutionInstance`s concurrently via in-process scheduling.
- Each `ExecutionInstance` runs in its own DI scope, ensuring clean cancellation, logging, and (later) plugin contexts.
- **All journal writes serialize through a single `IExecutionJournalWriter`** to respect SQLite's single-writer model. A startup guard enforces that exactly one execution worker is registered.

### State ownership

- **`ExecutionJournal` is append-only and is the absolute source of truth.**
- `NodeState` is a projection maintained for query convenience. The projection update and the journal append occur **within the same SQLite transaction**; on crash recovery, the projection can be rebuilt from the journal.

### Idempotency — honest framing

The journal, node execution state, and idempotency keys reduce duplicate side effects but **cannot guarantee exactly-once delivery to external systems**. Each node declares its semantics via `NodeSideEffectKind`, and the executor's retry behavior is constrained accordingly. Nodes that interact with non-idempotent external systems must either supply their own idempotency mechanism or be marked `NonIdempotentSideEffect` with a `RecoveryMode` of `RequireManualDecision`.

---

## 5. Built-in Nodes (Day-One Scope)

Aligned with Step 5. Risky nodes (Database Read/Write, File Read/Write) are deferred to Phase 2 for security reasons.

| Node | Category | Notes |
|------|----------|-------|
| Start | Trigger | Manual entry point; created by API trigger |
| Condition | Logic | If/Else branching via named outputs (`"true"` / `"false"`) |
| SetVariable | Data | Reads/writes workflow-scoped variable bag (see Section 7) |
| HTTP Request | Network | Uses `apiKeySecretRef`; retry policy honored by executor |
| Delay | Utility | Honors cancellation token; persists wake-up time in journal |
| Log | Utility | Writes structured log; never persists secret values |
| End | Utility | Terminal node; emits sentinel `"end"` output |

**Explicitly deferred from Step 5 scope:** Scheduled Trigger, Transform JSON. These can be added in a subsequent step without architectural change.

---

## 6. Extensibility

- **Subflows (no programming):** Users group nodes into a Macro. Subflows are versioned and **resolved at compile time by inlining**, so each `ExecutionPlan` is fully self-contained and immune to later edits of the subflow source. This makes plans larger but trivially debuggable and removes runtime resolution failures.
- **Plugins (programming):** Trusted, in-process plugins loaded via `AssemblyLoadContext`. Untrusted plugins, sandboxing, and hot-reload are out of MVP scope — the host requires a restart to load or replace plugin assemblies.
- **Secrets:** First-class concept. Node configurations never store raw secrets — only references such as `"apiKeySecretRef": "secret:http-api-prod"`. Node schemas explicitly tag secret-bearing fields. Secret values are resolved at execution time by an `ISecretResolver` and never serialized into the journal or emitted in SSE payloads.

---

## 7. Contract Definitions (The Core)

These contracts are implemented in Step 1 and are load-bearing for every later step.

### Canonical Workflow JSON

```json
{
  "id": "flow-1",
  "version": 1,
  "nodes": [
    {
      "id": "http-1",
      "type": "http.request",
      "config": {
        "url": "https://api.example.com",
        "apiKeySecretRef": "secret:api-prod"
      },
      "retryPolicy": { "maxRetries": 3 },
      "timeout": "00:00:30"
    }
  ],
  "edges": [
    { "from": "http-1", "output": "success", "to": "condition-1", "input": "in" }
  ]
}
```

### Strongly-Typed IDs (Step 1)

```csharp
public readonly record struct NodeId(string Value);
public readonly record struct WorkflowDefinitionId(string Value);
public readonly record struct ExecutionInstanceId(Guid Value);
```

### Compiler Output

```csharp
public sealed record ExecutionPlan(
    WorkflowDefinitionId DefinitionId,
    int Version,
    ImmutableArray<PlannedNode> Nodes,
    ImmutableArray<PlannedEdge> Edges,
    ImmutableDictionary<NodeId, ImmutableArray<NodeId>> AdjacencyList,
    ImmutableArray<NodeId> EntryNodes);

public sealed record CompilationResult(
    ExecutionPlan? Plan,
    ImmutableArray<CompilationDiagnostic> Diagnostics);

public sealed record CompilationDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    NodeId? NodeId = null,
    string? EdgeId = null);
```

The Compiler accumulates diagnostics so the UI can surface all issues in one round-trip rather than one-at-a-time.

### Node Interface & Result

```csharp
public enum NodeSideEffectKind { Pure, IdempotentSideEffect, NonIdempotentSideEffect }
public enum RecoveryMode       { RetryAutomatically, ResumeIfJournaled, RequireManualDecision }
public enum NodeExecutionStatus { Succeeded, Failed, RequiresManualDecision }

public sealed record NodeResult(
    string OutputName,          // Matches edge.output (e.g., "success", "true", "false", "end")
    JsonElement? Payload,       // Data passed to the downstream input
    NodeExecutionStatus Status);

public interface INodeTask
{
    string Type { get; }
    NodeSchema Schema { get; }
    NodeSideEffectKind SideEffectKind { get; }

    ValueTask<NodeResult> ExecuteAsync(
        NodeExecutionContext context,
        CancellationToken cancellationToken);
}
```

A `Failed` result with no matching error edge halts that branch and surfaces in the journal; this keeps the model uniform without forcing every node author to define error edges.

### Variable Scope (informs Step 1 context shape)

`NodeExecutionContext` exposes a workflow-scoped variable bag accessed as `context.Variables.Get<T>(name)` / `context.Variables.Set(name, value)`. The storage location (`ExecutionInstance.VariableState` JSON column vs. a dedicated table) is a Step-3 decision; the **context API shape is fixed in Step 1** so nodes don't retrofit later.

---

## 8. Real-Time Transport: SSE (Not SignalR)

The MVP uses **Server-Sent Events** for execution events flowing from the executor to connected UI clients. Rationale:

- **W3C standard;** no client library required (`new EventSource(url)` is built into every browser).
- **Mirrors the journal model:** SSE events carry a sequence ID matching the journal sequence number. Clients reconnecting send `Last-Event-ID` and the endpoint replays from the journal — which is required anyway for "user opens the UI after execution started."
- **Transport stays inspectable:** `curl -N https://host/api/executions/{id}/events` shows the live event stream for debugging.
- **Polyglot-friendly:** Any future consumer (Python script, Go service, third-party tool) speaks SSE without a proprietary client.

If bidirectional features are later required (collaborative editing, interactive node debugging), a WebSocket endpoint can be added **alongside** SSE for those specific features; SSE does not need to be the universal transport.

The executor depends on `IExecutionEventPublisher` (defined in `KnotGarden.Core`); the SSE-specific implementation lives in `KnotGarden.Api`. The executor has no knowledge of the transport.

---

## 9. Phase 1.1 / Deferred Decisions

| Topic | Decision / Status |
|-------|-------------------|
| Cancellation semantics | Cooperative cancellation with a hard-kill timeout; both states recorded in the journal. Concrete timeout value TBD. |
| Plugin hot-reload | Not supported in MVP. Host restart required. Phase 2 candidate. |
| Trigger model | Triggers are entry points that **create** `ExecutionInstance`s, not nodes executed within one. The Compiler enforces this. |
| Scheduled triggers | Deferred. When added, will use a `PeriodicTimer`-based scheduler in `KnotGarden.Infrastructure`. |
| Database / File nodes | Deferred to Phase 2 (security review required). |
| Distributed execution | Out of MVP scope. Requires replacing the SQLite-backed execution store. |

---

## 10. Project Structure

### Backend (C# .NET 10)

```
/Backend
  /src
    /KnotGarden.Api              (Host, Minimal APIs, SSE endpoints)
    /KnotGarden.Core             (Strongly-typed IDs, INodeTask, ExecutionPlan, IExecutionEventPublisher, ISecretResolver)
    /KnotGarden.Infrastructure   (EF Core SQLite context, Plugin Loader, Secret resolver impl)
    /KnotGarden.Features         (Vertical Slices)
      /Definitions             (CRUD, Versioning)
      /Compiler                (UI graph → ExecutionPlan; diagnostics)
      /Execution               (Hosted worker, DAG traversal, journal writer, event publisher impl)
      /Nodes                   (Day-1 node implementations)
  /tests
    /KnotGarden.Core.Tests
    /KnotGarden.Compiler.Tests
    /KnotGarden.Execution.Tests
    /KnotGarden.Nodes.Tests
    /KnotGarden.Api.IntegrationTests
```

### Frontend (React)

```
/Frontend
  /src
    /components                (UI shell, toolbars, properties panel)
    /nodes                     (React Flow custom node components)
    /api                       (HTTP client, EventSource wrapper)
    /schemas                   (Generated TS types from backend JSON schemas)
  /tests                       (Vitest unit; Playwright E2E in Step 8)
```

---

## 11. Implementation Plan (8 Steps)

The architecture above is realized through the following ordered staging. Each step has its own `step.md` with task checklist and acceptance criteria; this section is the bridge between architecture and execution plan.

| # | Step | Architectural sections it realizes | Notes |
|---|------|------------------------------------|-------|
| 1 | **Core Contracts & Schemas** | §7 (all contracts), §4 idempotency enums | Foundation for every later step. Strongly-typed IDs, `INodeTask`, `NodeResult`, `NodeExecutionContext`, `ExecutionPlan`, `CompilationResult`. |
| 2 | **Workflow Compiler** | §1 (Compiler role), §6 (subflow inlining), §7 (`CompilationResult`) | Validation, cycle detection, compile-time subflow inlining, accumulated diagnostics. |
| 3 | **Database & Persistence** | §3 (EF Core + SQLite), §4 (journal semantics), §7 (variable scope storage) | EF Core context, entity definitions, transactional journal+projection writes. |
| 4 | **Custom Execution Engine** | §4 (entire section) | Hosted worker loop, DAG traversal, journal append, idempotency enforcement, single-writer guard. |
| 5 | **Built-in Nodes** | §5 (Day-1 node table) | Start, Condition, SetVariable, HTTP Request, Delay, Log, End. HTTP node tests mock the network. |
| 6 | **API & SSE Publisher** | §1 (transport), §8 (entire section) | Minimal APIs for definitions and triggers; SSE endpoint with `Last-Event-ID` replay from journal. |
| 7 | **Frontend Canvas** | §2 (entire section), §7 (schema-driven UI) | Vite + React + TS + React Flow; custom nodes; schema-driven properties panel. |
| 8 | **End-to-End Integration** | All of the above | Wire frontend to API, connect SSE client, Playwright/Cypress flow execution tests. |

### Cross-cutting requirements (apply to all steps)

- Unit tests required per step; integration tests where the step crosses a boundary (Steps 3, 4, 6, 8).
- No step is "done" until its `step.md` task checklist and testing requirements are checked off.
- Steps build on one another — Step N may not begin before Step N−1 is complete, with the exception that Step 7 (Frontend Canvas) can proceed in parallel with Steps 3–6 against the contracts defined in Step 1.

---

## Open Questions for User Confirmation

Before scaffolding begins, please confirm or reject:

1. **Scheduled Trigger and Transform JSON dropped from Day-1 nodes** to match Step 5. Acceptable, or should either be added back?
2. **EF Core as the SQLite data access layer** (per Step 3). Acceptable, or do you want Dapper / raw `Microsoft.Data.Sqlite` for the high-write journal path?
3. **Subflow inlining at compile time** locked in. Confirm — once plans are stored as inlined, switching to by-reference later is a migration.
4. **Step 7 parallelizable with Steps 3–6** (frontend against contracts from Step 1). Acceptable, or do you want strict serial execution?
