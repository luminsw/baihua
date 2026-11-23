import { chromium, FullConfig } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';

/**
 * 全局登录：走 CLI token 免密登录流程，把 auth cookie 写入 storage-state.json。
 *
 * 所有项目共享 storageState: './storage-state.json'，因此在这里登录一次即可。
 * 流程与 bh dashboard 自动登录一致：
 *   POST /api/auth/cli-token（loopback 专用）→ 拿一次性 token
 *   → 访问 /?cli-token=xxx → 服务端种 webui_auth cookie
 */
export default async function globalSetup(config: FullConfig) {
  const base = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5177';
  const storagePath = path.join(__dirname, '..', 'e2e', 'storage-state.json');
  const cliTokenUrl = base.replace(/\/$/, '') + '/api/auth/cli-token';

  const browser = await chromium.launch();
  const context = await browser.newContext();
  const page = await context.newPage();

  try {
    const resp = await page.request.post(cliTokenUrl);
    if (!resp.ok()) {
      throw new Error(`CLI token 获取失败: ${resp.status()} ${await resp.text()}`);
    }
    const body = await resp.json();
    const token = body?.token;
    if (!token) throw new Error('CLI token 响应缺少 token 字段');

    await page.goto(base.replace(/\/$/, '') + '/?cli-token=' + encodeURIComponent(token));
    await page.waitForLoadState('networkidle');

    // 确认 cookie 已种上（否则 storage-state 无意义）
    const cookies = await context.cookies();
    const authCookie = cookies.find(c => c.name === 'webui_auth');
    if (!authCookie) {
      throw new Error('未种上 webui_auth cookie，登录失败');
    }

    await context.storageState({ path: storagePath });
    console.log(`[globalSetup] 登录成功，storage-state 已写入: ${storagePath} (cookie: ${authCookie.name})`);
  } finally {
    await browser.close();
  }
}
