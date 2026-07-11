# Tier C — Execution Cluster Decoupling — Seam Plan

Status: Draft for review
Author: generated against repo state 2026-07-09 (branch `refactor/backend-modularization`, HEAD `2d04109`)
Primary drift authority: `Backend/KnotGarden.Features/module.yaml` (`slice_rules`) enforced by `Backend/KnotGarden.Architecture.Tests/SliceBoundaryTests.cs`

## 1. Goal

Break the **Execution ↔ Notifications cycle** and the two producer edges into Execution
(**Polling → Execution**, **Schedules → Execution**), plus the IL-invisible **const edge
Execution → Polling**. This shrinks `baseline_slice_edges` from 6 to 2, leaving only the two
`Nodes → {Execution, Notifications}` edges for Tier D.

Unlike every Tier B extraction, this is a **real 2-cycle** (each of Execution and Notifications
references a type in the other), so it can't be sealed by a single one-directional inversion — both
arms must go behind a Core seam in the same tier or the cycle just rotates.

## 2. The edges, as they exist today

Each edge is a **single call site** — the cycle is thin, which makes the work small and low-risk.

| # | Edge | Where | What crosses |
|---|------|-------|--------------|
| 1 | `Execution → Notifications` | [WorkflowExecutor.cs:31,48,367](../Backend/KnotGarden.Features/Execution/WorkflowExecutor.cs) | Executor holds an optional `FailureAlertQueue?` and calls `.Enqueue(instance.Id)` on the failure path. |
| 2 | `Notifications → Execution` | [ErrorWorkflowWorker.cs:171](../Backend/KnotGarden.Features/Notifications/ErrorWorkflowWorker.cs) | Worker resolves Execution's concrete `ErrorWorkflowRunEnqueuer` from DI to start the error-handler run. |
| 3 | `Polling → Execution` | [PollRunEnqueuer.cs:19,71](../Backend/KnotGarden.Features/Polling/PollRunEnqueuer.cs) | Holds Execution's `WorkflowExecutionQueue`, calls `.QueueExecution(id)`. |
| 4 | `Schedules → Execution` | [WorkflowEnqueueService.cs:24,123](../Backend/KnotGarden.Features/Schedules/WorkflowEnqueueService.cs) | Same: holds `WorkflowExecutionQueue`, calls `.QueueExecution(id)`. |
| 5 | `Execution → Polling` (const, IL-invisible) | [WorkflowExecutor.cs:1838](../Backend/KnotGarden.Features/Execution/WorkflowExecutor.cs) | Reads `PollRunEnqueuer.PayloadVariableKey` (`"__pollPayload"`); inlined at compile time so NetArchTest can't see it — not in the baseline, but a real source coupling. |

Note what is **not** an edge: `ActiveWorkflowVersionService` lives in
`KnotGarden.Infrastructure.Persistence` (an allowed project dependency), not in the Execution slice —
so the enqueuers' use of it is fine and stays. The **only** slice-level thing Polling and Schedules
pull from Execution is `WorkflowExecutionQueue.QueueExecution`.

## 3. Seams

Three Core interfaces + one Core constants holder. All go in `KnotGarden.Core/Contracts` (or
`Core/Domain` for the constants), reference only Core primitives (`ExecutionInstanceId`,
`WorkflowDefinitionId`), and follow the Tier B inversion recipe: **interface in Core, implementation
stays in the owning slice, consumer depends on the interface.**

The queues are producer/consumer channels — the interface exposes **only the producer method**; the
consumer (the background worker that drains the channel) keeps the concrete type inside the owning
slice, so the seam surface stays minimal.

### Seam 1 — `IWorkflowExecutionQueue` — breaks edges 3 & 4

```csharp
namespace KnotGarden.Core.Contracts;
public interface IWorkflowExecutionQueue
{
    void QueueExecution(ExecutionInstanceId executionId);
}
```
- **Implements:** `Execution/WorkflowExecutionQueue` (add `: IWorkflowExecutionQueue`; it already has the method).
- **Consumers switch to the interface:** `PollRunEnqueuer`, `WorkflowEnqueueService`.
- Execution's own worker still resolves the concrete `WorkflowExecutionQueue` for `DequeueAsync`/`TryDequeue` — those stay off the interface.

