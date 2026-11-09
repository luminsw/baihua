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

## 2.5 行业画像（全行业适配，2026-11-09 追加）

题库 schema 本身行业无关，但题型与术语此前写死为中医（`formula`=选方剂 / `pattern`=选证型）。
现引入「行业画像」层，把行业差异收敛到一处：

| 位置 | 作用 |
|---|---|
| `QuizIndustryProfileCatalog` | 内置画像 + 未登记行业兜底 |
| `GET api/learn/industries` | 生成页行业/题型下拉数据源 |
| `baihua_learn_industries`（MCP） | Agent 查询行业与可选题型 |
| `QuizIndustryProfileDto` | 行业 code/name、题型 label/description/optionKind、条目称谓、输入提示 |

**内置画像**：`general`（通用知识）、`tcm`（中医）、`driving`（驾照/交通法规）、`programming`（编程）、`english`（英语）、`math`（数学）、`law`（法律）、`medicine`（现代医学）。

规则：
- **中医画像保持 `formula` / `pattern` 原值**，既有 219 方 / 259 题数据与 `/learn` 页完全兼容。
- 未登记行业自动使用「通用知识」画像（`single_answer` / `definition` / `application`），并保留用户填写的 code/name。
- 题型可自定义：UI 选「自定义题型」填 code + 说明；MCP 传 `target` + `target_description`。
- 校验器**仅在传入画像时**提示「target 不在行业画像内」，且只是 warning，不阻断。
- 校验结果的题型统计改为 `targetCounts` 字典（原 `formulaQuestionCount` / `patternQuestionCount` 已移除）。

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
- 分批生成：按题量每批 10 题逐批调用，最后合并成一个包再整体校验
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
| 行业画像 | 8 个内置画像；E2E 断言切换行业后题型动态变化（编程→「选输出结果」、中医→「选方剂」） |

已知限制：
- AI 生成路径需用户已配置可用 provider；E2E 走「粘贴题库包」通道验证，不消耗 AI 额度。
- 当前环境 `dotnet test` 的 testhost 因 `OpenProcess` 权限问题无法启动（与代码无关），单测改用临时反射执行器运行。
## 7. 题库学习统计（2026-11-09 追加）

点开题库后展示统一口径的学习进度（Web 选学习者页 + 花记鸿蒙/安卓题库卡片与做题配置页）。

| 指标 | 口径 |
|---|---|
| 总题数 `questionCount` | 题库题目总数 |
| 已刷题数 `answeredCount` | 做过的不同题目数（去重） |
| 已掌握 `masteredCount` | 每题「最近一次作答」为正确 |
| 待复习 `wrongCount` | 每题「最近一次作答」为错误 |
| 累计答题 `totalAttempts` | 答题次数（含重复） |
| 正确率 `accuracy` | 答对次数 / 答题次数（0-100） |
| 覆盖率 `coverage` | 已刷 / 总题数（0-100） |
| 最近练习 `lastAnsweredAt` | 最近一次作答时间 |

- 后端：`GET api/learn/banks/{bankCode}/stats` → `QuizBankStatsBundleDto`（整体 + 各家庭成员）；`QuizBankService.GetStatsAsync` 取出答题记录后在内存按题分组（取每题最近一次），避免 EF provider 差异。
- Web `/learn`：选家庭成员页顶部统计卡 + 每个成员按钮显示「已刷 x / y · 正确率 z%」；每轮结束回到选择页时刷新。
- 花记鸿蒙：`QuizRepository.getStats` 扩展 `answeredCount` / `masteredCount` / `accuracy`；`QuizListPage` 卡片与 `QuizStudyPage` 配置页展示。
- 花记安卓：`QuizDao.getStats` 增加 `COUNT(DISTINCT questionCode)`；`QuizBankPage` 卡片与 `QuizPage` 配置页展示。
- 移动端为离线判定，统计取本地 `quiz_attempts`，与 Web 端口径一致但数据源独立（不依赖服务端答题记录）。
## 8. 普通用户友好：代码自动生成 + 自适应题量（2026-11-09 追加）

**代码类字段全部自动生成**（行业代码 / 题库代码 / 题号前缀对普通用户过于专业）：

