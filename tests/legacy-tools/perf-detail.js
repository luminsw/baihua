const { chromium } = require('playwright');

const PAGES = [
  { path: '/', name: '首页' },
  { path: '/openclaw', name: 'OpenClaw' },
  { path: '/local-models', name: '本地模型' },
  { path: '/settings', name: 'AI 设置' },
  { path: '/vaults', name: '知识库' },
  { path: '/search', name: '搜索' },
  { path: '/messages', name: 'AI 对话' },
  { path: '/health', name: '健康检查' },
];

async function main() {
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();

  for (const { path, name } of PAGES) {
    console.log(`\n=== ${name} (${path}) ===`);
    await page.goto('http://localhost:5177' + path, { waitUntil: 'networkidle', timeout: 30000 });

    const apis = await page.evaluate(() => {
      return (window.performance.getEntriesByType('resource') || [])
        .filter(r => r.name.includes('/api/') || r.name.includes(':8788'))
        .map(r => ({
          name: r.name.replace(/.*:\/\/[^/]+/, ''),
          duration: Math.round(r.duration),
          startTime: Math.round(r.startTime),
        }))
        .sort((a, b) => b.duration - a.duration);
    });

    for (const api of apis) {
      const flag = api.duration > 50 ? '⚠️' : '  ';
      console.log(`  ${flag} ${String(api.duration).padStart(5)}ms @ ${String(api.startTime).padStart(5)}ms  ${api.name}`);
    }
  }

  await browser.close();
}

main().catch(console.error);
