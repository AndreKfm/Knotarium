# Authoring help pages

The help under `help/` is hand-written static HTML with **no build step**. It is copied verbatim
into `wwwroot/help` by `publish.ps1` and the `Dockerfile`, and the backend serves it at `/help`.

## Adding a page

1. Copy `pages/_TEMPLATE.html` to `pages/<name>.html`.
2. Set `<title>`, the `<meta name="description">`, and `<body data-kg-page="<name>.html">`.
3. Write the content inside `<article class="kg-article">`.
4. In `assets/nav.js`, find the page's entry and **remove `pending: true`**.
5. Regenerate and verify:

   ```bash
   node scripts/build-help-index.mjs
   node scripts/check-help-links.mjs
   ```

Do **not** hand-write a `<header>`, the sidebar, the outline or the previous/next links — `help.js`
generates all of them. Duplicating that markup is how a 40-page site drifts out of sync.

## The rules that matter

**Accuracy beats completeness.** Every default, field name and limit must come from the source, not
from `README.md` and not from memory. Where the README and the code disagree, the code wins and the
discrepancy is worth calling out in the prose. The manifests under `nodes/` are a stale second copy —
the authoritative node catalog is
`Backend/Knotarium.Features.Compiler/InMemoryNodePackageManifestProvider.cs`.

**Screens have no URLs.** Navigation lives in React state, not the address bar. Never write "go to
`/settings`". Write a click path with `<span class="kg-path">Settings &rsaquo; Capabilities</span>`.

**Write prose, not fragments.** Complete sentences, technical terms spelled out. Tables carry short
enumerable facts (name, type, default); the explanation goes in the surrounding paragraphs, not
crammed into a cell. No arrow chains like `A → B → fails`.

**Say what breaks.** The useful half of a setting's documentation is what happens when it is wrong:
which failure it causes, whether it fails loudly or silently, and what to do about it.

## Markup vocabulary

| Pattern | Use |
|---|---|
| `<p class="kg-lead">` | One lead paragraph directly under the `<h1>`. Also becomes the page's search-index summary. |
| `<h2>` / `<h3>` | Sections. `help.js` generates the ids and the outline — never write `id=` yourself. |
| `<div class="kg-table-wrap"><table>` | **Always** wrap tables; this is what lets them scroll on a phone instead of breaking the layout. |
| `<table class="kg-table-keys">` | Adds long-key wrapping. Use for configuration/field tables. |
| `<div class="kg-note">` | Neutral aside. Variants: `is-tip`, `is-warning`, `is-danger`. |
| `<span class="kg-note-label">` | First child of a note — a short sentence-case label, not a generic "Note". |
| `<span class="kg-path">A &rsaquo; B</span>` | A location in the UI. |
| `<kbd>Ctrl</kbd>` | Keys. |
| `<span class="kg-pill is-on\|is-off\|is-danger\|is-warning\|is-info">` | Short status: On by default, Off by default, Privileged. |
| `<div class="kg-cards"><a class="kg-card">` | Links onward at the end of a page. |
| `<figure class="kg-figure">` | Inline SVG diagram plus `<figcaption>`. |
| `<nav id="kg-pagenav" ...>` | Leave empty; it is filled in. Keep it last inside the article. |

### Code blocks

Highlighting is hand-applied with spans — there is no syntax highlighter. Available:
`tok-comment`, `tok-key`, `tok-str`, `tok-num`, `tok-cmd`. Keep lines under about 90 characters;
longer lines scroll horizontally inside the block, which is supported but harder to read.

### Diagrams

Inline `<svg viewBox="0 0 700 …">` with `role="img"` and an `<aria-labelledby>` `<title>`. Use the
palette: background `#0e1420`, surface `#17212f`, border `#2e3f56`, accent `#6366f1`, success
`#10b981`, warning `#f59e0b`, error `#ef4444`, info `#06b6d4`, primary text `#f3f4f6`, muted
`#6b7280`. Set width via the viewBox only — never a fixed `width` attribute — so it scales.

Diagrams are for structure and lifecycle, not decoration. A page does not need one.

## Accessibility and offline

- The site must work with no network. Never reference a CDN, a Google Font, or a remote image.
- Content lives in the HTML. JavaScript only adds navigation, so a page must still read top to
  bottom with scripting off.
- Every `<svg>` that carries meaning needs a `<title>`; decorative ones get `aria-hidden="true"`.