- 生成页默认只显示：行业（名称下拉）、行业名称、题库名称、生成主题、题型、题量、难度等业务字段。
- 行业代码 / 题库代码 / 题号前缀收进「高级选项」折叠区，留空即自动生成。
- 生成服务 `EnsureCode`：留空时生成 `ind_xxxxxx` / `bank_xxxxxx` / `ai_xxxxxx`（6 位随机，保证 append 模式不撞号）。
- 行业下拉仍映射到画像 code（tcm/programming 等），确保复用既有行业与题型。
- MCP `baihua_learn_prompt_template` 同样支持 `industry_code` / `bank_code` / `code_prefix` 留空自动生成。

**自适应题量**（固定数量未必最适合，让 AI 按知识点覆盖决定）：

- `QuizGenerateRequestDto.QuestionCountMode`：`fixed`（默认）/ `auto`。
- UI 题量下拉新增首选「自适应（由 AI 按知识点覆盖决定 8-30 题）」并设为默认。
- 提示词改为「先规划覆盖该主题所需的知识点，再为每个知识点生成 1-3 题；总题数 8-30，覆盖完整性优先于数量」，并追加硬性规则「每个主要知识点至少 1 题，不为凑数生成重复题」。
- 自适应题量按 **3 批 × 10 题（上限 30）** 分批：每批提示只覆盖该主题对应部分、不跨批重复；单批输出上限 16000 tokens；固定模式仍按 10 题分批。
- MCP 侧 `question_count = 0` 表示自适应。
## 9. 生成改为后台任务（2026-11-09 追加）

**问题**：原 `POST api/learn/generate/preview` 是同步长请求（最长 15 分钟），页面关闭/刷新会取消 Blazor circuit 并连带取消后端生成；请求线程也被长时间占用。

**方案**：任务化 + 进度轮询，生成在后台继续，页面可关闭。

| 部分 | 设计 |
|---|---|
| 契约 | `QuizGenerateJobDto`（jobId/status/progress/message/result/error/时间戳）、`QuizGenerateJobSummaryDto`（列表轻量项）、`QuizGenerateJobCreatedDto` |
| 状态 | `queued → running → succeeded / failed / canceled`；结果仍为 `QuizGeneratePreviewDto`（题库包+校验），**不自动入库** |
| 存储 | 进程内 `ConcurrentDictionary`，保留 2 小时、最多 50 条；重启丢失（预览结果本就需要用户确认，可接受） |
| 队列 | `Channel<string>` + `BackgroundService` 单消费者，**全局单并发**（避免同时打爆 AI provider） |
| 进度 | Generator 每批完成回调 `IProgress<QuizGenerateProgress>`（第 i/n 批），任务表换算百分比 |
| 端点 | `POST api/learn/generate/jobs`（立即返回 jobId）、`GET .../jobs/{id}`、`GET .../jobs?limit=10`、`POST .../jobs/{id}/cancel`；旧同步端点保留兼容 |
| UI | 创建任务后显示进度并每 2s 轮询；页面关闭不影响任务；重新进入 `/learn/generate` 自动恢复运行中任务并展示最近任务列表 |
| 超时 | 由后台任务自己的 CancellationToken 控制；不再依赖页面 HTTP 请求超时 |

**验证**：
- 单测（注入 fake `IQuizBankGenerator`）：状态流转、进度、失败、取消、列表、过期清理；
- E2E：页面渲染 / 最近任务区 / 粘贴通道回归；
- 手工 API 全链路：创建真实任务后不保持请求，轮询到完成，验证结果可预览导入（证明关页面不影响）。

## 10. 运维：bh 启动与调用方 Job Object 隔离（2026-11-09 追加）

**问题**：`bh.ps1` 用 `Start-Process -RedirectStandardOutput` 启动服务，调用方（如 CodeArts 工具的 background 命令）在 timeout 到期时会**强制终止整个进程树**，把 bh-server/bh-webui 一并杀死。现场指纹：应用日志无关闭记录、`pids/*.pid` 残留、无 .NET 崩溃事件。

**修复**：`Start-One` 改用 WMI `Win32_Process.Create` + 临时启动脚本（含环境变量）分离启动；服务不继承调用方 Job Object；端口就绪后按端口反查真实 pid 写入 pid 文件（`Stop-One` 已有的端口 fallback 仍保留）。
## 11. 生成稳定性修复（2026-11-09 追加）

真实生成大题库时暴露并修复的三个问题：

