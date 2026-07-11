# Tier D — Nodes Slice Decoupling — Seam Plan

Status: Draft for review
Author: generated against repo state 2026-07-09 (branch `refactor/backend-modularization`, HEAD `cda2f96`)
Primary drift authority: `Backend/KnotGarden.Features/module.yaml` (`slice_rules`) enforced by `Backend/KnotGarden.Architecture.Tests/SliceBoundaryTests.cs`

## 1. Goal

Break the final two cross-slice edges — **`Nodes → Execution`** and **`Nodes → Notifications`**
— emptying `baseline_slice_edges` entirely. This completes the slice-**edge** decoupling of the
Features assembly (the last structural coupling among the six in-Features slices). Both are single
call sites and one-directional (no cycle), so each is a straight Tier-B/C-style inversion.

> After Tier D, `baseline_slice_edges: []`. Only `baseline_appdbcontext_users` remains (all five
> slices still inject the shared EF `AppDbContext`) — that is a separate, deliberately-deferred
> persistence ratchet, **not** part of Tier D.

## 2. The edges

| # | Edge | Where | What crosses |
|---|------|-------|--------------|
| A | `Nodes → Execution` | [HttpRequestNodeTask.cs:18,20,39-40](../Backend/KnotGarden.Features/Nodes/HttpRequestNodeTask.cs) | Node holds an optional `ExecutionTelemetry?` and calls one method — `StartOutboundHttpActivity(uri, method, context)` — for a client tracing span. |
| B | `Nodes → Notifications` | [SendNotificationNodeTask.cs:9,23,25,69](../Backend/KnotGarden.Features/Nodes/SendNotificationNodeTask.cs) | Node injects `NotificationDispatcher` and calls `SendAsync(channel, NotificationMessage, ct)`, constructing a `NotificationMessage`. |

## 3. Seams

### Seam D1 — `IOutboundHttpTelemetry` — breaks edge A

`ExecutionTelemetry` (Execution slice) is a large meter/activity aggregate used throughout the
executor; `HttpRequestNodeTask` needs exactly one method off it. Expose that method as a narrow Core
seam rather than moving the whole telemetry class.

```csharp
namespace KnotGarden.Core.Contracts;
using System.Diagnostics;
public interface IOutboundHttpTelemetry
{
    Activity? StartOutboundHttpActivity(Uri uri, string method, NodeExecutionContext context);
}
```
- **Implements:** `Execution/ExecutionTelemetry` (method already exists).
- **Consumer:** `HttpRequestNodeTask` — optional field/param `ExecutionTelemetry?` → `IOutboundHttpTelemetry?` (stays optional/nullable); drop `using KnotGarden.Features.Execution`.
- `NodeExecutionContext` is already in `Core.Contracts`; `Activity` is `System.Diagnostics`. Interface references nothing slice-local.
- DI: `ExecutionTelemetry` is a singleton (Program.cs:88) — alias `AddSingleton<IOutboundHttpTelemetry>(sp => sp.GetRequiredService<ExecutionTelemetry>())`. **Load-bearing** (same as the Tier C sinks): the node's param is optional (`= null`), and MS.DI honors default parameter values for unregistered services, so without the alias production HTTP spans silently stop.

### Seam D2 — `INotificationDispatcher` + move `NotificationMessage` to Core — breaks edge B

The dispatcher's signature names `NotificationMessage`, so a Core interface can't reference it while
it lives in the slice. Move the record to `Core.Domain` — a **consistency fix**, since its sibling
domain types `NotificationChannel` and `NotificationChannelType` already live there; `NotificationMessage`
is the odd DTO left in the slice.

```csharp
// moved: Notifications/NotificationMessage.cs  ->  Core.Domain.NotificationMessage
namespace KnotGarden.Core.Contracts;
public interface INotificationDispatcher
{
    Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken cancellationToken);
}
```
- **Move** `NotificationMessage` record to `Core.Domain` (8 referencing files gain/keep `using KnotGarden.Core.Domain;` — mechanical; `FailureAlertMessage.ToNotification()`, the four senders, `INotificationSender`, `NotificationDispatcher`, the node, and the test).
- **Implements:** `Notifications/NotificationDispatcher` (method already matches).
- **Consumer:** `SendNotificationNodeTask` — inject `INotificationDispatcher` instead of the concrete; drop `using KnotGarden.Features.Notifications`, add `using KnotGarden.Core.Domain` for the `NotificationMessage` it constructs.
- DI: keep the concrete `AddScoped<NotificationDispatcher>()` (the `/notification-channels/{id}/test` endpoint and `FailureAlertWorker` inject it directly and may stay concrete) and add `AddScoped<INotificationDispatcher>(sp => sp.GetRequiredService<NotificationDispatcher>())`.

## 4. Execution order & ratchet

1. **Seam D1** → remove `Nodes->Execution` from `baseline_slice_edges`.
2. **Seam D2** → remove `Nodes->Notifications`. `baseline_slice_edges` is now **empty** (`[]`).

The `No_new_cross_slice_edges` ratchet is bidirectional — each removal must land with its seam or the
`fixedAlready` assertion fails.

## 5. Out of scope

- `baseline_appdbcontext_users` (all five slices) — persistence seams are a later, separate effort.
- Physically extracting Nodes/Execution/Notifications into leaf projects — the empty edge baseline
  enables it, but the shared `AppDbContext` still binds them into the one assembly.

## 6. Verification

- `dotnet test Backend/KnotGarden.Architecture.Tests` green after each step; `baseline_slice_edges` empty at the end.
- HttpRequest + SendNotification + Notification tests green; full unit + Api suites at the known baseline (unit 1009/1010 mapper baseline; Api 4 known arming failures).
