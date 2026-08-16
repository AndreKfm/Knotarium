# Changelog

All notable changes to Knotarium are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each tag's commit-level notes are auto-generated on the
[Releases page](https://github.com/AndreKfm/Knotarium/releases); this file carries
the user-facing highlights per version.

## [Unreleased]

Nothing yet.

## [1.0.1] — 2026-08-16

A patch release for one startup failure found while smoke-testing the 1.0.0
container. Nothing else changed; upgrading is a straight swap.

### Fixed

- **Restarting shortly after an abrupt stop no longer refuses to start.** Only one
  executor may own a database, enforced by a heartbeat that a worker deletes when
  it shuts down cleanly. A worker *killed* instead — a container stop that reaches
  its timeout and escalates to `SIGKILL`, a power loss — never got that far, and
  left behind a registration that looked current but would never be renewed.
  Startup treated it as proof of a live worker and aborted fatally, taking the
  whole host down with it, so a container would exit on boot with nothing serving.
  Restarting within ten seconds of stopping was enough to trigger it, and the
  error named a process that no longer existed. Startup now waits for the
  abandoned heartbeat to age out instead of refusing; a worker that really is
  alive keeps renewing and is still correctly rejected.
- **Claiming the executor slot is now atomic.** Reaping expired registrations,
  confirming none are live, and inserting our own happen in one transaction. Split
  across separate steps, two executors starting together could both find the table
  empty and both register — the double-execution the guard exists to prevent.

### Added

- `Execution__WorkerHeartbeatStaleSeconds` (default `10`, range 2–120) — how long a
  worker's registration stays valid before the owning process is presumed dead.
- `Execution__StartupGuardWaitSeconds` (default `30`, range 0–300) — how long
  startup waits for another registration to expire before giving up. `0` restores
  the previous fail-fast behavior.

## [1.0.0] — 2026-08-16

First stable release. Eighteen release candidates (`v1.0.0-rc.1` through
`v1.0.0-rc.18`) preceded it, so the sections below describe the product as a whole
rather than the delta from the last candidate.

### Added

#### Workflow engine

- Node-based execution engine with conditionals, `switch`, loops, parallel
  fan-out/fan-in, and reusable sub-flows with typed inputs and outputs.
- Triggers: manual, scheduled, webhook, and polling (with change detection).
- Run-level parallelism with bounded dispatch and backpressure.
- Error handling: a global error workflow, on-failure alert channels (webhook,
  Slack, email), and a dead-letter view with discard and replay.

#### Nodes

- Protocol and I/O: HTTP request, database query, file read/write, SMTP send,
  IMAP fetch, and message queues.
- AI/LLM: prompt, router, agent, plus `AI Verify` and `AI Diff`, which turn a
  model's answer into structured data that a deterministic rule of your own
  accepts or rejects. The agent node's tools are your own allow-listed workflows.
- Inline C# compiled at runtime with Roslyn, and custom node packages built the
  same way.

#### Editor

- React Flow canvas with proximity auto-connect, insert-on-edge, undo/redo,
  snap-to-grid, multi-select align/distribute, and one-key auto-layout.
- Fuzzy node search palette, sticky notes and groups, sub-flow drill-down, and
  level-of-detail rendering at low zoom.
- Per-node property inspector with typed outputs and `{{ }}` reference
  autocompletion; the condition editor resolves `{{ $node.… }}` against the last
  real run so a branch can be evaluated before publishing.
- Version history with diff and restore. Every activation is additionally
  recorded — who activated which version, when, why, and what it replaced —
  and is queryable through `GET /api/workflows/{id}/activation-history`, with
  `GET /api/workflows/{id}/active-version-at` answering which version was live
  at a given instant. No interface surfaces this yet; it is API-only for now.

#### Runs

- Time-travel run inspection: step through any past run and see each node's
  inputs, outputs, and the variables before and after it ran.
- Live node-status painting on the canvas while a workflow executes.

#### Portability

- Templates (`.kgtpl`) with install-time parameters, a built-in gallery, and a
  persisted user template library.
- Integration bundles (`.kgbundle`) for exporting and installing node sets.
- Full-instance backup and restore (`.kgbak`), passphrase-encrypted.

#### Security

- Cookie-based authentication with multi-user support, secure by default.
- Deny-by-default file-access policy; the code-execution and database
  capabilities are off until switched on.
- Outbound HTTP checked against an SSRF egress policy.
- Optional out-of-process sandbox for user-authored C#.
- Per-workflow credentials, encrypted at rest with an auto-generated key.

#### Deployment

- One self-contained .NET process serving both the API and the UI, with an
  embedded SQLite database — no Node.js, Python, or separate database server.
- Storage sits behind a pluggable database-provider seam; SQLite is the default
  and a Postgres provider is scaffolded.
- Distribution: Windows installer (registers a service), zero-install Windows
  zip, self-contained Linux tarball, and a multi-arch container image
  (`amd64` + `arm64`) on GHCR, plus a Docker Compose quickstart.
- The complete documentation ships offline inside every instance at `/help`.

### Deprecated

- The Docker Compose shorthand variables were renamed from `KG_*` to
  `KNOTARIUM_*` (`KNOTARIUM_ENCRYPTION_KEY`, `KNOTARIUM_AUTH_ENABLED`,
  `KNOTARIUM_SIGNING_KEY`), the `KG_` prefix being a leftover from the project's
  former name. The Compose file falls back to the old spellings, so an existing
  `.env` keeps working, and setting both is harmless — the `KNOTARIUM_` one
  wins. The fallback will be removed in a future release. Note that these are
  Compose interpolation variables, not application settings; the application's
  own configuration names are unchanged.

### Known limitations

- Release binaries are **not code-signed**, so Windows SmartScreen and Defender
  may warn about an unknown publisher or flag the installer as a false positive.
  Verify the published SHA-256 for each artifact. This is a structural obstacle
  for a single maintainer rather than an oversight — the reasoning, and why the
  container image avoids it entirely, is in the
  [install guide](help/pages/install.html). See also the [README](README.md#download).
- macOS builds are not published; run from source or use the container image.

[Unreleased]: https://github.com/AndreKfm/Knotarium/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/AndreKfm/Knotarium/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/AndreKfm/Knotarium/releases/tag/v1.0.0
