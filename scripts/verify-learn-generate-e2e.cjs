// /learn/generate 页面端到端验证（粘贴题库包 → 校验预览 → 删除一题 → 确认导入）
// 说明：不依赖 AI provider，走「粘贴题库包」通道，验证生成页的校验/预览/append 导入全链路。
const { chromium } = require('playwright');

const stamp = Date.now().toString().slice(-8);
const INDUSTRY = 'e2e_ind';
const BANK = 'e2e_' + stamp;

function makeQuestion(code, itemCode, stem, analysis) {
  return {
    questionCode: code,
    itemCode,
    questionType: 'single_choice',
    target: 'formula',
    stem,
    stemDetail: { patient: 'E2E 测试对象，男，40 岁', signs: ['条件一', '条件二'] },
    options: [
      { key: 'A', content: '错误选项甲', isCorrect: false, explanation: '与条件二不符' },
      { key: 'B', content: '正确选项', isCorrect: true, explanation: '同时满足两个条件' },
      { key: 'C', content: '错误选项乙', isCorrect: false, explanation: '缺少条件一' },
      { key: 'D', content: '错误选项丙', isCorrect: false, explanation: '方向相反' },
    ],
    analysis,
    discrimination: [{ versus: '错误选项甲', point: '是否具备条件二' }],
    difficulty: 3,
    sourceRef: '',
  };
}

const packageJson = JSON.stringify({
  formatVersion: 1,
  industry: { code: INDUSTRY, name: 'E2E 测试行业' },
  bank: { code: BANK, name: 'E2E 测试题库 ' + stamp, version: 'v1', description: 'playwright 自动验证' },
  items: [
    {
      itemCode: 'e2e_item_1',
      itemType: 'concept',
      name: 'E2E 条目一',
      summary: '自动验证用条目',
      content: {
        indications: ['情形一'],
        discriminations: [{ versus: '条目二', point: '关键区分点' }],
      },
    },
  ],
  questions: [
    makeQuestion('e2e_q_1', 'e2e_item_1', 'E2E 题干一：根据条件选择最合适的答案。', '解析一：条件一与条件二共同指向正确选项。'),
    makeQuestion('e2e_q_2', 'e2e_item_1', 'E2E 题干二：根据条件选择最合适的答案。', '解析二：由条件一可排除错误选项。'),
  ],
}, null, 2);

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();

  const tokenResp = await page.request.post('http://127.0.0.1:5177/api/auth/cli-token');
  const { token } = await tokenResp.json();

  await page.goto(`http://127.0.0.1:5177/learn/generate?cli-token=${token}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('.source-tabs', { timeout: 20000 });
  console.log('✓ 生成页渲染');

  const hasTopicField = await page.locator('label:has-text("生成主题")').count();
  if (!hasTopicField) throw new Error('缺少生成主题字段');
  console.log('✓ 表单字段齐全');

  await page.click('button:has-text("粘贴题库包 JSON")');
  await page.waitForTimeout(400);

  const pasteBox = page.locator('textarea[placeholder^="{"]');
  if (await pasteBox.count() !== 1) throw new Error('未找到粘贴 JSON 输入框');
  await pasteBox.fill(packageJson);
  console.log('✓ 已粘贴题库包 JSON');

  await page.click('button:has-text("校验并预览")');
  await page.waitForSelector('.preview-q', { timeout: 20000 });
  await page.waitForTimeout(400);

  const before = await page.locator('.preview-q').count();
  if (before !== 2) throw new Error(`预览题目数应为 2，实际 ${before}`);
  console.log('✓ 校验并进入预览，题目数：', before);
  console.log('  预览头：', (await page.textContent('.meta-line')).replace(/\s+/g, ' ').trim().slice(0, 80));

  const errorPanel = await page.locator('.alert-error').count();
  if (errorPanel > 0) throw new Error('预览出现校验错误：' + (await page.textContent('.alert-error')));
  console.log('✓ 校验无 errors');

  await page.click('.preview-q >> nth=0 >> button:has-text("删除")');
  await page.waitForTimeout(1200);
  const after = await page.locator('.preview-q').count();
  if (after !== 1) throw new Error(`删除后题目数应为 1，实际 ${after}`);
  console.log('✓ 删除一题后重新校验，题目数：', after);

  const importBtn = page.locator('button:has-text("确认导入")');
  if (await importBtn.isDisabled()) throw new Error('校验通过后导入按钮不应禁用');
  await importBtn.click();
  await page.waitForSelector('.success-panel', { timeout: 20000 });
  await page.waitForTimeout(400);

  const successText = (await page.textContent('.success-panel')).replace(/\s+/g, ' ').trim();
  console.log('✓ 导入成功：', successText.slice(0, 90));

  // 回题库页确认新题库出现
  await page.goto(`http://127.0.0.1:5177/learn?cli-token=${token}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('.bank-card', { timeout: 20000 });
  const bankCards = await page.locator('.bank-name').allTextContents();
  if (!bankCards.some(t => t.includes('E2E 测试题库'))) {
    throw new Error('题库列表未出现新建的 E2E 题库：' + bankCards.join(' | '));
  }
  console.log('✓ 题库列表已出现新建题库');

  await page.screenshot({ path: 'tmp-learn-generate.png', fullPage: true });
  console.log('✓ 截图已存 tmp-learn-generate.png');

  await browser.close();
  console.log(`\n=== /learn/generate 端到端验证全部通过 ===`);
  console.log(`E2E 写入：industry=${INDUSTRY} bank=${BANK}`);
})().catch(e => { console.error('✗ 验证失败：', e.message); process.exit(1); });