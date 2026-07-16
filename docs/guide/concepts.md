# Core concepts

A quick tour of the building blocks. If you haven't run anything yet, start with
[Install & first workflow](/getting-started) and come back here to understand what you built.

## Workflows, nodes, and edges

A **workflow** is a graph. **Nodes** do the work; **edges** connect one node's *output port* to
the next node's *input port* and carry the flow (and data) along.

- **Start / End** — where a run begins and finishes.
- **Nodes** — HTTP request, log, delay, set a variable, conditionals, loops, database query,
  file read/write, email, inline C# code, the AI nodes, and more.
- **Ports** — most nodes emit a single `result`. **Branch nodes** have several: the HTTP
  Request node has `success` and `error`; the Condition node has `true`/`false`; the AI Router
  node has one port per category.

### Referencing data between nodes

A node reads an upstream node's output with an expression:

```
{{ $node.<node-id>.output.<field> }}
```

You can also read **variables** with `$variables.<name>` (set by a *Set Variable* node).
In the editor, each node shows its outputs as chips you can **drag** into a field, or click to
**promote** to a named variable — no need to memorise field names.

## Triggers and runs

A **run** is one execution of a workflow. Runs start from a **trigger**:

- **Manual** — you click *Run* (or fire it via the API).
- **Webhook** — an inbound HTTP call.
- **Schedule** — a cron-like timer.
- **Polling** — periodically checks a source and runs only on new data.
- **Event** — an inbound signal (device / external system integrations).

Automatic triggers only fire when the instance is **armed** (a global runtime switch) and the
workflow is **enabled** — a manual run works either way.

### The run timeline

Every run is **journaled** node-by-node on a single-writer engine, so the execution view shows
exactly what happened: which nodes ran, their status, outputs, and any failure. From there you
can **replay** a run from a chosen node, or inspect a failed run in the dead-letter view.

## Versions, publishing, and activation

- **Draft** — the workflow you edit on the canvas.
- **Publish** — freezes an immutable **version**. Automatic triggers run the published
  (active) version; the version history is your audit + rollback trail.
- **Enabled / disabled** — a per-workflow switch that gates webhook/schedule/poll triggers
  (and cooperatively cancels in-flight runs when you disable it). Manual runs are unaffected.

## Security & capabilities

Knotarium is **secure by default**. The most privileged things a node can do are gated and
**off until an administrator turns them on** under **Settings → Capabilities**:

- **Code execution** — the inline C# node and compiled custom packages.
- **Database** — the database-query node.
- **AI agent** — the AI Agent node's tool-use loop (see [AI](/guide/ai)).

Two more policies apply regardless:

- **File access** — the file nodes are deny-by-default; you grant specific directories under
  **Settings → File Access** (traversal- and symlink-safe).
- **Egress** — outbound HTTP is policed against an allowlist / SSRF rules; AI providers use the
  same policed client.

When you install a template or an integration bundle that contains a privileged node, the
importer flags it and asks you to acknowledge before it's installed.

## Reusing work

- **Templates** — export a single workflow, or install one from the built-in gallery.
- **Bundles** — package several workflows + node packages as a signed integration library.
- **Sub-flows** — call one workflow from another as a reusable step, with typed inputs/outputs.
- **Backups** — a passphrase-encrypted snapshot of the whole instance.
