import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('知识库浏览', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/browse');
  });

  test('浏览页加载成功', async ({ page }) => {
    await expect(page.locator('h2')).toContainText('知识库浏览');
  });

  test('知识库以卡片形式显示，不是下拉框', async ({ page }) => {
    // 不应该有下拉框
    await expect(page.locator('select.form-select')).not.toBeVisible();
    // 应该有知识库卡片
    const cards = page.locator('.vault-folder-card');
    await expect(cards.first()).toBeVisible({ timeout: 10000 });
    // 至少有一个知识库
    const count = await cards.count();
    expect(count).toBeGreaterThan(0);
  });

  test('点击知识库卡片进入浏览', async ({ page }) => {
    // 点击第一个知识库卡片
    await page.locator('.vault-folder-card').first().click();
    await waitForBlazor(page);

    // 应该显示"返回知识库列表"按钮（进入浏览态）
    await expect(page.locator('button').filter({ hasText: /返回知识库列表/ })).toBeVisible();

    // 面包屑应该显示进入的知识库名称（环境数据决定，不断言具体名）
    await expect(page.locator('.breadcrumb')).toBeVisible();
  });

  test('显示文件夹和笔记卡片', async ({ page }) => {
    await page.locator('.vault-folder-card').first().click();
    await waitForBlazor(page);

    // 检查是否有笔记卡片或文件夹卡片
    const noteCards = page.locator('.vault-note-card');
    const folderCards = page.locator('.vault-folder-card');

    const noteCount = await noteCards.count();
    const folderCount = await folderCards.count();

    expect(noteCount + folderCount).toBeGreaterThan(0);
  });

  test('点击文件夹进入子目录', async ({ page }) => {
    await page.locator('.vault-folder-card').first().click();
    await waitForBlazor(page);

    // 查找文件夹卡片并点击
    const folderCard = page.locator('.vault-folder-card').filter({ hasText: /基础/ });
    if (await folderCard.isVisible().catch(() => false)) {
      await folderCard.click();
      await waitForBlazor(page);

      // 面包屑应该更新
      await expect(page.locator('.breadcrumb')).toContainText('基础');
    }
  });

  test('点击笔记打开弹窗预览', async ({ page }) => {
    await page.locator('.vault-folder-card').first().click();
    await waitForBlazor(page);

    // 知识库内可能是 分类文件夹 → 笔记 两级结构：若当前是文件夹，先进入第一个文件夹
    const folderCard = page.locator('.vault-folder-card').first();
    if (await folderCard.isVisible().catch(() => false)) {
      await folderCard.click();
      await waitForBlazor(page);
    }

    // 查找笔记卡片并点击
    const noteCard = page.locator('.vault-note-card').first();
    await expect(noteCard).toBeVisible({ timeout: 10000 });
    await noteCard.click();
    await waitForBlazor(page);

    // 弹窗应该出现
    await expect(page.locator('.modal-title')).toBeVisible();

    // 关闭弹窗
    await page.locator('.modal .btn-close, .modal-footer .btn-secondary').first().click();
    await expect(page.locator('.modal-title')).not.toBeVisible();
  });

  test('返回知识库列表按钮有效', async ({ page }) => {
    await page.locator('.vault-folder-card').first().click();
    await waitForBlazor(page);

    // 点击返回
    await page.locator('button').filter({ hasText: /返回知识库列表/ }).click();
    await waitForBlazor(page);

    // 应该回到知识库列表视图
    await expect(page.locator('.vault-folder-card').first()).toBeVisible();
    await expect(page.locator('button').filter({ hasText: /返回知识库列表/ })).not.toBeVisible();
  });
});
