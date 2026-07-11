# Knotarium — Architecture v5.3 (Path B & UI Ergonomics)

> [!SUCCESS]
> Status: Completed and synchronized with the implemented MVP 5 codebase as of 2026-05-31. All 17 documented implementation steps are now shipped, validated, and reflected in this architecture baseline.

## Goal

Provide a detailed architectural and product-design specification for **Path B** (Engine Robustness: Scheduling, Retries, and Resumability) merged with a **UI Scalability Overhaul** (Searchable Sidebar Palette, Unified Package Discovery, and a modern Operations Dashboard).

This document elevates the v5.2 spec to a formal, **invariant-first product and engine architecture (v5.3)**. It frames every scheduling, retry, and resumability feature as a strict, verifiable engine invariant and crash-recovery boundary, eliminating architectural drift and guaranteeing robust runtime behavior. The document now serves as the post-implementation reference for the completed MVP 5 delivery.

## Implementation Status

* **Delivery State:** Complete.
* **Execution Engine:** Scheduling, resumability, hashed single-use resume tokens, persistent retry state, crash-recovery markers, and manual-decision work-item flows are implemented.
* **Operations UI:** The sidebar palette, unified package discovery, observability badges, run filtering, trigger-origin badges, and timeline-grouped dashboard are implemented.
* **Validation Baseline:** Focused frontend tests, backend API tests, and production frontend builds passed for the final MVP 5 slices.

---

## 1. UI Scalability & Sidebar Palette Invariants

To guarantee visual and operational scale as custom node packages expand past 40+ items:

* **Invariant 1.1 (Sidebar Authority):** The horizontal canvas toolbar is retired. A persistent, docked **Sidebar Palette** on the left of the canvas serves as the authoritative interface for node insertion.
* **Invariant 1.2 (Fuzzy Search):** A fuzzy-search text input is pinned to the top of the sidebar palette, executing instantaneous client-side filtering over all available nodes.
* **Invariant 1.3 (Manifest-Driven Grouping):** Nodes in the sidebar are organized strictly into collapsible groups mapped to their manifest `category` (Trigger, Logic, Data, Network, Utility). Alphabetical sorting is enforced within each category group.
* **Invariant 1.4 (Single-Source Registry):** The UI loads all nodes—both built-in and dynamic custom packages—from a single source (`api.getNodePackages()`). Hardcoded built-in canvas elements are prohibited, preventing duplicate node listings.
* **Invariant 1.5 (Ephemeral Client State):** A "Recent / Pinned" group is pinned to the top of the sidebar. This state is managed solely client-side via **Zustand + `localStorage`** and is isolated from the backend SQLite database models.

---

## 2. Run-Level vs. Node-Level Statuses

To ensure clean observability, Knotarium maintains a strict separation between run-level status, persisted execution node state, and runtime node execution result status.

### Run-Level Status (`ExecutionStatus`)
* `Pending` — Enqueued, awaiting worker pickup.
* `Running` — Actively traversing nodes.
* `Suspended` — Parked, waiting for an external resume event.
* `Cancelled` — Aborted by manual user request or hard-kill cancellation timeout.
* `Completed` — Succeeded all DAG paths.
* `Failed` — Terminated due to unhandled error.
* `WaitingForRetry` — Parked specifically for a scheduled retry attempt after retryable failure.

> [!NOTE]
> The user-facing Operations Dashboard translates `Suspended` into the friendly `"Waiting"` status badge and `WaitingForRetry` into `"Retrying"`.

### Persisted Execution Node State (`NodeStatus`)
This is the durable node state stored on `ExecutionInstance.NodeStates` and used by workflow orchestration, recovery, resume traversal, and execution-detail projections.

* `Pending` — Awaiting execution.
* `Running` — Node is actively executing.
* `Completed` — Node completed successfully and its outputs are durable.
* `Failed` — Node failed terminally.
* `Waiting` — Node is parked awaiting an external resume event or similar continuation boundary.
* `RequiresManualDecision` — Node failed in a state that must be resolved explicitly by a user decision.

