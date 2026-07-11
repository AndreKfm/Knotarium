# Version Management Surface — Refactored Plan

> **Verification status (2026-06-16):** Every "current state" claim below was checked against the
> codebase. Confirmed: `ActiveWorkflowVersion` is a single overwriting pointer (no append-only log;
> only a generic `AuditEntry` exists); `WorkflowVersion` has exactly 5 fields with **no** provenance
> (`Origin`/`SourceVersionId`/`CreatedBy`/`Label`); no unique `(WorkflowDefinitionId, VersionNumber)`
> index; `VersionNumber = max + 1` is **not** transaction-wrapped; no concurrency token; `GET /versions`
> returns **full node/edge payloads**. Two findings refine the plan and are annotated inline:
> **(a) execution pinning already happens at `ExecutionInstance` creation** (`WorkflowEnqueueService`,
> manual-trigger path) — V0.1 is *verify+extend*, not *build*; **(b) triggers are currently
> definition-scoped and synced at *publish*** (`WorkflowScheduleSynchronizer`,
> `WorkflowPollingTriggerSynchronizer`), so V3.1 introduces a **design change** (version-scoped trigger
> binding), not a fix.

## Status

Versioning engine: **complete.** Version management surface + git-interop: **absent.**

The honest characterization is not "missing versioning" — it's "versioning engine present, management
surface absent." Most of what follows is last-mile presentation/ergonomics on top of data we already
hold. The exceptions — flagged as **CORRECTNESS GATES** below — are small data-model additions that
fix claims the current model cannot actually back, or that prevent silent breakage. Those must land
before the corresponding capability is advertised.

---

## 1. Framing — what the model does and does not guarantee

The model's defining strength is **reproducibility of the workflow definition**, not editor
convenience. Every execution records its `WorkflowVersionId` against an immutable published version,
so the exact graph + configuration that an execution was launched against is permanently recoverable.

**Scope the claim precisely.** Do **not** claim "guaranteed deterministic replay." Version pinning
reproduces the graph and config payload, but execution behavior also depends on things outside the
versioned definition:

- Node implementation / plugin code version (a `SendEmail` node can change implementation under the same type id)
- Runtime / container / service version
- Referenced sub-workflows and their versions
- Credentials and external configuration, environment variables, feature flags
- Input payload, trigger metadata, and external service state

**Correct wording to use everywhere:**

> Every execution is permanently tied to the exact immutable workflow definition that was selected
> for it. Full deterministic replay additionally requires pinning of node/runtime dependencies,
> which is out of scope for this surface.

Node-level versioning remains a **separate axis** (a custom-node-registry concern, not a
workflow-version concern). It is intentionally out of scope here, but because it is unpinned it
**caps the reproducibility claim** and must be acknowledged rather than implied away.

**Competitive position** (keep, but scoped): the per-run version pointer is a cleaner reproducibility
guarantee than Node-RED Projects (git history is editor-side, not pinned per run) and more explicit
than n8n (per-execution workflow snapshot rather than a version pointer). On the data model we are at
**parity or better — provided the corrections below land.**

---

## 2. Data-model changes

### 2.1 Activation history — CORRECTNESS GATE

This is the largest gap. **Verified:** `ActiveWorkflowVersion` stores only the current pointer
(`WorkflowDefinitionId`, `WorkflowVersionId`, `ActivatedAtUtc`); `ActivateAsync` upserts, overwriting
the previous activation info. The model therefore cannot answer **"which version was live at time
T?"** — fork-forward keeps the version history monotonic but does not preserve the *activation
timeline*, and `WorkflowVersion.CreatedAt` is a publish time, not an activation time.

Add an **append-only activation log**; keep `ActiveWorkflowVersion` as a fast current-state projection.

