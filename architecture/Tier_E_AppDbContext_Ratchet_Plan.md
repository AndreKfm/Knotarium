# Tier E — AppDbContext Ratchet — Scoping & Seam Plan

Status: **IMPLEMENTED (2026-07-09).** Decision resolved: **5 → 1** (Execution sanctioned as the AppDbContext owner). All four tractable slices seamed; `baseline_appdbcontext_users` is now `[Execution]`. Commits: Nodes `28754e8`, Notifications `526c772`, Polling `5534877`, Schedules `e6f0b5c`. Notes below reflect the original plan; a few seam names changed in implementation (see §4a).
Author: generated against repo state 2026-07-09 (branch `refactor/backend-modularization`, HEAD `96a5b9f`)
Primary drift authority: `Backend/Knotarium.Features/module.yaml` (`slice_rules.baseline_appdbcontext_users`) enforced by `Backend/Knotarium.Architecture.Tests/SliceBoundaryTests.cs`

## 1. Where we are

After Tier D, `baseline_slice_edges` is empty — no cross-slice **type** edges remain. The only
coupling the drift guard still tracks is direct EF `AppDbContext` injection:

```yaml
baseline_appdbcontext_users:
  - Execution
  - Nodes
  - Notifications
  - Polling
  - Schedules
```

Moving a slice behind a Core store seam (the proven `ISettingsStore` / `INodePackageStore` recipe:
interface in Core, EF adapter in `Infrastructure.Persistence`, host-wired, slice injects the
interface) drops it from this list. Target = empty… **but see §2.**

## 2. The headline finding: this is NOT "apply the recipe 5×"

The five slices have wildly different query surfaces:

| Slice | DbSets touched | EF-internal use (ChangeTracker/`Entry`/transactions) | Verdict |
|---|---|---|---|
| **Execution** | 9 (ExecutionInstances, ExecutionWorkItems, JournalEntries, NodeStates, NodeRetryStates, CorrelationTokens, ActiveWorkers, WorkflowDefinitions, WorkflowVersions, NodePackages) | **Heavy — 18 sites**: `Entry(x).Property(...).IsModified`, `ChangeTracker.Entries<WorkflowVersion>()`, multi-entity `BeginTransaction` | **Keep as-is** |
| **Nodes** | 2 (NodePackages, NotificationChannels) | none | Tractable |
| **Notifications** | 3 (ExecutionInstances, JournalEntries, NotificationChannels) | none (journal writes already go via `IExecutionJournalWriter`) | Tractable |
| **Polling** | 3 (PollingTriggers, ExecutionInstances, WorkflowDefinitions) | 1 transaction (run creation) | Tractable |
| **Schedules** | 3 (Schedules, ScheduleFires, ExecutionInstances) | **transactional idempotent fire-claim** across ScheduleFires + ExecutionInstances | Tractable but fiddly |

**Execution cannot be cleanly hidden behind a repository.** Its writes use EF change-tracking
mechanics directly — partial-property updates (`Entry(instance).Property(e => e.Status).IsModified =
true`) that are load-bearing for replay/suspension correctness, `ChangeTracker` sweeps, and
transactions spanning many entities. A repository interface over this would either **leak EF types
into Core** (defeating the seam) or force a **lossy rewrite** of the replay/recovery/suspension engine.
Execution *is* the persistence orchestrator; that's a legitimate role, not drift.

### Recommended target: **5 → 1, not 5 → 0**

Move the four tractable slices behind store seams and **sanction Execution as the single AppDbContext
owner**, documented as intentional in `module.yaml` (a comment on the residual baseline entry, or a
dedicated `sanctioned_appdbcontext_users: [Execution]` key vs. the ratcheted `baseline_`). This turns
the residual from "drift we haven't fixed" into "an explicit architectural decision", which is the
honest and valuable end state.

## 3. Reuse first — seams that already exist

