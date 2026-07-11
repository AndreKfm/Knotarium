# OpenAPI Importer — Implementation Plan

Status: Draft for review
Author: generated against repo state 2026-06-07
Primary architecture authority: `architecture/system.yaml`, `docs/Knotarium_MVP_Architecture-5.md`

## 1. Goal

Let a user import an OpenAPI / Swagger document, browse its operations and schemas, and drag any operation onto the workflow canvas. Importing a spec **generates one compiled node named after the API** that can perform any operation in that API; the operation and its arguments are node properties. The call target (base URL, server variables) and authentication (API key, Bearer, Basic, OAuth2) come from a reusable **Server Configuration** resolved at execution time on top of the existing Credential/Secret subsystem.

### Confirmed decisions

| Topic | Decision |
|---|---|
| Node model | **One compiled node per imported API**, named after the API, handling all of its operations. Operation is selected via a property; dragging a specific operation drops this node pre-set to that operation. |
| Generation | Reuse the existing dynamic compiled-node pipeline (`INodePackageGenerator` → Roslyn compile in `DynamicCustomNodeTask` → DB-stored `NodePackage`/`NodePackageVersion` → `DbNodePackageManifestProvider`). |
| Spec storage | **DB-persisted and versioned**. Re-import creates a new spec version; regenerating the node creates a new node-package version. |
| Auth scope | Reuse `ICredentialCipher` / `ISecretResolver` / `ICredentialAccessor`. Support **API key (header/query), HTTP Bearer, HTTP Basic, and OAuth2 client-credentials**. Authorization-code/implicit deferred (can't run unattended). |
| Spec formats | **OpenAPI 3.0, OpenAPI 3.1, and Swagger 2.0**, in **JSON and YAML**. Swagger 2.0 up-converted to a 3.x internal model on import. |
| `$ref` scope | **Single-document specs only for v1.** Internal `$ref`s resolved; external/multi-file specs are flag-and-rejected with a clear error. |
| Schema evolution | Pre-release with a planned fresh start → **keep `EnsureCreated()` and recreate the dev DB** when schema changes. No EF migrations, no `CREATE TABLE` guards. |
| Library | Add **`Microsoft.OpenApi`** (Infrastructure adapter, behind a Core `IOpenApiParser` contract). |

## 2. How this maps onto the existing architecture

The platform already has the pieces this feature plugs into:

- Built-in nodes live in `Backend/Knotarium.Features/Nodes/*NodeTask.cs` with a discovery manifest under `nodes/<Name>/manifest.yaml`. `HttpRequest` is the closest precedent (`HttpRequestNodeTask.cs` + `nodes/HttpRequest/`). REST Caller follows the same pattern.
- `INodeContext` exposes `Http` (`IHttpClient`), `Credentials` (`ICredentialAccessor.GetSecretAsync`), `State`, and `Logger`. The REST Caller executor consumes these — no new runtime capability is required beyond what `HttpRequest` already uses.
- Credentials are stored encrypted in the `Credentials` table via `AesCredentialCipher` and resolved through `ICredentialAccessor` / `ISecretResolver`. Server-config auth reuses this; it does not introduce a parallel secret store.
- Persistence uses EF Core with `DbSet<>` entities on `AppDbContext`. **The DB is created with `EnsureCreated()` (see `Program.cs:167`), not EF migrations.** New tables only appear on a fresh DB file; see §7 for the schema-evolution caveat.
- The node editor (`Frontend/src/node-editor/`) and component tree (`Frontend/src/components/`) host the palette, canvas, and property panel. Frontend contracts live in `Frontend/src/types.ts`.

### Module placement (must respect `system.yaml` boundaries)

| Concern | Module | Rationale |
|---|---|---|
| Spec domain model, contracts (`IOpenApiParser`, `IServerConfigStore`, DTOs) | `Knotarium.Core` | Stable cross-module contracts and domain primitives; no volatile helpers. |
| Parse + normalize spec, grouping logic, import use-case, REST Caller `RestCallerNodeTask`, request-build/auth orchestration | `Knotarium.Features` | Use-case and node behavior. Slice-local folders `Features/OpenApi/` and `Features/Nodes/`. |
| EF entities + repositories for specs/versions/server-configs, OAuth2 token cache, credential encryption | `Knotarium.Infrastructure` | Persistence and security adapters satisfying Core contracts. |
| HTTP endpoints for import/list/server-config + DI wiring | `Knotarium.Api` | Transport and composition root only — no business logic. |
| Discovery manifest for the REST Caller node | `nodes/RestCaller/` | File-system-discovered manifest + assets. |
| Importer UI, operation/schema browser, drag-and-drop, dynamic property form, server-config UI | `Frontend` | UI composition + node-editor experience. |

Boundary risks to actively avoid (from `system.yaml` review_focus): keep parsing/grouping out of `Api`; keep OAuth token-refresh policy in `Features`/`Infrastructure`, not `Core`; do not let `NodeRuntime` learn about OpenAPI.

## 3. Internal spec model (the normalization layer)

All three input dialects collapse to one internal model so the rest of the system never branches on spec version.

- Detect dialect: `swagger: "2.0"` → Swagger 2.0; `openapi: 3.0.x` / `3.1.x` → OpenAPI 3.x. Accept JSON or YAML (YAML parsed to JSON first).
- Up-convert Swagger 2.0: `host`+`basePath`+`schemes` → `servers[]`; `definitions` → `components.schemas`; `securityDefinitions` → `components.securitySchemes`; body/formData params → `requestBody`; `produces`/`consumes` → media types.
- Resolve internal `$ref`s within the document. External `$ref` / multi-file specs are **out of scope for v1** — detect and reject with a clear error message.
- **Library (confirmed):** `Microsoft.OpenApi` (Microsoft.OpenApi.Readers) reads 2.0/3.0/3.1 in JSON+YAML into a single object model. It is referenced **only** by the `Infrastructure` reader adapter, behind the Core `IOpenApiParser` contract, so `Features`/`Core` stay library-agnostic.

Normalized shape (conceptual, defined as records in `Core`):

```
ImportedSpec     { Id, Title, Version, OriginalFormat, ServersDefault[], Tags[], ImportedAtUtc, SpecVersionNumber }
ApiOperation     { OperationId, Method, PathTemplate, Summary, Tags[], Parameters[], RequestBody?, Responses[], SecurityRefs[] }
ApiParameter     { Name, In(path|query|header|cookie), Required, Schema, Description }
ApiRequestBody   { Required, MediaTypes[], Schema }
ApiSchema        { Name, JsonSchema (raw), Properties[] }   // properties drive the drag-and-drop form
SecurityScheme   { Name, Type(apiKey|http|oauth2), Scheme(bearer|basic), In, ParamName, Flows }
```

## 4. Feature 1 & 2 — Import, group, and list

### Import flow
1. `POST /api/openapi/specs` accepts an uploaded file or pasted text (multipart or JSON body), mirroring the existing node-package install endpoint style (`Program.cs:1331`).
2. `Api` hands raw bytes to a `Features` use-case `ImportOpenApiSpecHandler`, which calls `IOpenApiParser` (Infrastructure adapter) → normalized `ImportedSpec`.
3. Persist via `IOpenApiSpecStore` (Infrastructure): a new `OpenApiSpec` row plus an `OpenApiSpecVersion` row (monotonic `SpecVersionNumber`). Re-importing the same logical spec (matched by user-chosen id or title) appends a new version; prior versions stay queryable — mirrors the existing `WorkflowVersion` / `NodePackageVersion` versioning pattern.
4. Response returns the spec id, version number, and a grouped operation/schema summary.

### Grouping & listing
- Operations grouped by **tag** (primary), falling back to first path segment when untagged. Endpoints:
  - `GET /api/openapi/specs` — list specs (latest version each).
  - `GET /api/openapi/specs/{id}/versions` — version history.
  - `GET /api/openapi/specs/{id}` — full normalized model for the browser UI (operations grouped, schemas listed).
  - `GET /api/openapi/specs/{id}/operations/{operationId}` — single operation detail incl. resolved parameter/body schema used to render the property form.
- Schemas/metadata listed from `components.schemas` with title, description, and property list.

## 5. Feature 3 — Per-API compiled node + drag-and-drop

### The node model: one compiled node per imported API
Importing a spec **generates a compiled node package named after the API** (e.g. "Petstore API") that handles every operation in that spec. This reuses the existing dynamic node pipeline end-to-end:

1. `ImportOpenApiSpecHandler` (Features) calls a new `OpenApiNodeGenerator` (Features) that emits a `GeneratedPackage(PackageId, ManifestYaml, ExecutorCode)` — the same record `INodePackageGenerator` already returns.
2. The manifest's `operationId` parameter uses `ParameterDefinition.Values` (already supported — see `NodePackageManifest.cs` and `NodeEditorSandboxService.ParameterDocument.Values`) populated with every operationId, so the editor shows a dropdown.
3. The generated `ExecutorCode` is a full `INodeExecutor` class. It is **compiled by the existing Roslyn path in `DynamicCustomNodeTask`** (assembly cached in a `CollectibleAssemblyLoadContext`), stored as a `NodePackage` + `NodePackageVersion`, and surfaced in the palette by `DbNodePackageManifestProvider` — no new compilation or registration machinery.
4. Re-importing the spec generates a **new node-package version** (monotonic), consistent with existing `NodePackageVersion` versioning.

Generated `manifest.yaml` (per API, illustrative for "Petstore"):

```
id: openapi.petstore            # derived from spec title, namespaced
displayName: Petstore API
category: Integrations
tier: Compiled
sideEffectKind: NonIdempotentSideEffect   # method-dependent; GET treated idempotent at runtime
recoveryMode: RetryAutomatically
capabilities: [http, credentials]
parameters:
  - name: operationId            # dropdown of all operations in this API
    type: string
    required: true
    expression: false
    values: [ getPetById, addPet, updatePet, deletePet, findPetsByStatus, ... ]
  - { name: serverConfigId, type: string, required: true,  expression: false }
  - { name: specVersion,    type: string, required: false, expression: false }  # pin spec version; default latest
  - { name: arguments,      type: string, required: false, expression: true  }  # JSON: { path:{}, query:{}, header:{}, body:{} }
outputs: [ { name: success }, { name: error } ]
```

The per-operation parameter/body fields are **not** fixed manifest parameters (operations differ too much); they are rendered dynamically into the `arguments` object by the editor (below). This keeps one compiled node able to handle the whole API while still giving "every element is a property to fill."

### Drag-and-drop UX
- The importer browser lists operations as draggable chips (`HTTP method + path + summary`), grouped by tag.
- Dragging an operation onto the canvas drops **this API's compiled node** with `operationId` pre-selected and `arguments` seeded with **every parameter and request-body field** from that operation's schema.
- The property panel renders one input **per parameter/body field**, grouped Path / Query / Header / Body. Required fields are marked; **optional params render but are omittable** (cleared = not sent). Each field is expression-enabled (`expression: true` convention) so values can reference upstream node outputs/variables.
- Changing `operationId` in the panel re-renders the argument form for the newly selected operation (schema fetched from the operation-detail endpoint, §4).
- `serverConfigId` is a dropdown of saved server configurations (Feature 4).

## 6. Features 4 & 5 — Server configuration + generated API node execution

### Server Configuration (Feature 4)
A reusable, persisted record the caller selects:

```
ServerConfig { Id, Name, BaseUrl, ServerVariables{}, SecuritySchemeType, AuthDetails, CredentialRef, CreatedAt, UpdatedAt }
```

- `BaseUrl` derives from the spec's `servers[]` (editable) with server-variable substitution.
- `CredentialRef` points at an existing row in the `Credentials` table (encrypted via `AesCredentialCipher`). **No secret material is stored on the ServerConfig itself** — only the reference, consistent with `system.yaml` (Infrastructure owns credential encryption/secret storage).
- Endpoints: `GET/POST/DELETE /api/server-configs` (+ `PUT` for edit), mirroring `/api/credentials` (`Program.cs:1071-1148`).
- UI: a Server Configurations screen alongside Credentials, plus inline "create from this spec's server" affordance in the importer.

### Auth schemes
| Scheme | Resolution at execution |
|---|---|
| API key | Inject secret into header or query param per `SecurityScheme.In`/`ParamName`. |
| HTTP Bearer | `Authorization: Bearer <secret>` (same shape `HttpRequest` already uses). |
| HTTP Basic | `Authorization: Basic base64(user:secret)`; username in config, password from credential. |
| OAuth2 (client-credentials) | Token endpoint exchange using client id/secret from credential; cache token until expiry in an `OAuthTokenCache` (Infrastructure); refresh on 401/expiry. Authorization-code/implicit flows deferred. |

### Generated API node executor (Feature 5)
The generated `ExecutorCode` is a compiled `INodeExecutor` (run via `DynamicCustomNodeTask`'s Roslyn path). The spec id is baked into the generated source at import time (the node *is* this API), so the node only needs `operationId` at runtime. At execution it:
1. Reads `operationId` / `specVersion` and loads the pinned operation from `IOpenApiSpecStore` (resolved via DI, not embedded as data, so spec edits don't require recompiling the node).
2. Loads `ServerConfig` by `serverConfigId`; builds the absolute URL from `BaseUrl` + substituted `pathTemplate` + query args.
3. Reads `arguments` JSON, places path/query/header values and serializes the body per the operation's media type. Omitted optional args are skipped.
4. Resolves auth via the scheme table above, using `context.Credentials.GetSecretAsync(credentialRef)` — the same dependency `HttpRequest` relies on.
5. Sends through `context.Http` (Infrastructure egress policy still applies), returns `success` (status, headers, parsed body) or `error`, matching `HttpRequest`'s result shape and `NodeExecutionStatus`.

Because the generated source is small and uniform, the generator can emit it from a fixed template (no LLM needed) — `OpenApiNodeGenerator` is deterministic string templating, which also makes its output unit-testable.

## 7. Persistence detail and the EnsureCreated caveat

New `AppDbContext` DbSets: `OpenApiSpecs`, `OpenApiSpecVersions`, `ServerConfigs`, and `OAuthTokenCache` (token cache may be in-memory). Configure each in `OnModelCreating` like existing entities; JSON columns via the existing `JsonValueConverter` for the normalized spec blob. Generated node packages reuse the existing `NodePackages` / `NodePackageVersions` tables — no new tables there.

**Schema evolution (decided):** keep the current `Database.EnsureCreated()` approach. `EnsureCreated()` does not alter an existing SQLite file to add new tables (flagged in `Program.cs:168`), but since the product is pre-release and a clean rebuild is planned, we simply **recreate the dev DB file** when the schema changes. No EF migrations and no `CREATE TABLE IF NOT EXISTS` guards are introduced — that complexity is deliberately deferred to the future fresh start.

## 8. Frontend work

- `Frontend/src/types.ts`: add `ImportedSpec`, `ApiOperation`, `ApiSchema`, `ServerConfig` contracts.
- New components under `Frontend/src/components/`: `OpenApiImporter` (upload/paste), `OperationBrowser` (grouped, draggable), `SchemaList`, `ServerConfigManager`.
- `Frontend/src/node-editor/`: drag source → canvas drop handler creating a `restCaller` node; dynamic property form driven by the operation-detail endpoint; reuse the expression-input control already used by other nodes.
- API client helpers in `Frontend/src/utils` (kept narrow per `system.yaml`).

## 9. Build order (incremental, each independently testable)

1. **Core contracts + normalized model** (`IOpenApiParser`, `IOpenApiSpecStore`, `IServerConfigStore`, records).
2. **Parser adapter** in Infrastructure (Microsoft.OpenApi) with normalization + Swagger 2.0 up-convert + external-`$ref` rejection; unit tests over sample 2.0 / 3.0 / 3.1, JSON + YAML.
3. **Persistence**: entities, DbSets, store implementations (recreate dev DB per §7).
4. **Import + list/group API** + `ImportOpenApiSpecHandler` in Features; tests mirroring `WorkflowApiTests`.
5. **Server configs**: entity, store, API, encryption reuse; tests.
6. **`OpenApiNodeGenerator`** (Features): deterministic template emitting `GeneratedPackage` (manifest with `operationId` `values` + executor source); generate-on-import wired through the existing compile/store/registry path; generator output unit tests.
7. **Executor request-build + auth** (API key/Bearer/Basic) inside the generated executor template; executor unit tests parallel to `HttpRequest` with mock `IHttpClient`/`ICredentialAccessor`.
8. **OAuth2 client-credentials** + token cache.
9. **Frontend**: importer + operation/schema browser.
10. **Frontend**: drag-and-drop + dynamic property form + `operationId` switching.
11. **Frontend**: server-config UI.
12. **End-to-end verification**: import Petstore in all three dialects, confirm a node appears named after the API, drag an operation, configure a server + credential, execute against a mock server; Playwright e2e following existing `Frontend/e2e` patterns.

## 10. Testing & verification

- Backend unit tests: parser/normalizer (per dialect, JSON+YAML, edge cases — no tags, no operationId, `$ref` chains, external-`$ref` rejection), `OpenApiNodeGenerator` output (manifest `values`, executor source), request builder (path/query/header/body placement, omitted optionals), each auth scheme, version monotonicity.
- API tests in `Knotarium.Tests/Api` for import/list/server-config endpoints.
- Node executor tests in the NodeRuntime/Features test projects mirroring `HttpRequest`, using mock `IHttpClient`/`ICredentialAccessor`.
- Frontend: component tests for the browser/form; Playwright e2e for the drag-and-drop → execute path.
- Petstore fixtures in 2.0/3.0/3.1 committed as test assets.

## 11. Resolved decisions

All prior open questions are now decided (pre-release, with a planned clean rebuild later):

1. **Schema evolution:** keep `EnsureCreated()`, recreate the dev DB on schema change. No migrations, no `CREATE TABLE` guards.
2. **`Microsoft.OpenApi`:** added, isolated to the Infrastructure parser adapter behind `IOpenApiParser`.
3. **OAuth2:** client-credentials only for v1; authorization-code/implicit deferred.
4. **External `$ref` / multi-file specs:** out of scope for v1 — detected and rejected with a clear error.
5. **Node model:** one **compiled node per imported API**, named after the API, handling all its operations, generated through the existing dynamic node-package pipeline.

No blocking questions remain. Implementation can begin at §9 step 1.
