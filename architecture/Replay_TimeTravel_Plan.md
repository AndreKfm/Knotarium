# Replay / Time-Travel Debugging — Implementation Plan

Status: Draft for review
Author: generated against repo state 2026-06-09
Primary architecture authority: `architecture/system.yaml`, `docs/KnotGarden_MVP_Architecture-5.md`

## 1. Goal

Let a user take a finished execution (typically a failed one), pick any node in its
graph, optionally edit the workflow to fix the problem, and **re-run the workflow from
that node onward using the exact inputs the run had at that point** — without re-executing
everything upstream. The original run is preserved; the replay is a new, linked execution.

This is a differentiator: n8n/Zapier let you *view* a past run but not resume-and-edit it;
Temporal supports replay but only in code, not visually. KnotGarden already has the durable
substrate (journal, per-node state, work-item resume) to make this nearly free.

### Confirmed decisions

| Topic | Decision |
|---|---|
| Replay model | A replay is a **new `ExecutionInstance`**, linked to the source via `ReplayOfExecutionId`. The source run is never mutated. |
| Data source for "original inputs" | **Persisted `NodeState.Outputs` of the source run.** The executor already resolves a node's inputs from its predecessors' `NodeState.Outputs`; seeding upstream node states as `Completed` feeds historical data with no special code. |
| Cut-point variable state | New **`NodeState.VariablesBefore`** column — a JSON snapshot of `GlobalVariables` captured when each node started. Cut-point variables are then an O(1) exact restore (no journal folding). |
| Scheduling | **No change to the scheduling core.** A new `Replay` work item (mirror of the existing `Retry` work item) schedules the cut-point node and calls the existing `ExecuteScheduledNodesAsync`. |
| Target version | Replay runs against a `WorkflowVersion`. `targetVersionId` parameter: omitted → replay against the **same** version (transient-failure re-run); provided → replay against a **fixed** version ("Fix & Replay"). |
| Reset semantics | "From node X" resets X **and its transitive forward closure** to fresh; every other completed source node is seeded. Forward-BFS reuses the pattern in `ResetLoopBodyNodes`. |
| Side effects | Replay re-executes X downstream **for real** (incl. non-idempotent effects). Surfaced as an explicit pre-replay warning computed from manifests; a **mock-side-effects mode** (replay original outputs instead of firing) is Phase 3. |
| Schema evolution | Match the in-tree convention used by the activation feature: idempotent SQLite `PRAGMA` check + `ALTER TABLE … ADD COLUMN` guard in `Program.cs`. |

## 2. How this maps onto the existing architecture

The mechanism leans entirely on components that already exist:

- **`NodeState`** (`Backend/KnotGarden.Core/Domain/NodeState.cs`) persists `Inputs`, `Outputs`,
  `Status`, `ExecutionCount` per node and survives the run. This is the seed material.
- **Input resolution** in `ExecuteScheduledNodesAsync`
  (`Backend/KnotGarden.Features/Execution/WorkflowExecutor.cs:580`) builds a node's inputs from
  incoming edges where `predecessorState.Status == Completed` and reads `predecessorState.Outputs`.
  → Seeding upstream states as `Completed` makes historical inputs flow automatically.
- **`ProcessRetryWorkItemAsync`** (`WorkflowExecutor.WorkItems.cs:117`) already resets one node to
  `Pending`, schedules it, and calls `ExecuteScheduledNodesAsync`. Replay is the generalization:
  reset a *set* and seed the rest.
- **Work-item durability**: `ExecutionWorkItem` + `WorkflowExecutionWorker` make the replay
  crash-recoverable like any other resume/retry.
- **`ResetLoopBodyNodes`** (`WorkflowExecutor.cs:850`) is an existing forward-BFS over `plan.Edges`
  from a node — the exact algorithm needed to compute the reset set.
- **`WorkflowVersion` / `ActiveWorkflowVersion`** give immutable snapshots to replay against, so
  "fixing" a node is just producing a new version.

## 3. Data model changes

### 3.1 `ExecutionInstance` (lineage + replay context)

```csharp
public ExecutionInstanceId? ReplayOfExecutionId { get; set; }  // source run (null for normal runs)
public NodeId? ReplayFromNodeId { get; set; }                  // cut-point node
// TriggerOrigin gains a new value: "replay"
```

### 3.2 `NodeState` (exact cut-point variable reconstruction)

```csharp
public string? VariablesBefore { get; set; }  // JSON snapshot of GlobalVariables at node start
```

