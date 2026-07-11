# Polling Trigger — Design

**Date:** 2026-06-14
**Branch:** `feat/polling-trigger`
**Status:** Approved (scope: Phase 1 + Phase 2 in this spec)

## Summary

A new `pollingTrigger` node that periodically polls an external source, detects
whether something new/changed since the last poll, and starts a workflow run
(only) when it has. The run receives the fetched payload as input.

The feature is built as a pluggable spine (`IPollSource`) with two concrete
source implementations delivered in this spec:

- **HTTP** — generic URL polling (method/headers/auth via existing credential plumbing).
- **OpenAPI** — poll an imported spec operation, reusing `ServerConfig`/`operationId`.

Further sources (queue, file, email) can slot into `IPollSource` later without
touching the spine. This is the "all three" synthesis: **C** is the architecture,
**A** (HTTP) and **B** (OpenAPI) are the first two source impls.

## Goals

- Periodically poll an external source on a configurable interval.
- Start a run **only when change-detection reports new data** — not on every poll.
- Pass the fetched payload into the run as the trigger node's output.
- Honor existing activation semantics: global arming (design-time vs runtime) and
  per-workflow `IsEnabled`.
- Be resilient: one failing trigger never stalls others; failures are observable.
- Keep the source layer pluggable so new poll sources are additive.

## Non-Goals

- Failure alerting / dead-letter routing (separate feature/branch).
- Webhook/push triggers (already exist).
- Per-poll fan-out of multiple new items into multiple runs (v1 emits the whole
  "new" payload to a single run; batching/splitting is a downstream-node concern).

## Architecture

The existing **scheduler** trigger is the template. A polling trigger is, in
effect, "a schedule that on each tick performs a fetch + change-detection and
conditionally starts a run." We build a parallel, analogous spine rather than
overloading the schedule tables.

### Mapping to existing machinery

| Schedule world (existing) | Polling world (new) |
|---|---|
| `scheduler` node (`triggerOnly: true`) | `pollingTrigger` node (`triggerOnly: true`), output port `result` |
| `Schedule` table, `NextFireAtUtc` | `PollingTrigger` table, `NextPollAtUtc` **+ `Cursor`** |
| `WorkflowScheduleSynchronizer` (sync on save) | `WorkflowPollingTriggerSynchronizer` |
| `SchedulingWorker` (10s loop, arming gate) | `PollingWorker` (same gate) |
| `ScheduleEvaluationService` | `PollEvaluationService` |
| `WorkflowEnqueueService.ClaimAndEnqueueScheduleAsync` | `PollEvaluationService` polls, enqueues only if changed |

**Key behavioral difference:** a schedule fire *always* starts a run; a poll
advances `NextPollAtUtc` every interval but starts a run *only when
change-detection reports new data*.

### Components

1. **Node manifest** — `nodes/PollingTrigger/manifest.yaml`
   - `triggerOnly: true`, `category: Trigger`.
   - Parameters: `intervalSeconds`, `sourceKind` (enum: `http` | `openapi`),
     `changeDetection` (enum: `etag` | `hash` | `json-cursor` | `always`),
     plus source-specific config (see below).
   - Output: `result` (the fetched payload).

2. **Persistence** — `PollingTrigger` domain entity + EF mapping (mirrors `Schedule`):
   - `Id` (Guid, deterministic from workflowId+nodeId via an id factory analogous
     to `WorkflowScheduleIdFactory`).
   - `WorkflowDefinitionId`.
   - `IntervalSeconds`.
   - `NextPollAtUtc` (UTC).
   - `ConfigJson` (string) — `sourceKind`, `changeDetection`, and source-specific
     config (url/method/headers/credentialRef OR serverConfigId/operationId/specVersion).
   - `Cursor` (string, nullable) — opaque last-seen state (etag, last-modified,
     hash, or extracted json value).
   - `IsActive` (bool).
   - `LastPolledAtUtc` (nullable), `LastError` (nullable) — for UI/diagnostics.
   - EF Core migration adds the table (SQLite + Postgres providers, like existing tables).

