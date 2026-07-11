# Plan — Dynamic Options / Resource Locator for Node Parameters

Branch: `feat/dynamic-options-resource-locator`

A reusable node-parameter capability where a parameter's allowed values are loaded at
design time from an external system over REST, rendered as a searchable dropdown, and
persisted as a **stable key** that survives reordering and resolves at run time. Design-time
loading is a pure query (no journal entry); the run-time write stays a normal classified
side-effect through REST.

**Single and multi-select.** A parameter may select one resource or many. Multi-select persists
an **array** of stable keys (order-preserving), renders as a chip multi-select, and resolves each
key at run time. Default run-time semantics: **fail closed if any key is unresolvable** (one missing
resource fails the node with a clear journal error) — consistent with the single-value default.

---

## How this maps onto the existing codebase

The spec's illustrative names are adapted to what already exists here:

| Spec concept | This repo |
| --- | --- |
| `ParameterDef` / `ParameterKind` | `ParameterDefinition` record — [NodePackageManifest.cs:8](Backend/KnotGarden.Core/Domain/NodePackageManifest.cs#L8). String `Type` discriminator (`"string"`, `"enum"`, `"credentialRef"`, …), not an enum. Add `"dynamicOptions"` / `"resourceLocator"`, plus a `Multiple` flag (default false) for multi-select. Extending the record needs an additive optional field so existing manifests keep deserializing — see the `[JsonConstructor]` at [NodePackageManifest.cs:71](Backend/KnotGarden.Core/Domain/NodePackageManifest.cs#L71) for the same pattern used for `Inputs`. |
| `IConnection` / resolved credential | `ServerConfigInfo` + `CredentialRef` → `ICredentialAccessor` / `ISecretResolver` ([CredentialAccessor.cs](Backend/KnotGarden.Infrastructure/Persistence/CredentialAccessor.cs)). `IServerConfigStore` holds BaseUrl + CredentialRef. |
| Options endpoint | New minimal-API endpoint, mirror [InlineCodeTestEndpoint.cs](Backend/KnotGarden.Api/InlineCodeTestEndpoint.cs) (`Map…Endpoint(this WebApplication)` extension, DI via handler params, returns `Results.Ok`). |
| `IOptionsLoader` registry | New keyed registry, registered in `Program.cs` DI like the node tasks. Doubles as the allowlist. |
| Frontend renderer | New `case` in `renderField()` — [ManifestForm.tsx:317](Frontend/src/components/shared/ManifestForm.tsx#L317). Reference the `'code'`/Monaco case for a custom field type. |
| API client | Plain `fetch` + `handleResponse` in [api.ts](Frontend/src/services/api.ts) (`API_BASE='/api'`). No react-query. |
| Persisted value | Stored in `node.data.properties[paramName]` as the `{ value, label, mode }` object. onChange via `handleFieldChange` → `onUpdateNodeProperties` (React Flow / zustand). |

Resolution decision (settled): execution-time resolution lives in a **shared helper** that any
node calls — not baked into a specific integration's node — so every node using a dynamic param
resolves the same way and new integrations reuse it.

---

## Phased build order

Each phase is independently shippable and testable. Phases 1–2 are backend-only; the
feature is visible end-to-end after phase 3.

### Phase 1 — Loader abstraction + registry + one real loader
**Backend, `KnotGarden.Features` (new `Options/` folder) + `KnotGarden.Core/Contracts`.**

- `IOptionsLoader` contract: `Name`, `Task<OptionListResult> LoadAsync(OptionLoadContext, CancellationToken)`.
- Records: `OptionLoadContext(serverConfigId/connection, dependsOn dict, search, pageCursor)`,
  `OptionItem(Label, Value, Description?)`, `OptionListResult(Options, HasMore, NextPage?)`.
- `IOptionsLoaderRegistry` keyed by `Name`; `Get(name)` returns null for unknown (the allowlist).
  Register concrete loaders + registry in `Program.cs` DI alongside the node tasks.
- First concrete loader (one real integration, named e.g. `<integration>.<resource>`): resolves the
  `ServerConfigInfo` + credential, performs the REST `GET` for the resource collection, maps each entry to
  `{ Label = displayName, Value = stableId ?? displayName }`. Uses `IHttpClientFactory` (same as
  [HttpRequestNodeTask.cs](Backend/KnotGarden.Features/Nodes/HttpRequestNodeTask.cs)).
- Treat `dependsOn` values as untrusted input.

*Done when:* unit test drives the loader against a stubbed `HttpMessageHandler` and gets `OptionItem[]`.

### Phase 2 — Design-time options endpoint (allowlist + error envelope + timeout)
**`KnotGarden.Api`, new `OptionsEndpoint.cs`.**

- `POST /api/integrations/{integrationType}/options/{loaderName}`, body `{ connectionId, dependsOn, search, page }`.
- Validate `loaderName` against the registry; unknown → `404`/`400` (reject before invoking anything).
- Wrap `LoadAsync` in a `CancellationTokenSource` timeout (~5–10s) linked to the request token.
- Always return **200** with envelope `{ options, hasMore, nextPage, error }`. Unreachable system /
  timeout → `error: { code: "SYSTEM_UNREACHABLE", message }`, empty options. Reserve 5xx for genuine bugs.
- Credentials never serialized into the response — labels + opaque values only.
- Map the endpoint in `Program.cs` (`app.MapOptionsEndpoint();`).

*Done when:* curl/integration test covers happy path, unknown-loader rejection, and offline → error envelope (still 200).

### Phase 3 — Frontend async dropdown (loading / error / empty / refresh + manual fallback)
**`Frontend/src`.**

- Add `'dynamicOptions' | 'resourceLocator'` to the `ParameterDefinition.type` union — [types.ts:185](Frontend/src/types.ts#L185).
  Add optional `optionsLoader`, `dependsOn`, `allowManualEntry`, and **`multiple`** (default false) fields to the type.
- `api.ts`: `loadNodeOptions(integrationType, loaderName, { connectionId, dependsOn, search, page })`.
- `useNodeOptions` hook: state machine `idle → loading → (ready | error | empty)`; loads on open and on
  `dependsOn` change; exposes `reload()`.
- New `AsyncOptionsField.tsx` component (custom — no UI kit installed; match existing dark-theme select styling):
  searchable select, spinner while loading, refresh button, and — when `error && allowManualEntry` — a manual
  `<input>` fallback so authoring is never hard-blocked. Debounce search if the loader supports server-side `search`.
  When `multiple`, render selected items as removable chips and keep the list open for further picks; otherwise
  single-select collapses on choose.
- Wire a new `case 'dynamicOptions'/'resourceLocator'` in `renderField()` — [ManifestForm.tsx:317](Frontend/src/components/shared/ManifestForm.tsx#L317).

*Done when:* opening a node with a dynamic param shows a live list; single- and multi-select both pick correctly;
offline shows error + manual entry.

### Phase 4 — Persisted value contract `{ value, label, mode }`
**Frontend + any backend reader.**

- Single-select persists `{ value, label, mode: "list" | "manual" }` via `handleFieldChange(param.name, obj)`.
  `value` = source of truth (stable id, else name); `label` = display cache; render from `label` without a reload.
- Multi-select persists `{ mode, items: [{ value, label }, …] }` — an order-preserving array. Same rule:
  `value` is truth, `label` is display-only cache.
- Backend/runtime reads `value`(s) only and **ignores** `label`. Tolerate legacy shapes for forward-compat:
  a bare string → `{ value, mode: "manual" }`; a single object → a one-item list when the param is `multiple`.

*Done when:* both single and multi selections round-trip through save/reload and render labels without re-fetching.

### Phase 5 — Execution-time resolver (stable key → live handle) + fail-closed
**Backend `KnotGarden.Features`, shared helper.**

- `ResolveResourceAsync(loaderName, storedValue, dependsOn, ct)`: re-reads the **live** list once (reuse the
  loader or a sibling resolve method), builds a **`stableKey → entry` map**, and indexes the stored key(s) into
  it — single value or an array. This is keyed/associative lookup (`resources["res_7f3a"]`), *not* positional
  access, which is precisely why reordering is safe. Returns the handle/index(es) the REST write needs,
  preserving input order for arrays.
- **Key uniqueness:** prefer the stable **id** as the key (always unambiguous). Name-keyed lookup
  (`resources["Front Office"]`) is only safe when names are unique in the collection — if the live list contains
  duplicate names for a name-typed key, **fail closed** with a clear "ambiguous reference" error rather than
  silently taking the first. The loader should set `Value = id` whenever the API exposes one so this stays a
  non-issue.
- No match (deleted/renamed) → **fail closed**: throw a node error with a clear message; surface to the run
  journal. For multi-select, **any** unresolvable key fails the whole node (report which keys were missing).
  Default fail-closed; leave a hook for a future configurable error-vs-skip / best-effort mode.
- Because resolution is against the current list, upstream reordering cannot misdirect the workflow.
- Resolve the whole array against a single live fetch — don't re-fetch per key (N+1).

*Done when:* a test proves reorder = same target; a single deleted key = node failure with a journal error;
and a multi-select with one missing key fails closed naming the missing key(s).

### Phase 6 — Caching layer (TTL + manual refresh)
**Backend (preferred — shared across editors).**

- Cache `OptionListResult` keyed by `(connectionId, loaderName, hash(dependsOn), search)`, TTL ~30–60s
  (`IMemoryCache`). Manual refresh from the UI busts the key (cache-control header or `?refresh=1`).
- Keeps the editor from hammering the external API on every node open.

*Done when:* repeated opens within TTL hit cache; refresh forces a live call.

### Phase 7 — Dependent / cascading options
**Both ends.**

- When a `DependsOn` parent param changes (pick a parent resource → load its dependent resources), invalidate
  dependent caches and reload; pass parent values through `dependsOn`. Frontend hook already keys on `dependsOn`
  (phase 3) — wire the parent values from sibling `properties` and clear the child selection when the parent changes.

*Done when:* changing the parent selection reloads the dependent list and clears a now-invalid child selection.

### Phase 8 (later) — Resource-locator modes
Pick-from-list (done) → add `by-ID` and `by-expression` modes behind the same `mode` field.

---

## Acceptance criteria
- Opening the node shows a live resource list pulled from REST.
- Selecting persists a stable key; reordering the resources upstream does not retarget the workflow.
- A `multiple` param selects several resources, persists an order-preserving array of stable keys, and renders
  them as removable chips.
- System offline at design time → manual entry works, error shown, authoring not blocked.
- Referenced resource deleted → run fails closed with a clear journal error. For multi-select, one missing key
  fails the node and the error names the missing key(s).
- External credentials never reach the browser (verify request/response payloads).
- Changing the parent selection reloads the dependent resource list.
- Loader name outside the allowlist is rejected by the endpoint.

## Cross-cutting
- **Security:** loaders run server-side with stored credentials; browser sees labels + opaque values only;
  enforce the allowlist; treat `dependsOn` as untrusted.
- **Side-effect classification:** option loading is a design-time read — no journal entry, pure query. The
  run-time write stays normally classified and goes through REST.
- **Offline:** design time never blocks authoring (error + manual entry); run time fails closed.

## Suggested commit slicing
One commit per phase (1–7), so the branch reads as the spec's build order. Phase 8 deferred.
