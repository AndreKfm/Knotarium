// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

export interface Tip {
  /** A single, self-contained tip sentence. Keep it one line, actionable, and feature-accurate. */
  text: string;
}

// Single source of truth for the "Tip of the Day" card (rendered by TipOfTheDay).
// Data-only so a test can assert the set stays sane. Every tip must describe a real,
// shipped feature — see getting-started.md and the README for the same claims.
export const TIPS: Tip[] = [
  { text: 'Press ? anywhere in the editor to see every keyboard shortcut.' },
  { text: 'New here? Install a starter from the Templates gallery — Hello World or Fetch from an API run with no setup.' },
  { text: 'Reference an upstream node’s output with {{ $node.<id>.output.<field> }} in any field.' },
  { text: 'Ctrl/⌘ + F (or ⌘ + K) opens node search — jump to any node by name.' },
  { text: 'The HTTP Request node has separate success and error branches, so you can handle failures explicitly.' },
  { text: 'Every run is journaled — open a run to step through each node’s input and output, or replay it.' },
  { text: 'Inline code, database access and the AI agent are off by default. Enable them under Settings → Capabilities only for instances you trust.' },
  { text: 'File Read/Write is deny-by-default — grant specific folders under Settings → File Access.' },
  { text: 'Every run uses the active published version — including Run, which activates the version picked in the toolbar first.' },
  { text: 'Pinned node outputs are honoured on manual runs only; scheduled, webhook and polling runs always call the real thing.' },
  { text: 'Share a workflow as a .kgtpl template or a .kgbundle — secrets are referenced by slot, never embedded.' },
  { text: 'Add a schedule, webhook, or polling trigger to run a workflow automatically. Manual runs always work.' },
  { text: 'Select a group of nodes and extract them into a reusable sub-flow to keep big graphs tidy.' },
];