### Runtime Node Result Status (`NodeExecutionStatus`)
This is the lower-level runtime outcome model used by built-in and dynamic node execution paths when returning immediate execution results from node tasks.

* `Pending` — Runtime work has not started.
* `Running` — Runtime work is active.
* `Succeeded` — Runtime execution finished successfully.
* `Failed` — Runtime execution failed.
* `Retrying` — Runtime execution should be retried according to retry policy.
* `RequiresManualDecision` — Runtime execution cannot continue safely without explicit user input.
* `TimedOut` — Runtime execution exceeded its allowed execution window.
* `Cancelled` — Runtime execution was cancelled before successful completion.

### Invariant 2.1 (Journal Versioning)
In accordance with DR-004, the journal serialization payloads include a schema version property (`v1`, `v2`, etc.) to maintain backward compatibility as new execution states are introduced.

---

## 3. Resumability & Suspend-Resume Invariants

* **Invariant 3.1 (Single Source of Truth):** The `ExecutionJournal` remains the absolute, single source of truth for execution states. Storing parallel serialized execution context blobs (such as JSON memory snapshots) is strictly prohibited.
* **Invariant 3.2 (Atomic Suspension):** When an execution pauses at a waiting node, the `WorkflowExecutor` appends a `WorkflowSuspended` entry to the journal, updates the `ExecutionInstance.Status` to `Suspended`, and saves the current variable bag to `VariableState` inside the database. **These operations must execute within a single atomic database transaction**, preventing projection desynchronization in the event of a crash during suspension.
* **Invariant 3.3 (Resume Traversal Precision):** Upon resume, the engine completes the waiting node, writing the callback request payload as that node's output. The executor traverses the DAG starting strictly from that node's outgoing edges; **re-executing already succeeded nodes during rehydration is prohibited**.

---

## 4. Scheduler Trigger Invariants

* **Invariant 4.1 (Trigger-Only Boundary):** The `Scheduler` is an **entry-point trigger** (like `Start`, it is not part of the mid-graph DAG execution, has no input ports, and does not run via `INodeExecutor`). Its manifest is parsed solely to register cron/interval schedules in the database.
* **Invariant 4.2 (Layer Decoupling):**
  * **`Knotarium.Api` (Host):** Registers the `IHostedService` background worker thread (`SchedulingWorker`).
  * **`Knotarium.Infrastructure` (Persistence):** Owns the `ScheduleFires` database schemas and schedule rule persistence.
  * **`Knotarium.Features` (Features):** The trigger-to-instance policy and enqueuing execution runs.
* **Invariant 4.3 (Fire-Level Idempotency):** The database maintains a `ScheduleFires` table with a **`UNIQUE(ScheduleId, PlannedFireAtUtc)`** constraint. When enqueuing a run, the worker inserts a record into this table. If a duplicate insert fails the unique constraint, the execution is rejected. This prevents double-firing after a server crash/restart, worker locking delays, or CPU clock-jumps.
* **Invariant 4.4 (Timezone and Cron Precision):** The database stores the cron expression and the human-configured `TimeZoneId` (e.g. `Europe/Berlin`) to preserve local semantic schedule rules. Only `NextFireAtUtc` is stored and tracked in UTC.

---

## 5. Retry & Failure Invariants

* **Invariant 5.1 (Idempotency-Gated Retries):** Auto-retries are strictly gated by the node's declared `NodeSideEffectKind`:
  * **`Pure` or `IdempotentSideEffect`:** Eligible for automatic retries based on the manifest's `retryPolicy`.
  * **`NonIdempotentSideEffect`:** **Strictly ineligible for automatic retries.** In case of failure, they route immediately to persisted `NodeStatus.RequiresManualDecision` (or fail immediately if manual decision is off), protecting external resources (like credit card payments) from duplicate calls.
