// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0
//
// Regenerates help/assets/search-index.json from the help pages.
//
// The help site itself has no build step — it ships as plain files. This script is a DEVELOPMENT
// convenience so the search index does not have to be maintained by hand; its output is committed
// alongside the pages. Run it after adding or editing a page:
//
//     node scripts/build-help-index.mjs
//
// It parses our own hand-written markup with regexes rather than pulling in an HTML parser. That is
// acceptable precisely because the input is not arbitrary HTML: every help page follows the same
// shell, verified by the structural checks below, and the script fails loudly if one does not.

import { readFile, writeFile, access } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const helpDir = join(repoRoot, 'help');
const pagesDir = join(helpDir, 'pages');

/** Mirrors the slugify() in help/assets/help.js — anchors must agree or search links land nowhere. */
function slugify(text) {
  return text.toLowerCase().trim()
    .replace(/[^\w\s-]/g, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-');
}

function decodeEntities(s) {
  const named = {
    amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ',
    rsaquo: '›', lsaquo: '‹', mdash: '—', ndash: '–', hellip: '…',
  };
  return s
    .replace(/&#(\d+);/g, (_, n) => String.fromCodePoint(Number(n)))
    .replace(/&([a-zA-Z]+);/g, (m, name) => (name in named ? named[name] : m));
}

/**
 * Visible text of a BODY fragment, with block boundaries preserved as spaces so words from
 * adjacent elements do not run together in a search snippet.
 */
function toText(html) {
  return decodeEntities(
    html
      .replace(/<(script|style|svg)\b[\s\S]*?<\/\1>/gi, ' ')
      .replace(/<!--[\s\S]*?-->/g, ' ')
      .replace(/<[^>]+>/g, ' ')
  ).replace(/\s+/g, ' ').trim();
}

/**
 * Visible text of a HEADING, matching the browser's `textContent` exactly — tags contribute
 * NOTHING, not even a space. This must not use toText(): for a heading like
 * `(<code>openapi.&lt;slug&gt;</code>)` the space-inserting version yields a trailing separator that
 * slugifies to a phantom `-`, so the anchor in the index would not match the id help.js generates
 * at runtime and the link would silently go nowhere.
 */
function headingText(html) {
  return decodeEntities(
    html
      .replace(/<(script|style|svg)\b[\s\S]*?<\/\1>/gi, '')
      .replace(/<!--[\s\S]*?-->/g, '')
      .replace(/<[^>]+>/g, '')
  ).replace(/\s+/g, ' ').trim();
}

/** Loads help/assets/nav.js by evaluating it against a stub global. */
async function loadNav() {
  const source = await readFile(join(helpDir, 'assets', 'nav.js'), 'utf8');
  const scope = {};
  // eslint-disable-next-line no-new-func
  new Function('window', source)(scope);
  if (!Array.isArray(scope.KG_NAV)) throw new Error('nav.js did not define window.KG_NAV as an array.');
  return scope.KG_NAV;
}

async function exists(path) {
  try { await access(path); return true; } catch { return false; }
}

const nav = await loadNav();
const entries = [];
const problems = [];

for (const group of nav) {
  for (const page of group.pages) {
    const filePath = join(pagesDir, page.file);

    if (page.pending) {
      if (await exists(filePath)) {
        problems.push(`${page.file} exists but is still marked pending in nav.js — drop the flag.`);
      }
      continue;
    }

    if (!(await exists(filePath))) {
      problems.push(`${page.file} is listed in nav.js but the file is missing.`);
      continue;
    }

    const html = await readFile(filePath, 'utf8');

    const article = html.match(/<article class="kg-article">([\s\S]*?)<\/article>/);
    if (!article) {
      problems.push(`${page.file} has no <article class="kg-article"> block — skipped.`);
      continue;
    }

    // Drop the previous/next nav so its link text does not pollute the last section's body.
    const body = article[1].replace(/<nav id="kg-pagenav"[\s\S]*?<\/nav>/g, ' ');

    // The lead paragraph indexes as the page-level entry, so a search for the page's subject
    // matches the page itself and not just whichever section happens to repeat the word.
    const lead = body.match(/<p class="kg-lead">([\s\S]*?)<\/p>/);
    entries.push({
      title: page.title,
      section: group.title,
      url: page.file,
      text: lead ? toText(lead[1]) : toText(body).slice(0, 400),
    });

    // Split on headings: everything until the next h2/h3 belongs to that heading.
    const parts = body.split(/<(h[23])\b[^>]*>([\s\S]*?)<\/\1>/);
    // parts = [before, tag, headingHtml, content, tag, headingHtml, content, ...]
    for (let i = 1; i < parts.length; i += 3) {
      const headingHtml = parts[i + 1] ?? '';
      const content = parts[i + 2] ?? '';
      const heading = headingText(headingHtml).replace(/#$/, '').trim();
      if (!heading) continue;

      const text = toText(content);
      entries.push({
        title: heading,
        section: `${group.title} · ${page.title}`,
        url: `${page.file}#${slugify(heading)}`,
        // A long section is truncated: the index is downloaded on first keystroke, and full page
        // text would balloon it for no ranking benefit.
        text: text.slice(0, 600),
      });
    }
  }
}

const outPath = join(helpDir, 'assets', 'search-index.json');
await writeFile(outPath, JSON.stringify(entries, null, 0) + '\n', 'utf8');

const sizeKb = (Buffer.byteLength(JSON.stringify(entries)) / 1024).toFixed(1);
console.log(`Wrote ${entries.length} entries to help/assets/search-index.json (${sizeKb} kB).`);

if (problems.length) {
  console.error('\nProblems found:');
  for (const p of problems) console.error(`  - ${p}`);
  process.exitCode = 1;
}
