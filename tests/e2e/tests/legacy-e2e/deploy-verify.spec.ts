import { test, expect } from '@playwright/test';

/**
 * 【旧套件迁移】原 tests/Baihua.Web.E2e/deploy-verify.spec.ts → tests/e2e/tests/legacy-e2e/
 *
 * 整体跳过原因（test.describe.skip）：
 *  1. 用例定位是「Docker/局域网部署后验证」，依赖部署形态（nginx、OpenObserve :5082 等）；
 *  2. 与 smoke.spec.ts 的健康检查 / 能力评估用例高度重叠（smoke 已覆盖本地环境）；
 *  3. 本地开发环境与 CI（无 Docker 部署）均不适用。
 * 如需部署后验证，请单独启用本文件（把 describe.skip 改为 describe）。
 */

const WEBUI_BASE = 'http://127.0.0.1:5177';   // WebUI（独立进程）
const SERVER_BASE = 'http://127.0.0.1:8788';  // 唯一后端 Baihua.Server

test.describe.skip('[legacy-e2e] 百花部署验证（Docker 部署专用，迁移后跳过）', () => {

  test('Baihua.Server 健康检查', async ({ request }) => {
    const resp = await request.get(`${SERVER_BASE}/health`);
    expect(resp.status()).toBe(200);
  });

  test('Baihua.Server API 能力评估', async ({ request }) => {
    const resp = await request.get(`${SERVER_BASE}/api/capability`);
    expect(resp.status()).toBe(200);
    const data: any = await resp.json();
    // 兼容 camelCase（当前）与 PascalCase（合并前 Family 特例）
    expect(data.Level ?? data.level).toBeTruthy();
    expect(data.AvailableFeatures ?? data.availableFeatures).toBeTruthy();
    expect(Array.isArray(data.AvailableFeatures ?? data.availableFeatures)).toBe(true);
  });

  test('WebUI 健康检查', async ({ request }) => {
    const resp = await request.get(`${WEBUI_BASE}/health`);
    expect(resp.status()).toBe(200);
  });

  test('WebUI Blazor 框架加载', async ({ page }) => {
    await page.goto(`${WEBUI_BASE}/login`);
    const html = await page.content();
    const hasBlazor = html.includes('<!--Blazor:') || html.includes('blazor.web');
    expect(hasBlazor, '页面应包含 Blazor 框架标记').toBe(true);
  });

  test('知识库列表 API 可访问', async ({ request }) => {
    const resp = await request.get(`${SERVER_BASE}/api/vaults`);
    expect([200, 401]).toContain(resp.status());
  });

  test('OpenObserve 可访问（如果启用）', async ({ request }) => {
    const resp = await request.get('http://127.0.0.1:5082/api/status', { maxRedirects: 5 }).catch(() => null);
    // OpenObserve 是可选的，404 或连接失败不算错误
    if (resp) {
      expect([200, 401, 404]).toContain(resp.status());
    }
  });

});
