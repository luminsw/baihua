import { Page } from '@playwright/test';

/**
 * FAM-35：CLI 一键授权（bh dashboard 同机制）。
 * POST /api/auth/cli-token（本机 loopback 专用）拿一次性 token，访问 /?cli-token= 种 auth cookie。
 */
export async function authorize(page: Page) {
  const base = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5177';
  const resp = await page.request.post(base.replace(/\/$/, '') + '/api/auth/cli-token');
  if (!resp.ok()) {
    throw new Error(`CLI token 获取失败: ${resp.status()}`);
  }
  const body = await resp.json();
  const token = body?.token;
  if (!token) throw new Error('CLI token 响应缺少 token 字段');
  await page.goto(base.replace(/\/$/, '') + '/?cli-token=' + encodeURIComponent(token));
  await page.waitForLoadState('networkidle');
}

/**
 * FAM-35：确保测试数据存在（幂等）——至少 1 个 Learner，否则看板/打卡/排行榜空态不渲染区域。
 * 创建后通过看板接口触发一次数据聚合，保证页面有内容。
 *
 * API_PORT 指向唯一后端 Baihua.Server（8788，三服务合一后家庭/AI/知识库同进程）。
 */
export async function ensureTestData(page: Page) {
  const apiBase = process.env.API_PORT
    ? 'http://127.0.0.1:' + process.env.API_PORT
    : 'http://127.0.0.1:8788';
  const learners = await page.request.get(apiBase + '/api/achievements/learners');
  if (!learners.ok()) throw new Error(`GET learners 失败: ${learners.status()}`);
  const list = await learners.json();
  if (!Array.isArray(list) || list.length === 0) {
    const created = await page.request.post(apiBase + '/api/achievements/learners', {
      data: { name: '小明', avatarEmoji: '🙂', color: '#007bff' },
      headers: { 'Content-Type': 'application/json' },
    });
    if (!created.ok()) throw new Error(`创建 Learner 失败: ${created.status()} ${await created.text()}`);
  }
}

export async function navigateTo(page: Page, path: string) {
  const base = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5177';
  const url = base.replace(/\/$/, '') + path;
  await page.goto(url);
  await page.waitForLoadState('networkidle');
}

export async function waitForBlazor(page: Page) {
  // Wait for Blazor to finish rendering: wait for network idle then for dashboard-specific container
  await page.waitForLoadState('networkidle');
  // Wait up to 5s for Blazor-rendered root element to appear
  try {
    await page.waitForSelector('.dashboard-page', { timeout: 5000 });
  } catch {
    // fallback short delay
    await page.waitForTimeout(500);
  }
}
