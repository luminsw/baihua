import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('AI配置功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/settings');
  });

  test('AI配置页面加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText(/AI\s*配置/, { timeout: 15000 });
  });

  test('显示设置表单区域', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.settings-form')).toBeVisible();
  });

  test('有添加按钮', async ({ page }) => {
    await waitForBlazor(page);
    const addBtn = page.locator('button').filter({ hasText: /添加/ }).first();
    await expect(addBtn).toBeVisible();
  });
});
