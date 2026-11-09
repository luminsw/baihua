# 题库生成能力方案（MCP + UI 双入口）

> 立项：2026-11-09
> 现状：题库只能靠 `tools/quiz-import` CLI 导入 JSON 包；WebUI 只有 `/learn` 刷题页，没有任何生成入口。
> 目标：让普通用户能通过界面、让 AI Agent 能通过 MCP，把「行业 + 提示词」变成可落库的选择题题库。

## 1. 结论：两种入口都做，共用一个内核

| | MCP 入口 | UI 入口 |
|---|---|---|
| 谁生成 | 用户自己的 Agent（DSH / Claude Code / Cursor） | 百花后端调用已配置的 AI provider |
| 质量上限 | 高（强模型 + 可反复调提示词） | 中（受后端单次提示词与上下文限制） |
| 适用用户 | 懂提示词的本人 | 家庭普通成员 |
| 维护成本 | 低（百花不硬编码选型/生成逻辑） | 中（模板、解析、预览页） |
| 阶段 | 阶段一（先做） | 阶段二 |

**关键判断**：MCP 方式不需要百花再调 AI —— Agent 本身就是 AI。百花只提供「结构模板 + 校验 + 存储」这三件不随时间变化的能力，生成逻辑交给提示词。这与项目既定的「变化快的功能交给 Agent、百花保留稳定层」原则一致。

两种入口最终都走同一条落库路径：

```
AI/Agent 产出 JSON  →  QuizBankPackageValidator 整体校验
                    →  用户/Agent 确认
                    →  QuizBankService.ImportPackageAsync(append 模式)
                    →  QuizIndustries / QuizBanks / QuizItems / QuizQuestions
```

## 2. 阶段一（已完成）：校验器 + append 导入 + MCP 工具

### 2.1 新增/修改

| 文件 | 改动 |
|---|---|
| `services/Baihua.Contracts/Learn/QuizBankContracts.cs` | 新增 `QuizBankImportOptions`、`QuizBankValidationResultDto` |
| `services/Baihua.Modules.Family/Services/Learning/QuizBankPackageValidator.cs` | 新增：题库包整体体检（错误/警告分级） |
| `services/Baihua.Modules.Family/Services/Learning/QuizBankPromptBuilder.cs` | 新增：把参数展开成完整生成提示词（内嵌 JSON 结构与硬性规则） |
| `services/Baihua.Modules.Family/Services/Learning/QuizBankService.cs` | `ImportPackageAsync` 增加 `QuizBankImportOptions` 重载（append 模式）；原签名保留转发 |
| `services/Baihua.Modules.Family/Services/Mcp/BaihuaLearnTools.cs` | 新增 4 个 MCP 工具 |
| `services/Baihua.Server/Program.cs` | 注册 `.WithTools<BaihuaLearnTools>()` |

### 2.2 MCP 工具

| 工具 | 作用 |
|---|---|
| `baihua_learn_list` | 列行业/题库（code、名称、条目数、题数），用于避免 code 冲突 |
| `baihua_learn_prompt_template` | 按参数生成完整提示词（含 JSON schema + 硬性规则 + 示例） |
| `baihua_learn_import_package` | JSON 字符串 → 整体校验 → 落库；支持 `dry_run` / `append_only` / `force` |
| `baihua_learn_export_package` | 导出完整题库包，用于增量修改后重新导入 |

#### 使用示例（在 DSH / Claude Code 中）

```
1) 先看有哪些题库，避免冲突：
   调用 baihua_learn_list

2) 取提示词模板（示例：中医·经方，追加 10 道选方题，前缀唯一）：
   调用 baihua_learn_prompt_template
     industry_code=tcm, industry_name=中医
     bank_code=jingfang, bank_name=经方
     topic=太阳病桂枝汤类方，重点考兼证与禁忌
     target=formula, question_count=10, difficulty=3
     reference_scope=伤寒论·太阳篇
     code_prefix=ai20261109

3) 按返回的 prompt 生成 JSON（Agent 自己完成）。

4) 先干跑校验：
   调用 baihua_learn_import_package(package_json=<JSON>, dry_run=true)

5) 校验通过后落库（默认 append_only=true，不会覆盖既有 219 方/259 题）：
   调用 baihua_learn_import_package(package_json=<JSON>)
```

### 2.3 校验规则

**Errors（整体拒绝落库）**
- 缺 `industry.code/name`、`bank.code/name`
- 包内 `itemCode` / `questionCode` 重复，或关键字段为空（itemCode、name、questionCode、stem、analysis、target）
- 选项不足 2 个、正确项数量 ≠ 1、选项 key 重复、选项内容重复、选项缺 key/content

**Warnings（不阻断）**
- 选项数 ≠ 4、缺 explanation、缺 discrimination
- `target` 非标准值（行业无关 schema 允许自定义）
- `difficulty` 超出 1–5、`questionType` 非 `single_choice`
- `items` 为空（题目只能引用库内已有条目）
- 条目缺 `itemType` / `content`

### 2.4 append 模式（防覆盖）

`QuizBankImportOptions.OverwriteExisting = false` 时：

- **行业/题库**：已存在则不改名称/版本/描述（`UpdateMetadata=true` 才改）
- **条目/题目**：已存在则原样保留，计入 `ItemsSkipped` / `QuestionsSkipped` 并写 Warning
- 新增代码正常插入；被跳过的条目仍会回填 `ItemId`，保证新题能正确关联旧条目

CLI 与历史批次导入保持默认 `OverwriteExisting = true`（行为不变）。

## 3. 阶段二（已完成）：UI 生成页

### 3.1 入口

`/learn` 题库卡片区新增「AI 生成题库」按钮 → `/learn/generate`。

