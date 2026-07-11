#!/usr/bin/env node
// Generate (or verify) the machine-readable Condition-editor handoff artifact FROM the prose TODO.
//
//   node scripts/condition-handoff.mjs          # regenerate the JSON from the TODO
//   node scripts/condition-handoff.mjs --check   # exit non-zero if the committed JSON is stale
//
// The prose TODO is the ONLY source of truth. The JSON is derived. The --check mode is the
// enforcement (run it in CI) so the canonical/derived relationship is enforced, not trusted —
// the same drift-killing rule applied to the FE/BE operator catalog, applied to our own docs.
//
// Parsed shape (strict, one line per tagged id):
//   - **<ID>** — *(<TAG>, <Phase ...>)* <text>        TAG in {BLOCK, DECIDE, LOCKED}
// Phases are parsed from "### Phase N — Title  <status emoji>" headings.

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const TODO = join(root, 'docs', 'plans', 'condition-node-editor-TODO.md');
const OUT = join(root, 'docs', 'plans', 'condition-node-editor.handoff.json');

const TAG_RE = /^- \*\*([A-Z]\d+)\*\* — \*\((BLOCK|DECIDE|LOCKED), ([^)]+)\)\* (.+)$/;
const PHASE_RE = /^### Phase (\d+) — (.+?)\s*(⏳|✅|🚧)?\s*$/;

function firstSentence(text) {
  // Trim markdown emphasis/code ticks and clip to the first sentence for a compact title.
  const plain = text.replace(/[*`]/g, '').trim();
  const m = plain.match(/^(.+?[.:])(\s|$)/);
  return (m ? m[1] : plain).replace(/[.:]$/, '').trim();
}

// Coalesce wrapped bullet continuations (indented 2+ spaces) into their bullet's logical line, so
// the full multi-line text of a tagged item is captured — but never across fenced code blocks.
function logicalLines(md) {
  const out = [];
  let inFence = false;
  for (const raw of md.split(/\r?\n/)) {
    if (/^\s*```/.test(raw)) { inFence = !inFence; out.push(raw); continue; }
    if (!inFence && /^\s{2,}\S/.test(raw) && out.length && /^- /.test(out[out.length - 1])) {
      out[out.length - 1] += ' ' + raw.trim();
    } else {
      out.push(raw);
    }
  }
  return out;
}

export function parseTodo(md) {
  const items = [];
  const phases = [];
  for (const raw of logicalLines(md)) {
    const line = raw.trimEnd();
    const tag = line.match(TAG_RE);
    if (tag) {
      const [, id, kind, phase, text] = tag;
      items.push({ id, tag: kind, phase: phase.trim(), title: firstSentence(text), text: text.trim() });
      continue;
    }
    const ph = line.match(PHASE_RE);
    if (ph) {
      const [, n, title, status] = ph;
      const done = status === '✅';
      phases.push({ phase: Number(n), title: title.trim(), status: done ? 'done' : 'todo' });
    }
  }
  items.sort((a, b) => a.id.localeCompare(b.id, undefined, { numeric: true }));
  phases.sort((a, b) => a.phase - b.phase);
  return {
    feature: 'condition-node-editor',
    branch: 'feat/condition-node-editor',
    source: 'docs/plans/condition-node-editor-TODO.md',
    generated_from_prose: true,
    counts: {
      block: items.filter((i) => i.tag === 'BLOCK').length,
      decide: items.filter((i) => i.tag === 'DECIDE').length,
      locked: items.filter((i) => i.tag === 'LOCKED').length,
    },
    items,
    phases,
  };
}

function render(md) {
  return JSON.stringify(parseTodo(md), null, 2) + '\n';
}

const check = process.argv.includes('--check');
const md = readFileSync(TODO, 'utf8');
const next = render(md);

if (check) {
  let current = '';
  try { current = readFileSync(OUT, 'utf8'); } catch { /* missing → stale */ }
  if (current !== next) {
    console.error(
      '✗ condition-node-editor.handoff.json is stale.\n' +
      '  Run: node scripts/condition-handoff.mjs');
    process.exit(1);
  }
  console.log('✓ handoff JSON is in sync with the TODO.');
} else {
  writeFileSync(OUT, next);
  const { counts } = parseTodo(md);
  console.log(`✓ wrote ${OUT}\n  ${counts.block} BLOCK · ${counts.decide} DECIDE · ${counts.locked} LOCKED`);
}
