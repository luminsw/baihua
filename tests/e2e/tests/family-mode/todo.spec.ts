import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize } from '../helpers';

// 个人待办清单（/todo）：家庭分类下的 TodoList 菜单页
// 覆盖：页面加载、新增、完成勾选、两步删除（仅中文 UI：产品固定 zh-CN，locale fixture 兼作回归护栏）
// 数据自清理：新增的测试项在本用例内删除；用例中断时残留项带 E2E- 前缀可手动清理。

test.describe('个人待办清单（TodoList）', () => {
  test.beforeEach(async ({ page }) => {
    await authorize(page);
    await navigateTo(page, '/todo');
    await waitForBlazor(page);
  });

  test('页面加载：标题与输入框可见', async ({ page }) => {
    await expect(page.locator('h1').first()).toContainText('待办清单', { timeout: 15000 });
    await expect(page.getByPlaceholder('添加新的待办…')).toBeVisible({ timeout: 15000 });
  });

  test('新增 → 勾选完成 → 两步删除 全流程', async ({ page }) => {
    const uniqueTitle = `E2E-待办-${Date.now()}`;
    const input = page.getByPlaceholder('添加新的待办…');

    // 1. 新增
    await input.fill(uniqueTitle);
    await page.getByRole('button', { name: '添加' }).click();
    const item = page.locator('li.todo-item', { hasText: uniqueTitle });
    await expect(item).toBeVisible({ timeout: 15000 });
    await expect(input).toHaveValue('');

    // 2. 勾选完成（行获得划线样式）
    await item.locator('input[type=checkbox]').check();
    await expect(item).toHaveClass(/todo-done/, { timeout: 15000 });

    // 3. 两步删除：第一次点击变成“确认删除？”，第二次真正删除
    await item.getByRole('button', { name: '删除' }).click();
    await expect(item.getByRole('button', { name: '确认删除？' })).toBeVisible();
    await item.getByRole('button', { name: '确认删除？' }).click();
    await expect(page.locator('li.todo-item', { hasText: uniqueTitle })).toHaveCount(0, { timeout: 15000 });
  });

  test('AI 生成：输入目标 → 预览确认 → 保存为一组待办', async ({ page }) => {
    // AI（本地 qwen2.5-7b）生成可能较慢，放宽超时
    test.setTimeout(300000);
    const goal = '发布arkts鸿蒙版花记';
    const goalInput = page.getByPlaceholder('输入目标，让 AI 拆解成可执行的待办，例如：办理护照 / 申请公租房…');
    const before = await page.locator('.todo-goal').count();

    // 1. 输入目标并点击 AI 生成 → 弹出预览确认框（此时未保存）
    await goalInput.fill(goal);
    await page.getByRole('button', { name: /AI 生成/ }).click();

    const modal = page.locator('.todo-preview-modal');
    await expect(modal).toBeVisible({ timeout: 180000 });
    await expect(modal.locator('li.todo-preview-item').first()).toBeVisible({ timeout: 15000 });
    // 预览阶段不应落库：目标卡片数量不变
    await expect(page.locator('.todo-goal')).toHaveCount(before, { timeout: 15000 });

    // 2. 确认保存 → 弹窗关闭、新目标卡片出现（AI 可能改写标题，不断言标题文案）
    await modal.getByRole('button', { name: '确认保存' }).click();
    await expect(modal).toHaveCount(0, { timeout: 15000 });
    const newGoal = page.locator('.todo-goal').nth(before);
    await expect(newGoal).toBeVisible({ timeout: 15000 });
    await expect(newGoal.locator('li.todo-item').first()).toBeVisible({ timeout: 15000 });
    await expect(goalInput).toHaveValue('');

    // 3. 数据自清理：两步删除新目标（级联删除其下待办）
    await newGoal.locator('.card-header').getByRole('button', { name: '删除', exact: true }).click();
    await newGoal.locator('.card-header').getByRole('button', { name: '删除目标及全部待办？', exact: true }).click();
    await expect(page.locator('.todo-goal')).toHaveCount(before, { timeout: 15000 });
  });

  test('API: 待办端点返回合法结构', async ({ request }) => {
    const apiBase = `http://127.0.0.1:${process.env.API_PORT || '8788'}`;
    const res = await request.get(`${apiBase}/api/todos`);
    expect(res.status()).toBe(200);
    const json = await res.json();
    expect(Array.isArray(json)).toBeTruthy();
  });
});
