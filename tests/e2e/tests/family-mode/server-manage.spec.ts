import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize } from '../helpers';

// #ac55ffdc：百花服务器页测试与修复
// 覆盖：百花 WebUI 设备管理页（/devices）
// - 待授权/已授权设备区域渲染（空态或列表）—— 仅中文（产品已固定 zh-CN，不再有英文/语言切换）
// - 设备授权 API：pending / authorized 端点
// 注意：设备数量依赖环境数据，只断言端点契约与区域渲染，不断言具体设备存在。

const apiPort = process.env.API_PORT || '8788';   // 唯一后端 Baihua.Server
const apiBase = `http://127.0.0.1:${apiPort}`;

test.describe('百花服务器页（WebUI 设备管理）', () => {
  test.beforeEach(async ({ page }) => {
    await authorize(page);
    await navigateTo(page, '/devices');
    await waitForBlazor(page);
  });

  test('设备管理页加载：标题', async ({ page }) => {
    await expect(page.locator('h2').first()).toBeVisible({ timeout: 15000 });
  });

  test('待授权设备区域渲染（空态或列表）', async ({ page }) => {
    await expect(page.getByText(/待授权设备/).first()).toBeVisible({ timeout: 15000 });
  });

  test('已授权设备区域渲染（空态或列表）', async ({ page }) => {
    await expect(page.getByText(/已授权设备/).first()).toBeVisible({ timeout: 15000 });
  });

  test('API: 待授权设备端点返回合法结构', async ({ request }) => {
    const res = await request.get(`${apiBase}/api/devices/pending`);
    expect(res.status()).toBe(200);
    const json = await res.json();
    expect(Array.isArray(json)).toBeTruthy();
  });

  test('API: 已授权设备端点返回合法结构', async ({ request }) => {
    const res = await request.get(`${apiBase}/api/devices/authorized`);
    expect(res.status()).toBe(200);
    const json = await res.json();
    expect(Array.isArray(json)).toBeTruthy();
  });
});
