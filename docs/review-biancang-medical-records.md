# 扁仓（中医本地大模型）与病历本功能 Review 报告

> 日期：2026-08-29
> 范围：最近 6 个提交（`64077ce` → `1bce309`）引入的扁仓 BianCang 医疗模型集成（P0 下载/转换、P1 结构化诊断输出），以及家庭病历本功能
> 结论：**仅记录问题，不修复。**

> ⚠️ **环境快照为合并前状态**：文中 `Family(8788)` 与 `AI(8791)` 当时是两个独立后端进程；
> commit `aa053f1`「三服务合一」后只有 `Baihua.Server`(8788)（家庭 + AI 同进程），8790/8791 已不存在。
> 文中 `services/Baihua.Family/…` 旧路径现为 `services/Baihua.Modules.Family/…`。正文按当时状态保留。

---

## 一、Playwright 测试结果

测试环境：Family(8788) + WebUI(5177) + AI(8791) + OVMS(8000) 均已运行，扁仓模型 `biancang` 已被 OVMS 加载。

### 1.1 现有测试 `medical-diagnosis.spec.ts`（family-mode）

| 用例 | 结果 | 说明 |
|---|---|---|
| API: 诊断端点返回结构化 JSON | ✅ 通过 | `structuredResultJson` 有效，可能原因 3 条 |
| AI 诊断返回结构化 JSON → 卡片式展示 | ❌ 失败 | `.medical-ai-result .structured-diagnosis` 未出现，UI 回退 Markdown 渲染 |

### 1.2 新增测试 `medical-records-crud.spec.ts`（本次新增）

| 用例 | 结果 | 说明 |
|---|---|---|
| API: 成员/记录完整 CRUD + 级联删除 | ✅ 通过 | 成员、病历（含用药）、更新、级联删除、404 校验全部正常 |
| API: 空姓名校验 400 | ✅ 通过 | 后端校验正常 |
| UI: 添加成员→详情→添加记录→编辑→删除 | ❌ 失败 | 前 7 步全部通过，第 8 步"返回列表后点击成员"超时，页面卡在"加载中..." |

### 1.3 关键探测（直接调诊断 API 两次）

| 请求 | modelUsed | structuredResultJson | aiResponse 首字符 |
|---|---|---|---|
| "头晕…血压偏高" | biancang | **null（解析失败）** | `{`（原始 JSON 字符串） |
| "咳嗽…发热" | biancang | 有值（解析成功） | `#`（Markdown 渲染结果） |

**结论：扁仓模型对"必须输出 JSON"指令遵循不稳定，且后端解析也存在偶发失败。**

---

## 二、问题清单

### P0 — 结构化 JSON 输出不可靠（核心问题）

- **位置**：`services/Baihua.Family/Services/Medical/MedicalAiService.cs`（`ParseStructuredResponse` / 系统提示词）+ 扁仓模型实际行为
- **现象**：扁仓 BianCang 7B INT4 模型同一症状，不同请求下有时输出纯 JSON、有时输出 Markdown 列表、有时输出 JSON 但被 max_tokens 截断。实测三种情况均已出现。
- **结果**：
  1. 模型输出 Markdown 时 → `ExtractJson` 返回 null → `StructuredResultJson=null`，回退 Markdown 展示（功能降级）；
  2. 模型输出 JSON 但解析失败（截断/格式问题）时 → `StructuredResultJson=null`，而 `AiResponse` 存的是**原始 JSON 字符串**，用户看到的是 `{"possibleCauses":[...]}` 这样的原文，体验极差；
  3. e2e 测试 `medical-diagnosis.spec.ts` 步骤 8-12 假设"一定输出结构化 JSON"，与实际不稳定行为矛盾，导致测试 flaky/失败。
- **建议**（不修，仅记录）：
  - 增加 JSON 输出兜底：解析失败时降级为 Markdown（清理 JSON 残留、换行、代码块标记），而不是把原始 JSON 丢给用户；
  - 采样参数控制：诊断场景应显式降低 temperature（当前 `ChatOptions` 只设了 `MaxOutputTokens=3000`，未设 `Temperature`/`TopP`）；
  - `MaxOutputTokens` 偏小，完整 4 条 cause + homeCare + 各字段易被截断，是"JSON 不完整"的诱因之一。

### P1 — `ParseStructuredResponse` 失败时把原始 JSON 当作正文存储展示

- **位置**：`MedicalAiService.cs:293-312`（`ParseStructuredResponse` 返回 `(null, raw)`，`DiagnoseAsync:118-120` 把 `raw` 当 markdown 落库）
- **问题**：解析失败时 `AiResponse` = 原始 JSON 文本，前端 `MedicalRecords.razor:294` 用 `MarkdownView` 渲染，直接显示 JSON 原文。

### P1 — `GoToList()` fire-and-forget 导致返回列表卡"加载中"

- **位置**：`services/Baihua.Web/Pages/MedicalRecords.razor:546-551`
  ```csharp
  private void GoToList()
  {
      _view = "list";
      _detail = null;
      _ = LoadAsync();   // fire-and-forget，丢弃 Task
  }
  ```