* **Invariant 5.2 (Default-Deny Side-Effects):** If a custom node omits the `sideEffectKind` property in its manifest, the compiler must default-deny and assign `NonIdempotentSideEffect`, protecting the engine from unsafe execution assumptions.
* **Invariant 5.3 (Persistent Retry State):** To ensure retry states survive server crashes and restarts, the engine persists attempt counts, `NextRetryAtUtc`, and the last failure exception reason directly to the database.
* **Invariant 5.4 (Jitter & Backoff):** Auto-retries implement exponential or linear backoff delayed execution, incorporating random jitter and a configured `maxDelaySeconds` parameter to prevent thundering herd problems.
* **Invariant 5.5 (Manual Decision Recovery):** When a non-idempotent or crash-ambiguous attempt cannot be safely retried, the node is parked in `RequiresManualDecision` and resumed only through the explicit manual-decision API/work-item flow.

---

## 6. Crash-Recovery & Security Invariants

* **Invariant 6.1 (Crash-Mid-Non-Idempotent Boundary):** 
  * Immediately *before* invoking a `NonIdempotentSideEffect` node, the engine must write an `"AttemptingExternalEffect"` marker in the `ExecutionJournal`.
  * If the system crashes during the call and recovers, the engine inspects the journal. If it finds the `"AttemptingExternalEffect"` marker without a corresponding completion entry, the engine **must never re-run the node and never assume success**. It must immediately transition the node to `RequiresManualDecision`, preventing silent double-execution.
* **Invariant 6.2 (Token Hardening):** 
  * The correlation token generated for webhook resumability is a cryptographically secure, high-entropy byte sequence.
  * The token is stored in the database **only in hashed form (SHA-256)**, protecting the system from database leak exploitation.
  * The token is assigned a Time-To-Live (TTL) and is consumed **transactionally** (invalidated/deleted immediately during the resume transaction to guarantee single-use).
* **Invariant 6.3 (Run Origin Observability):** Each execution persists a `TriggerOrigin` value (`manual`, `webhook`, or `schedule`) so the dashboard can distinguish user-triggered runs from externally resumed or scheduler-created runs without reconstructing origin from journal side-effects.

---

## 7. Restructured Phased Build Order

To align with the shared "parked execution" primitive (where retry-wait, suspend-wait, and Delay-wait use the exact same state machinery), the build phases are structured as follows:

```
                  +--------------------------------+
                  |  B-1: Sidebar Palette UI       |
                  |  (Persistent Searchable UI)    |
                  +--------------------------------+
                                  |
                                  v
                  +--------------------------------+
                  |  B-2: Resumability Core        |
                  |  (Status Enums, Hashed Tokens) |
                  +--------------------------------+
                                  |
                                  v
                  +--------------------------------+
                  |  B-3: Engine Retries           |
                  |  (Idempotency & Pre-Exec Log)  |
                  +--------------------------------+
                                  |
                                  v
                  +--------------------------------+
                  |  B-4: Scheduler Trigger        |
                  |  (Unique Fires, Timezones)     |
                  +--------------------------------+
                                  |
                                  v
                  +--------------------------------+
                  |  B-5: Operations Dashboard     |
                  |  (Timeline, Outcome Badges)    |
                  +--------------------------------+
```

### Phase B-1: The Searchable Sidebar Palette UI
- Transition `Canvas.tsx` from the horizontal palette to a persistent left-docked `SidebarPalette.tsx`.
- Support collapsible category grouping, alphabetical sorting, and fuzzy search.
- Integrate Zustand + `localStorage` for Pinned/Recent nodes.
- Unify node listings under `api.getNodePackages()`, eliminating duplicate built-ins.

### Phase B-2: State Machine & Execution Resumability Core (Keystone Phase)
- Define run-level `ExecutionStatus`, persisted execution node `NodeStatus`, and runtime-result `NodeExecutionStatus` enums with clear responsibility boundaries.
- Implement versioned `v2` journal entry payloads.
- Implement transactional suspension, writing `WorkflowSuspended` and the variable bag within a single atomic database transaction.
- Create `/api/executions/resume` POST gateway accepting hashed `CorrelationToken`s in headers/body with TTL validation and single-use transactional invalidation.
- Update `WorkflowExecutor.cs` to rehydrate state and traverse the DAG from the last suspended node.

