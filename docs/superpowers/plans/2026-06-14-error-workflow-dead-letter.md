# Error Workflow + Dead-Letter — Implementation Plan

**Goal:** When any workflow fails, auto-start a global default error workflow (Phase A); and give
failed runs a triage surface with replay + discard (Phase B).

**Architecture:** Clone three proven spines — failure-alert queue/worker, poll run-enqueuer, and
the scheduler/poll trigger wiring. Spec: `docs/superpowers/specs/2026-06-14-error-workflow-dead-letter-design.md`.

**Test command:** `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~ErrorWorkflow"`
Frontend: `cd Frontend && npm run build`.

---

## Phase A — Error-Workflow Trigger

- **A1. Global setting** — `AppSetting` key/value entity + `DbSet`/EF config + `AppSettings`
  startup DDL guard (`Program.cs`, beside `NotificationChannels`); `GlobalSettingsService`
  get/set `DefaultErrorWorkflowId`; `GET`/`PUT /api/settings/error-workflow`.
  Tests: `GlobalSettingsServiceTests` (round-trip, null-when-unset, blank clears).
- **A2. Manifest** — register `errorTrigger` in `InMemoryNodePackageManifestProvider`
  (`triggerOnly`, `result` output, no params). Test `ErrorTriggerManifestTests`.
- **A3. Queue + worker + builder + hook** —
  `ErrorWorkflowQueue` (clone `FailureAlertQueue`); `ErrorWorkflowWorker` (clone
  `FailureAlertWorker`) with loop guards; `FailureContextBuilder` extracted from
  `FailureAlertWorker` and reused there; executor: optional `ErrorWorkflowQueue?` ctor dep +
  `Enqueue` at the `WorkflowFailed` chokepoint in `PublishJournalEntryAsync`.
- **A4. Enqueuer** — `ErrorWorkflowRunEnqueuer` (clone `PollRunEnqueuer`): origin `"error"`,
  payload key `__errorPayload`, txn-then-queue, no-op without active version.
- **A5. Executor wiring** — `CreateTriggerOutputs` emits `__errorPayload` on `errorTrigger.result`;
  `IsTriggerCompatibleWithOrigin` maps `"error" → errorTrigger`.
- **A6. DI** — `Program.cs`: `ErrorWorkflowQueue` (singleton), `ErrorWorkflowWorker` (hosted),
  `ErrorWorkflowRunEnqueuer` (scoped), `GlobalSettingsService` (scoped). The singleton queue is
  auto-injected into the DI-resolved `WorkflowExecutor`.
- **A7. Frontend** — `api.getDefaultErrorWorkflow/setDefaultErrorWorkflow`; `ErrorWorkflowSetting`
  picker in the settings view.
- **A8. Tests** — `ErrorWorkflowDispatchTests`: loop-guard invariants (3 cases) + enqueuer e2e
  (origin/payload present; no-op without active version).

## Phase B — Dead-Letter Store

- **B1. Discard** — additive `ExecutionStatus.Discarded`; `ExecutionDiscardPolicy.CanDiscard`;
  `POST /api/executions/{id}/discard` (Failed→Discarded + journal, 409 otherwise).
  Test `ExecutionDiscardPolicyTests`.
- **B2. View** — `api.discardExecution`; `DeadLetterView` (lists `status=Failed`, Open + Discard);
  `App.tsx` adds the `dead-letter` view + nav entry.

## Files

**New (backend):** `Core/Domain/AppSetting.cs`, `Features/Settings/GlobalSettingsService.cs`,
`Features/Notifications/{ErrorWorkflowQueue,ErrorWorkflowWorker,FailureContextBuilder}.cs`,
`Features/Execution/{ErrorWorkflowRunEnqueuer,ExecutionDiscardPolicy}.cs`.
**Modified (backend):** `WorkflowExecutor.cs`, `WorkflowExecutor.Internals.cs` region (hook + 2
trigger spots), `Program.cs`, `AppDbContext.cs`, `InMemoryNodePackageManifestProvider.cs`,
`ExecutionStatus.cs`, `Notifications/FailureAlertWorker.cs` (refactor).
**Frontend:** `utils/api.ts`, `components/ErrorWorkflowSetting.tsx`, `components/DeadLetterView.tsx`,
`App.tsx`.

## Verification

- `dotnet test … --filter "FullyQualifiedName~ErrorWorkflow"` (16 tests) + the failure-alert /
  polling / execution suites (regression of the `FailureAlertWorker` refactor).
- Manual e2e: set a default error workflow in Settings; run a workflow rigged to fail → the error
  workflow starts (origin `error`, payload on `errorTrigger.result`) and the failed run appears
  under Dead Letter with working Open/Discard. Make the error workflow itself fail → no cascade.
