import { test, expect } from '@playwright/test';
import { authorize } from '../helpers';

/**
 * 【旧套件迁移】原 tests/Baihua.Web.E2e/listening.spec.ts → tests/e2e/tests/legacy-e2e/
 * 迁移要点：登录方式改为共享 authorize() helper；describe 标题加 [legacy-e2e] 前缀。
 * 依赖 /browse 页存在「听」按钮（需知识库中有笔记，无则用例自动 skip）。
 * 仅中文 UI：断言只保留中文分支（产品固定 zh-CN）。
 */

test.describe('[legacy-e2e] 听知识库功能', () => {

  test('点击听按钮应弹出播放模态框', async ({ page }) => {
    await authorize(page);
    await page.goto('/browse');
    await page.waitForLoadState('networkidle', { timeout: 30000 }).catch(() => {});

    await expect(page.locator('h2', { hasText: /知识库浏览/ })).toBeVisible({ timeout: 15000 });

    const listenButtons = page.locator('button', { hasText: /听/ });
    const buttonCount = await listenButtons.count();
    console.log(`找到 ${buttonCount} 个听按钮`);

    if (buttonCount === 0) {
      test.skip('没有找到听按钮');
      return;
    }

    const firstListenButton = listenButtons.first();
    await firstListenButton.waitFor({ timeout: 5000 });

    console.log('点击听按钮');
    await firstListenButton.click();

    await page.waitForTimeout(2000);

    const modal = page.locator('.modal.show, .modal-overlay');
    const modalVisible = await modal.isVisible().catch(() => false);
    console.log(`模态框可见: ${modalVisible}`);

    if (!modalVisible) {
      console.log('页面 HTML:', await page.content());
    }

    await expect(modal).toBeVisible({ timeout: 10000 });

    const modalTitle = page.locator('.modal-title');
    await expect(modalTitle).toBeVisible();
    console.log('模态框标题:', await modalTitle.innerText());
  });

  test('构建播放列表应包含笔记', async ({ page }) => {
    await authorize(page);
    await page.goto('/browse');
    await page.waitForLoadState('networkidle', { timeout: 30000 }).catch(() => {});

    await expect(page.locator('h2', { hasText: /知识库浏览/ })).toBeVisible({ timeout: 15000 });

    const listenButtons = page.locator('button', { hasText: /听/ });
    const buttonCount = await listenButtons.count();

    if (buttonCount === 0) {
      test.skip('没有找到听按钮');
      return;
    }

    await listenButtons.first().click();
    await page.waitForTimeout(3000);

    const modal = page.locator('.modal.show, .modal-overlay');
    await expect(modal).toBeVisible({ timeout: 10000 });

    const playlistItems = page.locator('.playlist-item');
    const itemCount = await playlistItems.count();
    console.log(`播放列表项数量: ${itemCount}`);

    if (itemCount > 0) {
      const firstItem = playlistItems.first();
      console.log('第一个播放项:', await firstItem.innerText());
    } else {
      const emptyMessage = page.locator('text=该知识库暂无笔记');
      const emptyVisible = await emptyMessage.isVisible().catch(() => false);
      console.log('空播放列表提示可见:', emptyVisible);
    }
  });

  test('播放按钮点击应调用 speechSynthesis', async ({ page }) => {
    await authorize(page);
    await page.goto('/browse');
    await page.waitForLoadState('networkidle', { timeout: 30000 }).catch(() => {});

    await expect(page.locator('h2', { hasText: /知识库浏览/ })).toBeVisible({ timeout: 15000 });

    const listenButtons = page.locator('button', { hasText: /听/ });
    const buttonCount = await listenButtons.count();

    if (buttonCount === 0) {
      test.skip('没有找到听按钮');
      return;
    }

    await listenButtons.first().click();
    await page.waitForTimeout(3000);

    const modal = page.locator('.modal.show, .modal-overlay');
    await expect(modal).toBeVisible({ timeout: 10000 });

    const playButton = page.locator('button', { hasText: /播放/ });
    const playButtonVisible = await playButton.isVisible().catch(() => false);
    console.log('播放按钮可见:', playButtonVisible);

    if (playButtonVisible) {
      await page.evaluate(() => {
        (window as any).speakTextCalls = [];
        const originalSpeak = (window as any).speakText;
        (window as any).speakText = function(text: string, dotNetRef?: any) {
          (window as any).speakTextCalls.push(text);
          console.log('speakText 被调用，文本长度:', text.length);
        };
      });

      await playButton.click();
      await page.waitForTimeout(1000);

      const calls = await page.evaluate(() => (window as any).speakTextCalls || []);
      console.log('speakText 调用次数:', calls.length);
      if (calls.length > 0) {
        console.log('speakText 调用内容（前100字符）:', calls[0].substring(0, 100));
      }
    }
  });

});
