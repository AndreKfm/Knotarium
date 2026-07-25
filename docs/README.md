# Documentation

The user documentation lives in [`../help/`](../help/) — hand-written static HTML with no build step.

- **Reading it:** a running instance serves it at <http://localhost:43120/help/>, or open
  `help/index.html` directly from this repository. There is also a **Help** button in the
  application header and a link on the sign-in screen.
- **Editing it:** see [`../help/AUTHORING.md`](../help/AUTHORING.md). Copy `help/pages/_TEMPLATE.html`,
  register the page in `help/assets/nav.js`, then run `node scripts/build-help-index.mjs` and
  `node scripts/check-help-links.mjs`.

A VitePress site used to live in this folder. It covered a small fraction of the product, described
an offline `/help/` build and a GitHub Pages deployment that were never actually wired up, and
contained several claims the code contradicted. It was removed once `help/` superseded it.
