// /learn 题库统计验证：点开题库后显示总题数/已刷/掌握/待复习/正确率，以及各家庭成员进度
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();

  const tokenResp = await page.request.post('http://127.0.0.1:5177/api/auth/cli-token');
  const { token } = await tokenResp.json();

  await page.goto(`http://127.0.0.1:5177/learn?cli-token=${token}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('.bank-card', { timeout: 20000 });
  console.log('✓ 题库列表渲染');

  await page.click('.bank-card');
  await page.waitForSelector('.player-select button', { timeout: 20000 });
  await page.waitForTimeout(600);

  const statsPanel = page.locator('.bank-stats');
  if (await statsPanel.count() === 0) throw new Error('点开题库后未显示统计面板');

  const statsText = (await statsPanel.textContent()).replace(/\s+/g, ' ').trim();
  console.log('✓ 题库统计：', statsText);

  for (const label of ['总题数', '已刷', '已掌握', '待复习', '正确率']) {
    if (!statsText.includes(label)) throw new Error(`统计面板缺少「${label}」：${statsText}`);
  }

  const statCount = await page.locator('.bank-stats .stat').count();
  if (statCount < 5) throw new Error(`统计项少于 5 个：${statCount}`);

  const learnerStats = await page.locator('.player-stat').allTextContents();
  console.log('✓ 家庭成员进度条数：', learnerStats.length);
  if (learnerStats.length === 0) {
    console.log('  （当前无家庭成员作答记录，跳过进度断言）');
  } else {
    console.log('  示例：', learnerStats[0].trim());
    if (!learnerStats[0].includes('已刷')) throw new Error('家庭成员进度未显示「已刷」：' + learnerStats[0]);
  }

  await page.screenshot({ path: 'tmp-learn-stats.png', fullPage: true });
  await browser.close();
  console.log('\n=== 题库统计展示验证通过 ===');
})().catch(e => { console.error('✗ 验证失败：', e.message); process.exit(1); });