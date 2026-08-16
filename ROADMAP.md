# Roadmap

Where Knotarium is headed. No dates — this is a self-funded project developed in the open; the
order below reflects real priorities, not promises. Feedback and PRs welcome
(see [CONTRIBUTING.md](CONTRIBUTING.md)).

## Recently shipped

- **AI nodes** — prompt, router, verify (evidence-gated), semantic diff, and an agent node whose
  tools are allow-listed workflows, plus one-shot workflow generation from a natural-language prompt.

## Near term

- **Connector packs** — curated, versioned integration bundles built on the OpenAPI importer +
  `.kgbundle` machinery (Slack, Google Sheets, Notion, …), with auth presets for common
  OAuth2/API-key patterns.
- **Human-in-the-loop** — an approval node that suspends a run until a person approves/rejects
  (with timeout escalation), plus a pending-approvals view. The suspension/resume infrastructure
  already exists.
- **Signed binaries** — code-sign the Windows installer and binaries. Both routes are already wired
  into CI (optional Authenticode via a certificate, or SignPath Foundation for open source) and
  activate the moment either is configured. The obstacle is obtaining the certificate, not the
  plumbing: since 2023 the key must sit on certified hardware, the EV certificate that would clear
  SmartScreen immediately is issued to organizations rather than individuals, and an individual
  certificate embeds the maintainer's legal identity in every binary while still earning reputation
  only slowly. SignPath Foundation is therefore the likely path. Until then the container image
  avoids the problem entirely — see the [install guide](help/pages/install.html).

## Mid term

- **Role-based access control** — today an authenticated user is effectively an administrator
  (documented in [SECURITY.md](SECURITY.md)); a real permission model is the prerequisite for
  team use.
- **PostgreSQL provider** — the database-provider seam exists and a Postgres provider is
  scaffolded; finishing it enables multi-instance deployments.
- **Community node registry** — discovery + install flow for custom node packages (the runtime
  already hot-loads packages).

## Longer term

- **Collaboration** — multi-user editing, comments, review flows on workflows.
- **Clustered execution** — queue-mode execution across multiple workers (single-instance,
  bounded-parallel today).

## Non-goals (for now)

- **SaaS offering** — Knotarium is self-hosted first; a hosted version is not planned.
- **Per-tenant isolation** — single-team instances are the supported model until RBAC and
  clustered execution exist.
