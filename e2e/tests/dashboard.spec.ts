import { test, expect, Page } from '@playwright/test';

const EMAIL = process.env.TEST_EMAIL || 'org@cloudaura.cloud';
const PASSWORD = process.env.TEST_PASSWORD || 'admin12345@$!';

async function loginAndNavigate(page: Page, path: string) {
  // Get JWT via API
  const res = await page.request.post('/api/v1/auth/login', {
    data: { email: EMAIL, password: PASSWORD },
  });
  const { token } = await res.json();

  // Set token in localStorage before navigating to the target page
  await page.goto('/dashboard/login');
  await page.evaluate((t) => localStorage.setItem('aura_token', t), token);
  await page.goto(path);
  // Wait for JS to execute and check auth
  await page.waitForTimeout(500);
}

test.describe('Dashboard', () => {
  test('dashboard overview loads', async ({ page }) => {
    await loginAndNavigate(page, '/dashboard');
    await expect(page).toHaveURL(/\/dashboard/);
    // Should not redirect to login
    await expect(page).not.toHaveURL(/\/login/);
  });

  test('essences page loads', async ({ page }) => {
    await loginAndNavigate(page, '/dashboard/essences');
    await expect(page.locator('h1')).toContainText('Essences');
  });

  test('deployments page loads', async ({ page }) => {
    await loginAndNavigate(page, '/dashboard/deployments');
    await expect(page.locator('h1')).toContainText('Deployments');
  });

  test('experiments page loads', async ({ page }) => {
    await loginAndNavigate(page, '/dashboard/experiments');
    await expect(page.locator('h1')).toContainText('Experiments');
  });

  test('users page loads', async ({ page }) => {
    await loginAndNavigate(page, '/dashboard/users');
    await expect(page.locator('h1')).toContainText('Users');
  });

  test('audit page loads', async ({ page }) => {
    await loginAndNavigate(page, '/dashboard/audit');
    await expect(page.locator('h1')).toContainText('Audit');
  });

  test('navigation bar has all links', async ({ page }) => {
    await loginAndNavigate(page, '/dashboard');
    const nav = page.locator('.nav-links');
    await expect(nav.locator('a[href="/dashboard"]')).toBeVisible();
    await expect(nav.locator('a[href="/dashboard/essences"]')).toBeVisible();
    await expect(nav.locator('a[href="/dashboard/deployments"]')).toBeVisible();
    await expect(nav.locator('a[href="/dashboard/experiments"]')).toBeVisible();
    await expect(nav.locator('a[href="/dashboard/users"]')).toBeVisible();
    await expect(nav.locator('a[href="/dashboard/audit"]')).toBeVisible();
  });

  test('unauthenticated user redirects to login', async ({ page }) => {
    await page.goto('/dashboard/essences');
    // JS checks token and redirects
    await page.waitForURL(/\/login/, { timeout: 5000 });
    await expect(page).toHaveURL(/\/login/);
  });
});
