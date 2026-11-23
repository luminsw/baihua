import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize, ensureTestData } from '../helpers';

// FAM-35：排行榜冒烟（FAM-22 友好化后）
// 路由：/leaderboard
test.describe('排行榜功能（FAM-22 后）', () => {
  test.beforeEach(async ({ page }) => {
    await authorize(page);
    await ensureTestData(page);
    await navigateTo(page, '/leaderboard');
  });

  test('默认显示"和自己比"视图（非全家庭排行）', async ({ page }) => {
    await waitForBlazor(page);
    // FAM-22 AC1：默认"和自己比"视图（本周 vs 上周）
    await expect(page.locator('text=和自己比').first()).toBeVisible({ timeout: 15000 });
    // 和自己比视图显示"上周"（AC2）
    await expect(page.locator('text=上周').first()).toBeVisible();
  });

  test('切换"家庭排行"显示孩子榜/大人榜 Tab', async ({ page }) => {
    await waitForBlazor(page);
    // 点击"家庭排行"视图切换（FAM-22 AC3）
    await page.locator('text=家庭排行').first().click();
    await expect(page.locator('text=孩子榜').first()).toBeVisible({ timeout: 15000 });
    await expect(page.locator('text=大人榜').first()).toBeVisible();
  });

  test('全家 Tab 默认隐藏（未开启设置时）', async ({ page }) => {
    await waitForBlazor(page);
    // 进入家庭排行视图（FAM-22 AC5：全家 Tab 默认关闭）
    await page.locator('text=家庭排行').first().click();
    await expect(page.locator('text=孩子榜').first()).toBeVisible({ timeout: 15000 });
    // 默认无"全家"Tab（allFamilyEnabled=false 条件渲染）
    await expect(page.locator('button:has-text("全家")')).toHaveCount(0);
  });
});

