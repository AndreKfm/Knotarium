# Live Single-Step Debugger — Design Plan

Status: Draft for review
Author: generated against repo state 2026-06-09
Related: `architecture/Replay_TimeTravel_Plan.md` (shipped). This builds on the same durable
substrate (journal, per-node `NodeState`, work items, `VariablesBefore` snapshots).

## 1. Goal

Let a user **pause a live run between nodes**, inspect (and optionally edit) the state at that
point, then **advance one node at a time** ("step"), **run to the next breakpoint** ("continue"),
or **stop**. Unlike the read-only time-travel inspector (which scrubs a *finished* run), this
controls an *in-flight* execution.

This is the interactive counterpart to replay:
- **Time-travel inspector** — read-only, post-mortem, "what did state look like at step N".
- **Replay** — re-run a finished run from a node with historical inputs.
- **Step debugger** (this doc) — drive a *live* run node-by-node, pausing the engine itself.

## 2. The key realisation: pausing is already a solved problem

The engine has a first-class **suspend/resume** mechanism we can reuse almost verbatim:

- `LegacyNodeResult.WaitForEvent` makes a node set `NodeStatus.Waiting`, the instance
  `ExecutionStatus.Suspended`, persists `VariableState`, and stops the loop
  (`WorkflowExecutor.cs:815`). A **work item** later resumes it (`ProcessResumeWorkItemAsync`).
- Manual decisions use the same shape: `NodeStatus.RequiresManualDecision` → operator action →
  `ManualDecision` work item reschedules the node (`WorkflowExecutor.WorkItems.cs`).

A step debugger is the **generalisation of this to "pause before/after any node, resume on
operator command"**. No new scheduling core — a new pause status + a new `Step` work item.

## 3. Data model changes

### 3.1 `ExecutionInstance`
```csharp
public bool DebugEnabled { get; set; }            // run is under debugger control
public string? DebugBreakpoints { get; set; }     // JSON: string[] of NodeIds to break on
public string? DebugMode { get; set; }            // "paused" | "stepping" | "running" | null
```
- `DebugMode == "paused"` ⇒ the engine is halted at a cut point waiting for an operator command.
- `DebugMode == "stepping"` ⇒ run exactly one node, then pause again.
- `DebugMode == "running"` ⇒ run freely until the next breakpoint (or completion).

### 3.2 `NodeState`
Reuse the **existing** `VariablesBefore` (already captured). Add nothing — the inspector panels
(inputs / variables-before / outputs) already read from `NodeState`. The only new persisted thing
is a pause marker on the instance.

A new `NodeStatus.Paused` (or reuse `Waiting` with a discriminator in `Outputs["__debug"]`).
Prefer a dedicated `NodeStatus.Paused` for clarity in the UI and queries.

### 3.3 Migration
Idempotent `PRAGMA table_info` + `ALTER TABLE ExecutionInstances ADD COLUMN …` for the three
debug columns, same pattern as `IsEnabled` / `ReplayOfExecutionId` in `Program.cs`.

## 4. Engine changes (the one real change)

`ExecuteScheduledNodesAsync` gains a **pre-node breakpoint check**, inserted right before a node
transitions to `Running` (`WorkflowExecutor.cs:~628`, next to the `VariablesBefore` write):

```
if (instance.DebugEnabled && ShouldBreakBefore(instance, currentNodeId))
{
    nodeState.Status = NodeStatus.Paused;
    instance.Status = ExecutionStatus.Suspended;     // reuse the suspended lifecycle
    instance.DebugMode = "paused";
    instance.VariableState = Serialize(instance.GlobalVariables);
    await PublishJournalEntryAsync(instance, "DebugPaused", $"Paused before '{currentNodeId}'.", …);
    await SaveChangesAsync();
    return;   // identical exit to WaitForEvent — loop unwinds, run is durable
}
```

`ShouldBreakBefore` returns true when:
- `DebugMode == "stepping"` (always break — single step), OR
- the node id is in `DebugBreakpoints` and `DebugMode == "running"`.

Because we **reuse the suspended exit path**, crash-recovery, SSE, and the worker model all work
unchanged. The paused run sits in the DB exactly like a `waitForEvent` suspension.

### Resume / Step / Continue
A new **`Step` work item** (mirror of `Resume`) drives the paused run forward:

```
ProcessStepWorkItemAsync:
  - load instance + plan
  - set DebugMode from payload ("stepping" | "running")
  - apply any operator variable edits (payload.VariableOverrides) to GlobalVariables
  - clear the Paused node back to Pending, instance.Status = Running
  - schedule { pausedNodeId }
  - ExecuteScheduledNodesAsync(...)         // runs one node (stepping) or until next breakpoint
  - CompleteExecutionIfStillRunningAsync(...)
```

`ExecuteScheduledNodesAsync` after running the stepped node will hit the breakpoint check again on
the *next* node (because `DebugMode == "stepping"`), re-pausing. Net effect: one node per `Step`.

