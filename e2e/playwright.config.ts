import { defineConfig } from '@playwright/test';

// Credentials come from the environment only; never commit a fallback.
if (!process.env.TEST_EMAIL || !process.env.TEST_PASSWORD) {
  throw new Error('Set TEST_EMAIL and TEST_PASSWORD before running e2e tests.');
}

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  retries: 1,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.BASE_URL || 'http://127.0.0.1:8006',
    headless: true,
    screenshot: 'only-on-failure',
    trace: 'on-first-retry',
  },
  projects: [
    { name: 'chromium', use: { browserName: 'chromium' } },
  ],
});
