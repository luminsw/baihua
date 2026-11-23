import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize, ensureTestData } from '../helpers';

// FAM-35：家长看板冒烟（FAM-20 重构后）
// 路由：/dashboard（FAM-20 未改路由，页面重构为两屏）
test.describe('家长看板功能（FAM-20 重构后）', () => {
  test.beforeEach(async ({ page }) => {
    await authorize(page);
    await ensureTestData(page);
    await navigateTo(page, '/dashboard');
  });

  test('看板加载：第一屏显示"今日三件事"区域且无 JS 报错', async ({ page }) => {
    const pageErrors: string[] = [];
    page.on('pageerror', (err) => pageErrors.push(err.message));

    await waitForBlazor(page);
    // 第一屏：今日三件事区域（FAM-20 AC1）
    await expect(page.locator('h2', { hasText: '今日三件事' })).toBeVisible({ timeout: 15000 });

    // 无 JS 报错（FAM-35-AC1）
    expect(pageErrors, `页面 JS 报错: ${pageErrors.join('; ')}`).toEqual([]);
  });

  test('第一屏显示连续打卡天数和最新成就', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('h2', { hasText: '连续打卡' })).toBeVisible();
    await expect(page.locator('h2', { hasText: '最新成就' })).toBeVisible();
  });

  test('页面有成员选择器（全部成员）', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('h2', { hasText: '今日三件事' })).toBeVisible();
    // 成员选择器：全部成员/单成员切换（FAM-20 AC5）
    const selector = page.locator('select, .member-selector, [class*="selector"]').first();
    await expect(selector).toBeVisible();
  });

  test('第二屏有成长时间线区域', async ({ page }) => {
    await waitForBlazor(page);
    // 滚动到第二屏（FAM-20 AC4）
    await page.locator('h2', { hasText: '今日三件事' }).scrollIntoViewIfNeeded();
    await expect(page.locator('h2', { hasText: '成长时间线' }).first()).toBeVisible({ timeout: 15000 });
  });
});

