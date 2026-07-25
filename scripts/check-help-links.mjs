// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0
//
// Verifies every internal link and asset reference in the offline help resolves to a real file,
// and that every in-page anchor (#foo) matches a heading the runtime will actually generate.
//
//     node scripts/check-help-links.mjs
//
// Exists because the help has no build step: without this, a renamed page or a retitled heading
// breaks a link silently, and offline documentation has no server to report a 404. Run it with
// build-help-index.mjs after editing pages.

import { readFile, access, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve, relative } from 'node:path';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const helpDir = join(repoRoot, 'help');

/** Must stay identical to slugify() in help/assets/help.js. */
function slugify(text) {
  return text.toLowerCase().trim()
    .replace(/[^\w\s-]/g, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-');
}

/*
 * Entities must be DECODED, not blanked. The browser slugifies `h.textContent`, where
 * `openapi.&lt;slug&gt;` has already become `openapi.<slug>` — slugify then drops the angle
 * brackets and the dot, giving `openapislug`. Replacing entities with a space instead yields
 * `openapi-slug`, so the checker would report a mismatch on a heading that actually resolves fine.
 * Keep this in step with toText() in build-help-index.mjs.
 */
const NAMED_ENTITIES = {
  amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ',
  rsaquo: '›', lsaquo: '‹', mdash: '—', ndash: '–', hellip: '…',
};

function stripTags(html) {
  return html
    .replace(/<(script|style|svg)\b[\s\S]*?<\/\1>/gi, ' ')
    .replace(/<[^>]+>/g, '')
    .replace(/&#(\d+);/g, (_, n) => String.fromCodePoint(Number(n)))
    .replace(/&([a-zA-Z]+);/g, (m, name) => (name in NAMED_ENTITIES ? NAMED_ENTITIES[name] : m))
    .replace(/\s+/g, ' ')
    .trim();
}

async function exists(p) {
  try { await access(p); return true; } catch { return false; }
}

async function htmlFiles(dir) {
  const out = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...await htmlFiles(full));
    else if (entry.name.endsWith('.html')) out.push(full);
  }
  return out;
}

/**
 * Every id a link can legitimately target: ids written into the markup (the skip-link's #kg-content,
 * the layout landmarks) plus the ids help.js generates at runtime for h2/h3 — including its
 * duplicate-disambiguation suffixes, so a page with two "Fields" headings is checked the way the
 * browser will actually resolve it.
 */
function headingIds(html) {
  const ids = new Set();
  for (const m of html.matchAll(/\sid="([^"]+)"/g)) ids.add(m[1]);

  const used = new Map();
  const re = /<(h[23])\b[^>]*>([\s\S]*?)<\/\1>/g;
  let m;
  while ((m = re.exec(html))) {
    const explicit = m[0].match(/\sid="([^"]+)"/);
    if (explicit) { ids.add(explicit[1]); continue; }
    const base = slugify(stripTags(m[2]));
    if (!base) continue;
    const n = (used.get(base) ?? 0) + 1;
    used.set(base, n);
    ids.add(n > 1 ? `${base}-${n}` : base);
  }
  return ids;
}

const files = await htmlFiles(helpDir);
const problems = [];
let checked = 0;

for (const file of files) {
  const html = await readFile(file, 'utf8');
  const ids = headingIds(html);
  const here = dirname(file);

  const refs = [...html.matchAll(/(?:href|src)="([^"]+)"/g)].map(m => m[1]);

  for (const ref of refs) {
    if (/^(https?:|mailto:|data:|#$)/.test(ref)) continue;
    checked++;

    if (ref.startsWith('#')) {
      const id = decodeURIComponent(ref.slice(1));
      if (!ids.has(id)) problems.push(`${relative(repoRoot, file)} -> ${ref} (no such heading on this page)`);
      continue;
    }

    const [pathPart, hash] = ref.split('#');
    const target = resolve(here, pathPart);
    if (!(await exists(target))) {
      problems.push(`${relative(repoRoot, file)} -> ${ref} (file not found)`);
      continue;
    }
    if (hash && target.endsWith('.html')) {
      const targetIds = headingIds(await readFile(target, 'utf8'));
      if (!targetIds.has(decodeURIComponent(hash))) {
        problems.push(`${relative(repoRoot, file)} -> ${ref} (target has no heading "#${hash}")`);
      }
    }
  }
}

// The search index points at anchors too, so a stale index is a broken link by another name.
const indexPath = join(helpDir, 'assets', 'search-index.json');
if (await exists(indexPath)) {
  const index = JSON.parse(await readFile(indexPath, 'utf8'));
  for (const entry of index) {
    const [pathPart, hash] = entry.url.split('#');
    const target = join(helpDir, 'pages', pathPart);
    checked++;
    if (!(await exists(target))) {
      problems.push(`search-index.json -> ${entry.url} (file not found)`);
    } else if (hash) {
      const targetIds = headingIds(await readFile(target, 'utf8'));
      if (!targetIds.has(hash)) problems.push(`search-index.json -> ${entry.url} (no matching heading — regenerate the index)`);
    }
  }
}

console.log(`Checked ${checked} references across ${files.length} help pages.`);
if (problems.length) {
  console.error(`\n${problems.length} broken reference(s):`);
  for (const p of problems) console.error(`  - ${p}`);
  process.exitCode = 1;
} else {
  console.log('All internal links and anchors resolve.');
}
