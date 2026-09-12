import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize } from '../helpers';

// 病历本 AI 诊断结构化输出（BianCang P1）
// 覆盖：页面加载 → 创建成员 → AI 诊断 → 结构化卡片展示 → 清理
// 依赖：Baihua.Server 8788（家庭 + AI 同进程）+ WebUI 5177 + AI 模型（biancang 或主模型）运行中

const API_BASE = `http://127.0.0.1:${process.env.API_PORT || '8788'}`;

test.describe('病历本 AI 诊断结构化输出', () => {
  let memberId: number | null = null;
  const memberName = `E2E-诊断-${Date.now()}`;

  test.afterAll(async ({ request }) => {
    // 数据自清理：删除测试成员（级联删除诊断记录）
    if (memberId) {
      await request.delete(`${API_BASE}/api/medical/members/${memberId}`);
    }
  });

  test('AI 诊断返回结构化 JSON → 卡片式展示', async ({ page, request }) => {
    // AI 诊断可能较慢（biancang 7B INT4），放宽到 5 分钟
    test.setTimeout(300000);

    // 1. 通过 API 创建测试成员
    const createResp = await request.post(`${API_BASE}/api/medical/members`, {
      data: {
        name: memberName,
        gender: '男',
        birthDate: '1990-01-15T00:00:00Z',
        bloodType: 'A',
        allergies: [],
        chronicDiseases: ['高血压'],
        notes: null,
      },
      headers: { 'Content-Type': 'application/json' },
    });
    expect(createResp.ok()).toBeTruthy();
    const member = await createResp.json();
    memberId = member.id;

    // 2. 导航到病历本页面
    await authorize(page);
    await navigateTo(page, '/medical-records');
    await waitForBlazor(page);

    // 3. 找到测试成员卡片并进入详情
    const memberCard = page.locator('.medical-member-card', { hasText: memberName });
    await expect(memberCard).toBeVisible({ timeout: 15000 });
    await memberCard.getByRole('button', { name: /查看详情/ }).click();
    await page.waitForLoadState('networkidle');

    // 4. 确认进入详情页：成员名字可见
    await expect(page.locator('h3', { hasText: memberName })).toBeVisible({ timeout: 15000 });

    // 5. 输入症状描述
    const symptomTextarea = page.locator('textarea.medical-textarea');
    await symptomTextarea.fill('最近三天头晕，伴有轻微恶心，血压偏高 150/95');

    // 6. 点击诊断按钮
    const diagnoseBtn = page.getByRole('button', { name: '开始分析' });
    await diagnoseBtn.click();

    // 7. 等待诊断完成（spinner 消失，结果卡片出现）
    const resultCard = page.locator('.medical-ai-result');
    await expect(resultCard).toBeVisible({ timeout: 240000 });

    // 8. 验证结构化诊断卡片出现
    const structuredCard = resultCard.locator('.structured-diagnosis');
    await expect(structuredCard).toBeVisible({ timeout: 10000 });

    // 9. 验证"可能原因分析"区域存在
    await expect(structuredCard.locator('.sd-section-title', { hasText: '可能原因分析' })).toBeVisible();
    // 验证至少有 1 个可能原因条目
    await expect(structuredCard.locator('.sd-cause').first()).toBeVisible();

    // 10. 验证"居家护理与观察建议"区域存在
    await expect(structuredCard.locator('.sd-section-title', { hasText: '居家护理' })).toBeVisible();

    // 11. 验证免责声明存在
    await expect(structuredCard.locator('.sd-disclaimer')).toBeVisible();
    await expect(structuredCard.locator('.sd-disclaimer')).toContainText('仅供参考');

    // 12. 验证扁仓 badge（如果模型是 biancang）
    // 注意：如果回退到主模型则没有 badge，所以只做软断言
    const biancangBadge = resultCard.locator('.badge', { hasText: '扁仓' });
    const hasBadge = await biancangBadge.count();
    if (hasBadge > 0) {
      console.log('[E2E] 扁仓模型被使用，badge 可见');
    } else {
      console.log('[E2E] 使用主模型（非扁仓），无 badge');
    }

    // 13. 验证历史记录中也出现结构化卡片
    const historyCard = page.locator('.medical-ai-history .structured-diagnosis');
    await expect(historyCard.first()).toBeVisible({ timeout: 10000 });
  });

  test('API: 诊断端点返回结构化 JSON 字段', async ({ request }) => {
    // 直接调 API 验证 StructuredResultJson 字段
    // 先创建成员
    const createResp = await request.post(`${API_BASE}/api/medical/members`, {
      data: {
        name: `E2E-API-${Date.now()}`,
        gender: '女',
        birthDate: '1985-06-20T00:00:00Z',
        bloodType: 'O',
        allergies: ['青霉素过敏'],
        chronicDiseases: [],
        notes: null,
      },
      headers: { 'Content-Type': 'application/json' },
    });
    expect(createResp.ok()).toBeTruthy();
    const m = await createResp.json();
    const tempMemberId = m.id;

    try {
      // 调用诊断 API
      const diagnoseResp = await request.post(`${API_BASE}/api/medical/diagnose`, {
        data: {
          memberId: tempMemberId,
          symptomText: '咳嗽一周，有黄痰，轻微发热 37.8℃',
          extraContext: '无其他不适',
        },
        headers: { 'Content-Type': 'application/json' },
        timeout: 240000,
      });
      expect(diagnoseResp.ok()).toBeTruthy();
      const result = await diagnoseResp.json();
      expect(result.success).toBeTruthy();
      expect(result.diagnosis).toBeTruthy();

      // 验证 StructuredResultJson 字段存在且为有效 JSON
      if (result.diagnosis.structuredResultJson) {
        const structured = JSON.parse(result.diagnosis.structuredResultJson);
        expect(structured.possibleCauses).toBeTruthy();
        expect(Array.isArray(structured.possibleCauses)).toBeTruthy();
        expect(structured.homeCare).toBeTruthy();
        expect(structured.disclaimer).toBeTruthy();
        expect(structured.disclaimer).toContain('仅供参考');
        console.log('[E2E] API 诊断返回结构化 JSON，可能原因数:', structured.possibleCauses.length);
      } else {
        console.log('[E2E] API 诊断未返回结构化 JSON（模型可能不支持 JSON 输出，回退 Markdown）');
      }

      // 验证 AiResponse 字段始终有值（Markdown 回退）
      expect(result.diagnosis.aiResponse).toBeTruthy();
      expect(result.diagnosis.aiResponse.length).toBeGreaterThan(0);
    } finally {
      await request.delete(`${API_BASE}/api/medical/members/${tempMemberId}`);
    }
  });
});