Several slice reads can switch onto existing Core contracts instead of new ones:
- **WorkflowDefinitions / WorkflowVersions** → `IWorkflowStore`, and `ActiveWorkflowVersionService` (already in `Infrastructure.Persistence`, already injected by Polling/Schedules for version resolution).
- **NodePackages (write)** → `INodePackageStore`; **all manifests** → `INodePackageCatalogProvider`; **single manifest** → `INodePackageManifestProvider`.
- **JournalEntries (write)** → `IExecutionJournalWriter` (Notifications' `ErrorWorkflowWorker` already uses it for the breadcrumb).

## 4. New seams needed (four tractable slices)

### Nodes → drops after
- `INotificationChannelStore.GetAsync(channelId, ct)` — `SendNotificationNodeTask` loads one channel.
- Node-package **read** for dynamic compilation: `DynamicCustomNodeTask` reads a package's binary/version and the registry does an existence check (`NodePackages.Any(id)`). Either extend an existing package seam with a read/exists method or add `INodePackageReadStore`. (Confirm the exact columns `DynamicCustomNodeTask` needs — assembly bytes vs. manifest — before choosing.)

### Notifications → drops after
- `INotificationChannelStore` (shared with Nodes): add `ListAsync(ct)` for `FailureAlertChannelResolver`'s default-channel scan.
- Failed-run context read: `FailureContextBuilder` + the two workers read an `ExecutionInstance` (with `NodeStates`) and a couple of `JournalEntries`. Add a focused query seam, e.g. `IFailedRunContextStore.GetAsync(executionId, ct) -> FailedRunContext` (instance summary + the failed/error journal entries), rather than exposing raw tables. Journal **writes** already have `IExecutionJournalWriter`.

### Polling → drops after
- `IPollingTriggerStore` — read due triggers + persist the change-detection cursor (`PollEvaluationService`).
- Run creation (`PollRunEnqueuer`) → the cross-cutting **`IRunSubmission`** seam (§5), which also removes its transaction/AppDbContext.

### Schedules → drops after
- `IScheduleStore.GetDueAsync(...)` — `ScheduleEvaluationService`.
- **The transactional fire-claim** (`WorkflowEnqueueService.ClaimAndEnqueueScheduleAsync`) writes a `ScheduleFire` + an `ExecutionInstance` atomically with unique-constraint idempotency. Wrap the whole unit as one seam method whose **EF implementation lives in Infrastructure** — e.g. `IScheduleFireClaimStore.ClaimAndCreateRunAsync(scheduleId, plannedFireAtUtc, nextFireAtUtc, ct) -> ScheduleEnqueueResult` — returning the created execution id so the slice can still push it to `IWorkflowExecutionQueue`. This preserves atomicity while moving the EF code out of the slice. **Fiddliest piece of Tier E.**

## 4a. What was actually built (deltas from the plan)

- **Nodes** — `INodePackageReadStore { bool Exists; Task<NodePackageVersion?> GetLatestVersionAsync }` (sync `Exists` to fit the sync node-task registry) + `INotificationChannelStore { GetAsync; ListAsync }`.
- **Notifications** — one combined `IExecutionReadStore { GetInstanceWithNodeStatesAsync; GetLatestJournalEntryAsync }` (instead of a bundled `IFailedRunContextStore` DTO — kept `FailureContextBuilder`'s logic intact, just swapped its `AppDbContext` param for the reader) + `INotificationChannelStore.ListAsync`. Journal writes already went through `IExecutionJournalWriter`.
- **Polling** — `IPollingTriggerStore { GetDueAsync; SaveAsync }`. Rather than a new `IRunSubmission`, `PollRunEnqueuer` was **relocated** Polling→Execution (it creates ExecutionInstances; Polling still consumes it via the existing `IPollRunEnqueuer` Core seam).
- **Schedules** — `IScheduleStore { GetDueAsync; AdvanceNextFireAsync }`; the transactional fire-claim `WorkflowEnqueueService` was **relocated** Schedules→Execution (not wrapped in an Infra `IScheduleFireClaimStore`), consumed via the existing `IWorkflowEnqueueService` Core seam.

The "relocate the run-creating service into Execution" move (Polling + Schedules) replaced the planned `IRunSubmission`/`IScheduleFireClaimStore` seams — simpler, no new interface, and correct domain placement (run creation belongs with Execution, the sanctioned owner). All EF read adapters live in `Infrastructure.Persistence` and return Core.Domain entities, so nothing EF leaks into Core.

## 5. Cross-cutting: `IRunSubmission` (Execution-owned) — NOT built (superseded by relocation; see §4a)

`PollRunEnqueuer`, `ErrorWorkflowRunEnqueuer`, and `ExternalSignalRunEnqueuer` share one shape:
resolve active version → build `ExecutionInstance(origin, globals)` → transaction → `QueueExecution`.
Consolidate into an Execution-owned `IRunSubmission.SubmitAsync(workflowId, triggerOrigin, globals,
ct) -> ExecutionInstanceId?`. Because it lives in Execution (the sanctioned owner), it needs no Core
store seam for the write. This removes AppDbContext from **Polling's** enqueuer for free and de-dups
three enqueuers. Schedules can't use the plain form (its create must share the fire-claim
transaction) — it keeps the dedicated `IScheduleFireClaimStore`.

## 6. Suggested order (easiest first; each drops one baseline entry)

1. **Nodes** — 2 small reads, no transactions. Warm-up.
2. **Notifications** — channel store + failed-run context query; journal writes already seamed.
3. **Polling** — `IPollingTriggerStore` + `IRunSubmission`.
4. **Schedules** — `IScheduleStore` + the transactional `IScheduleFireClaimStore`.

After each, delete the slice from `baseline_appdbcontext_users` (bidirectional ratchet forces it).
End state: `baseline_appdbcontext_users: [Execution]` (or a renamed sanctioned key).

## 7. Decision required before implementing

This is materially bigger and less mechanical than the edge tiers (new query seams with real
behavior, one transactional seam, and node-package read plumbing). Two forks:

- **Scope (recommended): 5 → 1.** Sanction Execution as the AppDbContext owner; seam the other four.
  Bounded, ~4 slice-sized commits, each independently green.
- **Full 5 → 0.** Also attempt Execution. Not recommended — high risk of leaking EF into Core or a
  lossy rewrite of the replay/recovery/suspension engine, for little architectural gain (Execution
  would still be a de-facto persistence hub, just behind a fatter interface).

Also to confirm: represent the sanctioned owner as a **comment on the residual `baseline_` entry**, or
a **separate `sanctioned_appdbcontext_users` key** (needs a small `SliceBoundaryTests` change to treat
it as allowed rather than ratcheted).

## 8. Out of scope

Physically extracting the four seamed slices into leaf projects — removing their AppDbContext use
*enables* it, but they still share the assembly with Execution and would need their remaining
cross-references (if any) checked. A separate step after Tier E.
