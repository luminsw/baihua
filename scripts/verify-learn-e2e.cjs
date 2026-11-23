// /learn 页面端到端验证：登录→选库→选成员→答题→反馈→结算 全流程
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();

  // 1. cli-token 登录并进入 /learn
  const tokenResp = await page.request.post('http://127.0.0.1:5177/api/auth/cli-token');
  const { token } = await tokenResp.json();
  await page.goto(`http://127.0.0.1:5177/learn?cli-token=${token}`, { waitUntil: 'networkidle' });

  // 2. 题库选择页
  await page.waitForSelector('.bank-card', { timeout: 15000 });
  const bankName = await page.textContent('.bank-name');
  console.log('✓ 题库列表渲染：', bankName.trim());

  // 3. 选择题库 → 家庭成员页
  await page.click('.bank-card');
  await page.waitForSelector('.player-select button', { timeout: 15000 });
  console.log('✓ 成员选择页渲染，按钮数：', await page.locator('.player-select button').count());

  // 4. 选择成员 → 进入刷题
  await page.click('.player-select button >> nth=0');
  await page.waitForSelector('.learn-question', { timeout: 15000 });
  await page.waitForSelector('.stem-patient', { timeout: 15000 });
  console.log('✓ 刷题页渲染，患者：', (await page.textContent('.stem-patient')).slice(0, 30));
  console.log('  体征条数：', await page.locator('.stem-signs li').count());
  console.log('  选项数：', await page.locator('.option-btn').count());

  // 5. 作答（选第一项）→ 反馈
  await page.click('.option-btn >> nth=0');
  await page.waitForSelector('.learn-result', { timeout: 15000 });
  const head = await page.textContent('.result-head');
  console.log('✓ 判定反馈：', head.trim().slice(0, 40));
  const analysisLen = (await page.textContent('.result-analysis .analysis-body')).trim().length;
  console.log('  解析长度：', analysisLen);
  const discRows = await page.locator('.discrimination-table tr').count();
  console.log('  区分点行数：', discRows);
  console.log('  AI 按钮存在：', await page.locator('.ai-row button').count() === 1);

  // 6. 逐题答完 → 结算（每题答完点「下一题/查看结算」，等待 Blazor 重渲染稳定）
  for (let i = 0; i < 15; i++) {
    if (await page.locator('.learn-summary').count() > 0) break;
    const optionBtns = page.locator('.learn-question .option-btn');
    if (await optionBtns.count() > 0) {
      await optionBtns.first().click();
      await page.waitForSelector('.learn-result', { timeout: 10000 });
      await page.waitForTimeout(400);
    }
    const finish = page.locator('button:has-text("查看结算")');
    if (await finish.count() > 0) { await finish.first().click(); break; }
    const next = page.locator('button:has-text("下一题")');
    if (await next.count() > 0) {
      await next.first().click();
      await page.waitForTimeout(400);
    }
  }
  await page.waitForSelector('.learn-summary', { timeout: 15000 });
  const score = await page.textContent('.summary-score');
  console.log('✓ 结算页渲染，得分：', score.trim());

  await page.screenshot({ path: 'tmp-learn-summary.png', fullPage: true });
  console.log('✓ 截图已存 tmp-learn-summary.png');

  await browser.close();
  console.log('\n=== /learn 端到端验证全部通过 ===');
})().catch(e => { console.error('✗ 验证失败：', e.message); process.exit(1); });