### Seam 2 — `IFailureAlertSink` + `IErrorWorkflowSink` — breaks edge 1 (Execution → Notifications arm of the cycle)

> **Correction found during implementation:** the executor produces into **two** Notifications
> spines on the failure path, not one — `FailureAlertQueue.Enqueue` (alert dispatch) **and**
> `ErrorWorkflowQueue.Enqueue` (start the global error workflow). Both are optional `?`-typed
> executor fields with the identical `void Enqueue(ExecutionInstanceId)` shape. Each needs its own
> sink seam (two distinct singleton destinations → two interfaces, not one shared type).

```csharp
namespace KnotGarden.Core.Contracts;
public interface IFailureAlertSink   { void Enqueue(ExecutionInstanceId executionId); }
public interface IErrorWorkflowSink  { void Enqueue(ExecutionInstanceId executionId); }
```
- **Implement:** `Notifications/FailureAlertQueue : IFailureAlertSink` and `Notifications/ErrorWorkflowQueue : IErrorWorkflowSink` (methods already exist).
- **Consumer:** `WorkflowExecutor` — change both optional fields/params from the concrete queues to the interfaces. They stay **optional/nullable** (executor already tolerates null sinks = alerting/error-workflow not wired).
- The `FailureAlertWorker` / `ErrorWorkflowWorker` keep their concrete queues for `DequeueAsync`.
- DI: alias each interface to its singleton (same load-bearing rationale — the optional params fall back to `null` if the container can't resolve them, silently no-op'ing the failure path).

> Naming note: the earlier memory sketch called this arm "IRunSubmission". That was a guess — the
> real Execution→Notifications coupling is the failure-alert enqueue, so the seam is a
> failure-alert sink, not a run-submission port. (An `IRunSubmission` consolidation is a *separate,
> optional* follow-on — see §6.)

### Seam 3 — `IErrorWorkflowRunEnqueuer` — breaks edge 2 (Notifications → Execution arm of the cycle)

```csharp
namespace KnotGarden.Core.Contracts;
public interface IErrorWorkflowRunEnqueuer
{
    Task<ExecutionInstanceId?> EnqueueAsync(
        WorkflowDefinitionId errorWorkflowId,
        ExecutionInstanceId sourceExecutionId,
        object? payload,
        IReadOnlyDictionary<string, object?>? extraGlobals = null,
        CancellationToken cancellationToken = default);
}
```
- **Implements:** `Execution/ErrorWorkflowRunEnqueuer` (signature already matches exactly).
- **Consumer:** `ErrorWorkflowWorker` resolves `IErrorWorkflowRunEnqueuer` instead of the concrete type; drops `using KnotGarden.Features.Execution;`.

### Seam 4 — `TriggerPayloadKeys` (Core constants) — breaks edge 5 (the const edge)

The poll-payload variable key is a **wire contract** shared between the writer (`PollRunEnqueuer`) and
the reader (`WorkflowExecutor.CreateTriggerOutputs`). Hoist it to a Core constants holder so neither
slice reaches into the other:

```csharp
namespace KnotGarden.Core.Domain;
public static class TriggerPayloadKeys
{
    public const string Poll = "__pollPayload";
    // (ErrorWorkflowRunEnqueuer.PayloadVariableKey / ExternalSignalRunEnqueuer.PayloadVariableKey are
    //  intra-Execution and need not move — but folding them in here keeps all trigger keys in one place.)
}
```
- `PollRunEnqueuer.PayloadVariableKey` becomes `=> TriggerPayloadKeys.Poll` (or callers use the Core const directly).
- `WorkflowExecutor.cs:1838` reads `TriggerPayloadKeys.Poll`.

## 4. DI wiring

Registration must preserve the **load-bearing singleton lifetime** of the queues (one writer-side
instance shared with the drain-side worker — see the comment at
`ExecutionServiceCollectionExtensions.cs:14`). Register the concrete as the singleton and alias the
interface to the *same* instance:

