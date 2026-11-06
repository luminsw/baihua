# 花记移动端题库功能计划（鸿蒙 + 安卓）

> 2026-11-06 | 前置调查完成。两端（arkts / kotlin 仓库）各自新增「从百花获取题库 + 离线做题」功能，百花后端零改动。

## 1. 现状与边界

**百花后端已完全就绪（2026-11-03 收官时已预留）：**

- `LearnBankController` 双路由：`api/learn/*`（WebUI）+ `mg/learn/*`（移动端同步域）
- `/mg/learn/` 已注册 `Program.cs` 三处路径表（设备授权中间件 + HMAC 签名中间件 + CORS 放行）
- 移动端消费端点（`mg/learn/*`，HMAC 签名 + 已配对设备自动校验）：

| 端点 | 用途 |
|---|---|
| `GET mg/learn/banks` | 题库概览列表（行业/题库名/版本/itemCount/questionCount） |
| `GET mg/learn/banks/{code}/package` | 全量题库包 `QuizBankPackage`（含答案，camelCase JSON，约 1.5MB） |

- 数据现状：jingfang 经方题库 **219 方 / 259 题**（选方 190 + 选证型 69）
- **鉴权零成本**：两端 HttpTransport 自带 HMAC 签名 + 设备头 + 401 恢复 + HTTPS 降级，新端点走既有传输层即可

**同步协议（控制器注释已定，两端照此实现）：**

1. 进题库页 → `GET banks` 拉概览（带 30 秒内存缓存，照抄知识库列表缓存模式）
2. 与本地库版本比对：本地无 → 「获取」按钮；服务器 version ≠ 本地 → 显示「有更新，点击获取」
3. 点获取 → `GET package` 全量下载 → 结构化落本地库（幂等覆盖）
4. 做题**全离线**：本地随机抽题 → 本地判定（`options[].isCorrect` 静态声明，比对所选 key）→ 展示解析/区分点
5. 作答记录不上传（服务端 `QuizAttempts` 仅供 Web 端 LearnerProfile 使用；移动端无成员概念，设备即个人）

## 2. 做题交互（两端一致，对齐 Web `/learn` 页）

会话式刷题流：

1. **配置轮次**：题目数选择（默认 10，可选 5/10/20）
2. **每题**：题型标签（选方/选证型）→ 患者概况 + 体征清单（结构化 StemDetail）→ 题干 → 4 选项按钮
3. **点选即判**（本地判定）：对错横幅 + 正确答案 + 各选项解析（Explanation）+ 📖 完整解析（Analysis）+ 🎯 区分点对照表（Discrimination）
4. **答完结算**：得分 + 错题回顾 + 「重做错题」+「再来一轮」
5. **UI 配色**：鸿蒙走 `ThemeColors`（茶青绿，亮暗资源自动切换），安卓走 `MaterialTheme.colorScheme`（绿主题）；全程暗色适配，禁止硬编码颜色
6. **文案**：操作描述用「获取」不用「同步」

## 3. 鸿蒙端改动清单（arkts 仓库）

| # | 文件 | 改动 |
|---|------|------|
| 1 | `baihua_sdk/src/main/ets/contract/QuizSync.ets`（新建） | `QuizBankOverview`/`QuizBankPackage`/`QuizQuestion`/`QuizOption` 等 DTO（字段名与后端 camelCase 完全一致——ArkTS `JSON.parse` 大小写敏感） |
| 2 | `baihua_sdk/src/main/ets/sync/QuizServiceImpl.ets`（新建） | `fetchBankListAsync`（GET banks，30s 缓存）+ `fetchBankPackageAsync`（GET package）；组合既有 `HttpTransport`，鉴权自动生效 |
| 3 | `baihua_sdk/Index.ets` | 导出新类型与服务 |
| 4 | `vault_sdk/src/main/ets/NoteDatabase.ets` | 新增 `quiz_banks`/`quiz_questions` 表 DDL |
| 5 | `vault_sdk/src/main/ets/QuizRepository.ets`（新建） | upsert 包/查列表/随机抽题/版本查询 |
| 6 | `entry/src/main/ets/services/QuizSyncService.ets`（新建） | 编排：拉包 → 入库 → 更新本地版本（照抄 `SyncService.syncVault` 骨架） |
| 7 | `entry/src/main/ets/pages/QuizListPage.ets`（新建） | 题库列表：信息卡 + 获取按钮 + 进度 + 已获取状态（视觉区分，参照 VaultRowComponent 状态映射范式） |
| 8 | `entry/src/main/ets/pages/QuizStudyPage.ets`（新建） | 做题页（会话式刷题流，交互骨架参照 CardStudyPage） |
| 9 | `entry/src/main/resources/base/profile/main_pages.json` | 注册两个新页面 |
| 10 | `entry/src/main/ets/components/TopNavBar.ets` | 页顶菜单加「📝 题库」项（与 记忆/师父/搜索 并列） |
| 11 | `entry/src/test/`（新建） | QuizSyncService / DTO 解析单测（mock 注入模式） |

## 4. 安卓端改动清单（kotlin 仓库）

| # | 文件 | 改动 |
|---|------|------|
| 1 | `baihua-sdk/.../contract/Quiz.kt`（新建） | DTO（`@SerializedName` 显式 camelCase，与后端契约对齐） |
| 2 | `baihua-sdk/.../sync/QuizServiceImpl.kt`（新建） | 组合 `HttpTransport` 的拉取服务（鉴权自动生效） |
| 3 | `app/.../data/NoteDatabase.kt` | `DATABASE_VERSION` 升版 + 新增 `quiz_banks`/`quiz_questions` 建表 |
| 4 | `app/.../data/QuizDao.kt`（新建） | 仿 VaultDao：upsert/列表/随机抽题/版本查询 |
| 5 | `app/.../data/NoteModels.kt` | 题库 data class |
| 6 | `app/.../sync/QuizSyncService.kt`（新建） | 编排：拉包 → 入库（仿 VaultSyncService，含密钥恢复检查） |
| 7 | `app/.../ui/QuizBankPage.kt`（新建） | 题库列表页（仿 BrowseVaultPage 卡片 + 进度条） |
| 8 | `app/.../ui/QuizPage.kt`（新建） | 做题页（交互仿 CardStudyPage） |
| 9 | `app/.../ui/MainActivity.kt` | NavHost 加 `quizBank`、`quiz/{bankCode}` 路由 |
| 10 | 入口位（TopNavBar 第五项 / HomeTab 快捷入口 / ProfileTab，见决策点 2） | 加入口 |
| 11 | `app/src/test/.../QuizDaoTest.kt`（新建） | DAO 单测（仿 NoteDatabaseCardCascadeTest） |

## 5. 验证

- 安卓：`./gradlew :app:compileDebugKotlin` + `testDebugUnitTest`（DAO 测试全绿）
- 鸿蒙：`hvigorw test`（新增单测通过；如环境可用跑 `scripts/verify-local.ps1`）
- 实机集成验证：连接百花服务器走通「列表 → 获取 → 做题 → 结算」（用户手动，两端各一次）

## 6. 已确认决策（2026-11-06 用户拍板）

1. **作答记录：本地持久化错题本**——答题历史入库（题库表外增 `quiz_attempts` 记录表），结算页错题回顾可跨会话留存
2. **入口：「我的」页菜单项**——鸿蒙 ProfilePage / 安卓 ProfileTab 菜单列表加「📝 题库」，首页顶栏不加项
3. **「AI 分析」按钮：带上**——判定反馈页加 AI 分析按钮，接两端既有 ai_sdk（DeepSeek）针对本题展开讲解