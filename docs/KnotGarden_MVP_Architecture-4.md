# KnotGarden — Architecture v4

## Goal

Capture the product-direction framing for KnotGarden beyond the baseline MVP engine and editor work. This version focuses on what KnotGarden should feel like as a product, not only how it is implemented.

## Positioning

KnotGarden is closer to **n8n** than to **Node-RED**, but it is not fully either one yet.

- **n8n direction:** workflow execution, persistence, credentials, execution history, triggers, built-in automation nodes, and product-oriented run inspection.
- **Node-RED direction:** a more technical low-code runtime/editor for wiring messages, devices, APIs, and transformations.

KnotGarden currently trends toward the automation-product shape rather than the technical flow-runtime shape. The platform already centers on workflows, runs, node outcomes, persistence, and execution tracking instead of generic message wiring.

What still separates KnotGarden from a more complete automation product is mostly product surface and operator experience rather than the core engine direction.

## Product Direction

If KnotGarden should feel more like an automation product, the next leverage points are:

1. Make results, inputs, and mappings first-class in the UI.
2. Add retries, per-node error handling, and resumability as product features.
3. Improve credentials and integrations UX so HTTP and API automation feels native, not technical.
4. Add richer trigger primitives like cron, webhook, email, queue, and app connectors.
5. Shift product copy and UI language away from "node runtime/editor" toward "automations", "runs", "results", and "operations".

## Repo and Package Naming

Different contexts need different naming variants.

**Brand / Marketing:** `Knot Garden` (with a space)

- Website, logo, documentation, and pitch material.
- Example: "Knot Garden is an open-source workflow automation tool."

**GitHub Organization / Repository:** several common options are acceptable.

- `knotgarden` — lowercase, one word, and the most common pattern in OSS.
- `knot-garden` — lowercase with a hyphen, also very common and easy to read.
- `KnotGarden` — PascalCase, valid, but less common in open-source projects.

**npm / PyPI / Crates:** package registry conventions generally require lowercase names.

- npm: `knotgarden` or `knot-garden`.
- PyPI: `knot-garden` for the package name. `snake_case` should be reserved for the Python import name when needed.
- Crates.io: `knot-garden` or `knotgarden`.

**CLI Command:** keep it short and easy to type.

- `knot` — preferred if available.
- `kg` — very short, but fairly generic.
- `knotgarden` — longer, but unambiguous.

**Domain:** no spaces, because HTTP and DNS do not allow them.

- `knotgarden.io`
- `knotgarden.dev`
- `knotgarden.com`

## Working Summary

KnotGarden should be treated as an **early workflow automation platform with a developer-first editor**. The engine and architecture already support that trajectory. The next meaningful gains come from improving product ergonomics, execution visibility, integration experience, and user-facing language.