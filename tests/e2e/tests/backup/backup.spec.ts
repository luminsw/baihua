import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('备份恢复功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/vaults');
    await waitForBlazor(page);
    await page.getByText('💾 备份恢复').click();
    await waitForBlazor(page);
  });

  test('备份恢复Tab加载成功', async ({ page }) => {
    await expect(page.getByText('💾 创建备份')).toBeVisible({ timeout: 15000 });
    await expect(page.getByText('📂 恢复备份')).toBeVisible();
  });

  test('创建备份区域有密码输入', async ({ page }) => {
    const passwordInput = page.locator('input[type="password"]').first();
    await expect(passwordInput).toBeVisible();
  });

  test('恢复备份区域有文件路径输入', async ({ page }) => {
    await expect(page.getByText('备份文件路径')).toBeVisible();
  });

  test('恢复备份有跨平台路径覆盖输入', async ({ page }) => {
    await expect(page.getByText('知识库根路径覆盖')).toBeVisible();
  });

  test('恢复备份有覆盖选项', async ({ page }) => {
    await expect(page.getByText('覆盖现有数据')).toBeVisible();
  });

  test('有验证和恢复按钮', async ({ page }) => {
    await expect(page.getByRole('button', { name: /验证备份/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /^恢复备份$/ })).toBeVisible();
  });

  test('备份列表区域可见', async ({ page }) => {
    await expect(page.getByText('已有备份')).toBeVisible();
  });

  test('创建备份按钮存在', async ({ page }) => {
    await expect(page.getByRole('button', { name: /创建全量备份/ })).toBeVisible();
  });
});
