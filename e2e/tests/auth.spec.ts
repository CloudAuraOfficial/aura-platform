import { test, expect } from '@playwright/test';

const API = '/api/v1/auth';
const EMAIL = process.env.TEST_EMAIL || 'org@cloudaura.cloud';
const PASSWORD = process.env.TEST_PASSWORD || 'admin12345@$!';

test.describe('Authentication API', () => {
  test('POST /auth/login with invalid credentials returns 401', async ({ request }) => {
    const res = await request.post(`${API}/login`, {
      data: { email: 'nobody@fake.com', password: 'WrongPass-1' },
    });
    expect(res.status()).toBe(401);
  });

  test('POST /auth/login with valid credentials returns JWT', async ({ request }) => {
    const res = await request.post(`${API}/login`, {
      data: { email: EMAIL, password: PASSWORD },
    });
    expect(res.ok()).toBeTruthy();
    const body = await res.json();
    expect(body.token).toBeTruthy();
    expect(body.refreshToken).toBeTruthy();
    expect(body.expiresAt).toBeTruthy();
  });

  test('POST /auth/refresh rotates tokens', async ({ request }) => {
    const login = await request.post(`${API}/login`, {
      data: { email: EMAIL, password: PASSWORD },
    });
    const { token, refreshToken } = await login.json();

    const res = await request.post(`${API}/refresh`, {
      data: { refreshToken },
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(res.ok()).toBeTruthy();
    const body = await res.json();
    expect(body.token).toBeTruthy();
    expect(body.token).not.toBe(token);
  });
});
