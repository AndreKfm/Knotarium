# KnotGarden — Architecture v3

## Goal

Provide an AI-facing architecture stewardship guide for this repository so new code is placed consistently, module boundaries are protected, and architectural drift is reduced during development.

This document is intentionally operational. It is meant to be followed by an AI assistant or human contributor when deciding where code belongs, what each component is allowed to do, and which dependencies are acceptable.

This document is the **KnotGarden-specific application layer** on top of the generic architecture review baseline in `docs/Architecture_Review_Guide.md`.

---

## Architecture Knowledge Sources

Use these sources in order. Higher-priority sources override lower-priority ones.

1. `architecture/system.yaml`
2. folder-local `module.yaml` manifests
3. `docs/KnotGarden_MVP_Architecture-3.md`
4. `docs/KnotGarden_MVP_Architecture-4.md`
5. `docs/KnotGarden_MVP_Architecture-2.md`
6. `docs/KnotGarden_MVP_Architecture.md`
7. project references in `Backend/*.csproj`
8. dominant observed repository pattern
9. `docs/Architecture_Review_Guide.md` as the generic baseline review protocol

Use the machine-readable manifests for the current module map, dependency boundaries, and placement rules. Use this document and the other KnotGarden-specific architecture documents for rationale, detail, and architectural intent.

If this document or another KnotGarden-specific document conflicts with the generic guide, the KnotGarden-specific document wins.

Existing violations never become the dominant pattern when they conflict with the documented rules in this file.

---

## How to Use This Document

Use `docs/Architecture_Review_Guide.md` for the generic review process, severity model, workflow, escalation rules, and reporting format.

Use this document for the KnotGarden-specific content that the generic guide intentionally does not define:

- the source-tree component map
- the current module boundaries
- the allowed dependency shape
- the placement rules for new code
- KnotGarden-specific interpretation of architectural ownership

---

## Source Tree Component Map

The entries below define the current architectural view of the repository. Each component identifies its architecture level, module name, intention, and viable change surface.

### Backend Components

| Path | Architecture Level | Module Name | Intention | Viable Changes |
|------|--------------------|-------------|-----------|----------------|
| `Backend/KnotGarden.Api` | Host / Transport / Composition Root | `KnotGarden.Api` | ASP.NET host, DI registration, HTTP endpoints, SSE transport, application bootstrap | endpoint wiring, request validation, transport DTO mapping, DI composition, host-level observability |
| `Backend/KnotGarden.Core/Contracts` | Core Contract Layer | `KnotGarden.Core.Contracts` | Shared abstractions and contracts used across backend modules | interfaces, IDs, execution contracts, stable DTO-like backend contracts |
| `Backend/KnotGarden.Core/Domain` | Core Domain Layer | `KnotGarden.Core.Domain` | Domain concepts and stable business primitives that must not depend on infrastructure | value objects, enums, stable domain models, domain rules with low volatility |
| `Backend/KnotGarden.Features/Compiler` | Application / Feature Slice | `KnotGarden.Features.Compiler` | Workflow validation and compilation into executable plans | compilation rules, diagnostics, plan construction, schema validation |
| `Backend/KnotGarden.Features/Execution` | Application / Feature Slice | `KnotGarden.Features.Execution` | Workflow execution, orchestration, journal publication, state progression | executor logic, worker logic, node-state transitions, replay-safe orchestration |
| `Backend/KnotGarden.Features/NodeEditor` | Application / Feature Slice | `KnotGarden.Features.NodeEditor` | In-app node authoring and test workflows | node-editor orchestration, package test flow, manifest validation coordination |
| `Backend/KnotGarden.Features/Nodes` | Application / Built-in Node Slice | `KnotGarden.Features.Nodes` | Built-in node implementations and node-task registry logic | built-in node behavior, registry mapping, input/output conventions |
| `Backend/KnotGarden.Infrastructure/Persistence` | Infrastructure Adapter | `KnotGarden.Infrastructure.Persistence` | EF Core, SQLite persistence, journal writers, durable storage concerns | DB access, repositories, EF mappings, provider-specific persistence |
| `Backend/KnotGarden.Infrastructure/Security` | Infrastructure Adapter | `KnotGarden.Infrastructure.Security` | Encryption, secret resolution, credential storage, security helpers | credential crypto, secret access, egress/security enforcement |
| `Backend/KnotGarden.NodeRuntime` | Runtime / Sandbox Boundary | `KnotGarden.NodeRuntime` | Dynamic node loading, declarative execution, analyzers, package runtime services | Roslyn analyzers, assembly loading, runtime registries, expression evaluation |
| `Backend/KnotGarden.Tests` | Test Layer | `KnotGarden.Tests` | Integration and feature-level regression tests for backend behavior | tests only; mirror production behavior without becoming a utility dumping ground |
| `Backend/KnotGarden.NodeRuntime.Tests` | Test Layer | `KnotGarden.NodeRuntime.Tests` | Focused tests for runtime and sandbox behavior | runtime tests only |

### Frontend Components

