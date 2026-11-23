import { test, expect } from '@playwright/test';

test('首页花记设备区域纵向排列：二维码→待授权→已授权', async ({ page }) => {
  await page.goto('/');
  // 等待设备区域出现
  const deviceStack = page.locator('.device-stack');
  await expect(deviceStack).toBeVisible({ timeout: 15000 });

  // 获取所有子卡片
  const cards = deviceStack.locator('.card');
  const count = await cards.count();
  console.log(`设备区域卡片数量: ${count}`);
  expect(count).toBe(3);

  // 验证顺序：第1个是扫码配对（qr-card），第2个是待授权（pending-card），第3个是已授权（device-card）
  const firstCardClass = await cards.nth(0).getAttribute('class');
  const secondCardClass = await cards.nth(1).getAttribute('class');
  const thirdCardClass = await cards.nth(2).getAttribute('class');

  console.log(`第1个卡片 class: ${firstCardClass}`);
  console.log(`第2个卡片 class: ${secondCardClass}`);
  console.log(`第3个卡片 class: ${thirdCardClass}`);

  expect(firstCardClass).toContain('qr-card');
  expect(secondCardClass).toContain('pending-card');
  expect(thirdCardClass).toContain('device-card');

  // 验证纵向排列：device-stack 是 flex-direction: column
  const flexDirection = await deviceStack.evaluate(el => {
    return getComputedStyle(el).flexDirection;
  });
  console.log(`flex-direction: ${flexDirection}`);
  expect(flexDirection).toBe('column');

  // 验证各卡片标题可见
  await expect(cards.nth(0).locator('h3')).toBeVisible();
  await expect(cards.nth(1).locator('h3')).toBeVisible();
  await expect(cards.nth(2).locator('h3')).toBeVisible();

  console.log('✅ 首页设备区域布局验证通过：纵向排列，顺序为 扫码配对→待授权→已授权');
});