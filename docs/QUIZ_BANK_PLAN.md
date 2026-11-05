# 多行业选择题题库（学习模块）实施计划

> 2026-10-27 立项。目标：把「百花拜师」中医学习数据做成**选择题题库**，补全伤寒/金匮全方剂内容，
> 在百花增加**选择题形式的学习模块**；数据库设计**行业无关**（行业→题库两级），为花阁/花记复用做准备。
>
> **状态（2026-11-03）**：S1–S5 已完成——契约/实体/服务/API/UI（/learn）落地，内容 219 方 259 题
> （`data/tcm-quiz/`：B0 基线包 + B1~B10 增量包），题库包导出端点可用；回归测试见
> `tests/Baihua.Family.Tests/Services/QuizBank*` 与 `tests/Baihua.Family.Tests/Learning/QuizBankSourceDataTests.cs`。

## 一、现状调查结论

| 资产 | 现状 | 对本计划的意义 |
|---|---|---|
| 百花学习域 | `FamilyDbContext` 已有 `LearnerProfile`/`StudyActivity`/`CardReviewState`；`Modules.Family/Services/Learning/` 已有 `QuizService`（亲子简答对战，卡片当题库）/Anki/成就引擎 | 学习模块**沿用现有学习域**（同 DbContext 同 Services 目录），不新建模块 |
| qihuang-journey | 69 案结构化 JSON：四诊症状＋标准答案（证型/方剂/病机/治法）＋**干扰项（方剂＋差异眼目）**＋讲解＋辨误标签＋全套词表（方剂 75/证型 71/症状 374…） | 已学内容的**最佳数据源**——干扰项与区分眼目正是选择题核心，近乎无损转换 |
| 拜师进度 | 六经 20 案＋金匮 64 案（痰饮/水气/胸痹/腹满/血痹虚劳/黄疸/血证/呕吐哕下利），约覆盖 70+ 方 | 已学 70 方可直接入库；剩 ~230 方需分批生产 |

## 二、数据模型（行业无关，中医是第一个实例）

表加在 `FamilyDbContext`（学习域惯例），实体放 `Baihua.Data/Entities/Family/QuizBankEntities.cs`：

```
QuizIndustries   行业：Code("tcm")/Name("中医")                    — 一级
QuizBanks        题库：IndustryId FK, Code("jingfang")/Name("经方")/Version — 二级
QuizItems        知识条目（以药方为核心）：BankId FK, ItemCode, ItemType("formula"),
                 Name(方名), Summary, ContentJson(条文/组成古量今量/煎服法/病机/治法/禁忌/鉴别)
QuizQuestions    选择题：BankId FK, ItemId?(关联方剂), Stem(题干:症状描述), StemJson(结构化四诊),
                 OptionsJson([{key,content,isCorrect,explanation}]), CorrectKey,
                 Analysis(正确结果+分析), DiscriminationJson(相似方/证型关键区分点),
                 Difficulty(1-5), SourceRef(伤寒XXX条/金匮第X篇)
QuizAttempts     答题记录：LearnerId?(FK,可空=访客), QuestionId FK, SelectedKey,
                 IsCorrect, TimeCostMs?, AnsweredAt, SessionId?
```

设计原则：
- **判定零 AI**：`isCorrect` 是数据静态字段，判定＝比对 `SelectedKey`，纯确定性。
- **选项/内容走 JSON 列**：题目以「题库包文件」为导入导出单位，无需 SQL 级查询选项内容；查询需求（按题库抽题/按方剂关联/难度筛选）全在结构化列上。
- **行业无关**：表结构无任何中医字段；中医语义全在数据内容里。做「驾照考试」行业时零改表。

## 三、题库包文件契约（百花↔花阁↔花记的交换格式）

放 `Baihua.Contracts/Learn/QuizBankContracts.cs`（WebUI 与 Server 共享；花阁/花记未来按同结构解析）。
JSON 序列化遵仓库规范（camelCase + case-insensitive）：

