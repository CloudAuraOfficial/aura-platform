import { test, expect } from '@playwright/test';

test.describe('Health & Public Endpoints', () => {
  test('GET /health returns healthy status', async ({ request }) => {
    const res = await request.get('/health');
    expect(res.ok()).toBeTruthy();
    const body = await res.json();
    expect(body.status).toBe('healthy');
    expect(body.timestamp).toBeTruthy();
  });

  test('GET /metrics returns Prometheus metrics', async ({ request }) => {
    const res = await request.get('/metrics');
    expect(res.ok()).toBeTruthy();
    const text = await res.text();
    expect(text).toContain('aura_http_requests_total');
  });

  test('GET / redirects to /dashboard', async ({ page }) => {
    await page.goto('/');
    await expect(page).toHaveURL(/\/dashboard/);
  });
});
