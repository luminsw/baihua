import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  timeout: 60000,
  retries: 0,
  use: {
    // 唯一后端 Baihua.Server（三服务合一后 8790/8791 已不存在）
    baseURL: 'http://127.0.0.1:8788',
    extraHTTPHeaders: {
      'Content-Type': 'application/json',
    },
  },
  projects: [
    {
      name: 'api',
      testMatch: /master-baishi\.spec\.ts/,
    },
  ],
});
