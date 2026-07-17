// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { SAMPLE_META, CATEGORY_ACCENT, type SampleCategory } from './onboardingSampleMeta';

// Mirror of the built-in starter templateIds shipped under
// Backend/Knotarium.Api/Templates/Sources (folder name -> `tpl_starter-<folder>`).
// Kept here as a maintenance guard: a new built-in starter should get a SAMPLE_META
// entry so it renders with a category accent + node-flow preview in the onboarding
// gallery instead of the neutral DEFAULT_SAMPLE_META fallback.
const BUILT_IN_STARTERS = [
  'tpl_starter-hello-world',
  'tpl_starter-http-post',
  'tpl_starter-webhook-receiver',
  'tpl_starter-scheduled-fetch',
  'tpl_starter-scheduled-heartbeat',
  'tpl_starter-set-a-variable',
  'tpl_starter-fetch-from-api',
  'tpl_starter-fetch-wait-fetch',
  'tpl_starter-delay-then-log',
  'tpl_starter-log-run-markers',
  'tpl_starter-ai-summarize',
  'tpl_starter-ai-evidence-check',
  'tpl_starter-ai-contract-diff',
  'tpl_starter-ai-support-triage',
  'tpl_starter-ai-agent-order-concierge',
  'tpl_starter-ai-agent-tool-order-status',
];

const VALID_CATEGORIES: SampleCategory[] = ['trigger', 'logic', 'data', 'network', 'ai'];

describe('onboardingSampleMeta', () => {
  it('has presentation metadata for every built-in starter', () => {
    const missing = BUILT_IN_STARTERS.filter((id) => !(id in SAMPLE_META));
    expect(missing, `built-in starters without SAMPLE_META (fall back to the neutral default): ${missing.join(', ')}`).toEqual([]);
  });

  it('only uses valid categories (accent + flow chips)', () => {
    for (const [id, meta] of Object.entries(SAMPLE_META)) {
      expect(VALID_CATEGORIES, `${id}.category`).toContain(meta.category);
      expect(CATEGORY_ACCENT[meta.category], `${id} accent`).toBeTruthy();
      for (const chip of meta.flow) {
        expect(VALID_CATEGORIES, `${id} flow chip "${chip.label}"`).toContain(chip.cat);
      }
    }
  });

  it('gives every entry a non-empty tag and tile icon', () => {
    for (const [id, meta] of Object.entries(SAMPLE_META)) {
      expect(meta.tag.trim(), `${id}.tag`).not.toBe('');
      expect(meta.icon.trim(), `${id}.icon`).not.toBe('');
    }
  });
});
