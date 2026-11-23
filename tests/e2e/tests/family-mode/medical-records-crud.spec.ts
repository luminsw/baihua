import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor, authorize } from '../helpers';

// 病历本全面功能测试：成员 CRUD + 病历记录 CRUD（含用药）+ 级联删除
// 依赖：Baihua.Server 8788（家庭 + AI 同进程）+ WebUI 5177 运行中；AI 诊断不在此文件覆盖（见 medical-diagnosis.spec.ts）

const API_BASE = `http://127.0.0.1:${process.env.API_PORT || '8788'}`;

test.describe('病历本 成员/记录 CRUD', () => {
  let uiMemberId: number | null = null;

  test.afterAll(async ({ request }) => {
    if (uiMemberId) await request.delete(`${API_BASE}/api/medical/members/${uiMemberId}`);
  });

  test('API：成员与病历记录完整 CRUD + 级联删除', async ({ request }) => {
    // 1. 创建成员
    const createResp = await request.post(`${API_BASE}/api/medical/members`, {
      data: {
        name: `E2E-CRUD-${Date.now()}`,
        gender: '女',
        birthDate: '1995-03-08T00:00:00Z',
        bloodType: 'B',
        allergies: ['青霉素过敏'],
        chronicDiseases: ['哮喘'],
        notes: '测试成员',
      },
      headers: { 'Content-Type': 'application/json' },
    });
    expect(createResp.ok()).toBeTruthy();
    const member = await createResp.json();
    const memberId: number = member.id;
    expect(member.name).toBeTruthy();
    expect(member.allergies).toContain('青霉素过敏');

    // 2. 列表应该包含该成员
    const listResp = await request.get(`${API_BASE}/api/medical/members`);
    expect(listResp.ok()).toBeTruthy();
    const list = await listResp.json();
    expect(list.some((m: any) => m.id === memberId)).toBeTruthy();

    // 3. 创建病历记录（含用药）
    const recResp = await request.post(`${API_BASE}/api/medical/members/${memberId}/records`, {
      data: {
        occurredAt: '2026-08-20T00:00:00Z',
        title: '过敏性哮喘发作',
        symptoms: ['咳嗽', '气喘'],
        diagnoses: ['哮喘急性发作'],
        medications: [{ name: '沙丁胺醇', dosage: '每次 1 喷', frequency: '每日 3 次', note: '按需使用' }],
        notes: '社区医院就诊',
      },
      headers: { 'Content-Type': 'application/json' },
    });
    expect(recResp.ok()).toBeTruthy();
    const record = await recResp.json();
    expect(record.title).toBe('过敏性哮喘发作');
    expect(record.medications.length).toBe(1);
    expect(record.medications[0].name).toBe('沙丁胺醇');

    // 4. 更新病历记录（改标题 + 增加症状，用药保持）
    const updResp = await request.put(`${API_BASE}/api/medical/records/${record.id}`, {
      data: { title: '哮喘发作（已缓解）', symptoms: ['咳嗽', '气喘', '胸闷'] },
      headers: { 'Content-Type': 'application/json' },
    });
    expect(updResp.ok()).toBeTruthy();
    const updated = await updResp.json();
    expect(updated.title).toBe('哮喘发作（已缓解）');
    expect(updated.symptoms).toContain('胸闷');

    // 5. 成员详情应含 1 条记录
    const detailResp = await request.get(`${API_BASE}/api/medical/members/${memberId}`);
    expect(detailResp.ok()).toBeTruthy();
    const detail = await detailResp.json();
    expect(detail.records.length).toBe(1);
    expect(detail.records[0].title).toBe('哮喘发作（已缓解）');

    // 6. 删除病历记录
    const delRec = await request.delete(`${API_BASE}/api/medical/records/${record.id}`);
    expect(delRec.ok()).toBeTruthy();

    // 7. 删除成员（级联删除）
    const delResp = await request.delete(`${API_BASE}/api/medical/members/${memberId}`);
    expect(delResp.ok()).toBeTruthy();

    // 8. 删除后再查应 404
    const goneResp = await request.get(`${API_BASE}/api/medical/members/${memberId}`);
    expect(goneResp.status()).toBe(404);
  });

  test('API：创建成员校验（空姓名应 400）', async ({ request }) => {
    const resp = await request.post(`${API_BASE}/api/medical/members`, {
      data: { name: '   ', gender: '男' },
      headers: { 'Content-Type': 'application/json' },
    });
    expect(resp.status()).toBe(400);
  });

  test('UI：添加成员 → 详情 → 添加记录（含用药）→ 编辑 → 删除', async ({ page }) => {
    const memberName = `E2E-UI-${Date.now()}`;

    await authorize(page);
    await navigateTo(page, '/medical-records');
    await waitForBlazor(page);

    // 1. 打开"添加成员"弹窗
    await page.getByRole('button', { name: /添加成员/ }).click();
    const memberModal = page.locator('.medical-modal');
    await expect(memberModal).toBeVisible();

    // 姓名是弹窗内第一个文本输入框
    await memberModal.locator('input.form-control').first().fill(memberName);
    // 过敏史 / 慢性病按 placeholder 定位
    await memberModal.locator('input[placeholder*="过敏史"]').fill('花生过敏');
    await memberModal.locator('input[placeholder*="慢性病"]').fill('鼻炎');
    await memberModal.getByRole('button', { name: '保存' }).click();
    await expect(memberModal).toBeHidden({ timeout: 10000 });

    // 2. 列表出现新成员卡片
    const memberCard = page.locator('.medical-member-card', { hasText: memberName });
    await expect(memberCard).toBeVisible({ timeout: 15000 });
    await expect(memberCard).toContainText('花生过敏');

    // 3. 进入详情
    await memberCard.getByRole('button', { name: /查看详情/ }).click();
    await page.waitForLoadState('networkidle');
    await expect(page.locator('h3', { hasText: memberName })).toBeVisible({ timeout: 15000 });

    // 解析成员 id（afterAll 清理用）
    const members = await page.request.get(`${API_BASE}/api/medical/members`);
    const all = await members.json();
    uiMemberId = all.find((m: any) => m.name === memberName)?.id ?? null;

    // 4. 添加病历记录
    await page.getByRole('button', { name: /添加记录/ }).click();
    const recordModal = page.locator('.medical-modal');
    await expect(recordModal).toBeVisible();

    // 标题（placeholder 含"如"）与症状 textarea
    await recordModal.locator('input[placeholder*="如"]').first().fill('过敏性鼻炎发作');
    await recordModal.locator('textarea[placeholder*="发热"]').fill('流清涕\n打喷嚏');
    // 添加用药
    await recordModal.getByRole('button', { name: /添加药物/ }).click();
    await recordModal.locator('input[placeholder="药名"]').fill('氯雷他定');
    await recordModal.locator('input[placeholder="剂量"]').fill('每次 1 片');
    await recordModal.getByRole('button', { name: '保存' }).click();
    await expect(recordModal).toBeHidden({ timeout: 10000 });

    // 5. 记录卡片出现
    const recordCard = page.locator('.medical-record-card', { hasText: '过敏性鼻炎发作' });
    await expect(recordCard).toBeVisible({ timeout: 15000 });
    await expect(recordCard).toContainText('氯雷他定');

    // 6. 编辑记录 → 改名
    await recordCard.getByRole('button', { name: /编辑/ }).click();
    const editModal = page.locator('.medical-modal');
    await expect(editModal).toBeVisible();
    await editModal.locator('input[placeholder*="如"]').first().fill('鼻炎发作（复查）');
    await editModal.getByRole('button', { name: '保存' }).click();
    await expect(editModal).toBeHidden({ timeout: 10000 });
    await expect(page.locator('.medical-record-card', { hasText: '鼻炎发作（复查）' })).toBeVisible();

    // 7. 删除记录（二次确认）
    const renamedCard = page.locator('.medical-record-card', { hasText: '鼻炎发作（复查）' });
    const delRecordBtn = renamedCard.getByRole('button', { name: /删除/ }).last();
    await delRecordBtn.click();
    await delRecordBtn.click();
    await expect(renamedCard).toBeHidden({ timeout: 10000 });

    // 8. 返回列表 → 删除成员（二次确认）
    await page.getByRole('button', { name: /返回/ }).click();
    await page.waitForLoadState('networkidle');
    await page.locator('.medical-member-card', { hasText: memberName })
      .getByRole('button', { name: /查看详情/ }).click();
    await page.waitForLoadState('networkidle');
    const delMemberBtn = page.getByRole('button', { name: /删除/ }).last();
    await delMemberBtn.click();
    await delMemberBtn.click();
    await expect(page.locator('h3', { hasText: memberName })).toBeHidden({ timeout: 10000 });
    uiMemberId = null;
  });
});