3. **Synchronizer** — `WorkflowPollingTriggerSynchronizer` (sibling of
   `WorkflowScheduleSynchronizer`), invoked at the same workflow save/publish point.
   - Reconciles `PollingTrigger` rows against `pollingTrigger` nodes in the
     definition (add / update / remove obsolete).
   - **Cursor preservation:** keep the existing `Cursor` on update unless the
     source identity changes (different `sourceKind` or target url/operation), in
     which case reset to null. This avoids a config tweak silently replaying old data.
   - Initializes `NextPollAtUtc = now` for new rows (poll promptly on first arm).

4. **Worker** — `PollingWorker : BackgroundService`
   - Same shape as `SchedulingWorker`: `PeriodicTimer`, honors
     `RuntimeArmingState.IsArmed`, per-tick scoped service resolution, top-level
     try/catch so the loop never dies.
   - Delegates to `IPollEvaluationService.EvaluateDuePollsAsync`.

5. **Evaluation** — `PollEvaluationService` / `IPollEvaluationService`
   - Selects active `PollingTrigger` rows where `NextPollAtUtc <= now` **and** the
     owning `WorkflowDefinition.IsEnabled` (matches schedule gating).
   - For each due trigger (isolated try/catch):
     1. Resolve the `IPollSource` by `sourceKind`.
     2. `PollAsync(new PollContext(ConfigJson, Cursor))`.
     3. On `HasNew == true`: create an `ExecutionInstance` (`TriggerOrigin = "poll"`,
        payload stored in `GlobalVariables` under reserved key `__pollPayload`),
        queue it via `WorkflowExecutionQueue`, set `Cursor = NewCursor`.
     4. Always: advance `NextPollAtUtc += IntervalSeconds`, set `LastPolledAtUtc`,
        clear or set `LastError`.
     5. All DB writes for one trigger in a single transaction (cursor + next-poll
        advance committed together — at-least-once with cursor-based dedup).
   - Uses injectable `TimeProvider` (as existing services do) for testability.

6. **Pluggable source seam (C)** — `IPollSource`
   ```csharp
   public interface IPollSource
   {
       string Kind { get; }                                  // "http", "openapi"
       Task<PollResult> PollAsync(PollContext ctx, CancellationToken ct);
   }
   public sealed record PollContext(string ConfigJson, string? Cursor);
   public sealed record PollResult(bool HasNew, object? Payload, string? NewCursor);
   ```
   - Resolved by `Kind` (keyed DI or a small registry, same idea as the node registry).
   - Cursor semantics are owned entirely by each source; the spine treats it as opaque.

7. **Source impls**
   - **`HttpPollSource` (A)** — reuses `IHttpClientFactory` + `ISecretResolver`
     exactly as `HttpRequestNodeTask` does. Config: url, method, headers,
     `apiKeySecretRef`. Change-detection strategies (selected by `changeDetection`):
     - `etag` / `last-modified` → conditional request (`If-None-Match` /
       `If-Modified-Since`); `304 Not Modified` ⇒ `HasNew=false`; otherwise store
       the new validator as cursor.
     - `hash` → hash the response body, compare to cursor.
     - `json-cursor` → extract a value (e.g. max id / timestamp) via the existing
       keyed-path access helper; `HasNew` when extracted value differs/advances
       past cursor.
     - `always` → `HasNew=true` every poll (interval-driven fetch).
   - **`OpenApiPollSource` (B)** — reuses `OpenApiInterpreterExecutor` +
     `ServerConfig`/`operationId`/`specVersion` resolution. Same change-detection
     strategies operate on the operation's response.

8. **Executor wiring** (`WorkflowExecutor.cs`)
   - `CreateTriggerOutputs` (~:1666): for `pollingTrigger`, emit
     `instance.GlobalVariables["__pollPayload"]` on the `result` port.
   - `IsTriggerCompatibleWithOrigin` (~:1677): map `TriggerOrigin "poll"` →
     `pollingTrigger` node type.

