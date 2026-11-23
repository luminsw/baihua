import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('每日一帖功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/daily-card');
  });

  test('每日一帖页面加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('每日一帖', { timeout: 15000 });
  });

  test('有知识库选择区域', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.vault-selector')).toBeVisible();
  });

  test('有进度显示区域', async ({ page }) => {
    await waitForBlazor(page);
    const progress = page.locator('.progress-section');
    const noCard = page.locator('.no-card');
    await expect(progress.or(noCard)).toBeVisible({ timeout: 10000 });
  });

  test('有翻转提示或完成提示', async ({ page }) => {
    await waitForBlazor(page);
    const flipHint = page.locator('.card-hint');
    const noCard = page.locator('.no-card');
    await expect(flipHint.or(noCard)).toBeVisible({ timeout: 10000 });
  });

  test('有难度选择或完成提示', async ({ page }) => {
    await waitForBlazor(page);
    const cardContainer = page.locator('.card-container');
    const noCard = page.locator('.no-card');
    await expect(cardContainer.or(noCard)).toBeVisible({ timeout: 10000 });
  });
});
