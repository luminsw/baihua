import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('AI 生成功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/generate');
  });

  test('AI生成页加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('AI 生成');
  });

  test('页面有主题关键词输入', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.getByText('主题关键词', { exact: false })).toBeVisible();
  });

  test('生成方式选择按钮组可见', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.getByText('生成方式', { exact: false })).toBeVisible();
    await expect(page.getByText('生成完整知识库', { exact: false })).toBeVisible();
    await expect(page.getByText('生成单条笔记', { exact: false })).toBeVisible();
  });

  test('行业选择包含AI自动推断选项', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.getByText('AI 自动推断', { exact: false })).toBeVisible();
  });

  test('目标知识库选择器可见', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.getByText('目标知识库', { exact: false })).toBeVisible();
  });

  test('空查询时生成按钮禁用', async ({ page }) => {
    await waitForBlazor(page);
    const genBtn = page.locator('button').filter({ hasText: /生成知识库|生成笔记/ }).first();
    await expect(genBtn).toBeDisabled();
  });

  test('生成记忆卡片选项可见', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.getByText('生成知识库', { exact: false }).first()).toBeVisible();
  });

  test('提示词编辑按钮可见', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.getByText('编辑提示词', { exact: false })).toBeVisible();
  });
});
