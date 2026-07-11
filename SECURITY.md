# Security Policy

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report privately via GitHub's **[Private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)**
(the *Security* tab → *Report a vulnerability*), or by email to `<SECURITY-CONTACT>`.

Include what you did, what you observed, and ideally a minimal reproduction. We aim to acknowledge reports
promptly. This is an early-stage project maintained on a best-effort basis — please set expectations accordingly.

## Supported versions

Only the latest `main` is supported during early development. There are no security backports yet.

---

## Threat model (read this before you deploy)

Knotarium is a **workflow engine that can execute code and reach the host**: it runs inline C# scripts,
arbitrary SQL, filesystem reads/writes, outbound HTTP, and email/MQ. The single most important consequence:

> **Anyone who can author, import, or trigger a workflow can potentially run code on the host.**
> The real trust boundary is *who can create/run workflows*, not *which nodes exist*.

Design for that. Concretely:

- **Do not expose an instance directly to the public internet.** Put it behind your own
  authentication and a reverse proxy (TLS termination, rate limiting). Cookie auth is on by default,
  but it is not a substitute for network-level controls.
- **Run the host process with least privilege** (dedicated user, minimal filesystem access). In-app
  controls limit *who can author a dangerous node*; OS permissions limit the *blast radius* if they do.
- **Treat imported templates/bundles and AI-generated workflows as untrusted input.** The importer warns
  when a payload contains privileged nodes and requires explicit acknowledgement — heed it.

### Built-in controls

- **File access** — the File Read/Write nodes are **deny-by-default**. Grant specific directories in
  *Settings → File Access*; path traversal and symlink escapes are resolved and blocked server-side, and a
  free-space reserve caps writes.
- **Capabilities** — the **inline-code** and **database** node capabilities are **off by default**
  (*Settings → Capabilities*). Enable only what you trust every workflow to use.
- **Import warnings** — templates require confirmation, and bundle installs are refused until privileged
  nodes are acknowledged.
- **Credentials at rest** — stored credentials are encrypted with a key you supply
  (`Security__Credentials__EncryptionKeyBase64`). Keep it out of version control and stable across restarts.
- **Auth** — cookie-based login gates the management API; the first run creates an admin.

### Known limitations (current stage)

- **Role model is minimal.** A per-role permission system (RBAC) is not implemented yet, so in an
  auth-enabled instance an authenticated user is effectively an administrator. A first admin-only gate exists
  only on the security-settings mutations. Do not rely on in-app roles for multi-user isolation today.
- **Single-instance, SQLite.** No multi-tenant isolation or clustered/queue-mode hardening yet.
- **Inline code / SQL are not sandboxed** by design (they need the runtime compiler). The capability switch
  gates *whether they run at all*, not *what they can do* once enabled.

## Scope

In scope: authentication/authorization bypass, path-traversal or sandbox escapes in the file-access guard,
credential disclosure, injection, and privilege escalation beyond the documented trust boundary.

Out of scope: the documented, by-design ability of an authorized workflow author to run code once the
corresponding capability is explicitly enabled; and issues that require an already-privileged local account.