```csharp
// Execution
services.AddSingleton<WorkflowExecutionQueue>();
services.AddSingleton<IWorkflowExecutionQueue>(sp => sp.GetRequiredService<WorkflowExecutionQueue>());
services.AddScoped<ErrorWorkflowRunEnqueuer>();
services.AddScoped<IErrorWorkflowRunEnqueuer>(sp => sp.GetRequiredService<ErrorWorkflowRunEnqueuer>());

// Notifications
services.AddSingleton<FailureAlertQueue>();
services.AddSingleton<IFailureAlertSink>(sp => sp.GetRequiredService<FailureAlertQueue>());
```

(The `ErrorWorkflowRunEnqueuer` is `AddScoped` today — keep that; only add the interface alias.)

## 5. Execution order & the ratchet

The drift test `No_new_cross_slice_edges` has a **bidirectional** assertion: it fails not only on a
*new* edge but also when a baseline edge no longer exists but is still listed (`fixedAlready`). So each
seam lands atomically with its baseline deletion — you can't "fix ahead" or "forget to un-list".

Suggested commit sequence (each is green on its own):

1. **Seam 4 (const):** add `TriggerPayloadKeys`, repoint writer+reader. No baseline line to remove (edge 5 was never IL-visible) — but it clears the source coupling the module.yaml comment flags.
2. **Seam 1 (`IWorkflowExecutionQueue`):** invert Polling + Schedules → remove `Polling->Execution` **and** `Schedules->Execution` from `baseline_slice_edges`.
3. **Seam 3 (`IErrorWorkflowRunEnqueuer`):** invert the Notifications worker → remove `Notifications->Execution`.
4. **Seam 2 (`IFailureAlertSink`):** invert the executor → remove `Execution->Notifications`. **Cycle is now open.**

After step 4, `baseline_slice_edges` reads:
```yaml
baseline_slice_edges:
  - Nodes->Execution      # Tier D
  - Nodes->Notifications  # Tier D
```

Do steps 2–4 in this order so the cycle's two arms (steps 3 & 4) are the last to fall — until both are
inverted the pair is still a cycle, and inverting only one just relocates it.

## 6. Explicitly out of scope for Tier C

- **`baseline_appdbcontext_users` is untouched.** Execution, Notifications, Polling, and Schedules keep injecting `AppDbContext`; persistence seams are a later ratchet. Tier C is edges only.
- **Optional consolidation (`IRunSubmission`).** `PollRunEnqueuer`, `WorkflowEnqueueService`, and `ErrorWorkflowRunEnqueuer` are near-identical (resolve active version → build `ExecutionInstance` with a `TriggerOrigin` + globals → transaction → queue). A future `IRunSubmission` owned by Execution could absorb the Poll and Error enqueuers, additionally removing their `AppDbContext` use. **Schedules can't fully delegate** — its `ClaimAndEnqueueScheduleAsync` writes the `ScheduleFire` idempotency claim and the `ExecutionInstance` in one transaction, so it keeps its own `AppDbContext` and uses `IWorkflowExecutionQueue` only for the post-commit push. This is a bigger refactor with real behavior surface; keep it out of the cycle-breaking tier.
- **Extracting Execution / Notifications as leaf projects.** Breaking the cycle *enables* this but doesn't do it — the `Nodes → {Execution, Notifications}` edges (Tier D) and the shared `AppDbContext` still bind them into the assembly.

## 7. Verification

- `dotnet test Backend/KnotGarden.Architecture.Tests` — `No_new_cross_slice_edges` must stay green after each step; `SliceScan.CurrentEdges()` should report the two Nodes edges only when Tier C is done.
- Full backend test suite green (the executor failure path, error-workflow loop-guard, poll/schedule enqueue idempotency, and the trigger-output payload plumbing all have existing coverage — the seams are behavior-preserving).
- Smoke: a failing run still (a) fires failure-alert channels and (b) starts the configured global error workflow; a poll with new data and a schedule fire each still start a run and land the payload on the trigger's `result` port.
