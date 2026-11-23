// 家庭首页 + /learn 多行业文案验证：首页入口、题库卡片、刷题页区分点标题
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();

  const tokenResp = await page.request.post('http://127.0.0.1:5177/api/auth/cli-token');
  const { token } = await tokenResp.json();

  await page.goto(`http://127.0.0.1:5177/family?cli-token=${token}`, { waitUntil: 'networkidle' });

  const body = await page.textContent('body');
  if (!body.includes('题库学习')) throw new Error('首页缺少「题库学习」');
  if (!body.includes('多行业题库')) throw new Error('首页缺少「多行业题库」');
  if (body.includes('经方题库') || body.includes('辨证选方')) {
    throw new Error('首页仍有中医专用入口文案');
  }
  console.log('✓ 家庭首页入口已改为多行业文案');

  const learnLink = page.locator('a[href="/learn"]').first();
  if (await learnLink.count() === 0) throw new Error('首页缺少 /learn 入口');
  await learnLink.click();
  await page.waitForSelector('.bank-card', { timeout: 20000 });

  const metaText = (await page.locator('.bank-meta').first().textContent()).replace(/\s+/g, ' ').trim();
  if (metaText.includes('方剂')) throw new Error('题库卡片仍显示「方剂」：' + metaText);
  if (!metaText.includes('条目')) throw new Error('题库卡片缺少「条目」：' + metaText);
  console.log('✓ 题库卡片文案通用：', metaText);

  await page.click('.bank-card');
  await page.waitForSelector('.player-select button', { timeout: 20000 });
  await page.click('.player-select button >> nth=0');
  await page.waitForSelector('.learn-question', { timeout: 20000 });
  await page.click('.option-btn >> nth=0');
  await page.waitForSelector('.learn-result', { timeout: 20000 });
  await page.waitForTimeout(400);

  const html = await page.content();
  if (html.includes('易混淆方')) throw new Error('刷题页仍显示「易混淆方」');
  const discTitle = page.locator('.result-discrimination .analysis-title');
  const discCount = await discTitle.count();
  if (discCount > 0) {
    const text = await discTitle.first().textContent();
    if (!text.includes('易混淆项')) throw new Error('区分点标题未通用化：' + text);
  }
  console.log('✓ 刷题页文案通用，区分点区块存在：', discCount > 0);

  await browser.close();
  console.log('\n=== 家庭首页与题库页多行业文案验证通过 ===');
})().catch(e => { console.error('✗ 验证失败：', e.message); process.exit(1); });