### Phase B-3: Engine Retries & Idempotency Enforcement
- Parse manifest `retryPolicy` configurations and default-deny `NonIdempotentSideEffect` for omitted fields.
- Implement in-engine retry loops in `WorkflowExecutor.cs` strictly gated by `NodeSideEffectKind`.
- Track, persist, and log the `Retrying` node state and attempt counts.
- **Implement Pre-Execution Markers:** Write the `"AttemptingExternalEffect"` journal marker before executing non-idempotent nodes, and enforce the manual-decision crash recovery boundary.

### Phase B-4: Scheduler Trigger & Background Worker
- Register the background `SchedulingWorker` hosted service in `Api`.
- Implement `ScheduleFires` persistence adapters with a `UNIQUE(ScheduleId, PlannedFireAtUtc)` constraint.
- Add cron/interval evaluation in `Features` with timezone-aware checks and worker heartbeats.
- Register the `Scheduler` node as a trigger entry-point.

### Phase B-5: Operations Dashboard & Runs UI
- Build status badges (`Suspended` [Waiting], `Cancelled`, `Retrying`, `Failed`, `Succeeded`) into the execution viewer and dashboard.
- Support workflow filtering, run-origin markers, and timeline-bucketing in the dashboard run list.
- Persist trigger-origin metadata on execution creation so dashboard list views can render `Manual`, `Webhook`, and `Schedule` badges directly from the execution projection.
- Expose filtered `/api/executions` queries so the operations panel can request status-scoped and search-scoped run slices without client-only full-list scans.

## Completion Summary

The MVP 5 architecture described in this document is now implemented across the backend engine, persistence model, minimal API surface, and frontend operations experience. The primary delivered outcomes are:

* a manifest-driven sidebar palette with unified package discovery and client-side pinned/recent ergonomics
* resumable parked executions with hashed correlation-token resume flows and atomic suspension boundaries
* persistent retry orchestration with crash-recovery protection for non-idempotent external effects
* scheduler-trigger idempotency via `ScheduleFires` uniqueness and background scheduling workers
* an operations dashboard with observability badges, manual-decision controls, trigger-origin labeling, run filtering, and timeline buckets

This document should now be treated as the maintained architecture baseline for the shipped MVP 5 system rather than as a pending design proposal.

---

## 8. Workflow Storage & Deployment Architecture (Post-MVP Extension)

The shipped MVP 5 runtime currently persists workflow definitions directly in the database and executes them from that persisted form. The next architectural step is to separate **development-time workflow authoring artifacts** from **runtime execution artifacts** so the platform can support Git-based workflow development, explicit publishing, safer deployment, and version-based rollback.

### 8.1 Goal

Use **files as the source format during development and Git versioning**, while using a **database as the runtime source in production**.

This model combines:

* easy editing and Git integration
* safe production execution
* versioning and rollback support
* clear separation between draft and deployed workflows

### 8.2 Core Model Invariant

* **Invariant 8.1 (Storage-Neutral Runtime Model):** The workflow engine operates only on a storage-neutral domain model. Storage implementations may load or persist workflows differently, but the runner, compiler, and executor must consume the same canonical objects.

The neutral workflow model includes at minimum:

* `WorkflowDefinition`
* `WorkflowVersion`
* `Trigger`
* `Condition`
* `Action`
* `Connector`
* `Variable`

### 8.3 Workflow Store Abstraction

The runtime must depend on a storage abstraction rather than on file or database details.

```csharp
public interface IWorkflowStore
{
  Task<WorkflowDefinition?> GetAsync(
    string workflowId,
    CancellationToken ct);

  Task<IReadOnlyList<WorkflowDefinition>> ListAsync(
    CancellationToken ct);
}
```

* **Invariant 8.2 (Store Decoupling):** The workflow runner must never know whether a workflow came from files or a database.

### 8.4 File-Based Storage

The file-backed store is the development-time authoring source.

**Primary responsibilities:**

* local development
* draft workflow authoring
* Git diff and review
* manual editing and refactoring

**Recommended structure:**

```text
/workflows
  /drafts
    alarm-camera.json
    send-email.json
    open-door.json
```