| Entity | Fields |
|---|---|
| **`WorkflowVersionActivation`** (new, append-only) | `Id`, `WorkflowDefinitionId`, `WorkflowVersionId`, `ActivatedAtUtc`, `ActivatedBy`, `ActivationReason`, `RestoredFromVersionId?`, `PreviousActiveVersionId?`, `CorrelationId` |
| **`ActiveWorkflowVersion`** (kept as projection) | `WorkflowDefinitionId`, `WorkflowVersionId`, `ActivatedAtUtc`, `ConcurrencyToken` |

Activation must atomically (a) insert the activation-history row and (b) update the
`ActiveWorkflowVersion` projection. Only with this log is "what was live at time T" genuinely correct.

### 2.2 Version provenance + actor — CORRECTNESS GATE (provenance)

**Verified:** the current schema cannot distinguish a restored version from a fresh publish
(`WorkflowVersion` has no origin/source fields), so the promised "restored from v3" audit event is
**not storable**. Add:

| `WorkflowVersion` — new fields | Purpose |
|---|---|
| `Origin` (`Published` \| `Restored` \| `Imported`) | distinguishes how the version was created |
| `SourceVersionId?` | for Restored/Imported: the version it was copied from |
| `CreatedBy` | actor (audit) |
| `CreationReason?` / `Label?` | human-readable note, settable at publish/restore |

### 2.3 Integrity constraints — CORRECTNESS GATE

**Verified absent — all three needed:**

- **Unique constraint** on `(WorkflowDefinitionId, VersionNumber)` — prevents the `max + 1` race
  producing duplicate numbers (currently no index; `max+1` is not transaction-wrapped).
- `VersionNumber` computed **inside the transaction** (or DB-assigned).
- **Optimistic concurrency token** on `ActiveWorkflowVersion` — prevents lost-update on concurrent
  activations (currently no token exists).

---

## 3. Execution pinning rule — CORRECTNESS GATE

> **Verified already correct for the main paths:** `WorkflowVersionId` is resolved and persisted at
> `ExecutionInstance` **creation** in `WorkflowEnqueueService` (and the manual-trigger path in
> `Program.cs`), not when a worker claims/starts it. This gate is therefore **verify + extend**, not
> build-from-scratch — the work is confirming and covering the remaining creation paths below.

The "activation never disrupts in-flight executions" property is only true if pinning happens at the
right moment. There is a race between activation and execution startup.

**Rule:** resolve and persist `WorkflowVersionId` **atomically when the `ExecutionInstance` is
created** (not when a worker claims it or when it starts running). After creation, activation changes
affect only **future** execution instances; queued and running executions continue on their pinned
version.

Define the same rule explicitly for: **scheduled** executions, **retries**, **resumptions** (e.g.
human-in-the-loop wait/resume), and **child/sub-workflow** executions. Each of these creates an
`ExecutionInstance` and must pin at creation. *(These paths need an audit to confirm they go through
the same creation-time pinning — the webhook/enqueue and manual paths are confirmed; scheduler/poll/
resume/child paths are to be verified.)*

---

## 4. Activation is not free — trigger re-binding

> **Verified — this is a DESIGN CHANGE, not a fix.** Today triggers are **definition-scoped** and
> synced at **publish** (`WorkflowScheduleSynchronizer.SyncAsync` / `WorkflowPollingTriggerSynchronizer.SyncAsync`,
> called from `PublishAsync`); `ActivateAsync` only moves the pointer and does **not** touch triggers.
> The plan below makes trigger config effectively **version-scoped** (re-bound on activation). That is
> a deliberate semantic shift — call it out explicitly before implementing, because it changes when a
> webhook path / cron schedule starts taking effect (activation, not publish).

Activating (or restoring-then-activating) a version whose trigger config differs from the live one —
different webhook path, different cron schedule — has a side effect: the live trigger bindings must be
re-registered.

Activation must, **within the same atomic operation as §2.1**:

1. Insert the activation-history row.
2. Update the `ActiveWorkflowVersion` projection.
3. Re-register trigger bindings to match the newly active version (deregister old webhook/schedule,
   register new).