## 5. Starting a debug run

Two entry points:
1. **Launch in debug mode** — `POST /api/executions` with `{ debug: true, breakpoints: [...] }`.
   Instance created with `DebugEnabled = true`, `DebugMode = "paused"` (breaks before the first
   node) or `"running"` (breaks only on breakpoints).
2. **Attach to a running run** — `POST /api/executions/{id}/debug/pause` flips `DebugEnabled` and
   sets `DebugMode = "stepping"` so it pauses at the next node boundary. (Best-effort: it pauses
   *before the next node*, not mid-node — consistent with the node-granular model.)

## 6. API

```
POST /api/executions/{id}/debug/attach     { breakpoints?: string[] }   → 202
POST /api/executions/{id}/debug/breakpoints { breakpoints: string[] }   → 200
POST /api/executions/{id}/debug/step        { variableOverrides?: {} }  → 202  // one node
POST /api/executions/{id}/debug/continue    { }                         → 202  // to next breakpoint
POST /api/executions/{id}/debug/stop        { }                         → 202  // detach + finish/cancel
```
All but the last enqueue a `Step` work item (with the right `DebugMode`); `stop` clears
`DebugEnabled` and lets the run finish freely (or cancels it).

## 7. Frontend / UX

Reuse the **`TimeTravelInspector` shell** — it already renders inputs / variables / outputs per
node and steps through a node list. In debug mode it becomes *live*:
- Replace the read-only ◀ ▶ with **Step Over (one node)**, **Continue (to breakpoint)**, **Stop**.
- The "Variables at this step" panel becomes **editable** (a JSON/key-value editor) and edits are
  sent as `variableOverrides` on the next `step`/`continue`.
- **Breakpoints**: a gutter dot on each graph node (toggle). Persisted via `…/debug/breakpoints`.
- The graph highlight (already implemented via `highlightedNodeId`) marks the paused node.
- SSE: add `DebugPaused` to the event stream so the UI flips to "paused at node X" in real time.

The component boundary already exists — `inspectorSlot` on `ExecutionCanvasPanel` and the
`highlightedNodeId` plumbing are reused directly.

## 8. Edge cases

- **Loops / multiple executions of a node**: each iteration hits the breakpoint again (the check
  is per scheduling visit). `VariablesBefore` is overwritten per visit, so the inspector shows the
  *current* iteration's state — correct for live debug, unlike the post-mortem inspector which only
  retains the last.
- **Non-idempotent nodes**: stepping *executes them for real*. Offer the same **mock-side-effects**
  toggle as replay (`§10` of the replay plan) for debug runs — short-circuit using a prior output
  if available, else warn.
- **`waitForEvent` while debugging**: the node genuinely suspends on its own event; the debugger's
  pause and the node's wait coexist (both are `Suspended`; `DebugMode`/`Outputs["eventName"]`
  disambiguate).
- **Concurrency / cancellation**: a debug run that's deactivated cancels like any other
  (`IsExecutionCancelledAsync` already short-circuits work items).
- **Crash recovery**: a `Paused` run is just a `Suspended` row; `RecoveryService` leaves it for the
  operator, identical to a `waitForEvent` suspension.

## 9. Phased roadmap

| Phase | Scope | Effort |
|---|---|---|
| **1** | `DebugEnabled`/`DebugMode` + `NodeStatus.Paused`, breakpoint check in the engine, `Step` work item, `step`/`continue`/`stop` endpoints, launch-in-debug. Reuse inspector shell with live controls. | medium |
| **2** | Breakpoint gutter UI + `…/breakpoints`, `DebugPaused` SSE, live highlight. | small |
| **3** | Editable variables (`variableOverrides`), attach-to-running. | small–medium |
| **4** | Mock-side-effects in debug, conditional breakpoints (expression on `GlobalVariables`). | medium |

## 10. Why this is cheap

The expensive part — *durably pausing an in-flight workflow between nodes and resuming it on
command* — is already built and battle-tested as `waitForEvent` suspend/resume + work items. The
debugger is: one breakpoint check in `ExecuteScheduledNodesAsync`, one `Step` work item mirroring
`Resume`, three debug columns, and a live-controls reskin of the existing `TimeTravelInspector`.

## 11. Tests

- Engine: `DebugMode="stepping"` runs exactly one node then re-pauses (`NodeStatus.Paused`,
  `ExecutionStatus.Suspended`).
- `continue` runs to the next breakpoint and no further.
- `variableOverrides` mutate `GlobalVariables` before the next node and are visible to it.
- A breakpoint on node X pauses before X with X still `Pending` and upstream `Completed`.
- Stop detaches and the run completes normally.
- Frontend: step/continue/stop post the right payloads; breakpoint toggles persist; editable
  variables serialize into `variableOverrides`.
```