- **问题**：`GoToList` 是 `void`，`_ = LoadAsync()` 丢弃了异步任务。Blazor 不会在丢弃的 task 完成后自动 `StateHasChanged`，导致 `_members` 已更新但 UI 停在"加载中..."（测试截图确认）。
- **证据**：新增 UI 测试第 8 步稳定复现（retry 两次均卡"加载中"）。
- **建议**：改成 `private async Task GoToList()`（`@onclick` 直接 await），或让调用方 await；同类 `_ = LoadAsync()` / fire-and-forget 模式需排查。

### P2 — `TryGetMedicalModel` 同步阻塞 + 冗余判断 + 硬编码

- **位置**：`MedicalAiService.cs:172-190`
- **问题**：
  1. **同步阻塞**：`http.GetAsync(...).GetAwaiter().GetResult()`、`resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()` 在 async 方法中同步阻塞，反模式，有潜在死锁/线程池饥饿风险；
  2. **冗余判断**：`Contains("BianCang", OrdinalIgnoreCase) || Contains("biancang", OrdinalIgnoreCase)`，第二个条件被第一个（忽略大小写）完全覆盖；
  3. **硬编码**：OVMS 地址 `http://127.0.0.1:8000/v1/`（`MedicalAiService.cs:84`）走死值，未复用 `LocalAiOptions`，与 OVMS 部署地址配置解耦缺失。

### P2 — `OpenVinoCatalog` 重复模型条目

- **位置**：`services/Baihua.Contracts/LocalModels/OpenVinoCatalog.cs:16-23` 与 `:90-98`
- **问题**：`Id = "deepseek-r1-7b"` 定义了**两次**（完全相同的 Id 与大多数字段，中间夹了一个多余空行）。`GetById` 永远命中第一个，第二个是死数据，且若用于 UI 遍历会导致重复展示。
- **建议**：删除第 90-98 行的重复条目及多余空行。

### P2 — 模型下载/转换路径与命令硬编码

- **位置**：`services/Baihua.Family/Services/AI/ModelDownloadService.cs:235-253`
- **问题**：
  1. 脚本路径 `AppContext.BaseDirectory + "../../../scripts/..."` 依赖相对目录层级，publish/容器部署下脆弱；
  2. fallback 硬编码 `Environment.SpecialFolder.UserProfile + "src\\baihua\\scripts\\..."`，Windows 专属且耦合开发者目录结构；
  3. `FileName = "python"` 硬编码，Ubuntu 等环境需 `python3`。

### P2 — `convert_safetensors_to_ov.py` 量化逻辑疑似无效（需验证）

- **位置**：`scripts/convert_safetensors_to_ov.py:72-81`
- **问题**：使用 `OVModelForCausalLM.from_pretrained(..., export=True, load_in_4bit=True)`。`load_in_4bit`/`load_in_8bit` 是 **transformers 的 bitsandbytes 加载参数**，不是 optimum-intel 导出 OpenVINO IR 的量化方式（导出量化应使用 NNCF `compress_weights`，或 optimum-cli 的 `--weight-format int4`）。量化大概率失败并被 `except` 回退到**无量化 FP 导出**（~14GB），与目录描述"本地转 OpenVINO INT4 IR（~4.5GB）"不符。
- **旁证**：`scripts/quantize_ov_int4.py` 写的是**正确的** NNCF `nncf.compress_weights(INT4_SYM)` 流程，但 `ModelDownloadService` 从未调用它——即正确的量化脚本是死代码，被调用的转换脚本量化又疑似无效。

### P3 — e2e 测试基建不完整

- **位置**：`tests/shared-e2e/`（仅 `package-lock.json`，无 `package.json`、无 `node_modules`）
- **问题**：`global-setup.ts` 位于 shared-e2e 且 `import '@playwright/test'`，但该目录缺依赖，导致 `npx playwright test` 直接报 `Cannot find module '@playwright/test'`。本次为运行测试，临时在 `tests/shared-e2e/node_modules` 建了指向 `tests/e2e/node_modules` 的 junction 才跑通。
- **建议**：为 `shared-e2e` 补齐 `package.json`（依赖 `@playwright/test`）或统一到单一测试工程。

### P3 — 诊断接口无请求级并发/频率保护（次要）

- **位置**：`MedicalAiService.DiagnoseAsync` / `MedicalController.Diagnose`
- **说明**：单家庭场景影响小；去重/去抖/限流非必需，仅记录。

---

## 三、本次对仓库的临时改动（说明，非问题修复）

1. 新增测试文件 `tests/e2e/tests/family-mode/medical-records-crud.spec.ts`（病历本 CRUD + 级联删除 + UI 流程）。
2. 创建了 `tests/shared-e2e/node_modules`（junction → `tests/e2e/node_modules`），仅用于让 e2e 测试跑通，未改动任何被 review 的源码。

---

## 四、建议优先处理顺序

1. **P0 结构化输出兜底 + 采样参数**（直接影响"中医本地大模型"核心体验）
2. **P1 GoToList 返回列表 bug**（病历本 UI 可复现缺陷）
3. **P2 下载/转换量化与路径硬编码**（影响模型部署成功率与体积）
4. **P2 OpenVinoCatalog/冗余与死代码清理**
5. **P3 e2e 测试基建补齐**

（以上均未修复，按用户要求仅记录。）