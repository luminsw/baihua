import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize, ensureTestData } from '../helpers';

// FAM-35 增值冒烟：Phase 3 新页面（FAM-30 互考 / FAM-31 成就奖励墙）
test.describe('Phase 3 新页面冒烟', () => {
  test('互考页加载：/family/quiz 显示对战设置', async ({ page }) => {
    await authorize(page);
    await ensureTestData(page);
    await navigateTo(page, '/family/quiz');
    await waitForBlazor(page);
    await expect(page.locator('h1', { hasText: '亲子互考' })).toBeVisible({ timeout: 15000 });
    // 对战设置或空态引导（至少 2 位成员才可对战）
    const setup = page.locator('.quiz-setup');
    const empty = page.locator('.quiz-empty');
    await expect(setup.or(empty).first()).toBeVisible();
  });

  test('成就奖励页加载：显示家庭奖励区域', async ({ page }) => {
    await authorize(page);
    await ensureTestData(page);
    await navigateTo(page, '/achievements');
    await waitForBlazor(page);
    // 奖励进度区域（FAM-31：进度条或空态引导）
    const rewardSection = page.locator('.reward-section');
    await expect(rewardSection.first()).toBeVisible({ timeout: 15000 });
    const progress = page.locator('.reward-item, .reward-empty');
    await expect(progress.first()).toBeVisible();
  });
});
