import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('搜索功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/search');
  });

  test('搜索页加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('搜索');
  });

  test('搜索页有搜索输入框', async ({ page }) => {
    const input = page.locator('input.form-control').first();
    await expect(input).toBeVisible({ timeout: 10000 });
  });

  test('搜索页有搜索按钮', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('button').filter({ hasText: /搜索/ })).toBeVisible();
  });

  test('搜索页有行业筛选（有知识库数据时渲染）', async ({ page }) => {
    await waitForBlazor(page);
    // 行业筛选区仅在 vaults.Count > 0 时渲染；无数据时空态也属正常
    const vaultCount = await page.locator('.local-search select').count();
    if (vaultCount > 0) {
      await expect(page.locator('.local-search label').filter({ hasText: '行业' })).toBeVisible();
    }
  });

  test('搜索页有知识库筛选（有知识库数据时渲染）', async ({ page }) => {
    await waitForBlazor(page);
    // 知识库下拉在 vaults.Count > 0 时渲染
    const vaultCount = await page.locator('.local-search select').count();
    if (vaultCount > 0) {
      await expect(page.locator('.local-search label').filter({ hasText: '知识库' })).toBeVisible();
    }
  });

  test('输入搜索词后搜索按钮可用', async ({ page }) => {
    await waitForBlazor(page);
    const input = page.locator('input.form-control').first();
    await input.fill('测试搜索');
    await waitForBlazor(page);

    const searchBtn = page.locator('button').filter({ hasText: /搜索/ });
    await expect(searchBtn).toBeEnabled();
  });

  test('搜索页显示搜索提示', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('text=搜索提示')).toBeVisible();
  });
});