| 问题 | 现象 | 修复 |
|---|---|---|
| OpenAI SDK 默认网络超时 100 秒、内部重试 4 次 | 10 题以上长 JSON 整批超时（`Retry failed after 4 tries`），任务拖延数分钟后失败 | `OpenAIClientOptions.NetworkTimeout` 提高到 **3 分钟**（shim 与直连两处） |
| 取消令牌在 shim 链路不生效 | 任务可能永久 `running`，`CancelAfter` 无法中断底层调用 | **`Task.WhenAny(调用, 超时)` 硬兜底**，每批 4 分钟；超时返回可操作提示 |
| 自定义题型时 schema 仍显示画像默认 target | 中医画像下生成「穴位题」会被误导回「选方剂」 | schema 的 `target` 显示实际题型（`mixed` 才列画像全部题型） |

**真实数据验证（2026-11-09）**：用提示词「结合《针灸大成》和当前大学的针灸教材来设计题目，覆盖最重要最有用的知识点，提升临床能力和疗效」→ `deepseek-flash` 分 3 批生成 **30 条目 / 30 题**（自定义题型 `acupoint`），append 导入「针灸大成」（`bank_f335fe`）；`/learn` 列表出现该题库、统计 30 题、刷题页为真实针灸题（俞募配穴法，4 个穴位组合选项）。

> 注意：任务 `succeeded` 只代表**预览结果**已生成，必须在预览页点「确认导入」才算落库。
## 12. 收尾：自动打开结果定位 + 题库删除 + 一轮 review 优化（2026-11-09 追加）

### 12.1 「关闭页面后重开不自动打开」的真正原因：CLI Token 5 分钟过期

上一轮交接文档把该问题记成「自动化 E2E 不稳定」，怀疑过渲染管线、代理、超时、响应体积（均已排除）。
实测根因与界面无关：

```
POST /api/auth/cli-token → token 有效期 5 分钟（AuthService.CliTokenExpiryMinutes）
真实自适应生成耗时 6-8 分钟 → E2E 在生成完成后用同一个 token 打开新页面
→ 中间件 ValidateCliToken 失败 → 重定向 /login?returnUrl=...
→ 没有 .preview-q、没有 .alert-error；日志只有 LogDebug（默认级别看不到）
→ 看起来就是「页面停在表单、自动打开没生效」
```

复现证据：`node` 脚本取 token 后等 330 秒再打开，最终 URL 为 `/login?returnUrl=%2Flearn%2Fgenerate%3Fcli-token%3D...`。
结论：**生成页的自动打开逻辑本身没问题**（短脚本、全新浏览器、多题量均验证通过），是 E2E 脚本拿过期 token 复用了。

修复（测试侧）：`scripts/verify-learn-adaptive-e2e.cjs` 在重开页面时**重新获取 token**，并在注释里写明这个坑。
产品侧同时给自动打开加了可诊断性：进入生成页会 `LogInformation` 记录「自动打开了哪个任务、几分钟前完成」，
没有候选任务时 `LogDebug` 记录判定依据（避免下次再把「无候选」误判成渲染问题）。

### 12.2 显式 job 深链（不再只靠「最近 30 分钟」推断）

- 生成页支持 `?job=<jobId>`（`[SupplyParameterFromQuery]`）：任务管理页点某条生成任务直接跳到
  `/learn/generate?job=xxx` 精确打开该任务，不再「猜最近一条」。
- 无 `job` 参数时仍自动打开「最近 30 分钟内成功」的结果（关页面再回菜单也能接上），
  并新增「本页没在跟这条任务、但收到成功推送 → 直接打开」分支（例如从别的标签页发起）。
- 「返回修改」只压掉**当前打开的那一条**任务（`openedJobId`），不再误伤其它任务。

### 12.3 新增：删除题库

删除是管理动作，因此：

| 层 | 实现 |
|---|---|
| 契约 | `QuizBankDeleteResultDto`：题库代码/名称 + 条目数/题目数/答题记录数 + 是否连带删除行业 |
| 服务 | `QuizBankService.DeleteBankAsync`：**显式逐级删除**（答题记录 → 题目 → 条目 → 题库），返回精确计数；该行业下已无题库时一并删除，避免生成页出现孤立行业 |
| 端点 | `DELETE api/learn/banks/{bankCode}`，只挂 `api/learn`（管理面板域），**不挂 `mg/learn`** —— 移动端同步域不得具备删库能力 |
| WebUI | `/learn` 题库卡片加「🗑 删除」：站内两步确认（先变「确认删除」，再点执行，与全站自定义确认一致，不用原生 confirm），删除前提示「N 条目 / M 道题 / 答题记录将一并删除且不可撤销」，删除后显示计数 |
| 测试 | 服务层 5 例（连删/不影响同行业其它库/未知库/空代码/删后重新导入可重建）+ 控制器 2 例（计数 + 404）+ E2E（导入 → 列表出现 → 两步确认删除 → 列表与后端都不再有） |

