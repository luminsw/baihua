import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize } from '../helpers';

// 本地模型部署页（单表收敛后）行为回归锚。
// 覆盖：页面加载、模型表渲染、删除流程（弹窗 + 确认 + 成功消息）。

test.describe('本地模型部署页', () => {
  test.beforeEach(async ({ page }) => {
    await authorize(page);
    await navigateTo(page, '/local-models');
    await waitForBlazor(page);
  });

  test('页面加载：标题可见', async ({ page }) => {
    await expect(page.locator('h1').first()).toBeVisible({ timeout: 20000 });
  });

  test('模型表渲染：表头与模型行可见', async ({ page }) => {
    await expect(page.locator('table').first()).toBeVisible({ timeout: 20000 });
    // 表头列（模型 / 参数 / 大小 / 用途 / 工具 / 状态 / 操作）
    await expect(page.getByText('模型', { exact: true }).first()).toBeVisible();
    await expect(page.getByText('工具', { exact: true }).first()).toBeVisible();
  });

  test('删除流程：确认弹窗出现并可取消', async ({ page }) => {
    // 等模型表渲染
    await expect(page.locator('table tbody tr').first()).toBeVisible({ timeout: 20000 });
    // 点击第一行的删除按钮
    await page.locator('table tbody tr').first().getByRole('button', { name: '删除' }).click();
    // 确认弹窗标题可见
    await expect(page.getByRole('heading', { name: /确认删除/ })).toBeVisible();
    // 取消
    await page.getByRole('button', { name: '取消' }).click();
    // 弹窗关闭
    await expect(page.getByRole('heading', { name: /确认删除/ })).toHaveCount(0);
  });

  test('删除流程：确认后模型从列表移除', async ({ page }) => {
    // 等模型表渲染
    await expect(page.locator('table tbody tr').first()).toBeVisible({ timeout: 20000 });
    // 记录第一个模型的名字
    const firstRow = page.locator('table tbody tr').first();
    const modelName = (await firstRow.locator('td').first().innerText()).trim();
    expect(modelName.length).toBeGreaterThan(0);

    // 点击删除 → 确认
    await firstRow.getByRole('button', { name: '删除' }).click();
    await expect(page.getByText(`确定要删除模型 ${modelName} 吗？`)).toBeVisible();
    await page.getByRole('button', { name: '确认删除' }).click();

    // 成功消息出现
    await expect(page.getByText(/已删除/)).toBeVisible({ timeout: 20000 });

    // 该模型从列表移除
    await expect(page.locator('table tbody tr', { hasText: modelName })).toHaveCount(0);
  });
});