# Error Workflow + Dead-Letter — Design

**Status:** Implemented (branch `feat/error-workflow-dead-letter`).
**Related:** failure-alerting (`PublishJournalEntryAsync` chokepoint), polling-trigger
(trigger spine + run enqueuer), replay (`ReplayService`).

## Problem

Failure alerting *notifies* channels when a run fails, but offers no way to (a) run
recovery/compensation logic automatically, or (b) triage the runs that failed. This adds
both, in two phases sharing one branch.

## Phase A — Error-Workflow Trigger

When **any** workflow fails, automatically start a single **global default error workflow**,
passing the failed run's context into it.

- **Trigger node** `errorTrigger` (`triggerOnly`, output port `result`). No config — routing
  is a global setting, not per-node.
- **Global setting** `AppSettings` key/value row `DefaultErrorWorkflowId`, read/written via
  `GlobalSettingsService`; exposed at `GET`/`PUT /api/settings/error-workflow`.
- **Spine** (mirrors failure-alert): the executor enqueues the failed execution id into
  `ErrorWorkflowQueue` at the single `WorkflowFailed` chokepoint
  (`WorkflowExecutor.PublishJournalEntryAsync`), beside the existing failure-alert enqueue.
  `ErrorWorkflowWorker` (hosted) drains it and — when a default error workflow is configured —
  starts it via `ErrorWorkflowRunEnqueuer` (origin `"error"`, payload key `__errorPayload`,
  txn-then-queue; no-op if the handler has no active/published version).
- **Payload** (`errorTrigger.result`): `{ workflowId, workflowName, executionId, failedNodeId,
  errorMessage, triggerOrigin, timestampUtc }`, built by the shared `FailureContextBuilder`
  (extracted from `FailureAlertWorker` — the failed-node + journal error-message lookup now lives
  in one place).

### Loop prevention (the critical invariant)
`ErrorWorkflowWorker.ShouldStartErrorWorkflow` — **do not** start the error workflow when:
1. the failed run **is** the error workflow (`WorkflowDefinitionId == DefaultErrorWorkflowId`), or
2. the failed run was itself an error-handler run (`TriggerOrigin == "error"`).
Both guards required; pure + unit-tested.

## Phase B — Dead-Letter Store

A triage surface over failed runs, reusing the existing executions list + replay.

- **Discard**: additive `ExecutionStatus.Discarded`; `POST /api/executions/{id}/discard`
  transitions `Failed → Discarded` (+ journal entry), 409 otherwise. Rule in
  `ExecutionDiscardPolicy.CanDiscard`.
- **View**: a new "Dead Letter" nav view lists `GET /api/executions?status=Failed` (discarded
  runs drop off automatically). Per row: **Open** → execution detail (where `ReplayDialog` lives),
  **Discard** inline.

## Non-Goals

- Per-workflow error-workflow override (global default only, by decision).
- Dedup / rate-limiting of error runs under failure storms (1:1, like failure alerts).
- A bespoke replay UI in the list (reuses execution-detail replay).

## Conventions

- No EF migrations: EF model (`EnsureCreated`) + manual `CREATE TABLE IF NOT EXISTS` guards in
  `Program.cs` (added an `AppSettings` guard beside `NotificationChannels`).
- The error-workflow spine is a deliberate clone of the failure-alert queue/worker and the
  poll run-enqueuer to stay low-risk.