### 3.2 输入项（4 必填 + 5 可选）

**必填**
1. 行业（下拉 + 可新建）
2. 题库（下拉 + 可新建）
3. 生成主题 / 提示词（大文本框）
4. 题型 `target`：选方案 formula / 选证型 pattern / 混合 mixed

**可选（默认折叠或平铺）**
5. 参考资料范围（知识库下拉 / 自定义文本，如「伤寒论·太阳篇」）
6. 题量（5 / 10 / 20，上限 30；> 20 自动分批调用防截断）
7. 难度 1–5
8. 干扰项策略（同范围 / 随机）
9. 模型选择（默认主 provider 主模型）

### 3.3 生成流程（两步式，绝不自动入库）

```
填参数 → 后端拼提示词 → 调 AI（非流式、无 RAG/无工具）
      → 解析 JSON（去 ```json 包裹、提取首个 {...}）
      → QuizBankPackageValidator.Validate
      → 预览审核页（逐题展示：可删除 / 编辑 / 单题重生成 / 查看 errors+warnings）
      → 用户点「确认导入」→ ImportPackageAsync(append 模式)
```

### 3.4 后端接口（已实现）

| 端点 | 说明 |
|---|---|
| `POST api/learn/generate/preview` | 生成 + 校验，返回 `QuizBankPackage` + `QuizBankValidationResultDto` + 原始 JSON，不落库 |
| `POST api/learn/validate` | 校验任意题库包（粘贴模式 / 删除题目后复检），不落库 |
| `POST api/learn/import` | 把用户确认后的 `QuizBankPackage` 落库（默认 append，可显式覆盖） |

生成端点仅挂在 `api/learn`（管理域），**不挂 `mg/learn`**：生成与写入不属于移动端同步能力。

AI 调用在 `QuizBankGeneratorService` 内完成：分批（每批 10 题）→ 逐批调用 AI → `QuizBankResponseParser` 解析 → 合并 → 整体校验。解析失败自动追加「只输出 JSON」约束重试一次；仍失败则返回 `Success=false` 并保留原始响应，引导用户改用「粘贴题库包」方式人工修正。

WebUI 侧生成请求使用新增的 `FamilyApiAiLong` 客户端（15 分钟超时）；普通接口仍用 30s / 5 分钟客户端。

### 3.4.1 页面

`/learn/generate`（入口在 `/learn` 题库卡片区「✨ AI 生成题库」）：

- **AI 生成**：4 必填 + 5 可选表单；行业/题库 chips 可一键填入已有值
- **粘贴题库包 JSON**：把 MCP/Agent 产出的 JSON 直接粘进来校验预览（也可作为 AI 解析失败时的人工修正通道）
- **预览审核**：展示 errors/warnings、逐题展开解析与区分点、可逐题删除（删除后自动重新校验）
- **确认导入**：默认 append；errors 非空或题目为 0 时按钮禁用；「允许覆盖」需显式勾选

### 3.5 关键实现约束

- 提示词模板必须复用阶段一的 `QuizBankPromptBuilder`，保证 MCP 与 UI 产出结构一致
- `questionCode` 强制带用户可见的唯一前缀（默认 `ai`），避免撞车
- 分批生成时按 item 分组多次调用，最后合并成一个包再校验
- 预览页必须展示 `errors`，有 error 时禁止「确认导入」（除非管理员显式强制）

## 4. 风险与红线

1. **AI 生成内容必须人工预览确认后才落库**——中医等专业内容尤其如此；不使用本地模型生成专业内容（历史教训：本地模型曾污染医案/学习笔记）。
2. **append 为默认**：任何生成/Agent 写入都不得静默覆盖人工整理的题库。
3. **不编造出处**：提示词明确要求 `sourceRef` 不确定时留空；预览页建议高亮空出处。
4. **代码唯一性**：`questionCode` 撞车是最大隐患，靠「唯一前缀 + append 模式 + 校验重复」三重防护。
5. **MCP 鉴权**：`/mcp` 已有中间件（loopback/管理网段免鉴权，其它要求 `BAIHUA_AI_EXTERNAL_TOKEN`），生成能力不需要额外鉴权。

## 5. 验收

阶段一：
- `dotnet build services/BaiHua.slnx -c Release` 0 错误
- 新增 `QuizBankPackageValidatorTests` 全绿
- `QuizBankServiceTest` 新增 append 模式 3 个用例全绿（含「不覆盖既有内容」「只追加新码并回填 ItemId」「新库正常创建」）
- 真实 CLI 导入行为不变（默认覆盖模式）

阶段二：
- `/learn/generate` playwright E2E：填参数 → 预览 → 删除一题 → 确认导入 → 题库计数增加
- 中断/失败路径：AI 返回非 JSON、校验有 error 时禁止导入
## 6. 实施结果（2026-11-09）

阶段一与阶段二均已完成：

| 验证项 | 结果 |
|---|---|
| Release 全解决方案构建 | 0 错误 |
| 题库相关单测（校验器 / 提示词 / append / 解析器 / 分批 / 控制器） | 全绿 |
| MCP 工具单测（list / prompt_template / import / dry_run / append / export） | 全绿 |
| 真实题库数据回归（219 方 / 259 题） | 通过，CLI 默认覆盖行为未变 |
| `/learn/generate` playwright E2E | 粘贴 JSON → 校验预览 → 删除一题 → 确认导入 → 题库列表出现新库，全通过 |

已知限制：
- AI 生成路径需用户已配置可用 provider；E2E 走「粘贴题库包」通道验证，不消耗 AI 额度。
- 当前环境 `dotnet test` 的 testhost 因 `OpenProcess` 权限问题无法启动（与代码无关），单测改用临时反射执行器运行。