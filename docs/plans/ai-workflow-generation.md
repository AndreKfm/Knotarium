# Plan — AI Workflow Generation

Branch: `feat/ai-workflow-generation`

Generate a complete, valid Knotarium workflow (nodes + edges + configured properties) from a
natural-language intent, by handing Claude the node catalog and running its output through the
existing `WorkflowCompiler` in a bounded **generate → compile → repair** loop. Generation is a
long-running background job the frontend polls; the result is loaded onto the editor canvas as an
**unsaved preview** for the user to review, edit, and then save through the normal path.

The generator only produces **topology** — nodes, edges, and property values. Geometry (x/y) is
assigned afterward by the existing dagre auto-layout, and credentials it cannot bind are emitted as
`slot:<key>` placeholders the user resolves with the existing binding UI. No new credential, layout,
or persistence primitive is invented.

---

## Settled decisions

- **Catalog is inlined, not retrieved.** The full built-in catalog is 26 node types
  (`InMemoryNodePackageManifestProvider.cs`) — on the order of 5–6K tokens once projected. A vector
  store (Qdrant/embeddings) would have to be built from zero — there is *no* LLM/embedding/vector
  code in the repo today — and retrieval would blur the type-precise metadata the model needs.
  Inline wins on cost, latency, correctness, and dependency surface. *Revisit only if installed
  custom packages exceed ~100, at which point a Tier-2 retrieval over custom packages can layer on
  top of the always-inline built-ins.*
- **Unbound credentials reuse the existing `slot:<key>` primitive.** `CredentialSlotModule`
  already does extract / rebind / find-unbound, all pure and tested, and unbound slots already gate
  `Runnable`. The generator emits `slot:<key>` strings; the existing `CredentialSlotBinding` UI
  resolves them. No new code on the credential side.
- **Ingestion = preview on canvas, then save** (not save-immediately). The job result is a
  `WorkflowDefinition` loaded into the editor unsaved; the user reviews/edits and saves via the
  normal `POST /api/workflows`. A wrong generation costs nothing.
- **Bounded agentic repair loop** (not one-shot). `WorkflowCompiler` is a deterministic in-process
  validator with precise codes; its diagnostics are fed back to Claude for up to N (~3) repair
  passes before the job fails with the last diagnostics.
- **Queue / worker + poll** (not synchronous). The repair loop makes generation genuinely
  long-running, so it runs on the established `Channel<T>` + `BackgroundService` pattern with a job
  id the frontend polls.
- **Generator emits topology only.** No x/y coordinates from the model — geometry comes from the
  existing dagre auto-layout ("Tidy"). The model must never guess coordinates.

---

## How this maps onto the existing codebase

