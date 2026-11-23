import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize, ensureTestData } from '../helpers';

// FAM-35：学习打卡页冒烟（FAM-21）
// 路由：/family/checkin
test.describe('学习打卡页功能（FAM-21）', () => {
  test.beforeEach(async ({ page }) => {
    await authorize(page);
    await ensureTestData(page);
    await navigateTo(page, '/family/checkin');
  });

  test('打卡页加载：显示连续打卡天数', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('h1, h2', { hasText: '打卡' }).first()).toBeVisible({ timeout: 15000 });
    await expect(page.locator('text=连续打卡').first()).toBeVisible({ timeout: 15000 });
  });

  test('显示最近 7 天打卡日历（🔥/⬜ 格子）', async ({ page }) => {
    await waitForBlazor(page);
    // 7 天日历：.cal-cell 格子（FAM-21 AC4）
    await expect(page.locator('.cal-cell').first()).toBeVisible({ timeout: 15000 });
    const cellCount = await page.locator('.cal-cell').count();
    expect(cellCount, '打卡日历应显示 7 天').toBeGreaterThanOrEqual(7);
  });

  test('显示今日学习清单（按 Learner 分组）', async ({ page }) => {
    await waitForBlazor(page);
    // 今日清单：h2 标题（FAM-21 AC1）；无记录时显示空态引导
    await expect(page.locator('h2', { hasText: '今日学习清单' })).toBeVisible({ timeout: 15000 });
    const emptyState = page.locator('.empty-state');
    const recordItems = page.locator('.record-item');
    if (await emptyState.count() > 0) {
      await expect(emptyState).toBeVisible();
    } else {
      await expect(recordItems.first()).toBeVisible();
    }
  });

  test('空状态引导 CTA 指向每日卡片', async ({ page }) => {
    await waitForBlazor(page);
    const emptyCta = page.locator('a.cta-btn', { hasText: '前往每日卡片' }).first();
    if (await emptyCta.count() > 0) {
      // 空态存在时验证 CTA 跳转 /daily-card（FAM-21 AC2）
      const href = await emptyCta.getAttribute('href');
      expect(href).toContain('daily-card');
    }
    // 有数据时不强制空态（非空环境跳过）
  });
});