```json
{
  "formatVersion": 1,
  "industry": { "code": "tcm", "name": "中医" },
  "bank": { "code": "jingfang", "name": "经方", "version": "2026.11", "description": "…" },
  "items": [ { "itemCode": "f_xiexin_tang", "itemType": "formula", "name": "泻心汤",
               "summary": "…", "content": { …条文/组成/煎服法/病机/治法/禁忌/鉴别… } } ],
  "questions": [ { "questionCode": "q_053_1", "itemCode": "f_yinchen_hao_tang",
                   "stem": "…症状描述…", "stemDetail": {…四诊…},
                   "options": [ { "key": "A", "content": "茵陈蒿汤", "isCorrect": true,
                                  "explanation": "…" }, …干扰项+区分眼目… ],
                   "analysis": "…正确结果+分析…",
                   "discrimination": [ { "vs": "栀子柏皮汤", "point": "腹满便结+苔黄厚腻=湿热并重…" } ],
                   "difficulty": 3, "sourceRef": "金匮·黄疸病篇" } ]
}
```

- 百花：导出端点 `GET /api/learn/banks/{bankCode}/export`（落文件下载）＋ 导入端点（admin）。
- 花阁（未来）：上传此文件 → 解析入库。
- 花记（未来）：从百花/花阁拉取此文件 → 本地刷题。

## 四、数据管道

**内容主数据仓**：baihua 仓库 `data/tcm-quiz/`（数据驱动铁律——加内容=加 JSON 不动代码）：
```
data/tcm-quiz/
  01-shanghan-sun.md/json   伤寒·太阳篇（分批生产）
  …（按病篇一文件一批）
  _from-qihuang/            一次性：qihuang-journey 69 案转换脚本产物
```

**管道**：`scripts/tcm-quiz-build.*`（Node 或 pwsh）读 qihuang-journey `data/`＋`data/tcm-quiz/` 各批文件 →
拼装成 `QuizBankPackage` JSON → 百花导入端点（或 CLI）入库。
- qihuang-journey 保持独立游戏仓库不受影响；其 69 案做**一次性源转换**，此后新内容直接在 baihua 数据文件维护。
- 每案转 2 类题：①症状→**方剂**选择（干扰项=case.干扰项+同篇相似方）②症状→**病机/证型**选择（干扰项=同篇相似证型）；解析=讲解，区分点=眼目。

## 五、内容生产计划（补全伤寒+金匮）

总量估算：伤寒 ~112 方＋金匮 ~180 方（去重）≈ 300 方；每方 1-2 题 ≈ 400-600 题。已覆盖 ~70 方，**剩 ~230 方，是多会话工程**。

批次顺序（遵循拜师教学线的病篇次序，先易后难、先伤寒后金匮）：

| 批 | 范围 | 方数(约) |
|---|---|---|
| B0 | 已学转换（六经 20＋金匮 64 案） | 70 |
| B1 | 伤寒·太阳上/中（桂枝/麻黄/葛根系＋汗法变局） | ~25 |
| B2 | 伤寒·太阳下（结胸/痞/蓄水蓄血） | ~25 |
| B3 | 伤寒·阳明/少阳 | ~25 |
| B4 | 伤寒·三阴（太阴/少阴/厥阴） | ~30 |
| B5 | 伤寒·霍乱/阴阳易差后劳复＋合并整理 | ~10 |
| B6 | 金匮·痉湿暍/百合狐惑阴阳毒/疟病/中风历节 | ~30 |
| B7 | 金匮·肺痿肺痈咳嗽上气/奔豚/腹满(补遗) | ~30 |
| B8 | 金匮·五脏风寒/痰饮(补遗)/消渴小便不利淋/水气(补遗) | ~30 |
| B9 | 金匮·黄疸(补遗)/惊悸(补遗)/趺蹶手指臂肿转筋阴狐疝蛔虫 | ~25 |
| B10 | 金匮·妇人妊娠/产后/杂病 | ~40 |

内容生成铁律（承拜师线）：每方**条文照录原文**（标出处）、组成古量＋今量、煎服法全文、
病机/治法/禁忌、与相似方的**区分眼目**（干扰项设计即来源于此）；全部由助手（师父）按原文产出，
不交本地模型（幻觉教训红线）。

## 六、百花学习模块 UI

新页面 `/learn`（Blazor Server，InteractiveServer）：
1. **题库选择**：行业→题库两级列表（当前只有「中医→经方」）
2. **刷题流**：题干卡（症状描述＋四诊要点）→ 四选一（干扰项为相似方/证型）→ 提交后即时反馈：
   对错＋**正确答案＋分析**＋**「与易混淆选项的关键区分点」**（Discrimination 渲染为对照表）→ 下一题
