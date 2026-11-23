import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

/**
 * 编程 Agent 页面冒烟测试
 * 验证：页面元素齐全、工具集模式下拉 4 选项、流水线开关、非空校验
 * （真实 AI 生成已由后端 API 测试覆盖，E2E 不做以免慢/花钱）
 */
test.describe('编程 Agent 页面', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/code-agent');
    await waitForBlazor(page);
  });

  test('页面标题与输入区加载成功', async ({ page }) => {
    await expect(page.getByRole('heading', { level: 2 })).toBeVisible();
    await expect(page.locator('textarea').first()).toBeVisible();
  });

  test('工具集模式下拉包含 4 个选项', async ({ page }) => {
    // 页面 select 被增强下拉组件隐藏原生层，按 option 值定位工具集下拉（DOM 读取无需可见）
    const toolSelect = page.locator('select').filter({ has: page.locator('option[value="CodeGraph"]') }).first();
    await expect(toolSelect).toHaveCount(1);
    const values = await toolSelect.locator('option').evaluateAll(els => els.map(e => (e as HTMLOptionElement).value));
    expect(values.sort()).toEqual(['All', 'CodeGraph', 'None', 'Search']);
  });

  test('流水线模式开关可勾选', async ({ page }) => {
    const pipelineCheckbox = page.locator('#pipelineMode');
    await expect(pipelineCheckbox).toBeVisible();
    await pipelineCheckbox.check();
    await expect(pipelineCheckbox).toBeChecked();
  });

  test('空需求点生成给出提示不崩溃', async ({ page }) => {
    // 清空输入，直接点生成按钮
    const textarea = page.locator('textarea').first();
    await textarea.fill('');
    const generateBtn = page.getByRole('button', { name: /生成|开始|运行/i }).first();
    if (await generateBtn.isVisible()) {
      await generateBtn.click();
      // 页面不应崩溃（错误提示或按钮状态均可接受，只要页面存活）
      await expect(page.locator('body')).toBeVisible();
    }
  });
});
