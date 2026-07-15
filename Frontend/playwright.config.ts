import { defineConfig, devices } from '@playwright/test';

/**
 * See https://playwright.dev/docs/test-configuration.
 */
export default defineConfig({
  testDir: './e2e',
  /* Run tests in files in parallel */
  fullyParallel: false,
  /* Fail the build on CI if you accidentally left test.only in the source code. */
  forbidOnly: !!process.env.CI,
  /* Retry on CI only */
  retries: process.env.CI ? 2 : 0,
  /* Opt out of parallel tests on Windows during development. */
  workers: 1,
  /* Reporter to use. See https://playwright.dev/docs/test-reporters */
  reporter: 'html',
  /* Shared settings for all the projects below. See https://playwright.dev/docs/api/class-testoptions. */
  use: {
    /* Base URL to use in actions like `await page.goto('/')`. */
    baseURL: 'http://localhost:5273',

    /* Collect trace when retrying the failed test. See https://playwright.dev/docs/trace-viewer */
    trace: 'on-first-retry',
  },

  /* Configure projects for major browsers */
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],

  /* Run your local dev servers before starting the tests */
  webServer: [
    {
      command: 'dotnet run --project ../Backend/Knotarium.Api/Knotarium.Api.csproj --launch-profile http',
      url: 'http://localhost:43120/api/workflows',
      reuseExistingServer: !process.env.CI,
      stdout: 'pipe',
      stderr: 'pipe',
      timeout: 30000,
      // The OpenAPI importer e2e executes a generated node against the local mock
      // Petstore server on 127.0.0.1, which the egress policy blocks by default.
      // Relax loopback denial for the test backend only.
      env: { Security__HttpEgress__DenyPrivateNetworks: 'false' },
    },
    {
      command: 'npm run dev',
      url: 'http://localhost:5273',
      reuseExistingServer: false,
      stdout: 'pipe',
      stderr: 'pipe',
      timeout: 15000,
    },
  ],
});
