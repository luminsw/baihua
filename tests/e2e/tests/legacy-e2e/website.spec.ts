import { test, expect } from '@playwright/test';

/**
 * 【旧套件迁移】原 tests/Baihua.Web.E2e/website.spec.ts → tests/e2e/tests/legacy-e2e/
 *
 * 整体跳过原因（test.describe.skip）：
 *  1. 用例针对 Docker 部署形态，依赖 nginx 反向代理（http://localhost:80）提供 /admin 子路径；
 *  2. 本地开发环境（WebUI 直连 5177）与 CI 均无 nginx 入口，运行必失败；
 *  3. 对应覆盖点（Blazor 渲染、静态资源无 404、健康检查、/api/vaults 兼容）
 *     已由 smoke.spec.ts / migration.spec.ts 在直连形态下覆盖。
 * 如需部署后验证，请单独启用本文件（把 describe.skip 改为 describe）。
 */

const NGINX_BASE = 'http://localhost:80';

test.describe.skip('[legacy-e2e] 百花 Docker 部署测试（nginx 入口专用，迁移后跳过）', () => {

  test('根路径经 nginx 重定向到登录（非 200 直出）', async ({ request }) => {
    // nginx 当前设计：根路径转发到 WebUI，未登录时 302 → /login
    const resp = await request.get(`${NGINX_BASE}/`, { maxRedirects: 0 });
    expect([301, 302]).toContain(resp.status());
  });

  test('nginx 统一入口渲染 Blazor 页面（不白屏）', async ({ page }) => {
    await page.goto(`${NGINX_BASE}/login`);
    await page.waitForLoadState('domcontentloaded');
    const html = await page.content();
    expect(html.includes('<!--Blazor:') || html.includes('blazor.web'), '页面应包含 Blazor 框架标记').toBe(true);
  });

  test('nginx 入口静态资源无 404', async ({ page }) => {
    const errors: string[] = [];
    page.on('response', resp => {
      if (resp.status() >= 400 && resp.url().includes('_framework') === false) {
        errors.push(`${resp.status()}: ${resp.url()}`);
      }
    });
    await page.goto(`${NGINX_BASE}/login`);
    await page.waitForLoadState('networkidle', { timeout: 15000 });
    const criticalErrors = errors.filter(e =>
      e.includes('.css') ||
      e.includes('.js') ||
      e.includes('blazor.web')
    );
    expect(criticalErrors, `关键资源错误: ${criticalErrors.join(', ')}`).toHaveLength(0);
  });

  test('API 健康检查端点（nginx → WebUI）', async ({ request }) => {
    const resp = await request.get(`${NGINX_BASE}/health`);
    expect(resp.status()).toBe(200);
  });

  test('知识库列表 API 可访问', async ({ request }) => {
    const resp = await request.get(`${NGINX_BASE}/api/vaults`);
    expect([200, 401]).toContain(resp.status());
  });

  test('配对端点公开可访问', async ({ request }) => {
    const resp = await request.post(`${NGINX_BASE}/pair`, {
      data: { pairCode: 'wrong-code', deviceName: 'test' }
    });
    expect([400, 401]).toContain(resp.status());
  });

  test('旧版同步路径兼容', async ({ request }) => {
    const resp = await request.get(`${NGINX_BASE}/sync/system`);
    expect([200, 400, 404]).toContain(resp.status());
  });

});