| Concept | This repo |
| --- | --- |
| Node catalog (LLM input) | `InMemoryNodePackageManifestProvider` (26 built-ins) behind `DbNodePackageManifestProvider` (built-ins → binary packages → DB). Project, don't re-model. [InMemoryNodePackageManifestProvider.cs](Backend/Knotarium.Features/Compiler/InMemoryNodePackageManifestProvider.cs) |
| Manifest field schema | `ParameterDefinition` (`Name`/`Type`/`Required`/`Values`/`Description`), `OutputDefinition`. [NodePackageManifest.cs](Backend/Knotarium.Core/Domain/NodePackageManifest.cs) |
| Target representation | `WorkflowDefinition` (`Nodes`/`Edges`/`Metadata`), `NodeDefinition(Id,Type,Properties)`, `EdgeDefinition(Id,From,Output,To,Input)`. [WorkflowDefinition.cs](Backend/Knotarium.Core/Domain/WorkflowDefinition.cs) |
| Validator (repair signal) | `WorkflowCompiler.CompileAsync` → `ERR_INVALID_NODE_TYPE`, `ERR_MISSING_REQUIRED_PARAMETER`, `ERR_INVALID_SOCKET_MAPPING`, `ERR_CYCLE_DETECTED`, … In-process, no round-trip. [WorkflowCompiler.cs](Backend/Knotarium.Features/Compiler/WorkflowCompiler.cs) |
| Persist path (save) | `POST /api/workflows` → compile → `IWorkflowStore.UpsertAsync`. Reused unchanged by the canvas save. [Program.cs:1047](Backend/Knotarium.Api/Program.cs#L1047) |
| Outbound LLM call | `IHttpClientFactory.CreateClient("HttpNode")` — wrapped in `HttpEgressPolicyHandler` (SSRF/allowlist). `api.anthropic.com` must be allowlisted. [HttpRequestNodeTask.cs](Backend/Knotarium.Features/Nodes/HttpRequestNodeTask.cs), [HttpEgressPolicyHandler.cs](Backend/Knotarium.Infrastructure/Security/HttpEgressPolicyHandler.cs) |
| API key | `ISecretResolver` — `env:ANTHROPIC_API_KEY` or encrypted credential store. [ISecretResolver.cs](Backend/Knotarium.Core/Contracts/ISecretResolver.cs), [CredentialAccessor.cs](Backend/Knotarium.Infrastructure/Persistence/CredentialAccessor.cs) |
| Background job | `Channel<T>` queue + `BackgroundService` worker + scoped DI per item. Mirror `FailureAlertQueue`/`FailureAlertWorker`. [FailureAlertWorker.cs](Backend/Knotarium.Features/Notifications/FailureAlertWorker.cs) |
| Unbound credentials | `CredentialSlotModule` (`slot:<key>` extract/rebind/find-unbound) + `CredentialSlotBinding.tsx`. Reused as-is. [CredentialSlotModule.cs](Backend/Knotarium.Api/Services/WorkflowPortability/CredentialSlotModule.cs) |
| Geometry | Existing dagre auto-layout ("Tidy"). Generator emits no coordinates. [autoLayout.ts](Frontend/src/utils/autoLayout.ts) |
| DI registration | `Program.cs` service block alongside the node tasks and the existing queues/workers. |

---

## Phased build order

Each phase is independently shippable and testable. Phases 1–6 are backend-only; the feature is
visible end-to-end after phase 7.

### Phase 1 — Catalog projection + prompt builder
**Backend, `Knotarium.Features` (new `Ai/` folder).**

- `CatalogProjection`: pure function `DbNodePackageManifestProvider` → a compact, model-facing
  description. Per node keep `id`, `displayName`, `category`, `triggerOnly`, each parameter's
  `name`/`type`/`required`/`values`(enums)/`description`, and `outputs` (name + port). Drop
  execution-only metadata (tier, recoveryMode, timeouts, retry) the model doesn't need.
- `GenerationPromptBuilder`: assembles the system prompt — the projected catalog + the rules the
  compiler enforces (node `type` must exist; edge endpoints must reference declared nodes; port
  names must match manifest outputs/params; no cycles except loop constructs; required params
  non-empty; credentials the model can't bind → `slot:<kebab-key>`). Output contract: a single
  `WorkflowDefinition` JSON, **no coordinates**.

*Done when:* a unit test feeds the in-memory provider through `CatalogProjection` and asserts the
projected string contains every built-in id, enum values for `forLoop.mode`, and stays under a
fixed token budget; a second test asserts the prompt names the `slot:` convention.

### Phase 2 — Claude client + `IWorkflowGenerator` (single pass)
**Backend, `Knotarium.Features/Ai` + `Knotarium.Core/Contracts`.**

- `IWorkflowGenerator` contract: `Task<GenerationAttempt> GenerateAsync(GenerationRequest, CancellationToken)`
  where `GenerationRequest(intent, catalog, priorErrors?)` and `GenerationAttempt(workflow?, rawText, parseError?)`.
- `ClaudeWorkflowGenerator`: builds the prompt (Phase 1), resolves the key via `ISecretResolver`
  (`env:ANTHROPIC_API_KEY`), calls `api.anthropic.com/v1/messages` through
  `IHttpClientFactory.CreateClient("HttpNode")`, model from config (default `claude-opus-4-8`).
  Parses the JSON body into `WorkflowDefinition`; a parse failure is a non-throwing `parseError`
  (it feeds the repair loop just like a compile error).

*Done when:* an integration test drives the generator against a stubbed `HttpMessageHandler`
returning a canned messages-API response and asserts a parsed `WorkflowDefinition`; a second test
asserts a malformed body yields `parseError`, not an exception.

### Phase 3 — Generate → compile → repair loop
**Backend, `Knotarium.Features/Ai`.**

- `WorkflowGenerationOrchestrator`: calls `IWorkflowGenerator`, runs the result through
  `WorkflowCompiler.CompileAsync` **in-process**, and on parse-or-compile failure re-invokes the
  generator with the prior errors threaded into `GenerationRequest.priorErrors`. Bounded at
  `MaxRepairAttempts` (config, default 3). Returns `GenerationOutcome(workflow?, diagnostics, attempts)`.
- Repair prompts carry the exact `ERR_*` codes + messages so the model corrects the specific
  failure rather than regenerating blind.

*Done when:* a unit test with a fake `IWorkflowGenerator` scripted to return *invalid-then-valid*
drives the loop and asserts (a) it succeeds on attempt 2, (b) the second request's `priorErrors`
contains the first compile's `ERR_*` codes; a second test asserts it gives up after N attempts with
the last diagnostics.

### Phase 4 — Auto-layout + credential-slot finalization
**Backend, `Knotarium.Features/Ai` (layout helper may live wherever dagre is callable server-side; otherwise defer geometry to the frontend Tidy on load — see note).**

- After a valid topology, assign geometry. Preferred: a shared layout pass so the previewed graph
  opens tidy. If server-side dagre is impractical, emit nodes coordinate-less and have the canvas
  run the existing **Tidy** auto-layout immediately on preview-load (Phase 7) — pick one and record it.
- Credential finalization: any `credentialRef` parameter the model left unbound is normalized to a
  valid `slot:<kebab-key>` via `CredentialSlotModule`'s slug rules, and the set of open slots is
  reported on the outcome so the UI can prompt binding.

*Done when:* a test asserts a generated graph comes back with non-overlapping coordinates (or, if
deferred, that the outcome carries no coordinates and the frontend Tidy test covers placement); a
second test asserts an unbound `credentialRef` becomes a schema-valid `slot:` token and appears in
the reported open slots.

### Phase 5 — Job queue, worker, and job store
**Backend, `Knotarium.Features/Ai` + `Program.cs` DI.**

- `AiGenerationJobStore`: status per job (`Queued`/`Running`/`Succeeded`/`Failed`) holding the
  result `WorkflowDefinition`, open slots, diagnostics, and attempt count. (In-memory is acceptable
  for v1 — generation is interactive and ephemeral; persist only if jobs must survive restart.)
- `AiGenerationQueue` (`Channel<AiGenerationJobId>`) + `AiGenerationWorker` (`BackgroundService`),
  mirroring `FailureAlertQueue`/`FailureAlertWorker`: dequeue, open a scoped DI container, run the
  Phase 3 orchestrator, write terminal status to the store. Worker never crashes on a job failure.
- Register all three in `Program.cs` alongside the existing queues/workers.

*Done when:* a test enqueues a job (fake generator → valid workflow), runs the worker once, and
asserts the store transitions to `Succeeded` with the workflow; a failure-path test asserts
`Failed` with diagnostics and no worker crash.

### Phase 6 — API endpoints
**Backend, `Knotarium.Api` (new `AiGenerationEndpoint.cs`, mirror `InlineCodeTestEndpoint` extension style).**

- `POST /api/ai/generate` — body `{ intent }` → enqueues a job, returns `{ jobId }`.
- `GET /api/ai/generate/{jobId}` — returns `{ status, workflow?, openSlots?, diagnostics?, attempts? }`.
- Both behind the same auth as the rest of the API; intent length-capped.

*Done when:* endpoint tests assert POST returns a `jobId`, GET returns `Running` then `Succeeded`
with a `WorkflowDefinition`, and an unknown `jobId` returns 404.

### Phase 7 — Frontend: intent → poll → preview on canvas
**Frontend, `Frontend/src`.**

- "Generate with AI" entry (editor toolbar / empty-canvas affordance) → modal capturing the intent.
- `api.ts`: `generateWorkflow(intent)` (POST) and `getGenerationJob(jobId)` (GET); poll on an
  interval until terminal (reuse the polling shape used elsewhere).
- On `Succeeded`: load the returned `WorkflowDefinition` into the canvas **unsaved** (run **Tidy**
  if geometry was deferred in Phase 4), select all so the user sees the whole graph, and surface
  open `slot:` credentials via the existing `CredentialSlotBinding` panel. Save uses the existing
  `POST /api/workflows`. On `Failed`: show diagnostics + a "regenerate / edit intent" affordance.

*Done when:* an e2e (or jsdom-level) test types an intent, polls a stubbed job to `Succeeded`, and
asserts the generated nodes/edges render on the canvas in an unsaved state and that an unbound slot
shows the binding prompt.

### Phase 8 — Config, egress allowlist, and docs
**Backend config + `appsettings` + this doc.**

- `appsettings`: `Ai:Model` (default `claude-opus-4-8`), `Ai:MaxRepairAttempts` (default 3),
  `Ai:MaxIntentLength`. Key via `env:ANTHROPIC_API_KEY`.
- **Egress:** the `HttpEgressPolicyHandler` allowlist is *opt-in* — with the default empty
  `Security.HttpEgress.AllowDomains`, all hosts are permitted, so `api.anthropic.com` works out of the
  box. Only if a deployment switches to an explicit allowlist must it add `api.anthropic.com` (else
  generation is blocked). Do **not** blindly add it to `AllowDomains` — that flips the whole app into
  allowlist-only mode and blocks every other outbound call.
- Startup log warns (does not crash) if no Anthropic key is resolvable, so the feature degrades to a
  clear "not configured" error rather than an opaque 401.

*Done when:* a misconfigured instance (no key / not allowlisted) surfaces a clear, actionable error
at generate time and the warning appears at startup; the allowlist entry is covered by the egress
handler's tests.

---

## Open seams / explicitly deferred

- **Custom-package scale (Tier-2 retrieval).** Inline is correct until custom packages dwarf the
  built-ins. The catalog projection (Phase 1) is the natural seam to later split into
  "always-inline built-ins + retrieved custom packages" without touching the generator.
- **Job persistence.** In-memory job store is fine for interactive v1; swap to a table if jobs must
  survive a restart or be auditable.
- **Server-side vs. client-side layout.** Phase 4 picks one; the other becomes dead weight, so
  decide before building Phase 4, not during Phase 7.
- **Editing-by-prompt / iterative refinement** ("change the trigger to a webhook") is out of scope
  for v1 — this plan covers first-shot generation only. The orchestrator's `priorErrors` channel is
  the seam a future "refine this existing workflow" mode would reuse.
