/*
 * Copyright 2026 Andre Kaufmann
 * SPDX-License-Identifier: Apache-2.0
 *
 * The help site map — single source of truth for the sidebar, the previous/next footer links, and
 * the page titles used by the search index generator (scripts/build-help-index.mjs).
 *
 * There is no build step, so every page includes this file rather than duplicating a nav block.
 * Pages carrying `pending: true` are listed but not yet written: the sidebar renders them dimmed
 * and unclickable, so the shape of the finished manual is visible without shipping dead links.
 * Drop the flag when the page lands.
 */
window.KG_NAV = [
  {
    title: 'Getting started',
    pages: [
      { file: 'introduction.html',   title: 'What is Knotarium?' },
      { file: 'install.html',        title: 'Install and run' },
      { file: 'first-workflow.html', title: 'Your first workflow' },
      { file: 'concepts.html',       title: 'Core concepts' },
    ],
  },
  {
    title: 'Building workflows',
    pages: [
      { file: 'canvas.html',      title: 'The canvas editor' },
      { file: 'expressions.html', title: 'Expressions and data' },
      { file: 'variables.html',   title: 'Variables' },
      { file: 'conditions.html',  title: 'Conditions and branching' },
      { file: 'subflows.html',    title: 'Sub-flows and grouping' },
      { file: 'diagnostics.html', title: 'Diagnostics' },
    ],
  },
  {
    title: 'Running and debugging',
    pages: [
      { file: 'arming.html',         title: 'Runtime arming' },
      { file: 'triggers.html',       title: 'Triggers' },
      { file: 'runs.html',           title: 'Runs and the inspector' },
      { file: 'versioning.html',     title: 'Versions and publishing' },
      { file: 'error-handling.html', title: 'Error handling and alerts' },
      { file: 'dead-letter.html',    title: 'Dead letter queue' },
    ],
  },
  {
    title: 'Node reference',
    pages: [
      { file: 'nodes-overview.html', title: 'How to read this reference' },
      { file: 'nodes-triggers.html', title: 'Trigger nodes' },
      { file: 'nodes-logic.html',    title: 'Logic nodes' },
      { file: 'nodes-data.html',     title: 'Data nodes' },
      { file: 'nodes-network.html',  title: 'Network nodes' },
      { file: 'nodes-ai.html',       title: 'AI nodes' },
      { file: 'nodes-utility.html',  title: 'Utility nodes' },
    ],
  },
  {
    title: 'Sharing and reuse',
    pages: [
      { file: 'templates.html', title: 'Templates' },
      { file: 'bundles.html',   title: 'Bundles' },
      { file: 'openapi.html',   title: 'Importing OpenAPI' },
      { file: 'importer.html',  title: 'Importing configurations' },
    ],
  },
  {
    title: 'Administration',
    pages: [
      { file: 'settings-overview.html', title: 'Settings overview' },
      { file: 'security.html',          title: 'Security model' },
      { file: 'capabilities.html',      title: 'Capabilities' },
      { file: 'file-access.html',       title: 'File access' },
      { file: 'sandbox.html',           title: 'Sandbox' },
      { file: 'retention.html',         title: 'Retention and disk space' },
      { file: 'backup-restore.html',    title: 'Backup and restore' },
      { file: 'users.html',             title: 'Users and sign-in' },
      { file: 'configuration.html',     title: 'Configuration reference' },
    ],
  },
  {
    title: 'Extending Knotarium',
    pages: [
      { file: 'node-editor.html',       title: 'The node editor' },
      { file: 'custom-packages.html',   title: 'Custom node packages' },
      { file: 'rest-api.html',          title: 'REST API' },
      { file: 'build-from-source.html', title: 'Building from source' },
    ],
  },
  {
    title: 'Reference',
    pages: [
      { file: 'keyboard-shortcuts.html', title: 'Keyboard shortcuts' },
      { file: 'troubleshooting.html',    title: 'Troubleshooting' },
      { file: 'glossary.html',           title: 'Glossary' },
    ],
  },
];
