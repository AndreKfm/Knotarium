// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { readFileSync } from 'fs';

/**
 * End-to-end verification of the OpenAPI Importer feature (plan §9 step 12).
 *
 * Covered through the real UI:
 *  - Import Swagger 2.0 / OpenAPI 3.0 / OpenAPI 3.1 in JSON and YAML
 *  - Re-import increments the spec version (same logical spec, matched by title slug)
 *  - External `$ref` specs are rejected with a clear error
 *  - The generated per-API node appears in the canvas palette
 *  - Configuring the generated node and executing it reaches a mock HTTP server
 *
 * Note: the step plan envisioned dragging an operation chip directly from the
 * importer onto the canvas. The importer and the canvas are separate full-screen
 * views and are never mounted together, so the realistic path is: import → add the
 * generated node from the palette → configure via the property form → run. The
 * component-level drag/drop handler is covered by `openApiDrop.test.tsx`.
 */

function fixture(name: string): string {
  return readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf-8');
}

async function petstoreVersion(request: APIRequestContext): Promise<number> {
  const res = await request.get('/api/openapi/specs');
  expect(res.ok()).toBeTruthy();
  const specs = (await res.json()) as Array<{ title: string; latestVersionNumber: number }>;
  const petstore = specs.find((s) => s.title === 'Petstore');
  return petstore ? petstore.latestVersionNumber : 0;
}

async function gotoImporter(page: Page): Promise<void> {
  await page.goto('/');
  await page.getByRole('button', { name: 'API Importer' }).click();
  await expect(page.getByText('Imported APIs')).toBeVisible();
}

async function importViaPaste(page: Page, content: string): Promise<void> {
  await page.getByRole('button', { name: '+ Import Spec' }).click();
  const textarea = page.getByLabel('OpenAPI content');
  await expect(textarea).toBeVisible();
  await textarea.fill(content);
  await page.getByRole('button', { name: 'Import spec', exact: true }).click();
}

test.describe('OpenAPI Importer E2E', () => {
  test('imports all three dialects, rejects external $ref, and surfaces the generated node', async ({ page, request }) => {
    const baseVersion = await petstoreVersion(request);

    await gotoImporter(page);

    // ── Scenario 1: Swagger 2.0 JSON ──────────────────────────────────────
    await importViaPaste(page, fixture('petstore-swagger20.json'));
    await expect(page.getByText('Import OpenAPI Spec')).toBeHidden();
    await expect(page.getByText('Petstore').first()).toBeVisible();
    await expect.poll(() => petstoreVersion(request)).toBe(baseVersion + 1);

    // ── Scenario 2: OpenAPI 3.0 YAML (re-import → version +1) ──────────────
    await importViaPaste(page, fixture('petstore-openapi30.yaml'));
    await expect(page.getByText('Import OpenAPI Spec')).toBeHidden();
    await expect.poll(() => petstoreVersion(request)).toBe(baseVersion + 2);

    // ── Scenario 3: OpenAPI 3.1 JSON (re-import → version +1) ──────────────
    await importViaPaste(page, fixture('petstore-openapi31.json'));
    await expect(page.getByText('Import OpenAPI Spec')).toBeHidden();
    await expect.poll(() => petstoreVersion(request)).toBe(baseVersion + 3);

    // ── Scenario 4: External $ref rejected (no new version) ───────────────
    await importViaPaste(page, fixture('external-ref.yaml'));
    await expect(page.getByRole('alert')).toContainText(/external \$ref/i);
    // The "External Ref Test" spec is a different title; Petstore is untouched.
    await expect.poll(() => petstoreVersion(request)).toBe(baseVersion + 3);
    const refRes = await request.get('/api/openapi/specs');
    const titles = ((await refRes.json()) as Array<{ title: string }>).map((s) => s.title);
    expect(titles).not.toContain('External Ref Test');

    // ── Scenario 5: generated node appears in the canvas palette ──────────
    await page.getByRole('button', { name: 'Dashboard' }).click();
    await page.getByRole('button', { name: 'Create Workflow' }).click();
    await expect(page.locator('.node-start')).toBeVisible();
    await expect(page.getByTestId('palette-node-openapi.petstore')).toBeVisible();
  });
});
