import { test, expect } from '@playwright/test';

const API_URL = 'http://127.0.0.1:8788';   // 唯一后端 Baihua.Server

// Note: 后端 JSON 统一 camelCase（ASP.NET Core 默认）。合并前 Baihua.Family 曾是
// PropertyNamingPolicy=null 的 PascalCase 特例，三服务合一（commit aa053f1）后已统一，
// 因此断言用 camelCase key。请求体仍可写 PascalCase —— 服务端 PropertyNameCaseInsensitive=true。

test.describe('虚拟师父 - 拜师功能', () => {

  test('API: GET /api/master 应该返回空列表（不再是500）', async ({ request }) => {
    const response = await request.get(`${API_URL}/api/master`);
    expect(response.status()).toBe(200);
    const masters = await response.json();
    expect(Array.isArray(masters)).toBeTruthy();
    console.log(`✓ 师父列表正常 (${masters.length} 位)`);
  });

  test('API: POST /api/master/create 应该成功创建师父', async ({ request }) => {
    const response = await request.post(`${API_URL}/api/master/create`, {
      data: { Goal: '通过执业医师考试', Industry: '中医' }
    });

    console.log(`Status: ${response.status()}`);
    expect(response.status()).toBe(200);

    const result = await response.json();
    console.log(`Response: Success=${result.success}, MasterName=${result.masterName}`);

    // camelCase 属性
    expect(result.success).toBe(true);
    expect(result.masterId).toBeTruthy();
    expect(result.masterName).toBeTruthy();
    expect(result.stages).toBeTruthy();
    expect(result.stages.length).toBe(5);
    expect(result.stages[0].name).toBe('入道');
    expect(result.masterId).toMatch(/^[a-f0-9]{32}$/);
  });

  test('API: 空目标应该返回 400', async ({ request }) => {
    const response = await request.post(`${API_URL}/api/master/create`, {
      data: { Goal: '', Industry: '中医' }
    });
    expect(response.status()).toBe(400);
    const result = await response.json();
    expect(result.success).toBe(false);
    expect(result.message).toContain('目标不能为空');
  });

  test('API: 空行业应该返回 400', async ({ request }) => {
    const response = await request.post(`${API_URL}/api/master/create`, {
      data: { Goal: '通过考试', Industry: '' }
    });
    expect(response.status()).toBe(400);
    const result = await response.json();
    expect(result.success).toBe(false);
    expect(result.message).toContain('行业不能为空');
  });

  test('API: 创建成功后师父列表应该包含新师父', async ({ request }) => {
    const createRes = await request.post(`${API_URL}/api/master/create`, {
      data: { Goal: '学习Python编程', Industry: '计算机' }
    });
    expect(createRes.status()).toBe(200);
    const created = await createRes.json();
    expect(created.success).toBe(true);

    const listRes = await request.get(`${API_URL}/api/master`);
    expect(listRes.status()).toBe(200);
    const masters = await listRes.json();

    const found = masters.find((m: any) => m.masterId === created.masterId);
    expect(found).toBeTruthy();
    expect(found.masterName).toBe('图灵'); // 计算机 → 图灵
    expect(found.currentStage).toBe('入道');
    console.log(`✓ 创建并验证: ${found.masterName} (${found.masterId})`);
  });

  test('API: 删除师父应该成功（软删除）', async ({ request }) => {
    const createRes = await request.post(`${API_URL}/api/master/create`, {
      data: { Goal: '临时测试', Industry: '通用' }
    });
    const created = await createRes.json();
    expect(created.success).toBe(true);

    const deleteRes = await request.delete(`${API_URL}/api/master/${created.masterId}`);
    expect(deleteRes.status()).toBe(200);

    // 软删除后列表中不再出现
    const listRes = await request.get(`${API_URL}/api/master`);
    const masters = await listRes.json();
    const found = masters.find((m: any) => m.masterId === created.masterId);
    expect(found).toBeUndefined();
    console.log('✓ 软删除成功');
  });

  test('API: 删除不存在的师父返回 404', async ({ request }) => {
    const response = await request.delete(`${API_URL}/api/master/nonexistent12345`);
    expect(response.status()).toBe(404);
  });

  test('API: 获取师父画像', async ({ request }) => {
    const createRes = await request.post(`${API_URL}/api/master/create`, {
      data: { Goal: '通过教资考试', Industry: '教育' }
    });
    const created = await createRes.json();
    expect(created.success).toBe(true);

    const profileRes = await request.get(`${API_URL}/api/master/${created.masterId}/Profile`);
    expect(profileRes.status()).toBe(200);
    const profile = await profileRes.json();
    expect(profile.success).toBe(true);
    expect(profile.goal).toBe('通过教资考试');
    expect(profile.currentStage).toBe('入道');
    console.log(`✓ 画像: ${profile.goal}`);
  });

  test('API: 不同行业映射到正确师父名', async ({ request }) => {
    const cases = [
      { industry: '中医', expectedName: '岐伯' },
      { industry: '计算机', expectedName: '图灵' },
      { industry: '会计', expectedName: '算圣' },
      { industry: '法律', expectedName: '廷尉' },
      { industry: '建筑', expectedName: '鲁班' },
      { industry: '通用未知', expectedName: '先生' },
    ];

    for (const { industry, expectedName } of cases) {
      const res = await request.post(`${API_URL}/api/master/create`, {
        data: { Goal: `学习${industry}`, Industry: industry }
      });
      expect(res.status()).toBe(200);
      const r = await res.json();
      expect(r.masterName, `${industry} → ${expectedName}`).toBe(expectedName);
      // 清理
      await request.delete(`${API_URL}/api/master/${r.masterId}`);
    }
    console.log('✓ 所有行业名称映射正确');
  });

});
