import { defineConfig } from '@playwright/test';

// Drives an ALREADY-PROVISIONED install (the CI smoke test starts the stack
// first), so there is no webServer block here - nothing for Playwright to
// launch or own.
export default defineConfig({
  testDir: '.',
  // Generous, because the budget has to cover BOTH the waits and the
  // diagnostic that runs when one of them fails. A tight timeout made the
  // failure report itself time out, which is worse than no report.
  timeout: 180_000,
  expect: { timeout: 15_000 },
  // A flaky pass is worse than a fail here: this is the only thing standing
  // between a broken UI and a customer.
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.SHABANA_URL ?? 'http://localhost:8000',
    headless: true,
    screenshot: 'only-on-failure',
    locale: 'ar-EG',
  },
});
