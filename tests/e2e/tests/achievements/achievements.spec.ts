import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('成就墙功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/achievements');
  });

  test('成就墙页面加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('成就墙', { timeout: 15000 });
  });

  test('显示学习者选择栏', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.learner-bar')).toBeVisible();
  });

  test('有添加学习者按钮', async ({ page }) => {
    await waitForBlazor(page);
    const addBtn = page.locator('.add-learner');
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    await expect(page.locator('.add-learner-form')).toBeVisible();
  });
});
