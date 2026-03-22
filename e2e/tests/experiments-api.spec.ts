import { test, expect } from '@playwright/test';

const EMAIL = process.env.TEST_EMAIL || 'org@cloudaura.cloud';
const PASSWORD = process.env.TEST_PASSWORD || 'admin12345@$!';
const API = '/api/v1';

let token: string;
let experimentId: string;

test.beforeAll(async ({ request }) => {
  const res = await request.post(`${API}/auth/login`, {
    data: { email: EMAIL, password: PASSWORD },
  });
  const body = await res.json();
  token = body.token;
});

function auth() {
  return { Authorization: `Bearer ${token}` };
}

test.describe.serial('Experiments API — Full Lifecycle', () => {
  test('create a draft experiment', async ({ request }) => {
    const res = await request.post(`${API}/experiments`, {
      headers: auth(),
      data: {
        project: 'e2e-test',
        name: `E2E Test ${Date.now()}`,
        hypothesis: 'Playwright can test experiments end to end',
        variants: '[{"id":"control","weight":50},{"id":"variant_a","weight":50}]',
        metricName: 'response_time_ms',
      },
    });
    expect(res.status()).toBe(201);
    const body = await res.json();
    expect(body.status).toBe('Draft');
    experimentId = body.id;
  });

  test('list experiments', async ({ request }) => {
    const res = await request.get(`${API}/experiments?project=e2e-test`, {
      headers: auth(),
    });
    expect(res.ok()).toBeTruthy();
    const body = await res.json();
    expect(body.items.length).toBeGreaterThanOrEqual(1);
  });

  test('get experiment by id', async ({ request }) => {
    const res = await request.get(`${API}/experiments/${experimentId}`, {
      headers: auth(),
    });
    expect(res.ok()).toBeTruthy();
    const body = await res.json();
    expect(body.project).toBe('e2e-test');
  });

  test('transition Draft to Running', async ({ request }) => {
    const res = await request.put(`${API}/experiments/${experimentId}`, {
      headers: auth(),
      data: { status: 'Running' },
    });
    expect(res.ok()).toBeTruthy();
    const body = await res.json();
    expect(body.status).toBe('Running');
    expect(body.startedAt).toBeTruthy();
  });

  test('assign a variant', async ({ request }) => {
    const res = await request.post(`${API}/experiments/${experimentId}/assign`, {
      headers: auth(),
      data: { subjectKey: 'e2e-user-1' },
    });
    expect(res.ok()).toBeTruthy();
    const body = await res.json();
    expect(['control', 'variant_a']).toContain(body.variantId);
  });

  test('track a metric event', async ({ request }) => {
    const assign = await request.post(`${API}/experiments/${experimentId}/assign`, {
      headers: auth(),
      data: { subjectKey: 'e2e-user-1' },
    });
    const { variantId, subjectHash } = await assign.json();

    const res = await request.post(`${API}/experiments/${experimentId}/track`, {
      headers: auth(),
      data: { variantId, subjectHash, metricName: 'response_time_ms', metricValue: 42.5 },
    });
    expect(res.ok()).toBeTruthy();
  });

  test('get results with data', async ({ request }) => {
    const res = await request.get(`${API}/experiments/${experimentId}/results`, {
      headers: auth(),
    });
    expect(res.ok()).toBeTruthy();
    const body = await res.json();
    expect(body.metricName).toBe('response_time_ms');
    expect(Object.keys(body.variants).length).toBeGreaterThanOrEqual(1);
  });

  test('conclude experiment', async ({ request }) => {
    const res = await request.put(`${API}/experiments/${experimentId}`, {
      headers: auth(),
      data: { status: 'Concluded', conclusion: 'E2E lifecycle test passed' },
    });
    expect(res.ok()).toBeTruthy();
    const body = await res.json();
    expect(body.status).toBe('Concluded');
    expect(body.concludedAt).toBeTruthy();
  });

  test('reject invalid transition Concluded to Running', async ({ request }) => {
    const res = await request.put(`${API}/experiments/${experimentId}`, {
      headers: auth(),
      data: { status: 'Running' },
    });
    expect(res.status()).toBe(400);
  });
});
