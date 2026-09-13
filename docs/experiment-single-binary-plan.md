# 实验分支：self-contained 单二进制 + 嵌入式 DB + 单进程

> **状态**：实验性，不合 main。目的：体验"像 OpenClaw 那样单二进制一键跑"的形态。
> **分支**：`experiment/self-contained-single-binary`（基于 main）

## 动机

对标 OpenClaw 的 `curl | bash` 体验。当前百花部署需 k3s + PG + 多容器，太重。
本分支验证：能否打成**一个二进制 + 一个 SQLite 文件**，`./baihua` 直接跑出完整服务（含 WebUI）。

## 调查结论（已完成）

### DB 迁移（PG → SQLite）：中低难度
- 3 个 DbContext（Family/Vault/AI），38 张表，主键全 int 自增
- **无硬障碍**：无 jsonb/uuid/timestamptz/数组/pgvector 列类型；JSON 和向量都用 string 存
- `VaultNoteIndexer` 已有 SQLite FTS5 分支；`DatabaseInitializer` 已有 sqlite_master 分支
- 主要工作：`now()` → `CURRENT_TIMESTAMP`（45 处机械替换）、2 个文件手写 PG DDL 改写、provider 切换

### 进程合并（WebUI → Server）：中低难度
- WebUI 是 .NET 8+ Blazor Web App（Interactive Server），仅通过 HTTP + Contracts 耦合后端
- **无项目引用 Core/Data/Modules**，合并最有利因素
- 最大风险：SignalR JSON 序列化策略冲突（Server PascalCase vs Blazor circuit 期望 camelCase）+ 认证中间件合并

## 范围（最小体验版本）

**做**：
- SQLite 替换 PG（双 provider，按配置选；默认 SQLite）
- 合并 WebUI 进 Server 单进程
- self-contained 单二进制 publish
- 核心功能能跑：管理面板、配对、知识库同步、AI chat

**不做**（实验分支够体验即可）：
- PG→SQLite 数据迁移脚本（新装用空库）
- k8s/compose 部署改造（用 `dotnet run` 体验）
- ApiService 改进程内直调（保留 HTTP 自调 8788 能跑就行）
- 全面测试（编译过 + 核心路径能跑即可）

## 步骤

### B-1: SQLite provider + now() 统一
- `Baihua.Data.csproj` 加 `Microsoft.EntityFrameworkCore.Sqlite`
- 所有 `HasDefaultValueSql("now()")` → `HasDefaultValueSql("CURRENT_TIMESTAMP")`（PG 也认）
- 3 个 DbContext 的 `OnConfiguring` 兜底加 SQLite 分支

### B-2: 手写 PG DDL 改写
- `StartupOrchestratorHostedService.cs`：4 张表的 PG 方言 DDL 加 SQLite 分支
- `AiModule.cs`：2 张表的 PG 方言 DDL 加 SQLite 分支

### B-3: 连接串 + provider 选择
- `DbConnections.cs`：加 SQLite 文件路径构建（`BAIHUA_DB_PROVIDER=sqlite` + `SQLITE_PATH`）
- `Program.cs`：6 处 `UseNpgsql` 按配置选 `UseSqlite`/`UseNpgsql`

### B-4: 合并 WebUI 进 Server
- 迁移 `Baihua.Web` 的 Components/Pages/Shared/wwwroot/Hubs/Services/Middleware 进 `Baihua.Server`
- Server Program.cs 挂 `AddRazorComponents` + `MapRazorComponents`
- 解决 SignalR 序列化：统一 camelCase（AGENTS.md 倾向）
- 合并认证中间件（WebUI Cookie + Server 访问控制）
- `BaihuaServer:BaseUrl` 改 loopback 自调

### B-5: self-contained publish
- csproj 加 `PublishSingleFile` + `SelfContained` + `IncludeAllContentForSelfExtract`
- `dotnet publish -r linux-x64 -c Release` 产出单文件
- 验证：`./baihua-server` 起来，浏览器打开能登录看面板

## 风险
1. SignalR 序列化冲突可能致 Blazor circuit 异常 → 统一 camelCase + case-insensitive 容错
2. 认证中间件顺序错误可能致 Blazor 页面被 403 或 API 被重定向登录页 → 仔细调中间件顺序 + 豁免路径
3. Blazor 静态资源在 self-contained 单文件下可能路径异常 → 用 `IncludeAllContentForSelfExtract` 或 `PublishReadyToRun`

## 后续 C 分支（桌面 App）
基于 B 的单进程，用 Photino.NET 包一个原生窗口内嵌 ASP.NET Core + Blazor，双击打开即用。