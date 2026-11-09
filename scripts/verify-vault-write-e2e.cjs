// 端到端验证：服务端写文件权限（MCP 写笔记 → 读回 → /browse 页面可见）
//
// 背景：曾出现「服务能读不能写」（知识库写笔记 / 索引快照 / 自动备份全失败）。
// 根因是构建产物目录被打了 Low 完整性标签，Windows 据此让 bh-server.exe 以 Low IL 运行，
// 对 Medium 对象只有读权限。详见 docs/PLAN_NATIVE_CELL.md「Low 完整性标签」。
// 本脚本是该问题的回归锁：写不进去就立即失败。
//
// 用法：先 bh start（8788/5177 已起），再 `node scripts/verify-vault-write-e2e.cjs`
// 可用环境变量覆盖：BAIHUA_VAULT_ID / BAIHUA_VAULT_NAME / BAIHUA_BASE / BAIHUA_API
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const BASE = process.env.BAIHUA_BASE || 'http://127.0.0.1:5177';
const API = process.env.BAIHUA_API || 'http://127.0.0.1:8788';
const VAULT_ID = process.env.BAIHUA_VAULT_ID || '4b8d003ecbce45bbad26af3d8424cc30'; // 用AI赚钱
const VAULT_NAME = process.env.BAIHUA_VAULT_NAME || '用AI赚钱';
const VAULT_DIR = process.env.BAIHUA_VAULT_DIR || 'C:\\Users\\lumin\\.baihua\\vaults\\local\\商业\\用AI赚钱';

const stamp = Date.now().toString().slice(-6);
const NOTE_NAME = `_回归验证-写权限-${stamp}`;
const NOTE_PATH = `${NOTE_NAME}.md`;
const MARKER = `写权限回归验证 ${stamp}`;

/** MCP streamable-http：响应是 SSE，取 data: 行的 JSON */
async function mcpCall(body) {
  const resp = await fetch(`${API}/mcp`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
    body: JSON.stringify(body),
  });
  const text = await resp.text();
  const line = text.split(/\r?\n/).find((l) => l.startsWith('data: '));
  if (!line) throw new Error('MCP 响应没有 data 行：' + text.slice(0, 200));
  return JSON.parse(line.slice(6));
}

async function toolCall(id, name, args) {
  const payload = await mcpCall({ jsonrpc: '2.0', id, method: 'tools/call', params: { name, arguments: args } });
  if (payload.error) throw new Error(`MCP ${name} 出错：${JSON.stringify(payload.error)}`);
  const content = payload.result?.content?.[0]?.text ?? '';
  let parsed = null;
  try { parsed = JSON.parse(content); } catch { /* 非 JSON 文本 */ }
  return { raw: content, json: parsed };
}

(async () => {
  // 1) MCP initialize
  await mcpCall({
    jsonrpc: '2.0', id: 1, method: 'initialize',
    params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'verify-vault-write', version: '1.0' } },
  });
  console.log('✓ MCP initialize');

  // 2) 写笔记（服务端落盘）
  const write = await toolCall(2, 'baihua_vault_write_note', {
    vaultId: VAULT_ID, path: NOTE_PATH, content: `# ${MARKER}\n\n服务端写文件回归验证。\n`,
  });
  if (!write.json || write.json.ok !== true) {
    throw new Error('写笔记失败（服务端不能写文件）：' + write.raw);
  }
  console.log('✓ 服务端写笔记成功');

  const onDisk = path.join(VAULT_DIR, 'notes', NOTE_PATH);
  if (!fs.existsSync(onDisk)) throw new Error('笔记文件没有真正落盘：' + onDisk);
  console.log('✓ 文件已落盘：', fs.statSync(onDisk).size, '字节');

  // 3) 读回（MCP）
  const read = await toolCall(3, 'baihua_vault_read_note', { vaultId: VAULT_ID, path: NOTE_PATH });
  const content = read.json?.note?.content ?? '';
  if (!content.includes(MARKER)) throw new Error('读回内容与写入不一致：' + content.slice(0, 120));
  console.log('✓ 读回内容一致');

  // 4) 页面可见（/browse → 知识库 → 笔记 → 详情弹窗）
  const browser = await chromium.launch();
  const page = await browser.newPage();
  page.setDefaultTimeout(30000);
  const { token } = await (await page.request.post(`${BASE}/api/auth/cli-token`)).json();
  await page.goto(`${BASE}/browse?cli-token=${token}`, { waitUntil: 'networkidle' });

  const vaultCard = page.locator('.vault-folder-card', { hasText: VAULT_NAME }).first();
  await vaultCard.waitFor({ timeout: 20000 });
  await vaultCard.locator('div.fw-bold').first().click();
  const noteCard = page.locator('.vault-note-card', { hasText: NOTE_NAME }).first();
  await noteCard.waitFor({ timeout: 20000 });
  console.log('✓ /browse 列出了新笔记');
  await noteCard.click();
  await page.waitForSelector('.modal-body', { timeout: 20000 });
  await page.waitForFunction(
    (marker) => (document.querySelector('.modal-body')?.textContent || '').includes(marker),
    MARKER, { timeout: 20000 });
  console.log('✓ 笔记详情弹窗内容正确（markdown 已渲染）');
  await browser.close();

  // 5) 清理
  fs.unlinkSync(onDisk);
  console.log('✓ 已清理测试笔记');
  console.log('\n=== 服务端写文件（MCP + 页面）全部通过 ===');
})().catch((e) => {
  console.error('✗ 验证失败：', e.message);
  process.exit(1);
});