Captured in `ExecuteScheduledNodesAsync`, alongside the existing
`nodeState.Inputs = evaluatedInputs` assignment (`WorkflowExecutor.cs:604`):

```csharp
nodeState.VariablesBefore = JsonSerializer.Serialize(instance.GlobalVariables, PersistenceJsonOptions.Default);
```

This is the only new hot-path write. It avoids journal folding entirely: the
`JournalFoldService` reconstructs variables only from suspend snapshots and node outputs, not
from every `SetVariable` mutation, so a per-node snapshot is what makes replay *exact* rather
than best-effort. Reuses the same serialization already used for `ExecutionInstance.VariableState`.

### 3.3 Migration

Idempotent `PRAGMA table_info` checks + `ALTER TABLE ADD COLUMN` for
`ExecutionInstances.ReplayOfExecutionId`, `ExecutionInstances.ReplayFromNodeId`, and
`NodeStates.VariablesBefore`, in the existing startup migration block in `Program.cs`
(same pattern as the `IsEnabled` column).

## 4. Backend: `ReplayService`

`CreateReplayAsync(sourceExecutionId, fromNodeId, targetVersionId?, mockSideEffects)`:

1. Load source `ExecutionInstance` incl. `NodeStates`.
2. Resolve **target version** = `targetVersionId ?? source.WorkflowVersionId` (must be set).
   Load `WorkflowVersion` → compile → `ExecutionPlan`. Validate `fromNodeId` exists in the plan (else 400).
3. **Reset set** = `fromNodeId` ∪ transitive successors (forward-BFS over `plan.Edges`).
4. **Seed set** = source `NodeStates` with `Status == Completed` whose `NodeId` ∉ reset set.
5. Create new `ExecutionInstance`:
   - `WorkflowVersionId = targetVersion`, `Status = Pending`, `TriggerOrigin = "replay"`,
     `ReplayOfExecutionId = source`, `ReplayFromNodeId = fromNodeId`.
   - `GlobalVariables = Deserialize(sourceFromNodeState.VariablesBefore)` — the cut-point state.
6. **Clone seed `NodeState`s** (new `Id`, new `ExecutionInstanceId`, same `NodeId` / `Completed` /
   `Inputs` / `Outputs`). Reset-set nodes are *not* seeded — they are created fresh by the engine.
7. Enqueue a **`Replay` work item**: payload `{ fromNodeId, workflowVersionId, mockSideEffects }`.
8. Return `{ newExecutionId, warnings }` where warnings = downstream nodes whose manifest
   `SideEffectKind == NonIdempotentSideEffect`.

## 5. The `Replay` work-item handler

New `case "Replay"` in `ProcessWorkItemAsync` → `ProcessReplayWorkItemAsync`, a mirror of
`ProcessRetryWorkItemAsync`:

```
- Load instance + plan (LoadExecutionPlanAsync)
- instance.Status = Running
- scheduledNodes = { fromNodeId }
- await ExecuteScheduledNodesAsync(instance, plan, scheduledNodes, ct)   // existing engine
- await CompleteExecutionIfStillRunningAsync(instance, ct)
```

The inherited `NodeState`s feed inputs; reset-set nodes (incl. `fromNodeId`) have no seeded state
and are created fresh on demand by `ExecuteScheduledNodesAsync` (`WorkflowExecutor.cs:523`).
The scheduling core is untouched.

## 6. "Fix node 7" — the version flow

Execution always runs an immutable `WorkflowVersion`, so "fixing" means a new version. Both paths
are covered by the `targetVersionId` parameter:

- **Pure re-run** (transient failure): omit `targetVersionId` → same version. "Failed on a network
  blip, run again from X."
- **Fix & Replay**: user edits the workflow → publish (new version) → replay with that
  `targetVersionId` from node 7. Seeding matches **by `NodeId`**; property edits keep the id, so
  upstream seeds still line up. Upstream restructuring is allowed — whatever matches by `NodeId`
  is seeded, the rest runs fresh.
- Convenience (optional): a one-shot "snapshot current draft → new version → replay".

## 7. Side effects (the safety question)

Replay from X executes X downstream **for real**, including non-idempotent effects (HTTP POSTs,
etc.). The manifest already carries `SideEffectKind`
(`Backend/KnotGarden.Core/Domain/NodePackageManifest.cs`).

- **Pre-replay warning** (Phase 2): list downstream nodes with `NonIdempotentSideEffect`, computed
  from the plan + manifests, shown in the confirm dialog.
