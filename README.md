# KnotGarden

**Self-hosted, visual workflow automation.** Build automations as a graph of nodes on a canvas — HTTP, database, files, email, conditionals, loops, sub-flows, scheduled and webhook triggers — then run, version, and monitor them. One .NET process serves the API and the UI; data lives in a local SQLite file.

> **Status:** early / active development. The project name is provisional and may change.

<!-- Add a dashboard / canvas screenshot or GIF here. -->

---

## Quickstart (Docker)

```bash
docker compose up --build
```

Open **http://localhost:8080**. On first run you create an admin account. The SQLite database **and** the auto-generated credential-encryption key persist in the `knotgarden-data` volume, so credentials survive restarts with no extra setup.

> Bringing your own encryption key (e.g. to share one across instances)? `export KG_ENCRYPTION_KEY="$(openssl rand -base64 32)"` before `docker compose up`.

> Want to skip login for a throwaway local try? `KG_AUTH_ENABLED=false docker compose up --build`.

## Run from source

Prerequisites: **.NET 10 SDK** and **Node 22+**.

```bash
# Terminal 1 — backend (API on http://localhost:5232)
dotnet run --project Backend/KnotGarden.Api

# Terminal 2 — frontend (UI on http://localhost:5280, proxies /api to the backend)
cd Frontend && npm install && npm run dev
```

For a productive single-folder build (backend + bundled UI), Windows users can run `./publish.ps1`.

## Configuration

Set via environment variables (double-underscore = nested config):

| Variable | Purpose |
|---|---|
| `Security__Credentials__EncryptionKeyBase64` | 32-byte base64 key encrypting stored credentials. Optional — auto-generated into the data directory and reused if unset. Set it to bring your own key; then keep it stable, or saved credentials can't be decrypted. |
| `Security__PackageSigning__HostPrivateKeyBase64` | Base64 key to *sign* exported bundles (only needed if you use bundle export). |
| `Auth__Enabled` | Cookie auth. Default `true` (first run creates the admin). |
| `Storage__DataDirectory` | Machine-wide home for the SQLite DB + auto-generated credential key (Docker sets `/data`; defaults to `%ProgramData%\KnotGarden` on Windows, `CommonApplicationData/KnotGarden` elsewhere). Set it so a Windows service and an interactive run share one DB + key. |
| `Database__ConnectionString` | Overrides the SQLite location entirely (e.g. a Postgres connection string). Otherwise the DB lives at `<DataDirectory>/KnotGarden.db`. |

For local development, copy `Backend/KnotGarden.Api/appsettings.Development.json.example` to `appsettings.Development.json` (gitignored) and fill in your own keys.

## Features

- **Visual canvas** — drag/connect nodes, auto-layout, undo/redo, node search, sub-flow drill-down, sticky notes & groups.
- **Nodes** — HTTP, database query, file read/write, email (SMTP/IMAP), MQTT publish, conditionals, loops (incl. parallel), delay, inline C# code, and custom node packages.
- **Triggers** — manual, webhook, cron schedule, polling, error-handler, and event-driven device blocks.
- **Execution** — journaled runs with a step-through / replay visualizer; runtime versioning (publish + activate).
- **Reliability** — global error workflow, dead-letter queue with replay, failure-alert channels (webhook/Slack/email).
- **Portability** — export/import single-workflow templates and multi-package bundles; full encrypted backup/restore; import from an OpenAPI spec.
- **AI** — generate a workflow from a natural-language description.
- **Security** — deny-by-default file-access policy, capability gating for code/database nodes, cookie auth, and privileged-node warnings on import (see below).

## Security

This is an automation engine that can **execute code and touch the filesystem**, so treat a running instance as privileged:

- The **inline-code** and **database** node capabilities are **off by default** — enable them in *Settings → Capabilities* only if you trust every workflow on the instance.
- **File Read/Write** is **deny-by-default**; grant specific directories in *Settings → File Access* (path-traversal and symlink escapes are blocked server-side).
- **Do not expose an instance directly to the public internet** without putting it behind your own authentication/reverse-proxy and a threat model.

Found a vulnerability? Please see `SECURITY.md` (coming soon) rather than opening a public issue.

## Tech

.NET 10 (modular-monolith backend, SQLite) · React 19 + Vite + React Flow (frontend). ~1,750 automated tests.

## License

[Apache License 2.0](LICENSE). Contributions are welcome — a contributor process (and CLA) will be added shortly.
