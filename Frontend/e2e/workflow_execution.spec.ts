// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { test, expect } from '@playwright/test';

test.describe('Knotarium Visual Flow E2E Integration', () => {
  test('should create, connect, save, execute a workflow and trace live SSE updates', async ({ page }) => {
    // 1. Navigate to Knotarium Dashboard
    await page.goto('/');
    await expect(page.getByText('Workflow Control Center')).toBeVisible();

    // 2. Click "Create Workflow" button to enter the Canvas Editor
    await page.getByRole('button', { name: 'Create Workflow' }).click();
    await page.locator('.node-start').click();
    await expect(page.getByText('Start Node Properties')).toBeVisible();

    // 3. Edit workflow name to a custom string
    const titleInput = page.locator('input[type="text"]').first();
    await titleInput.click();
    await titleInput.fill('E2E Integration Test Flow');
    await titleInput.press('Enter');

    // 4. Add "Log" and "End" nodes using the floating toolbar
    await page.getByTestId('palette-node-log').click();
    await expect(page.locator('.node-log')).toBeVisible();

    await page.getByTestId('palette-node-end').click();
    await expect(page.locator('.node-end')).toBeVisible();


    // 6. Connect the nodes
    // Connect Start -> Log
    const startSource = page.locator('.node-start .react-flow__handle-right');
    const logTarget = page.locator('.node-log .react-flow__handle-left');
    await startSource.dragTo(logTarget, { force: true });

    // Connect Log -> End
    const logSource = page.locator('.node-log .react-flow__handle-right');
    const endTarget = page.locator('.node-end .react-flow__handle-left');
    await logSource.dragTo(endTarget, { force: true });

    // 7. Select Log node and configure its log message
    await page.locator('.node-log').click();
    await expect(page.getByText('Log Node Properties')).toBeVisible();

    const logTextarea = page.locator('textarea[placeholder*="Enter message"]');
    await logTextarea.fill('Hello from E2E integration test!');

    // 8. Save, Publish, and Run the workflow
    await page.getByRole('button', { name: 'Save Definition' }).click();
    await page.getByRole('button', { name: 'Publish Version' }).click();
    await page.getByRole('button', { name: 'Run Active Version' }).click();

    // 10. Track the live execution status transitioning to Completed via SSE
    await expect(page.getByText('Workflow run completed successfully.')).toBeVisible({ timeout: 15000 });

    // 11. Assert that the terminal console displays the custom log message
    await expect(page.getByText('Hello from E2E integration test!').first()).toBeVisible();
  });
});
