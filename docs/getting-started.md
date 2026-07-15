# Getting Started

This guide takes you from a running instance to your first workflow that calls an API
and logs the result — in about five minutes.

If you haven't started Knotarium yet, see the [Quickstart in the README](../README.md#quickstart-docker).

---

## 1. Open the app and create your admin account

Browse to **http://localhost:43120**. Auth is on by default, so the first visit asks you to
create an **admin account** — this is a local account on your instance, not a hosted service.

> Just kicking the tires? Start with `KG_AUTH_ENABLED=false docker compose up --build` to skip login.

## 2. Install a starter template

The fastest way to see a working run is to install one of the built-in starters instead of
building from scratch.

1. Open the **Templates** gallery.
2. Pick **Hello World** (a manual trigger → log → end) or **Fetch from an API**
   (an HTTP request whose response is logged).
3. Click **Install**. The template becomes a new workflow you can open on the canvas.

Both starters run with no extra configuration — no credentials, no capabilities to enable.

## 3. Understand what you're looking at

A workflow is a **graph of nodes** connected by **edges**:

- **Start / End** — where a manually-triggered run begins and finishes.
- **Nodes** do the work — HTTP request, log, delay, set a variable, conditionals, loops,
  database query, file read/write, email, inline C# code, and more.
- **Edges** carry the flow (and data) from one node's output port to the next node's input.
  A node references an upstream node's output with `{{ $node.<id>.output.<field> }}`.

Some nodes **branch**: the HTTP Request node has a **success** and an **error** port, so you can
handle failures explicitly (the *Fetch from an API* starter logs each).

## 4. Run it and watch the execution

1. With a workflow open, click **Run**.
2. Open the **execution / run view** to watch it step through the nodes. Each run is
   **journaled** — you can see each node's input and output, and step through or replay the run.

For *Fetch from an API*, the log line shows the HTTP status and the response body pulled from the
live call.

## 5. Make it your own

- **Swap the URL** in the HTTP Request node for your own API.
- **Add a trigger** so it runs on its own: a **cron schedule**, a **webhook**, or **polling**.
  Manual runs always work; webhook/schedule triggers fire only when the workflow is **enabled**.
- **Version it**: publish a new version and **activate** it — triggers use the active version,
  while manual runs use whatever you're editing.

## 6. Working with credentials and secrets

Nodes that need a password, token, or API key read from the **credential store**. Credentials are
encrypted at rest with a key that lives outside the database (auto-generated on first use, or
supplied via `Security__Credentials__EncryptionKeyBase64`). Templates and bundles reference
credentials by **slot**, so sharing a workflow never leaks a secret.

## 7. A note on capabilities and safety

Knotarium can **execute code and touch the filesystem**, so a few powerful features are locked
down by default:

- **Inline-code** and **database** nodes are **off by default** — enable them under
  *Settings → Capabilities* only for instances whose workflows you trust.
- **File Read/Write** is **deny-by-default**; grant specific directories under
  *Settings → File Access*.

See the [Security section of the README](../README.md#security) before exposing an instance.

## Where to go next

- **Templates & bundles** — export a workflow as a shareable `.kgtpl`, or a multi-package `.kgbundle`.
- **Reliability** — a global error workflow, a dead-letter queue with replay, and failure-alert
  channels (webhook / Slack / email).
- **Import** — generate nodes from an OpenAPI spec, or a whole workflow from a natural-language
  description (AI).
- **Configuration** — see the [Configuration table in the README](../README.md#configuration).

Questions or a rough edge? Open an issue.
