# Step C1: Minimal APIs & Server-Sent Events

## Goal
Implement modern backend Minimal API endpoints for workflow, execution, and credential administration, and build the real-time Event stream utilizing SSE short polling.

## Proposed Changes

### Minimal API Endpoint Registry
Define and map all load-bearing API routing in `Program.cs`:
- **Workflow CRUD**: `GET`, `POST`, `PUT`, `DELETE` operations on `/api/workflows` and `/api/workflows/{id}`.
- **Workflow Versions**: `POST /api/workflows/{id}/versions` (saving dynamic drafts) and `POST /api/workflows/{id}/publish` (pinning active compiled versions) (§3).
- **Execution Start**: `POST /api/executions` (starting a background workflow execution) (§4).
- **Credential CRUD**: `GET`, `POST`, `DELETE` on `/api/credentials` (managing secure secret references) (§3, §11).
- **Node Packages**: `GET /api/node-packages` (lists installed extensions) and `POST /api/node-packages/install` (verifies signatures and installs ZIP packages) (§5).

### Server-Sent Events Engine
Build the real-time streaming channel at `GET /api/executions/{id}/events`:
- **Short Polling**: Query the `ExecutionJournal` database table at a **fixed interval of 100ms** by default (§9). Database notification triggers are deferred until Postgres provider migration occurs behind `IExecutionEventSource` abstractions (§9).
- Re-read missing event sequences upon reconnection using standard W3C `Last-Event-ID` request headers.

---

## Constraints from Architecture
- **Event Ordering**: Events streamed over SSE must perfectly match the database journal sequence index to allow robust state tracking on browsers (§9).
- **Endpoint Security**: The package install API (`/api/node-packages/install`) must verify cryptographically valid signatures prior to execution context registry (§5, §13).
- **Encryption Key Isolation**: Credential endpoints must never return raw secret values; they must perform operations strictly against key references (§11).
