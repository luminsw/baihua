import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize } from '../helpers';

// 本地模型注册表页（纯展示 + Agent 初始化提示词）行为回归锚。

test.describe('本地模型注册表页', () => {
  test.beforeEach(async ({ page }) => {
    await authorize(page);
    await navigateTo(page, '/local-models');
    await waitForBlazor(page);
  });

  test('页面加载：标题可见', async ({ page }) => {
    await expect(page.locator('h1').first()).toBeVisible({ timeout: 20000 });
    await expect(page.locator('h1')).toContainText('本地大模型');
  });

  test('提示词卡片：标题与复制按钮可见', async ({ page }) => {
    await expect(page.locator('.prompt-card')).toBeVisible({ timeout: 20000 });
    await expect(page.locator('.prompt-card h2')).toContainText('Agent 初始化提示词');
    await expect(page.getByRole('button', { name: /一键复制/ })).toBeVisible();
  });

  test('提示词内容：包含关键步骤', async ({ page }) => {
    const promptText = page.locator('.prompt-text');
    await expect(promptText).toBeVisible({ timeout: 20000 });
    await expect(promptText).toContainText('诊断硬件');
    await expect(promptText).toContainText('baihua_local_model_list');
    await expect(promptText).toContainText('baihua_local_model_register');
  });

  test('注册表渲染：空表显示引导或表格可见', async ({ page }) => {
    // 页面要么显示空表引导文案，要么显示注册表表格
    const alert = page.locator('.alert-info');
    const table = page.locator('table');
    await expect(alert.or(table)).toBeVisible({ timeout: 20000 });
  });
});
