import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('记忆卡片功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/cards');
  });

  test('记忆卡片页面加载成功', async ({ page }) => {
    await expect(page.locator('h2')).toContainText('记忆卡片', { timeout: 15000 });
  });

  test('有搜索输入框', async ({ page }) => {
    await waitForBlazor(page);
    const input = page.locator('input[placeholder*="搜索卡片"]');
    await expect(input).toBeVisible();
  });

  test('有统计卡片区域', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.card').first()).toBeVisible();
  });

  test('有卡片总数统计', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.getByText('卡片总数')).toBeVisible();
  });
});