3. **结算页**：正确率/用时/错题回顾（可重做错题）
4. **「AI 分析」按钮**：每题反馈区可选展开；点击才把题干＋用户答案＋正确解析发给百花 AI 模块
   （`api/assistant/chat`，走用户已配 provider）做**延伸讲解**——按钮旁标注「由 AI 生成，可能产生费用」；
   未配 provider 时禁用。AI 只做讲解延伸，**判定永远是确定性比对**（不付费功能完整可用）。
5. 答题写 `QuizAttempts`；后续可接 StudyActivity 打卡/成就引擎（增强项，非首版必须）。
6. 学习者身份复用 `LearnerProfile`（可选「访客」匿名刷题）。

后端：`Modules.Family/Controllers/Learn/QuizBankController.cs`（api/learn/*）＋
`Services/Learning/QuizBankService.cs`（导入/导出/抽题/判定/记录）——零 AI 判定路径。

## 七、实施阶段

| 阶段 | 内容 | 交付物 |
|---|---|---|
| S1 | 契约＋实体＋DbContext＋建表 | Contracts/Learn、QuizBankEntities、编译过 |
| S2 | 转换脚本＋导入/导出端点＋B0（69 案）入库 | 题库包 JSON、api/learn/*、库里有 70 方 ~140 题 |
| S3 | 学习模块 UI（刷题流＋反馈＋结算＋AI 按钮） | /learn 页面，playwright 验证 |
| S4 | 内容批次 B1…B10（多会话，每批一文件一导入） | 覆盖全伤寒金匮 ~300 方 |
| S5 | 导出文件端到端＋（未来）花阁上传/花记下载对接 | 题库包文件可下载分发 |
| S6 | 移动端同步端点（服务端，2026-11-05 完成） | `mg/learn/*` 双前缀＋鉴权域接入；花记两端接入后续任务 |

## 八、移动端题库同步协议（S6，2026-11-05）

题库是结构化数据（非文件树），不照搬知识库的 manifest→file 两步协议，简化为「列表比对版本 → 全量拉包」：

| 端点 | 返回 | 说明 |
|---|---|---|
| `GET /mg/learn/banks` | `QuizBankOverviewDto[]`（camelCase） | 含 `bankCode`/`version`/`questionCount`，端上比对版本决定是否更新 |
| `GET /mg/learn/banks/{bankCode}/package` | `QuizBankPackage`（camelCase） | 全量包，**含 `isCorrect`**——花记下载后本地离线判定（判定零 AI：比对选项 key） |

鉴权与知识库 `/mg/vaults`→`/mg/manifest` 同域（`LearnBankController` 双前缀 `api/learn`＋`mg/learn`，
Program.cs 三处路径表均加 `/mg/learn/`）：HMAC 签名（mobileApiPaths）＋已配对设备（vaultMobilePaths）＋
局域网放行（publicPaths）。移动端调用需携带 `X-Device-Id` 与 HMAC 签名头（与 Kotlin `RequestSigner` 算法一致）。

同步协议（花记端建议实现）：
1. 本地存 `bankCode → version`（已下载版本）；
2. 启动/刷新时拉 `GET /mg/learn/banks`，任一题库 `version` 与本地不同 → 拉该题库 `/package` 全量覆盖；
3. 本地入库后即可离线刷题（抽题、判定、进度全在端上，无服务端 QuizAttempts 记录）。

服务端状态：S6 已落地（服务端先行）；花记鸿蒙/安卓两端接入为后续独立任务。

## 九、待确认决策点

1. **UI 时机**：你原话「数据完全准备好后再做模块」。但 S4（230 方）需要很多轮会话——
   建议 S2/S3 先行（已学 70 方先用起来），S4 内容与使用并行补全；**若坚持数据全齐再做 UI，S3 挪到 S4 之后**。
2. **模块归属**：放 Family 学习域（本计划方案，不新建模块）；如你希望中医独立成 `Baihua.Modules.Tcm` 再说。
3. **行业/题库命名**：行业 `tcm/中医`、题库 `jingfang/经方`（你提的命名方向，我沿用）。
4. **答题记录归属**：挂 LearnerProfile（家庭成员各自进度）＋访客匿名模式——确认这个口径。