Define the **failure semantics**: if trigger re-binding fails, the whole activation rolls back (no
partial state where the active pointer moved but triggers didn't). Record trigger-binding changes on
the activation-history row.

---

## 5. Restore — fork-forward, validated, transactional

**Semantics: fork-forward, not reactivate-in-place.** Reactivating an old version in place makes
"what was live at time T" ambiguous (the same version number live across non-contiguous ranges).
Copying the old payload into a new version number keeps a strictly monotonic, append-only history
with its own timestamp and audit trail. This is the only choice consistent with the immutable publish
model.

**Endpoint:** `POST /api/workflows/{id}/restore/{versionId}?activate=false`

`activate=false` **by default** lets a user copy an old version forward, fix incompatibilities, and
only then activate — important because old versions may be invalid against the current environment
(see validation below).

**Steps (single transaction):**

1. Validate `versionId` belongs to `{id}` (route + tenant boundary).
2. Compatibility validation (see below).
3. Load target `WorkflowVersion` payload.
4. Create new `WorkflowVersion`: `VersionNumber = max + 1` (in-tx), same payload, `Origin = Restored`,
   `SourceVersionId = versionId`, `CreatedBy`, optional reason/label.
5. If `activate=true`: perform the atomic activation (§2.1 + §4).
6. Return provenance.

**Compatibility validation** (before activate; surfaced as warnings when `activate=false`):

- All referenced node types installed and supported
- Required node implementation versions available
- Referenced credentials still exist (by reference)
- Referenced sub-workflows exist
- Graph validation passes; no blocked/deprecated node types

**Concurrency / failure semantics:**

- Whole operation transactional; a failed activation must not leave a version that looks like a
  successful restore (unless `activate=false`, where an un-activated forward copy is the intended result).
- Optimistic-concurrency conflict on activation → **409, retryable**.
- Optional **idempotency key** on restore requests to make retries safe.

**Restore response:**

```json
{
  "versionId": "...",
  "versionNumber": 8,
  "origin": "Restored",
  "restoredFromVersionId": "...",
  "activated": true,
  "activatedAtUtc": "..."
}
```

---

## 6. Folder export / import (git-interop, BYO-git)

**Decision: ship filesystem export/import, not in-app git.** Emit workflow files to an export folder
and let the user bring their own git (or rsync, S3 sync, CI, backup tooling). Git becomes one consumer
of the folder, not a feature we own. In-app git/promotion UI is optional later sugar for non-technical
users, layered on top of this — not a prerequisite.

### 6.1 Three-layer model (do not blur these)

| Layer | Role | Gitted? |
|---|---|---|
| **Database** | authoritative history + audit: immutable versions, activation log, per-run pinning | **Never** |
| **Export folder** | current published state per workflow, as deterministic files — the interop surface | yes, by the user |
| **Git** (user-run, optional) | history/transport of the folder | the user's concern |

The folder is a **projection** of the DB, written on publish and read on import. We never git the live
storage/DB (binary, churning, secret-bearing, corruption-prone). Because the DB carries full history
and git carries the folder's history, the folder only needs each workflow's **current published
definition** — no need to duplicate the version log as N files.

### 6.2 Export

On publish (or on demand), serialize the published `WorkflowVersion` to a file in the configured
export folder.

**Deterministic serialization** — the single most important requirement, or the whole git story
collapses into noise:

- Canonical key ordering (stable JSON, or YAML)
- Stable node/edge ordering
- **Layout split out** (node positions, dimensions, viewport) into a separate file or section so
  cosmetic moves don't pollute logic diffs (mirrors the behavioral-vs-layout diff split in §7)
- **No secrets, ever.** Credential and variable **references/IDs only**; encrypted values stay in the
  DB. The folder is the most likely thing to be accidentally committed to a public repo — this is
  non-negotiable regardless of who runs git.
- Include a small **manifest** per workflow (`workflowId`, `versionNumber`, `label`, `checksum`) for
  unambiguous import and drift detection.

### 6.3 Import

Importing a file creates a **new immutable `WorkflowVersion`** (`Origin = Imported`, `SourceVersionId`
null or external ref), preserving the append-only invariant. It never mutates in place.

Run the **same compatibility validation** as restore (§5) before the imported version can be activated.

Treat import as **replace-creating-new-version, not merge.** Do not attempt graph merges; git's
line-merge is wrong for node graphs and can yield structurally invalid workflows. Enforce
one-directional flow per instance (mirrors n8n's "pull overwrites" pragmatism).

### 6.4 Endpoints

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/workflows/{id}/export` | write current published version to export folder |
| POST | `/api/workflows/import` | import a file → new `Imported` version (validated, inactive by default) |

---

## 7. The in-app surface (does not replace any of the above)

Git/folder interop does **not** replace the in-app surface, and the activation log/diff are not
replaced by git: a git client showing line-diffs of node JSON is unreadable to editor users, and git
can answer neither "what was live at T" (§2.1) nor per-run pinning (§3).

### 7.1 Version list + payload separation — (perf gate)

> **Verified:** `GET /api/workflows/{id}/versions` currently returns **full node/edge payloads** for
> every version. This is the perf problem below, confirmed.

Fetching all versions with full node/edge payloads gets expensive. Split:

| Method | Route | Returns |
|---|---|---|
| GET | `/api/workflows/{id}/versions` | paginated **metadata**: `Id`, `VersionNumber`, `CreatedAt`, `CreatedBy`, `Label`, `Origin`, `IsActive`, `RestoredFromVersionId`, `NodeCount`, `ExecutionCount` |
| GET | `/api/workflows/{id}/versions/{versionId}` | full nodes + edges, **only when** preview/diff needs them |
| GET | `/api/workflows/{id}/active-version` | current live version |

Add pagination + ETags/caching. Client-side diff is fine, but load only the two selected payloads.

### 7.2 History panel

Collapsible drawer on the editor: version number, timestamp, author, label, active badge, origin.
Click to preview; restore button (gated on preview existing). Thin `useWorkflowVersions(workflowId)`
hook over the metadata endpoint. Toolbar toggle; **pick a shortcut that doesn't collide** with
browser/OS (Ctrl+H is browser history, Cmd+H hides the app on macOS — choose another, e.g. register
it through the existing `?` shortcut registry / `keyboardShortcuts.ts`).

### 7.3 Safe read-only preview — must not mutate editor state

Do **not** swap the editor's live node/edge collections. Use an explicit editor-mode state machine:

```
EditorMode = Draft | PublishedPreview(versionId) | Diff(leftVersionId, rightVersionId)
```

Define: **unsaved-draft protection** (snapshot the working draft, restore on close), **preview
replaces vs overlays** (replace, in a separate read-only canvas state), **editing/autosave disabled**
during preview, **behavior when the active version changes remotely** mid-preview. This prevents
accidental publication or loss of local changes.

### 7.4 Diff (client-side)

- **Separate behavioral changes** (node type, configuration, connections) **from layout changes**
  (position, dimensions, viewport); let users collapse cosmetic diffs.
- Canonical JSON comparison; **normalize default values** before comparing.
- Mask credential identifiers; confirm secrets are never stored in version payloads in the first place.
- Prefer **persistent edge IDs** where available — the `source+sourceHandle+target+targetHandle`
  composite key collapses duplicate parallel edges.
- Most-wanted diff is usually **working draft vs. active**; support the draft as one side, not just
  two committed versions.

### 7.5 Labels (nice-to-have)

Already covered by `Label`/`CreationReason` in §2.2. Surface in the panel; auto-populate on restore
("Restored from v3").

---

## 8. Cross-cutting: authorization, audit, retention

### 8.1 Permissions

Publish/activate/restore/import are production-affecting and more sensitive than viewing. Separate:

`WorkflowVersion.View` · `.Publish` · `.Activate` · `.Restore` · `.Import`

### 8.2 Audit

Every publish/activate/restore/import records: actor, timestamp, source version, previous active
version, new active version, reason, correlation/request id. (Most of this becomes first-class via
§2.1/§2.2.) The UI must make explicit that a restore/activation affects **future executions only** —
it does **not** undo side effects already caused by the newer version's executions.

### 8.3 Retention / deletion policy

Define before any cleanup logic can undermine the audit/replay guarantee:

- Published versions are **immutable**.
- Versions **referenced by any execution cannot be deleted**.
- The **active version cannot be deleted**.
- Workflow deletion **archives** versions rather than hard-deleting.
- Retention is defined separately for **execution data** vs. **workflow definitions**.

---

## 9. Implementation order

**Gate principle:** a correctness gate must land **before** the capability it underpins is advertised.

| Phase | Item | Type |
|---|---|---|
| **V0** | Append-only activation history (`WorkflowVersionActivation`); `ActiveWorkflowVersion` becomes projection w/ concurrency token | **CORRECTNESS GATE** — required before claiming "what was live at T" |
| **V0.1** | Execution pinning moment **verified + extended**: confirmed at instance creation for enqueue/manual; cover scheduled/retry/resume/child paths | **CORRECTNESS GATE** (largely already satisfied) |
| **V0.2** | Unique `(WorkflowDefinitionId, VersionNumber)` constraint; transactional, optimistic-concurrency activation | **CORRECTNESS GATE** |
| **V0.3** | Version provenance + actor fields (`Origin`, `SourceVersionId`, `CreatedBy`, `Label`) | **CORRECTNESS GATE** (provenance) |
| **V0.4** | Reproducibility wording corrected in all docs/marketing | claim correction |
| **V1** | Paginated metadata history panel + version-detail endpoint | surface |
| **V2** | Safe read-only preview (separate editor-mode state) | surface |
| **V3** | Restore endpoint: fork-forward, validated, transactional, provenance, `?activate=false` | surface (depends on V0.x) |
| **V3.1** | Trigger re-binding as part of atomic activation — **DESIGN CHANGE** (triggers move from definition-scoped@publish to version-scoped@activate) | **CORRECTNESS GATE** (activation side effect) |
| **V4** | Restore UI + confirmation (gated on V2 preview) | surface |
| **V5** | Diff: behavioral vs layout, canonical JSON, edge IDs | surface |
| **V6** | Export to folder (deterministic, secret-free, manifest) | interop |
| **V7** | Import from folder → new `Imported` version (validated, inactive default) | interop |
| **V8** | Permissions, audit surfacing, retention/deletion policy | hardening |
| **V9** | *(optional, on demand only)* idempotency keys, ETags, in-app git/promotion UI | harden / future |

**Sequencing notes:**

- V0.x are foundational and partly parallelizable; **V0 (activation log) is the single thing that
  must precede any public audit claim.**
- V1 and V3 can be developed in parallel (frontend panel against mock data while the restore endpoint
  lands), but **V2 must precede V4** — restore-without-preview is a footgun.
- V6/V7 (folder interop) depend only on deterministic serialization + the provenance fields (V0.3);
  they're independent of the panel/diff work and can run in parallel once V0.3 is in.
- Resist V9 until there's real demand; most teams are satisfied by folder export/import + their own git.

---

## 10. One-line summary

Versioning engine is **done** and is a genuine strength once scoped to "definition reproducibility,
audited by an append-only activation log." The remaining work is: **fix the activation-history /
pinning / provenance gates**, build a **safe in-app surface** (paginated history → preview → validated
fork-forward restore → behavioral diff), and add **deterministic, secret-free folder export/import** so
users bring their own git — keeping the **DB authoritative, the folder a projection, and git the
user's concern**.
