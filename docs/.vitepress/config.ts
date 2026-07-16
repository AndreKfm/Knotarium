import { defineConfig } from 'vitepress'

// One markdown source → static HTML, delivered two ways:
//  - Online: GitHub Pages (build with DOCS_BASE=/Knotarium/).
//  - Offline: bundled with the app and served by the backend (build with DOCS_BASE=/help/),
//    or shipped as a folder the user opens locally.
// The base is build-time, so each target sets DOCS_BASE; default '/' works for dev + a root host.
export default defineConfig({
  title: 'Knotarium',
  description: 'Self-hosted visual workflow automation — user guide',
  lang: 'en-US',
  base: process.env.DOCS_BASE || '/',
  lastUpdated: true,
  cleanUrls: true,
  // Dead internal links fail the build — a missing page or a link to a file outside the docs source
  // (e.g. ../README.md) is caught here instead of shipping a 404. localhost example URLs are exempt:
  // they point at the reader's own instance, so they're not reachable at build time by design.
  ignoreDeadLinks: [/^https?:\/\/localhost/],

  themeConfig: {
    nav: [
      { text: 'Get started', link: '/getting-started' },
      { text: 'Guide', link: '/guide/concepts' },
      { text: 'AI', link: '/guide/ai' },
    ],

    sidebar: [
      {
        text: 'Getting started',
        items: [
          { text: 'Download & run', link: '/install' },
          { text: 'Install & first workflow', link: '/getting-started' },
          { text: 'Core concepts', link: '/guide/concepts' },
        ],
      },
      {
        text: 'AI',
        items: [
          { text: 'AI provider & AI nodes', link: '/guide/ai' },
        ],
      },
    ],

    // Built-in, fully offline search — no external service.
    search: { provider: 'local' },

    socialLinks: [
      { icon: 'github', link: 'https://github.com/AndreKfm/Knotarium' },
    ],

    editLink: {
      pattern: 'https://github.com/AndreKfm/Knotarium/edit/main/docs/:path',
      text: 'Edit this page on GitHub',
    },

    footer: {
      message: 'Apache-2.0 licensed.',
      copyright: 'Knotarium',
    },
  },
})
