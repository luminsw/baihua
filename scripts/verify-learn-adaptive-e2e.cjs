// 真实 E2E（会调用 AI）：自适应两阶段生成 → 任务页合并列表 → 关闭页面重开自动打开结果 → 任务页深链
//
// 用法：先 `bh start`（8788/5177 已构建当前代码），再 `node scripts/verify-learn-adaptive-e2e.cjs`
// 耗时：约 4-10 分钟（真实 AI 生成）；不导入题库、不改动题库数据。
//
// 注意（历史坑）：WebUI 的 CLI Token 只有 5 分钟有效期（AuthService.CliTokenExpiryMinutes）。
// 生成动辄 6-8 分钟，所以「重开页面」必须重新取 token，否则落回 /login，看起来像「自动打开失败」。
const { chromium } = require('playwright');

const BASE = 'http://127.0.0.1:5177';
const API = 'http://127.0.0.1:8788';
const BANK = '自适应验证库_' + Date.now().toString().slice(-6);
const TOPIC = '临床常用配穴方法（俞募、原络、五输）与常见病证取穴';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function newToken(browser) {
  const page = await browser.newPage();
  const resp = await page.request.post(`${BASE}/api/auth/cli-token`);
  const { token } = await resp.json();
  await page.close();
  return token;
}

async function waitJobDone(request, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < deadline) {
    const resp = await request.get(`${API}/api/learn/generate/jobs?limit=5`);
    if (resp.ok()) {
      const jobs = await resp.json();
      const job = jobs.find((j) => (j.bankName || '') === BANK);
      if (job) {
        last = job;
        if (['succeeded', 'failed', 'canceled'].includes(job.status)) return job;
      }
    }
    await sleep(3000);
  }
  return last;
}

async function waitPreview(page, timeoutMs, label) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const count = await page.locator('.preview-q').count();
    if (count > 0) return count;
    await sleep(500);
  }
  const url = page.url();
  const alert = await page.locator('.alert-error').allTextContents();
  throw new Error(`${label} 未出现预览（url=${url} alert=${JSON.stringify(alert)}）`);
}

(async () => {
  const browser = await chromium.launch();
  const token = await newToken(browser);
  const url = (p) => `${BASE}${p}${p.includes('?') ? '&' : '?'}cli-token=${token}`;

  const page = await browser.newPage();
  page.setDefaultTimeout(30000);
  const setSelectByOptionText = async (locator, text) => {
    await locator.evaluate((el, t) => {
      const opt = Array.from(el.options).find((o) => o.textContent.includes(t));
      if (!opt) throw new Error('选项不存在：' + t);
      el.value = opt.value;
      el.dispatchEvent(new Event('change', { bubbles: true }));
    }, text);
  };

  // 1) 生成页
  await page.goto(url('/learn/generate'), { waitUntil: 'networkidle' });
  await page.waitForSelector('.source-tabs, .preview-q');

  if ((await page.locator('.preview-q').count()) > 0) {
    // 上次跑留下的成功任务被自动打开了：返回表单开始新任务
    await page.click('button:has-text("返回修改")');
    await page.waitForSelector('.source-tabs');
    console.log('✓ 已从历史结果返回表单（自动打开生效）');
  }

  // 2) 填表：题库名 + 主题 + 自适应题量
  await page.locator('label:has-text("题库名称") + input').fill(BANK);
  await page.locator('.form-grid textarea').fill(TOPIC);
  await setSelectByOptionText(page.locator('.form-grid select').nth(2), '自适应');
  console.log('✓ 已填写：题库名=' + BANK + '，题量=自适应');

  // 3) 创建后台任务
  await page.click('button:has-text("开始生成")');
  await page.waitForSelector('.job-card');
  await sleep(2500);
  const cardText = (await page.locator('.job-card').textContent()).replace(/\s+/g, ' ').trim();
  console.log('✓ 任务已创建：', cardText.slice(0, 100));

  // 4) 任务页：生成任务并入原任务列表（不再单独区块）
  await page.goto(url('/tasks'), { waitUntil: 'networkidle' });
  await page.waitForSelector('.task-list');
  await sleep(1200);
  if ((await page.locator('.learn-jobs').count()) > 0) throw new Error('仍有独立 learn-jobs 区块');
  const listText = (await page.locator('.task-list').textContent()).replace(/\s+/g, ' ').trim();
  if (!listText.includes('题库生成') || !listText.includes(BANK)) {
    throw new Error('/tasks 未在任务列表显示生成任务');
  }
  console.log('✓ /tasks 已合并显示生成任务');

  // 5) 等后端任务完成（真实 AI）
  const job = await waitJobDone(page.request, 15 * 60 * 1000);
  if (!job || job.status !== 'succeeded') {
    throw new Error(`任务未成功：${job ? job.status + ' ' + (job.error || '') : '未找到'}`);
  }
  console.log('✓ 后端任务完成：', job.status, job.message);

  // 6) 关掉页面，用**新 token**重开：应自动打开刚生成的结果
  await page.close();
  const token2 = await newToken(browser);
  const page2 = await browser.newPage();
  page2.setDefaultTimeout(30000);
  await page2.goto(`${BASE}/learn/generate?cli-token=${token2}`, { waitUntil: 'networkidle' });
  const reopened = await waitPreview(page2, 60000, '重开页面自动打开');
  const head = (await page2.locator('.preview-head').textContent()).replace(/\s+/g, ' ').trim();
  if (!head.includes(BANK)) throw new Error('自动打开的不是本次任务结果：' + head);
  console.log(`✓ 关闭后重开自动打开结果：题目数=${reopened} | ${head.slice(0, 100)}`);

  // 7) 任务页深链：点这条任务 → /learn/generate?job=<id> 精确打开
  await page2.goto(`${BASE}/tasks?cli-token=${token2}`, { waitUntil: 'networkidle' });
  await page2.waitForSelector('.task-list');
  await page2.locator('.task-item', { hasText: BANK }).first().click();
  await page2.waitForURL(/\/learn\/generate\?job=/, { timeout: 30000 });
  const deepLinkCount = await waitPreview(page2, 60000, '任务深链打开');
  const deepHead = (await page2.locator('.preview-head').textContent()).replace(/\s+/g, ' ').trim();
  if (!deepHead.includes(BANK)) throw new Error('深链打开的不是该任务结果：' + deepHead);
  console.log(`✓ 任务页深链精确打开：job=${job.jobId} 题目数=${deepLinkCount}`);

  // 8) /tasks 终态
  await page2.goto(`${BASE}/tasks?cli-token=${token2}`, { waitUntil: 'networkidle' });
  await page2.waitForSelector('.task-list');
  await sleep(1200);
  const finalText = (await page2.locator('.task-list').textContent()).replace(/\s+/g, ' ').trim();
  if (!finalText.includes('已完成')) throw new Error('/tasks 未显示已完成');
  console.log('✓ /tasks 最终显示：已完成');

  await browser.close();
  console.log('\n=== 自适应两阶段 + 任务列表合并 + 自动打开 + 深链 全部通过 ===');
  console.log(`本次任务：${job.jobId}（题库名 ${BANK}，未导入题库）`);
})().catch((e) => {
  console.error('✗ 验证失败：', e.message);
  process.exit(1);
});