| Path | Architecture Level | Module Name | Intention | Viable Changes |
|------|--------------------|-------------|-----------|----------------|
| `Frontend/src/components` | Presentation Layer | `Frontend.Components` | Application UI, workflow canvas, execution inspection, visual interaction | React components, UI composition, page-level state wiring |
| `Frontend/src/node-editor` | Presentation / Authoring Layer | `Frontend.NodeEditor` | Node package authoring experience in the UI | editor UX, node package authoring flows, preview/test UI |
| `Frontend/src/utils` | Client Adapter Layer | `Frontend.Utils` | API adapters, schema mapping, client-side helpers with stable reuse | API wrappers, mapping helpers, narrow reusable client utilities |
| `Frontend/src/types.ts` | Shared Frontend Contract Layer | `Frontend.Types` | TypeScript contracts that mirror backend API payload shape | stable frontend types, mapping-friendly contract definitions |
| `Frontend/src/test` and `Frontend/src/__tests__` | Test Layer | `Frontend.Tests` | Frontend verification and regression coverage | tests only |

### Node Package Components

| Path | Architecture Level | Module Name | Intention | Viable Changes |
|------|--------------------|-------------|-----------|----------------|
| `nodes/*` | Package Extension Surface | `NodePackage.<Name>` | File-system-discovered node packages used by the runtime/editor | node manifests, declarative behavior, package-specific assets/tests |

### Non-Product Components

| Path | Architecture Level | Module Name | Intention | Viable Changes |
|------|--------------------|-------------|-----------|----------------|
| `docs` | Documentation Layer | `Docs` | Architecture, implementation planning, product direction, internal guidance | architecture docs, planning docs, contributor guidance |
| `run.ps1` | Developer Tooling | `Tooling.Run` | Local developer startup orchestration | startup automation only |

---

## Dependency Rules

These rules are binding for normal development unless the human architect explicitly decides otherwise.

### Allowed Backend Dependencies

- `KnotGarden.Api` may reference `KnotGarden.Features`, `KnotGarden.Infrastructure`, and `KnotGarden.Core`.
- `KnotGarden.Features` may reference `KnotGarden.Core`, `KnotGarden.Infrastructure`, and `KnotGarden.NodeRuntime`.
- `KnotGarden.Infrastructure` may reference `KnotGarden.Core` only.
- `KnotGarden.NodeRuntime` may reference `KnotGarden.Core` only.
- `KnotGarden.Core` must not reference any other project.

### Disallowed or Suspicious Dependencies

- `KnotGarden.Core` must not depend on `Infrastructure`, `Features`, `Api`, or `NodeRuntime`.
- `KnotGarden.NodeRuntime` must not depend on `Infrastructure`, `Features`, or `Api`.
- `KnotGarden.Infrastructure` must not depend on `Features` or `Api`.
- Frontend code must not encode backend persistence concerns directly.
- Tests may reference production modules, but production modules must not reference tests.

### Boundary Interpretation

- `KnotGarden.Api` is the composition root, not the home for core business logic.
- `KnotGarden.Features` owns use-case and orchestration logic.
- `KnotGarden.Infrastructure` owns persistence and environment-specific adapters.
- `KnotGarden.NodeRuntime` owns dynamic node runtime concerns, not application workflow policy.
- `KnotGarden.Core` owns stable contracts and domain primitives.

---

## Placement Rules for New Code

### 1. Host and Transport

Place code in `KnotGarden.Api` when it is primarily about:

- HTTP endpoint shape
- SSE transport
- DI registration
- host startup
- request/response mapping
- host-level logging or telemetry wiring

Do not place workflow execution policy or node business behavior here unless it is truly transport-specific.

### 2. Feature and Use-Case Logic

Place code in `KnotGarden.Features` when it is primarily about:

- compiling workflows
- running workflows
- authoring nodes
- implementing built-in nodes
- coordinating domain contracts to fulfill a use-case

Prefer slice-local placement over broad shared folders.

### 3. Domain and Contracts

Place code in `KnotGarden.Core` when it is:

- stable across modules
- contract-defining
- domain-oriented rather than storage-oriented
- unlikely to require infrastructure dependencies

Do not move volatile feature helpers into `Core` just to make references easier.

### 4. Persistence and Security Adapters

Place code in `KnotGarden.Infrastructure` when it is about:

- EF Core and database details
- SQLite-specific or provider-specific persistence
- credential encryption and secret storage
- environment-specific enforcement such as HTTP egress policy

Infrastructure should satisfy contracts; it should not own feature decisions.

### 5. Runtime and Package Execution

Place code in `KnotGarden.NodeRuntime` when it is about:

- assembly loading
- dynamic node package execution
- analyzers and runtime guards
- declarative execution helpers
- expression evaluation within the runtime boundary

Do not move workflow orchestration concerns here unless they are truly runtime-engine concerns.

### 6. Frontend

Place code in `Frontend/src/components` for UI composition and user-facing workflow screens.

Place code in `Frontend/src/utils` only for narrow reusable client helpers. Avoid creating broad `helpers` dumping grounds.

Place shared API contracts in `Frontend/src/types.ts` when they reflect stable backend payloads used in multiple UI areas.

### 7. Node Packages

Place node-package-specific behavior and assets under `nodes/<PackageName>` when they belong to a file-system-discovered package rather than a built-in backend node.

---

## KnotGarden-Specific Review Focus

When using the generic review guide against this repository, pay specific attention to these KnotGarden-specific risks:

- business logic drifting into `KnotGarden.Api` instead of staying in `KnotGarden.Features`
- volatile helpers being pushed into `KnotGarden.Core` only to simplify references
- `KnotGarden.Infrastructure` taking ownership of feature policy instead of adapter responsibilities
- `KnotGarden.NodeRuntime` expanding into application orchestration instead of remaining a runtime boundary
- broad shared frontend helper growth in `Frontend/src/utils`
- confusion between built-in nodes in `Backend/KnotGarden.Features/Nodes` and file-system node packages in `nodes/*`