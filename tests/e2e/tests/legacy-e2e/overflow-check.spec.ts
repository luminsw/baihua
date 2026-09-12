// 验证消息气泡溢出修复：发送长问题（OpenVINO 回复），暗色模式测气泡边界
// 【旧套件迁移】原 tests/Baihua.Web.E2e/overflow-check.spec.ts → tests/e2e/tests/legacy-e2e/
// 迁移要点：CommonJS require → ESM import；登录改为共享 authorize() helper；
// 截图输出改到 testInfo.outputPath（不再写死 cwd 相对路径）。
import { test, expect } from '@playwright/test';
import { authorize } from '../helpers';

test('[legacy-e2e] chat bubble overflow check', async ({ page }, testInfo) => {
  // 1. cli-token 登录
  await authorize(page);
  await page.waitForLoadState('networkidle', { timeout: 30000 }).catch(() => {});
  await page.waitForTimeout(2500);

  // 2. 暗色模式
  await page.evaluate(() => document.documentElement.setAttribute('data-bs-theme', 'dark'));

  // 3. 去 AI 对话页
  await page.goto('/messages');
  await page.waitForTimeout(3000);

  // 4. 输入长问题并发送
  const ta = page.locator('textarea.chat-textarea');
  await ta.waitFor({ timeout: 15000 });
  await ta.fill('请详细介绍一下软考系统架构师的考试内容，包括所有科目的详细说明、考试重点和备考建议，尽量写详细一些，包括列表和表格。');
  // 仅中文 UI（产品固定 zh-CN）
  const sendBtn = page.locator('button:has-text("发送")').first();
  await sendBtn.click();

  // 5. 等待 AI 回复完成（typing 指示器消失 + 出现 ai 气泡）
  await page.waitForSelector('.message.ai .message-text', { timeout: 30000 });
  await page.waitForSelector('.typing-indicator', { state: 'detached', timeout: 180000 }).catch(() => {});
  await page.waitForTimeout(2000);

  // 6. 测量溢出
  const viewport = page.viewportSize();
  const overflow = await page.evaluate(() => {
    const vw = document.documentElement.clientWidth;
    const issues = [];
    for (const el of document.querySelectorAll('.message .message-content')) {
      const r = el.getBoundingClientRect();
      if (r.right > vw + 1 || r.left < -1) {
        issues.push({ type: 'content-overflow', right: Math.round(r.right), vw, text: (el.innerText || '').slice(0, 40) });
      }
      // 文字是否超出气泡背景
      const textEl = el.querySelector('.message-text');
      if (textEl) {
        const tr = textEl.getBoundingClientRect();
        if (tr.right > r.right + 1) {
          issues.push({ type: 'text-beyond-bg', textRight: Math.round(tr.right), bgRight: Math.round(r.right) });
        }
      }
    }
    return { vw, issues };
  });
  console.log('VIEWPORT:', viewport.width, '| overflow issues:', JSON.stringify(overflow));

  await page.screenshot({ path: testInfo.outputPath('chat-overflow-check.png'), fullPage: false });
  expect(overflow.issues.length, `溢出问题: ${JSON.stringify(overflow.issues)}`).toBe(0);
});
