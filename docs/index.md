---
layout: home

hero:
  name: Knotarium
  text: Self-hosted visual workflow automation
  tagline: Build automations as a graph of typed nodes — HTTP, databases, files, email, and AI — then run, version, and monitor them. One process serves the API and the UI; your data stays on your machine.
  actions:
    - theme: brand
      text: Get started
      link: /getting-started
    - theme: alt
      text: Core concepts
      link: /guide/concepts
    - theme: alt
      text: GitHub
      link: https://github.com/AndreKfm/Knotarium

features:
  - title: Visual & typed
    details: Drag nodes onto a canvas and wire their output ports to the next node's inputs. Reference upstream data with expressions; the editor surfaces each node's outputs as draggable chips.
  - title: Journaled & replayable
    details: Every run is recorded node-by-node on a single-writer engine, so you can inspect the timeline, replay from a node, and trust that what ran is what you see.
  - title: AI nodes
    details: An AI provider you configure once powers prompt, routing, evidence-check, and semantic-diff nodes — plus an opt-in AI Agent that calls your own workflows as tools.
  - title: Secure by default
    details: Privileged capabilities (code execution, database, filesystem, the AI agent) are off until an administrator enables them; egress and file access are policed.
---