- **Mock-side-effects mode** (Phase 3): non-idempotent nodes return their **original output from the
  source run** (already in `NodeState.Outputs`) instead of firing. Makes replay safe for pure logic
  debugging. This is the capability that distinguishes KnotGarden from code-only Temporal replay.

## 8. API

```
POST /api/executions/{id}/replay
  body: { fromNodeId: string, targetVersionId?: guid, mockSideEffects?: bool }
  → 202 { newExecutionId, warnings: [{ nodeId, sideEffectKind }] }

GET  /api/executions/{id}/replays      // lineage chain (runs where ReplayOfExecutionId == id)
```

## 9. Frontend / UX

In `Frontend/src/components/ExecutionDetail/index.tsx` (the run view):

- Per-node action **"Re-run from here"** (context menu / hover) on the run graph.
- Prominent **"Fix & Replay"** on the failed node.
- Pre-replay dialog: target version (original vs current) + side-effect warning + mock toggle.
- Show replay runs as a **lineage chain** (`ReplayOfExecutionId`), e.g. "Replay #2 of run abc, from node 7".

## 10. Edge cases & honest caveats

- **Loops (`forLoop`)**: loop counters live in `GlobalVariables` (`__loop_*`), so they restore via
  `VariablesBefore`. Replaying *into the middle* of an iteration stays tricky → MVP recommends
  replaying at a loop boundary; document the caveat.
- **`waitForEvent` / manual-decision upstream**: their resolved result is in the seeded `NodeState`,
  so they are not re-prompted. Correct.
- **`NodeId` changes between versions**: seeding matches by `NodeId`; renamed/new upstream nodes
  have no seed and run fresh. Acceptable — make it transparent in the UI.
- **Non-idempotent effects**: the central safety concern — see §7.

## 11. Phased roadmap

| Phase | Scope | Effort |
|---|---|---|
| **1 — MVP** | `VariablesBefore` snapshot, data model + migration, `ReplayService`, `Replay` work item, endpoint, basic "Re-run from here" button. Same version. | small–medium |
| **2 — Fix & Replay** | Selectable target version, lineage view, side-effect warning in UI. | small |
| **3 — Killer** | **Mock-side-effects mode** (original outputs instead of real calls) for safe debugging. | medium |

## 12. Tests

- `ReplayService`: reset/seed sets correct on a small multi-node plan (unit).
- E2E: run with a failing node → fix → replay from node → new run completes; upstream outputs
  identical, downstream re-executed.
- Variables: `SetVariable` upstream → replay from a later node sees the correct value (proves
  `VariablesBefore`).
- Mock mode: non-idempotent node does **not** fire and returns its original output.

---

**Why this is cheap:** Phase 1 does not touch the scheduling engine — it reuses inherited
`NodeState`s plus a new work item, both established patterns. The only new hot-path code is a
single `VariablesBefore` snapshot write.

## 13. Implementation steps (individually testable)

Each step compiles on its own, ships behind no half-built UI, and has an explicit test gate. The
order is dependency-driven; steps 1–3 are independent of each other and can land in any order /
in parallel. "Test gate" = the assertion that proves the step is done before moving on.

### Step 1 — Capture `VariablesBefore` (persistence only, no replay)
- **Changes**: add `NodeState.VariablesBefore` (string?) + EF config + SQLite migration guard in
  `Program.cs`; write the snapshot in `ExecuteScheduledNodesAsync` next to `nodeState.Inputs = …`
  (`WorkflowExecutor.cs:604`).
- **Test gate**: run a workflow containing a `SetVariable` before a downstream node; assert each
  node's `VariablesBefore` deserializes to the `GlobalVariables` as they were when that node
  started (and that a node *after* the `SetVariable` sees the new value, one *before* does not).
- **Depends on**: nothing. Harmless additive column — shippable alone.

### Step 2 — Reset/seed calculator (pure function)
- **Changes**: new `ReplayPlanCalculator.Compute(plan, fromNodeId, sourceNodeStates)` returning
  `(IReadOnlySet<NodeId> ResetSet, IReadOnlyList<NodeState> SeedSet)`. Forward-BFS over
  `plan.Edges` (same shape as `ResetLoopBodyNodes`, `WorkflowExecutor.cs:850`).
- **Test gate**: unit tests on small hand-built plans — linear, branch, diamond (join node must
  land in reset set when reachable from X), self-loop. No DB, no engine. This isolates the
  riskiest logic and tests it exhaustively.
- **Depends on**: nothing.

### Step 3 — Lineage fields on `ExecutionInstance`
- **Changes**: add `ReplayOfExecutionId` (nullable) + `ReplayFromNodeId` (nullable) + EF config +
  migration; accept `TriggerOrigin == "replay"`; surface both fields in the execution GET
  projection.