不提供 MCP 删除工具：Agent 一旦误调就会清掉人工整理的题库，删除只由人类在管理面板操作。

### 12.4 本轮 review 的其它优化

| 位置 | 问题 | 处理 |
|---|---|---|
| `/tasks` | 点生成任务忽略 `jobId`，只跳到生成页 | 改为 `/learn/generate?job=<id>` 深链 |
| `/tasks` | 「隐藏已完成」对生成任务不生效 | 新增 `VisibleLearnJobs()`，与普通任务同一口径 |
| `/learn` | 导出按钮固定导出 `banks[0]`（多题库时导错库） | 导出按钮移到每张题库卡片上 |
| `/learn` | 导出链接指向 `/api/learn/...`，但 **WebUI 没有 API 反向代理**，浏览器点了必然 404（旧实现也是坏的，只是没人点过） | `Baihua.Web` 新增 `GET /api/learn/banks/{code}/export`：用登录 Cookie 鉴权、经 `_longHttpClient` 代取后端文件后以附件回给浏览器；E2E 断言真实下载到 JSON |
| `/learn` | 无题库时看不到「去生成」入口 | 生成入口移出 `else` 分支，始终可见 |
| 生成器 | 跨批合并 `AddRange` 不去重，AI 忽略题号前缀时产生重复题号（导入时静默跳过/覆盖） | 新增 `MergeBatch`：按 code 去重并计入 warnings，预览页可见「第 N 批有 K 条重复已丢弃」 |
| 生成器 | 固定题量批次也用 16000 tokens 上限（超配） | 统一 8000（每批 ≤10 题足够），删掉多余常量 |
| 生成器 | 规划知识点阶段最长 3 分钟，UI 一直停在 0% | 规划阶段上报进度「正在规划知识点…」 |
| 任务表 | 取消「排队中」的任务要先等 Worker 取到才变状态（UI 先闪「生成中」） | 排队中取消立即置 `canceled` 并推送，Worker 取到后直接跳过 |
| 生成页 | 「返回修改」用「第一条成功任务」当 dismissed，可能压错任务 | 用实际打开的 `openedJobId` |
| 生成页 | Hub 是 fire-and-forget，页面在连接建立期间关闭会泄漏连接 | 加 `disposed` 标记，晚到的连接立即释放 |

### 12.5 验证（本轮，2026-11-09 16:1x）

- `dotnet build services/BaiHua.slnx -c Release`：0 错误。
- 单测（本机 testhost 无法 `OpenProcess` 父进程，仍用反射执行器跑同一测试程序集）：
  `PASS 291, FAIL 0, SKIP 1`（SKIP 为需要 xunit fixture 的 `Fam22SettingsRouteTests`，反射无法注入构造参数）。
  新增用例：删库 5 例（连删/不影响同行业其它库/未知库/空代码/删后重新导入可重建）、控制器删除 2 例、
  跨批去重 4 例、排队任务取消 1 例。
- `scripts/verify-learn-generate-e2e.cjs`（不耗 AI，可随时重跑、自行清理）：
  粘贴包 → 校验预览 → 删一题 → 导入 → 列表出现 → **真实下载导出 JSON（1768 字节）** → 两步确认删除 → 后端列表不再有。
- `scripts/verify-learn-adaptive-e2e.cjs`（真实 AI，约 6 分钟）：自适应两阶段生成 **30 条目 / 30 题（5 批，deepseek-flash）**
  → `/tasks` 合并列表 → 关闭页面后（新 token）重开**自动打开 30 题结果** → 点任务行走 `?job=` 深链**精确打开同一任务** → `/tasks` 显示已完成。
- 数据核对：真实库未受影响（经方 259 题 / 219 条目、针灸大成 30 题 / 30 条目；行业仅 `tcm`）。
- 已知环境坑（非代码问题，供后续参考）：
  - `bh build-restart server` 依赖调用方 shell 的 `PG_PASSWORD`（native cell 不管理 PG）；缺失时服务起不来，
    表现为 `28P01 password authentication failed`。本机值在系统环境变量里（`HKLM\SYSTEM\...\Environment`），
    也可从任意管理员 shell 的 `$env:PG_PASSWORD` 读取。
  - WebUI 的 CLI Token 只有 5 分钟，长任务 E2E 重开页面必须重新取 token（见 12.1）。
