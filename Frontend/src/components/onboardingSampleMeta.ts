// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Presentation metadata for the onboarding sample cards. The gallery API returns only a manifest
// (name, description), so the category accent, tile icon, tag, and node-flow chips live here — keyed
// by the built-in templateId. Templates without an entry fall back to a neutral default.

export type SampleCategory = 'trigger' | 'logic' | 'data' | 'network' | 'ai';

export interface FlowChip {
  label: string;
  icon: string;   // key into the icon map in OnboardingEmptyState
  cat: SampleCategory;
}

export interface SampleMeta {
  category: SampleCategory;
  icon: string;   // tile icon key
  tag: string;
  flow: FlowChip[];
}

// Card accent per category (matches the accents the dashboard already uses elsewhere).
export const CATEGORY_ACCENT: Record<SampleCategory, string> = {
  trigger: '#34d399',
  logic: '#a99bff',
  data: '#f0b429',
  network: '#22d3ee',
  ai: '#f472b6',
};

export const DEFAULT_SAMPLE_META: SampleMeta = { category: 'logic', icon: 'log', tag: 'STARTER', flow: [] };

const log: FlowChip = { label: 'Log', icon: 'log', cat: 'logic' };

export const SAMPLE_META: Record<string, SampleMeta> = {
  'tpl_starter-hello-world': {
    category: 'trigger', icon: 'manual', tag: 'MANUAL · TRIGGER',
    flow: [{ label: 'Manual', icon: 'manual', cat: 'trigger' }, log],
  },
  'tpl_starter-log-run-markers': {
    category: 'logic', icon: 'marker', tag: 'SCAFFOLD · LOGIC',
    flow: [log, { label: '· · ·', icon: 'marker', cat: 'logic' }, log],
  },
  'tpl_starter-delay-then-log': {
    category: 'logic', icon: 'delay', tag: 'DELAY · LOGIC',
    flow: [{ label: 'Delay', icon: 'delay', cat: 'logic' }, log],
  },
  'tpl_starter-fetch-from-api': {
    category: 'network', icon: 'http', tag: 'HTTP · NETWORK',
    flow: [{ label: 'HTTP Request', icon: 'http', cat: 'network' }, log],
  },
  'tpl_starter-fetch-wait-fetch': {
    category: 'network', icon: 'repeat', tag: 'POLL · NETWORK',
    flow: [{ label: 'HTTP', icon: 'http', cat: 'network' }, { label: 'Delay', icon: 'delay', cat: 'logic' }, { label: 'HTTP', icon: 'http', cat: 'network' }],
  },
  'tpl_starter-http-post': {
    category: 'network', icon: 'http', tag: 'HTTP · NETWORK',
    flow: [{ label: 'HTTP POST', icon: 'http', cat: 'network' }, log],
  },
  'tpl_starter-scheduled-fetch': {
    category: 'trigger', icon: 'schedule', tag: 'SCHEDULE · TRIGGER',
    flow: [{ label: 'Schedule', icon: 'schedule', cat: 'trigger' }, { label: 'HTTP', icon: 'http', cat: 'network' }, log],
  },
  'tpl_starter-scheduled-heartbeat': {
    category: 'trigger', icon: 'schedule', tag: 'CRON · TRIGGER',
    flow: [{ label: 'Schedule', icon: 'schedule', cat: 'trigger' }, log],
  },
  'tpl_starter-set-a-variable': {
    category: 'data', icon: 'variable', tag: 'VARIABLE · DATA',
    flow: [{ label: 'Set Var', icon: 'variable', cat: 'data' }, log],
  },
  'tpl_starter-webhook-receiver': {
    category: 'trigger', icon: 'webhook', tag: 'WEBHOOK · TRIGGER',
    flow: [{ label: 'Webhook', icon: 'webhook', cat: 'trigger' }, log],
  },
  'tpl_starter-ai-support-triage': {
    category: 'ai', icon: 'ai', tag: 'AI · TRIAGE',
    flow: [
      { label: 'AI Extract', icon: 'ai', cat: 'ai' },
      { label: 'AI Router', icon: 'ai', cat: 'ai' },
      { label: 'AI Draft', icon: 'ai', cat: 'ai' },
    ],
  },
};