- **Test gate**: persistence round-trip; `GET /api/executions/{id}` returns the two fields
  (null for ordinary runs).
- **Depends on**: nothing.

### Step 4 — `ReplayService.CreateReplayAsync` (build + seed + enqueue), same version
- **Changes**: new `ReplayService` that loads the source run, resolves the target version (= source
  version for now), runs Step 2's calculator, creates the new `ExecutionInstance`
  (`GlobalVariables` from the source cut-point node's `VariablesBefore`), clones the seed
  `NodeState`s, and enqueues a `Replay` work item. Returns `{ newExecutionId, warnings }`.
- **Test gate**: call the service directly (no HTTP); assert the new instance exists with correct
  seeded `NodeState`s (upstream `Completed` with original outputs, reset nodes absent), correct
  `GlobalVariables`, lineage fields set, and exactly one pending `Replay` work item. The handler
  need not run yet.
- **Depends on**: Steps 1, 2, 3.

### Step 5 — `Replay` work-item handler (end-to-end replay, same version)
- **Changes**: `ProcessReplayWorkItemAsync` + new `case "Replay"` in `ProcessWorkItemAsync`
  (`WorkflowExecutor.WorkItems.cs`), mirroring `ProcessRetryWorkItemAsync`: load instance+plan,
  `Status = Running`, schedule `{ fromNodeId }`, run `ExecuteScheduledNodesAsync`, complete.
- **Test gate**: integration — seed a source run, create a replay via the service, process the
  work item; assert the replay run completes, downstream nodes re-executed (fresh
  `ExecutionCount`), upstream seeded outputs unchanged, and a node reading a variable set upstream
  gets the correct value.
- **Depends on**: Step 4. **This is the first point where replay actually works.**

### Step 6 — Replay + lineage endpoints
- **Changes**: `POST /api/executions/{id}/replay` (body `{ fromNodeId, targetVersionId?,
  mockSideEffects? }`) → 202 `{ newExecutionId, warnings }`; `GET /api/executions/{id}/replays`.
  Compute `warnings` from the plan + manifests (`SideEffectKind == NonIdempotentSideEffect`).
- **Test gate**: API test — run a workflow that fails at a node, `POST …/replay` from that node,
  assert 202 and the new execution eventually completes; assert `warnings` lists the expected
  non-idempotent downstream nodes; `GET …/replays` returns the lineage. **End of Phase 1 (no UI).**
- **Depends on**: Step 5.

### Step 7 — Frontend "Re-run from here" (Phase 1 UI)
- **Changes**: `api.replayExecution(...)` client method; per-node "Re-run from here" action in
  `ExecutionDetail`; a confirm dialog; navigate to the new run.
- **Test gate**: vitest/component test that the action posts the right payload and routes to the
  returned execution id; manual smoke on a real failed run.
- **Depends on**: Step 6.

### Step 8 — Fix & Replay: target version selection (Phase 2)
- **Changes**: UI to choose original vs. current version; optional "snapshot draft → new version →
  replay" convenience endpoint/flow. (`targetVersionId` is already wired in Step 4.)
- **Test gate**: API test replaying against a *different, edited* version; assert the edited node's
  new behaviour runs while upstream seeds are reused.
- **Depends on**: Step 6.

### Step 9 — Side-effect warning surfacing (Phase 2)
- **Changes**: render the `warnings` array (from Step 6) in the confirm dialog and lineage view.
- **Test gate**: assert the dialog lists every non-idempotent downstream node for a known plan;
  no warning when the downstream is purely idempotent.
- **Depends on**: Steps 6, 7.

### Step 10 — Mock-side-effects mode (Phase 3)
- **Changes**: honour `mockSideEffects` in the executor — when replaying, a node with
  `NonIdempotentSideEffect` and an available source output short-circuits to that original output
  instead of invoking its task.
- **Test gate**: integration — the non-idempotent node's task is **not** invoked; its original
  output is forwarded; downstream sees it. Toggle off → task *is* invoked.
- **Depends on**: Step 5 (engine), Step 6 (flag plumbing).

### Dependency summary

```
1 ─┐
2 ─┼─► 4 ─► 5 ─► 6 ─► 7 ─► 9
3 ─┘                   └─► 8
                  5,6 ─► 10
```

Phase 1 = Steps 1–7. Phase 2 = Steps 8–9. Phase 3 = Step 10. Each step is its own commit with its
test gate green.
