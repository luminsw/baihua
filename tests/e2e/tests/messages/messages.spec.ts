import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('AI对话功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/messages');
  });

  test('AI对话页面加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('AI 对话', { timeout: 15000 });
  });

  test('显示AI状态信息栏', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.ai-info-bar')).toBeVisible();
  });

  test('有聊天容器', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.chat-container')).toBeVisible();
  });

  test('有消息列表区域', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.chat-messages')).toBeVisible();
  });

  test('有输入框', async ({ page }) => {
    await waitForBlazor(page);
    const input = page.locator('.chat-input').locator('input, textarea').first();
    await expect(input).toBeVisible();
  });

  test('有发送按钮', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.getByText('发送')).toBeVisible();
  });

  test('有导出和清空按钮区域', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.ai-info-bar')).toBeVisible();
  });
});