9. **Frontend** — `PollingTriggerPropertyForm` (sibling of `RestCallerPropertyForm`)
   - Interval + `changeDetection` selector.
   - `sourceKind` selector switching between HTTP fields (url/method/headers +
     credential dropdown) and OpenAPI fields (the existing resource picker for
     server config + operation).
   - Falls back to `ManifestForm` rendering where a custom control isn't needed.

## Data Flow

```
PollingWorker (armed)
  -> PollEvaluationService.EvaluateDuePollsAsync
       -> for each due PollingTrigger (IsEnabled workflow):
            IPollSource.PollAsync(config, cursor)
              -> PollResult{HasNew, Payload, NewCursor}
            if HasNew:
              create ExecutionInstance(TriggerOrigin="poll",
                                       GlobalVariables["__pollPayload"]=Payload)
              WorkflowExecutionQueue.QueueExecution(...)
              cursor = NewCursor
            advance NextPollAtUtc; set LastPolledAtUtc/LastError   [one transaction]
  ...
WorkflowExecutionWorker dequeues -> WorkflowExecutor
  entry node resolution maps "poll" -> pollingTrigger
  CreateTriggerOutputs emits __pollPayload on `result`
  downstream nodes consume via edges
```

## Error Handling

- Per-trigger isolation: each due trigger evaluated in its own try/catch; one
  failure never blocks others or kills the worker loop (mirrors `SchedulingWorker`).
- On poll failure: `NextPollAtUtc` still advances (no hammering); `LastError` set;
  logged via `ILogger`. No `ExecutionInstance` exists for a failed/empty poll, so
  failures are surfaced through the `PollingTrigger` row (`LastError`,
  `LastPolledAtUtc`) and logs — not the execution journal.
- Routing poll failures to alert channels is explicitly the separate
  failure-alerting feature; only the observable hooks (`LastError`) are added here.

## Testing Strategy

Test-first (TDD):

- **Change-detection units** — each strategy (`etag`, `hash`, `json-cursor`,
  `always`): new vs unchanged transitions, cursor advance.
- **`HttpPollSource`** — with a fake `HttpMessageHandler`: 304 path, body-hash
  path, json-cursor extraction, header/credential injection.
- **`OpenApiPollSource`** — operation resolution + change-detection over a stubbed
  interpreter response.
- **`WorkflowPollingTriggerSynchronizer`** — add/update/remove reconcile; cursor
  preserved on benign edits, reset on source-identity change.
- **`PollEvaluationService`** — due selection respects `IsEnabled` and
  `NextPollAtUtc`; idempotent advance via injected `TimeProvider`; `HasNew=false`
  produces no `ExecutionInstance`.
- **Integration** — changed response ⇒ exactly one run with payload on `result`;
  unchanged response ⇒ zero runs; disabled workflow ⇒ zero polls.

## Phasing

1. **Phase 1** — node manifest, `PollingTrigger` persistence + migration, cursor,
   synchronizer, `PollingWorker` + `PollEvaluationService`, `IPollSource` seam,
   `HttpPollSource` (all four strategies), run wiring (`GlobalVariables` +
   executor output/origin), frontend form. End-to-end HTTP polling.
2. **Phase 2** — `OpenApiPollSource` reusing `OpenApiInterpreterExecutor` +
   `ServerConfig`/operation; resource-picker UI in the property form.

## Open Questions / Risks

- **Conditional fields in the property form.** ManifestForm may not support
  show/hide by `sourceKind`; if so the custom `PollingTriggerPropertyForm` carries
  the conditional logic (already planned). Confirm during implementation.
- **Poll concurrency.** Single `PollingWorker` instance assumed (same as
  `SchedulingWorker`). If multi-instance hosting is ever introduced, a claim
  mechanism analogous to `ScheduleFire`'s unique constraint would be needed;
  out of scope for v1 (noted, not built).
- **Large payloads in `GlobalVariables`.** Fetched bodies are stored in the
  execution's variable state (JSON). Acceptable for v1; very large responses are a
  user responsibility (same as httpRequest body handling today).
