const { chromium } = require('playwright');

const PAGES = [
  { path: '/', name: '首页' },
  { path: '/search', name: '搜索' },
  { path: '/build', name: 'AI 构建' },
  { path: '/messages', name: 'AI 对话' },
  { path: '/tasks', name: '任务' },
  { path: '/openclaw', name: 'OpenClaw' },
  { path: '/vaults', name: '知识库' },
  { path: '/local-models', name: '本地模型' },
  { path: '/settings', name: 'AI 设置' },
  { path: '/health', name: '健康检查' },
  { path: '/devices', name: '移动端' },
];

const BASE_URL = 'http://localhost:5177';

async function measurePage(page, url, name) {
  const navStart = Date.now();

  try {
    const response = await page.goto(url, {
      waitUntil: 'networkidle',
      timeout: 30000,
    });

    const navEnd = Date.now();
    const totalMs = navEnd - navStart;

    // 测量 First Contentful Paint 等客户端指标
    const metrics = await page.evaluate(() => {
      const perf = window.performance;
      const nav = perf.getEntriesByType('navigation')[0];
      const paint = perf.getEntriesByType('paint');
      const lcp = perf.getEntriesByType('element').find(e => e.entryType === 'largest-contentful-paint');

      return {
        domContentLoaded: nav ? nav.domContentLoadedEventEnd - nav.startTime : 0,
        loadComplete: nav ? nav.loadEventEnd - nav.startTime : 0,
        fcp: paint.find(p => p.name === 'first-contentful-paint')?.startTime || 0,
        lcp: lcp ? lcp.startTime : 0,
      };
    });

    // 统计 API 请求耗时
    const apiCalls = await page.evaluate(() => {
      return (window.performance.getEntriesByType('resource') || [])
        .filter(r => r.name.includes('/api/'))
        .map(r => ({
          name: r.name.split('/api/')[1]?.split('?')[0] || r.name,
          duration: Math.round(r.duration),
          size: r.transferSize,
        }))
        .sort((a, b) => b.duration - a.duration);
    });

    return {
      name,
      url,
      status: response?.status() || 0,
      totalMs,
      ...metrics,
      apiCalls,
      slow: totalMs > 50 || metrics.loadComplete > 50,
    };
  } catch (err) {
    return {
      name,
      url,
      status: 0,
      totalMs: -1,
      error: err.message,
      slow: true,
    };
  }
}

async function main() {
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext();
  const page = await context.newPage();

  console.log('🎭 Playwright 页面加载性能测试');
  console.log('================================');
  console.log(`目标: ${BASE_URL}`);
  console.log(`阈值: > 50ms 视为需要优化`);
  console.log('');

  const results = [];

  for (const { path, name } of PAGES) {
    const result = await measurePage(page, BASE_URL + path, name);
    results.push(result);

    const status = result.error
      ? `❌ ${result.error.slice(0, 60)}`
      : result.slow
        ? '⚠️ 慢'
        : '✅ OK';

    console.log(`${status.padEnd(30)} | ${name.padEnd(12)} | 导航: ${String(result.totalMs).padStart(4)}ms | DOMReady: ${String(Math.round(result.domContentLoaded || 0)).padStart(4)}ms | Load: ${String(Math.round(result.loadComplete || 0)).padStart(4)}ms`);

    if (result.apiCalls && result.apiCalls.length > 0) {
      for (const api of result.apiCalls.slice(0, 3)) {
        const apiSlow = api.duration > 50 ? '⚠️' : '  ';
        console.log(`  ${apiSlow} API: ${api.name.padEnd(30)} ${String(api.duration).padStart(4)}ms`);
      }
    }
  }

  console.log('\n================================');
  console.log('📊 总结');
  console.log('================================');

  const slowPages = results.filter(r => r.slow && !r.error);
  const errorPages = results.filter(r => r.error);

  if (errorPages.length > 0) {
    console.log(`❌ 加载失败: ${errorPages.length} 个页面`);
    for (const r of errorPages) {
      console.log(`   - ${r.name}: ${r.error}`);
    }
  }

  if (slowPages.length > 0) {
    console.log(`⚠️ 需要优化 (>50ms): ${slowPages.length} 个页面`);
    for (const r of slowPages) {
      console.log(`   - ${r.name}: 导航 ${r.totalMs}ms, DOMReady ${Math.round(r.domContentLoaded || 0)}ms, Load ${Math.round(r.loadComplete || 0)}ms`);
      if (r.apiCalls && r.apiCalls.length > 0) {
        const slowApis = r.apiCalls.filter(a => a.duration > 50);
        for (const api of slowApis) {
          console.log(`     └─ API /api/${api.name}: ${api.duration}ms`);
        }
      }
    }
  } else {
    console.log('✅ 所有页面加载时间均在 50ms 以内');
  }

  await browser.close();
}

main().catch(console.error);