**Implementation:**

* `FileWorkflowStore`

* **Invariant 8.3 (Files as Development Source):** Draft workflows are stored as files and treated as the authoritative source artifact during development.

### 8.5 Database Storage

The database-backed store is the runtime and deployment source.

**Primary responsibilities:**

* production execution
* version management
* audit trail
* activation control

**Recommended tables:**

* `Workflows`
* `WorkflowVersions`
* `ActiveWorkflowVersions`
* `ExecutionHistory`

**Implementation:**

* `DatabaseWorkflowStore`

* **Invariant 8.4 (Database as Runtime Source):** Production execution must resolve workflows only from the database-backed runtime store.

### 8.6 Publisher Boundary

A dedicated publisher moves validated file-based drafts into the runtime database.

**Flow:**

```text
Draft File
  -> Validation
  -> Normalization
  -> Version Creation
  -> Database
```

**Publisher responsibilities:**

* validate workflow structure
* validate connectors
* validate variables
* create immutable version records
* store workflow in the runtime database

* **Invariant 8.5 (Publish Boundary):** The only path from file-based drafts into runtime execution is the publisher. The runner must never execute draft files directly.

### 8.7 Workflow Lifecycle States

Recommended workflow states:

* `Draft`
* `Validated`
* `Published`
* `Active`
* `Archived`

* **Invariant 8.6 (Explicit Lifecycle State):** Every workflow version must expose a visible deployment state so operators can distinguish authoring state from runtime state.

### 8.8 Explicit Activation

Publishing must not automatically activate a workflow version.

**Flow:**

```text
Publish
  -> Inactive Version
  -> Manual Activation
```

Benefits:

* safer deployment
* testing before activation
* easier rollback

* **Invariant 8.7 (Publish Is Not Activation):** A published version is inert until explicitly activated.

### 8.9 Runtime Read Path

The workflow runner resolves only active versions from the runtime database.

```text
WorkflowRunner
  -> DatabaseWorkflowStore
  -> Active Workflow Version
  -> Execute
```

* **Invariant 8.8 (Runtime Reads Active Only):** Production execution must read only the active version of a workflow from the database. Draft files are never part of the execution path.

### 8.10 Git-Centric Development Flow

The intended development process is:

```text
Edit Draft
  -> Save JSON
  -> Git Diff
  -> Git Commit
  -> Publish
  -> Activate
```

Files remain the source artifact for collaborative development.

The database contains deployed runtime state.

* **Invariant 8.9 (Git Owns Draft History):** File changes are tracked through Git, while runtime deployment history is tracked through immutable published versions in the database.

### 8.11 Rollback Model

Rollback is version-based rather than file-copy-based.

**Flow:**

```text
Select Previous Version
  -> Activate
```

No draft-file copy is required because the previously published runtime version already exists in the database.

* **Invariant 8.10 (Rollback by Reactivation):** Rollback is performed by activating a previously published version rather than mutating or re-importing source files.

### 8.12 High-Level Architecture

```text
FileWorkflowStore
    -> WorkflowPublisher
    -> DatabaseWorkflowStore
    -> WorkflowRunner
```

### 8.13 Design Principles

1. Files are the source of truth during development.
2. Git tracks workflow changes.
3. The database stores deployed workflow versions.
4. Production executes only database versions.
5. Rollback is performed by reactivating previous versions.
6. Workflow execution is independent of storage implementation.

### 8.14 Implementation Direction

This architecture implies the following concrete evolution steps beyond MVP 5:

* introduce the storage-neutral workflow domain boundary if current models leak persistence assumptions
* add `IWorkflowStore` and provide both `FileWorkflowStore` and `DatabaseWorkflowStore`
* introduce a `WorkflowPublisher` application service that validates, normalizes, versions, and persists drafts
* split workflow authoring UX into draft management, publish, and activation flows
* update runtime resolution so execution reads only active database versions
* expose version history, activation, and rollback operations in the operations UI

This extension preserves the existing runtime strengths of the current database-backed executor while restoring files and Git as the natural workflow-authoring surface for development teams.
