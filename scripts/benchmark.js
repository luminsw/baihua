/**
 * 简单的 HTTP 压力测试脚本（Node.js）
 * 用法：node benchmark.js <url> <concurrency> <duration_seconds>
 */
const http = require('http');
const https = require('https');
const url = require('url');

const targetUrl = process.argv[2] || 'http://127.0.0.1:8788/api/health/simple';
const concurrency = parseInt(process.argv[3] || '10');
const durationSec = parseInt(process.argv[4] || '10');

const parsed = url.parse(targetUrl);
const client = parsed.protocol === 'https:' ? https : http;

let total = 0, success = 0, failed = 0;
let latencies = [];
const startTime = Date.now();
let isRunning = true;

function requestOne() {
  if (!isRunning) return;
  const reqStart = Date.now();
  const req = client.request({
    hostname: parsed.hostname,
    port: parsed.port,
    path: parsed.path,
    method: 'GET',
    timeout: 5000,
  }, (res) => {
    const latency = Date.now() - reqStart;
    latencies.push(latency);
    total++;
    if (res.statusCode >= 200 && res.statusCode < 300) success++;
    else failed++;
    res.resume();
    requestOne();
  });
  req.on('error', () => {
    total++; failed++;
    requestOne();
  });
  req.on('timeout', () => {
    req.destroy();
    total++; failed++;
    requestOne();
  });
  req.end();
}

for (let i = 0; i < concurrency; i++) requestOne();

setTimeout(() => {
  isRunning = false;
  const elapsed = (Date.now() - startTime) / 1000;
  latencies.sort((a, b) => a - b);
  const p50 = latencies[Math.floor(latencies.length * 0.5)];
  const p95 = latencies[Math.floor(latencies.length * 0.95)];
  const p99 = latencies[Math.floor(latencies.length * 0.99)];
  const avg = latencies.reduce((a, b) => a + b, 0) / latencies.length;
  const rps = total / elapsed;

  console.log('\n========== 压测结果 ==========');
  console.log(`URL:        ${targetUrl}`);
  console.log(`并发数:     ${concurrency}`);
  console.log(`持续时间:   ${elapsed.toFixed(1)}s`);
  console.log(`总请求数:   ${total}`);
  console.log(`成功:       ${success} (${(success/total*100).toFixed(1)}%)`);
  console.log(`失败:       ${failed}`);
  console.log(`RPS:        ${rps.toFixed(1)}`);
  console.log(`平均延迟:   ${avg.toFixed(1)}ms`);
  console.log(`P50 延迟:   ${p50}ms`);
  console.log(`P95 延迟:   ${p95}ms`);
  console.log(`P99 延迟:   ${p99}ms`);
  console.log('==============================');
}, durationSec * 1000);
