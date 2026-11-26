# 开发说明（家庭版 Family）

## 环境信息

### PowerShell 版本

| 命令 | 版本 | 默认编码 |
|------|------|----------|
| `pwsh` | 7.6.4 | UTF-8（无需 BOM） |
| `powershell` | 5.1 | GBK（需要 UTF-8 BOM） |

**推荐使用 `pwsh`（PowerShell 7）**，默认支持 UTF-8，无需处理 BOM 问题。

### BOM 处理方式

- **PowerShell 7 (`pwsh`)**: 默认 UTF-8，脚本文件不需要 BOM。中文显示正常。
- **PowerShell 5 (`powershell`)**: 默认 GBK 编码，脚本文件需要 **UTF-8 with BOM** 才能正确显示中文。
- **脚本头部**: 建议添加 `chcp 65001` 确保控制台使用 UTF-8：
  ```powershell
  chcp 65001 > $null
  [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
  ```
- **改动含中文的 `.ps1` 后必须复查 BOM**：不少编辑器/工具（含本仓库助手所用的文件改写工具）保存时会**丢掉 BOM**，
  文件在 `pwsh` 下正常、在 `powershell` 5.1 下就变 GBK 乱码甚至语法错误。改完自检：
  ```powershell
  $b=[IO.File]::ReadAllBytes('tools\bh\bh.ps1'); $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
  # 补 BOM：$bom=[byte[]](0xEF,0xBB,0xBF); [IO.File]::WriteAllBytes($p, $bom + [IO.File]::ReadAllBytes($p))
  ```

### 终端中文乱码修复（`dotnet build` 输出）

运行 `dotnet build` 时，.NET 输出的 UTF-8 中文可能被 `pwsh` 误解码为 GBK 导致乱码（如"鐢熸垚澶辫触"）。

**修复方式**（已写入 PowerShell Profile `C:\Users\lumin\Documents\PowerShell\profile.ps1`）：
```powershell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$PSDefaultParameterValues['*:Encoding'] = 'utf8'
```
配置后重启终端或重新打开 VS Code 即可生效。

### 服务日志编码（native cell）

`out\native\logs\{server,webui}.log` 是**控制台输出重定向**来的（`start-*.cmd` 里 `exe >> log 2>> log.err`），
所以文件编码一度跟随启动上下文：从带 UTF-8 控制台的终端启动写 UTF-8，计划任务启动写系统 OEM 代码页（简中 936/GBK），
同一个文件被两种编码追加，用 `Get-Content`（pwsh 7 默认 UTF-8）看就是一半乱码。

现在的约定：**日志一律 UTF-8**，三处一起保证（2026-11-09 起）：

- 服务进程内 `Console.OutputEncoding = UTF8(no BOM)`（`Baihua.Server` / `Baihua.Web` 启动第一行）；
- `start-*.cmd` 生成时先 `chcp 65001 >nul`（子进程/原生输出同样 UTF-8）；
- 新建日志文件带 UTF-8 BOM（记事本 / `powershell` 5.1 也能正确识别）。

排查旧文件时：`bh logs <svc>` 会先按严格 UTF-8 解码、失败再回退 ANSI 代码页，两种都能看清；
需要重新开始可直接把旧文件改名（服务停掉后 `Move-Item server.log server.log.mixed-<时间>.bak`，再让 bh 重建）。

控制台（→ `.log`）只有 **Serilog 一个 provider**（`Program.cs` 里已移除 `AddConsole()`）：
两者都写 stdout 会让每条日志出现两次、日志体积翻倍。级别/类别阈值统一由 `builder.Logging` 管线
（`SetMinimumLevel` + `AddFilter`）决定，Serilog 侧不再写第二份 `ByExcluding`，
否则 `Microsoft.AspNetCore` / EF Core 的 **Warning/Error 会被静默吞掉**（控制台上看不到 500、Kestrel、EF 慢查询告警）。

### 项目目录结构

```
C:\Users\lumin\src\
├── baihua/           # 百花（局域网服务器，本项目）
├── mdyj-cloud/        # 花阁官网（云端版）
├── kotlin/            # 花记 Android 客户端
└── arkts/             # 花记鸿蒙客户端
```

### 命令行工具

| 项目 | Linux/Mac | Windows |
|------|-----------|---------|
| 百花 | `./tools/bh/bh.sh`（Linux 原生 systemd + SQLite） | `bh`（native：dotnet 进程 + SQLite）；cmd 下同名 `.cmd` |
| 花阁 | `./hg` | `.\hg.ps1` |

> 百花 CLI 在 Windows 上只有**一种形态（2026-11-25 起）**：
> - `bh <cmd>` = **native**：dotnet 进程跑在 Windows（开发仓库需 .NET SDK 10；部署包 `apps\` 为自包含产物，无需 .NET）；
>   可选容器组件 ovms-gpu / bh-open-webui 走 WSL 3 自带 `wslc.exe`，缺失仅对应功能降级。
>
> `bh dev <cmd>` / `bh native <cmd>` 是历史别名（等价于 `bh <cmd>`）。
> **Linux 上 `bh` 只有一种形态：原生 systemd 服务 + SQLite**（`tools/bh/linux/native/bh.sh`，见 `docs/LINUX_NATIVE.md`）。
> 首次运行与完整命令见 `tools/bh/README.md`。

> 当前架构为**单一后端进程**：`Baihua.Server`（8788）承载家庭 / AI / 知识库三个模块，
> 业务代码拆在三个类库里；WebUI（5177，Blazor Server）仍是独立进程。
> - **Baihua.Server** (8788) — 唯一后端宿主（配置/日志/遥测、模块装配、端点映射）
> - **Baihua.Modules.Family / .Ai / .Vault** — 业务模块类库（家庭/亲子、AI、知识库）
> - **Baihua.Web** (5177) — WebUI，独立进程
>
> 合并前是三个独立服务 + 三库 + 服务间 HTTP 互调，已在 commit `aa053f1` 合并为单进程（历史见 git 提交记录）。
>
> **单一 SQLite 数据库**：默认 `$BAIHUA_HOME/db/baihua.db`（`BAIHUA_DATABASE_PATH` 可覆盖），
> 三个模块保留自己的 `DbContext`（`Baihua.Data` 的 `FamilyDbContext`/`VaultDbContext`/`AIDbContext`），
> 表结构由 `Baihua.Data.DatabaseInitializer` 按上下文统一建表/补表；连接串见 `Baihua.Data.DbConnections.Baihua`。
> 启动时统一设置 WAL / `busy_timeout` / 外键。PostgreSQL 已退役：从旧库迁移用 `bh db-migrate`
> （`tools/db-migrate`，只读源库、逐表摘要校验、支持 `--rewrite-prefix` 重写路径），详见 `docs/LINUX_NATIVE.md`。
> bh 工具链已不再包含 `PG_*` / postgres 容器逻辑，服务端不读写 PostgreSQL。

## 助手 / 自动化约定

- **执行任务时先检查当前是 Windows 还是 Linux**，再选用对应平台的命令与路径写法（PowerShell vs bash、反斜杠 vs 正斜杠、bh 的 win 与 linux 版本等），以免用错命令。
- **服务运行由用户手动按需启停**，助手不自动拉起/保持后台进程：
  - Baihua.Server **8788**（单一后端）、Baihua.Web **5177**（OpenVINO 推理跑在 Windows `ovms` 容器 / Linux 独立 OVMS 容器（REST :8000））
  - 启停统一用 `bh start` / `bh stop`；开发调试可单独 `dotnet watch run`
  - 若某服务未监听，先询问用户是否需要启动，不要擅自拉起
- **WebUI 与后端之间的共享数据类型和 API 接口定义必须放在 `Baihua.Contracts`**，两边禁止各自重复定义。新增或修改 API 契约时，先更新 Contracts，再让两边引用同一版本。
- **共享业务服务（如 `VaultSettingsService`、`VaultNoteIndexer`）放在 `Baihua.Core`**，`Baihua.Modules.Family`、`Baihua.Modules.Ai` 和 `Baihua.Modules.Vault` 均通过引用 `Baihua.Core` 使用（同进程直调）。
- **跨模块调用只经 `Baihua.Core.Modules` 下的接口**（`IBaihuaModule`、`IVaultQueryService`、`IComfyArtworkStore`、`IEmbeddingConfigProvider`、`IAiConfigService`）——**进程内直调，不再有模块间 HTTP**。禁止重引入服务间 HTTP 客户端或转发中间件。

## JSON 序列化与反序列化规范（强制）

> 背景：曾因 `QRToolPanel` 用裸 `JsonSerializer.Deserialize`（默认 PascalCase + 大小写敏感）反序列化后端 camelCase 响应，导致主 AI API Key 二维码不显示；又因合并前 `Baihua.Family` 历史遗留 `PropertyNamingPolicy=null`（PascalCase）与 AI/Vault（camelCase）不一致，导致移动端 `/mg/pair` 字段全部丢失、安卓 `AuthorizationWatcher` 授权判定失效。以下规范防止同类问题。

### 后端（Baihua.Server 统一）
- **JSON 序列化统一 camelCase**（ASP.NET Core 默认）。`AddJsonOptions` 中**禁止**设 `PropertyNamingPolicy = null`；并显式加 `PropertyNameCaseInsensitive = true`（容错入参大小写）。合并后只有一个宿主配置 JSON（`services/Baihua.Server/Program.cs`），三个模块的控制器共享同一套选项。
- SignalR `PayloadSerializerOptions`：`Baihua.Server` 当前保留 PascalCase（内部 WebUI 消费，case-insensitive 容错）；新增 hub 应 camelCase 统一，避免新坑。
- 具名 DTO 若需对移动端/外部暴露特定 key，用 `[JsonPropertyName("camelCase")]` 显式标注（参考 `DeviceBackupDtos.cs`）。

### C# 消费端（WebUI / 服务间互调）
- **优先 `GetFromJsonAsync<T>` / `PostAsJsonAsync`**（HttpClient 扩展，自动 web defaults：camelCase + 大小写不敏感）。
- 必须手动读 body（按状态码分支解析错误体）时，用 `JsonSerializerOptions.Web` 或复用项目已有的 `_caseInsensitiveJsonOptions` / `JsonHelper.CaseInsensitive`，**禁止裸 `JsonSerializer.Deserialize<T>(json)`**（默认 PascalCase + 大小写敏感，遇 camelCase 响应字段全 null）。
- 本地缓存/DB JSON 字段：写读必须**同一套** options；建议也统一 case-insensitive 以防万一。

### 移动端（鸿蒙 ArkTS / 安卓 Kotlin）
- DTO 字段统一 **camelCase**，与后端一致。ArkTS `JSON.parse(x) as T` 与 Gson 默认均**大小写敏感**，字段名必须与后端 JSON key 完全一致。
- 判定接口成功**不要依赖后端可能不返回的 `success` 字段**，改用业务字段非空判断（如 `sharedSecret != null`），与 `DeviceRegistrationService`/`VaultSyncService` 对齐。

### DSH 插件（TypeScript）
- `JSON.parse` + camelCase 接口访问，与后端一致。`baihua-dsh-plugin` 的 `DshController` 已是 camelCase 特例；绘图网关响应直接读 camelCase（`data.success`/`data.fileUrl`），**不要**再做 PascalCase→camelCase 转换。
- **`git push` 失败时**，先启动代理再重试：`pwsh -File "C:\Users\lumin\myhysteria\start.ps1"`，等待几秒后设置 `$env:HTTPS_PROXY="socks5://127.0.0.1:1080"` 再 `git push`。若代理服务器的 443/22 端口同时超时，多半是出口 IP 变化被阿里云安全组拦截：用 `C:\Users\lumin\aliyun-cli\aliyun.exe` 放行新 IP（需先 `aliyun configure`），完整流程见 project-manager 仓库 `docs/ALIYUN_SECURITY_GROUP.md`（服务器地址等敏感信息见本机 `~/.hysteria/config.yaml`，勿写入公开文档）。

## DSH 插件 / 集成（3 个独立仓库）

> 架构定位：**百花 = 能力提供方**（算力池 / 本机模型 / 知识库 / 家庭数据），**DSH（DeepSeek Harness）= 编排与交互面**。
> 插件仓库位于 `~/src/`（org `luminsw`）；部署与配置总文档见 `docs/DSH_INTEGRATION.md`。

| 插件 | 方向 | 作用 | 安装位置 |
|---|---|---|---|
| `baihua-dsh-plugin` | 百花 Web → DSH | 桥接：agent 会话驱动（HTTP+WS `/dsh-bridge/*`）、`bh_*` 运维工具、`baihua_draw*` 绘图、DSH 设置页「百花服务状态」卡片 | DSH web profile（127.0.0.1:3080），`lanListen 0.0.0.0:3081` 局域网桥 |
| `baihua-local-ai-dsh-plugin` | DSH → 百花本地 AI | 探测 OVMS/shim/算力池，注册 `baihua-local` LLM provider + `local_ai_small_task` 小任务工具（省线上 token） | DSH web profile |
| _（百花内置）_ | 百花 → 任意 MCP 客户端 | 标准 MCP（streamable-http `/mcp` 端点，挂在 `Baihua.Server`）：知识库 / 家庭能力（检索/列表/读笔记/创建知识库/写笔记 + 记账/任务），DSH 经 `dsh-mcp-client` 接入（工具名带 `mcp__baihua__` 前缀） | `Baihua.Server:8788/mcp` |

**agent 可直接调用的工具**（由上述插件注册）：

- `bh_status` / `bh_logs` / `bh_op_status` — 只读运维，直接用
- `bh_start` / `bh_stop` / `bh_restart` / `bh_build` / `bh_build_restart` / `bh_update` / `bh_git_commit_push` / `bh_dsh_restart` / `bh_bootstrap` — 变更类运维/宿主机操作，**执行前先询问用户**（插件侧已挂审批门：ask 策略时在 DSH 界面确认，never 时自动拒绝）；编译/更新为长操作，用返回的 `opId` 轮询 `bh_op_status`
- `baihua_draw` / `baihua_draw_video` — 经算力池绘图网关出图/出视频（txt2img / txt2video，支持跨机）
- `local_ai_small_task` — 小而有界的文本任务（短摘要/分类/取词/起标题/简短改写）交给本机 AI，省线上 token；**长文档 / 多步推理 / 写代码用远程模型**
- `mcp__baihua__*` — 百花数据工具：只读（知识库检索/列表/读笔记、记账汇总、任务列表）+ 写（`baihua_vault_create` 建知识库、`baihua_vault_write_note` 写笔记），统一经 `Baihua.Server` 内置 `/mcp` 端点暴露（实现见 `services/Baihua.Modules.Family/Services/Mcp/BaihuaMcpTools.cs`；`baihua-dsh-plugin` 不再注册数据工具）

> 插件配置在 `~/.dsh/cordis.patch.yml`（token / drawGatewayUrl / drawToken / comfyModelType 等）；
> 三个自研 DSH 插件已改**本地 link 方式**安装：`~/.dsh/profiles/web/package.json` 依赖为
> `link:../../../src/<repo>`（bundle 自动挂层），各仓库 `node_modules` 为 junction →
> `~/.dsh/profiles/node_modules`（复用 DSH 全局依赖层，版本与单例一致）；
> 改插件源码后**重启 DSH 即生效**（无需 git push/重装，`npx @deepseek-ai/dsh web`）。

## 目录

- `services/Baihua.Server/`：单一后端宿主（8788；唯一进程：配置/日志/遥测、模块装配、控制器/SignalR/MCP/WebSocket 端点映射）
- `services/Baihua.Modules.Family/`：家庭模块（亲子功能、设备配对、算力池绘图网关、内置 MCP 工具）
- `services/Baihua.Modules.Ai/`：AI 模块（模型、聊天、配置、OpenAI 兼容端点）
- `services/Baihua.Modules.Vault/`：知识库模块（Vault、Sync、Search）
- `services/Baihua.Web/`：家庭版 Web 界面（Blazor Server，5177，仍是独立进程）
- `services/Baihua.Contracts/`：共享 DTO 与接口契约
- `services/Baihua.Core/`：共享服务层（含 VaultSettingsService、DeviceService 等；跨模块接口在 `Baihua.Core/Modules/`）
- `services/Baihua.Data/`：共享 EF Core 数据层（单库多 DbContext + `DatabaseInitializer`）
- `services/BaiHua.slnx`：服务端解决方案（包含所有 services/ 项目及 libs/MobileContract）
- `tools/bh/`：百花 CLI（Windows：`bh` = `win/native` dotnet 进程 + SQLite（唯一形态）；Linux：`linux/native/` = systemd + SQLite，`bh` 即入口）
- `libs/MobileContract/`：移动端契约（DTO、接口定义；服务端设备配对/管理适配层在用）
- `docs/`：协议与架构文档
- `scripts/`：开发、发布、部署脚本
- `tests/Baihua.Family.Tests/`：后端配对服务测试

## 访问授权

```bash
# 一键打开管理面板（自动启动服务）
bh dashboard
```

WebUI（5177）用 CLI Token Cookie 登录；管理 API（8788）默认仅允许 loopback 访问，容器/反向代理部署用 `BAIHUA_ADMIN_ALLOWED_NETS`（CIDR 列表）显式放行网段，`BAIHUA_TRUSTED_PROXY_NETS` 声明受信任代理网段；移动端走 `/mg/*` 公开端点 + HMAC 签名设备鉴权。

## 常用命令

```bash
# 一键打开管理面板（自动启动/登录 WebUI）
bh dashboard

# 或手动分别启动（合并后只有 1 个后端进程 + 1 个 WebUI）
# 终端 1：后端（家庭 / AI / 知识库 三模块同一进程）
cd services/Baihua.Server && dotnet watch run --non-interactive --no-hot-reload --urls "http://0.0.0.0:8788"
# 终端 2：WebUI
cd services/Baihua.Web && dotnet watch run --non-interactive

# 编译验证（推送前必须执行）
dotnet build services/BaiHua.slnx -c Release
```

## 端口

| 服务 | 端口 | 说明 |
|------|------|------|
| Baihua.Server | 8788 | 唯一后端 HTTP API（家庭/亲子、AI、知识库三模块合一；含 MCP `/mcp`） |
| Baihua.Web | 5177 | HTTP Blazor Server（仍是独立进程） |
| OVMS（本地推理） | 8000 | OpenAI 兼容；Windows 为 wslc 容器 `ovms-gpu`，Linux 用 compose `--profile inference` 的 `openvino` 容器或自行部署 |
| Open WebUI | 8080 | AI 对话页（独立应用，**可选**）：Windows 统一为 wslc 容器 `bh-open-webui`（数据卷 `bh-openwebui-data`，镜像固定 v0.13.3）；Linux 不在默认部署里，Web 的 `/open-webui` 是入口页（打开链接 + 部署步骤），需要时自行 docker run |
| 局域网入口 | 80 | Windows native 用 `bh lan on` 的 portproxy（:80 → 127.0.0.1:8788）/ Linux 系统 nginx（`bh proxy install`） |

**路由命名（合并后）**：单进程下 `api/AI/*` 与 `api/ai/*` 在 ASP.NET Core 大小写不敏感路由下冲突，
助手域（ask / chat / chat-stream / providers / prompt-templates / generate-missing-note / functions-call）
已改名 **`api/assistant/*`**；AI 模块的移动端聊天端点仍在 `api/ai/chat/completion`、`api/ai/chat/stream`
（花记在用，**不得改动**）。端点级回归锁见 `NoAmbiguousRoutesTests`。

## 命名约定（TaskRunner → Baihua 已全部统一）

> 项目早期名为 **TaskRunner**，现已按服务域全部统一为 **Baihua.***，**代码/配置/部署中不得再出现 TaskRunner**。
> 各层命名必须与下表一致：

| 层 | Server (8788) | Web (5177) |
|---|---|---|
| 命名空间 / 目录 | `Baihua.Server`（宿主）+ `Baihua.Modules.Family` / `Baihua.Modules.Ai` / `Baihua.Modules.Vault` | `Baihua.Web` |
| Docker compose 服务名 | `server` | `webui` |
| 容器名 | `bh-server` | `bh-webui` |
| 可执行文件 / dll | `bh-server` | `bh-webui` |
| HttpClient / 配置键 | `BaihuaServer:BaseUrl`（WebUI 侧唯一的后端配置键） | — |
| 环境变量前缀 | `BAIHUA_*` | — |
| 日志 / 指标服务名 | `Baihua.Server` | `Baihua.Web` |
| Dockerfile | `Dockerfile.server` | `Dockerfile.webui` |
| 配置目录 | `/opt/baihua/config/server`（合并前为 family/ai/vault 三份） | `/opt/baihua/config/webui` |
| 数据库（SQLite） | `$BAIHUA_HOME/db/baihua.db`（单文件；合并前为 `family`/`ai`/`vault` 三库） | — |

> 合并前的 `bh-family`/`bh-ai`/`bh-vault` 容器名已删除，不得再出现；WebUI 侧不再读取
> `FamilyApi:BaseUrl` / `AiApi:BaseUrl` / `VaultApi:BaseUrl` 三个配置键（源码里 `FamilyApi`/`AiApi`/`VaultApi`
> 仅作为 HttpClient 名字保留兼容别名，实际都指向唯一的 `BaihuaServer:BaseUrl`）。

**开发环境边界（强制约定，2026-11-22）**：只支持 **纯 Windows** 与 **纯 Linux** 两种开发环境，**不提供"在 WSL 里开发"的路径**。
- **纯 Windows**：只有 native 一种运行形态（2026-11-25 起）——`bh`（dotnet 进程 + SQLite）。
  可选容器组件（ovms-gpu / bh-open-webui）依赖 WSL 3 自带的 `wslc.exe` 作为运行时，但这属于 Windows 侧的运行实现，**不等于 WSL 开发环境**。
- **纯 Linux**：只有原生 systemd + SQLite 一种（`tools/bh/linux/native/bh.sh`，`bh` 即入口）。
- **禁止再引入/建议**：把仓库与工具链迁到 WSL 发行版里开发、在 WSL 内跑 k3s、Windows 侧 `bh-k3s` / Docker Desktop 等形态。
- 新增部署方式前先确认能落进这两格，落不进就不做。

**部署形态**（Windows：native；Linux：原生 systemd + SQLite）：
- **native（Windows 唯一形态，开发与最终用户同一套）**：`bh-server.exe`（8788）+ `bh-webui.exe`（5177）以 dotnet 进程跑在 Windows，
  不依赖容器；数据库为 SQLite（`%USERPROFILE%\.baihua\db\baihua.db`，`BAIHUA_DATA_HOME` 可覆盖），无需安装 PostgreSQL。
  `bh build`（dotnet publish 到 `out\native`）→ `bh start`（启动经计划任务 + GUI 启动器，无控制台窗口）。
  `bh backup [目录]` / `bh restore <zip> [-y]`：数据目录冷备份/恢复（排除 bin/logs/models/open-webui-venv →
  zip + .sha256；restore 把旧目录改名 `.pre-restore-<时间>` 保留，可回滚）；两者都要求先 `bh stop`（SQLite WAL 运行中打包会损坏）。
  局域网入口：`bh lan on` 设置宿主 `:80 -> 127.0.0.1:8788` portproxy（netsh）+ 防火墙。
- **最终用户部署包**：`scripts/make-deploy-package.ps1` 生成 `dist/baihua-deploy-<ver>.zip`——
  bh CLI + `apps\{server,webui}\`（self-contained win-x64 publish，最终用户**无需 .NET**）+ README.txt/VERSION.txt，
  镜像仓库结构（包根下 `tools\bh\` + `apps\`，native cell 的 `apps\` 检测自动生效）；脚本内文本统一转 CRLF，Linux CI 也能安全打包。
  Release workflow 打 tag 时自动生成部署包并附到 GitHub Release。
  打包硬规矩（2026-11-23 实机首启验证发现，首版 v0.1.0 附件包即栽在这条）：
  CRLF 归一化时 `.ps1` 必须写回 **UTF-8 with BOM**——`bh.cmd` 走 Windows PowerShell 5.1，无 BOM 按 GBK 读中文注释会吞换行、解析全崩（pwsh 7 下自测不出）；`apps\` 二进制绝不参与归一化。
- **wslc（Windows，已退役 2026-11-25）**：`bh <cmd>` 曾用 WSL 3 `wslc.exe` 跑 server/webui/nginx 容器（`docker/.env.wslc` 配置、
  `bh backup` tar 数据卷 + 恢复演练，验收手册 `docs/WSLC_BACKUP_RESTORE_TEST.md` 已随之删除）。
  `bh wslc` / `bh docker` 现在明确报错退出；遗留的 wslc 数据卷 `bh-data`/`bh-openwebui-data`/镜像仍保留，
  `bh-openwebui-data` 由 open-webui 容器继续使用。历史数据已迁入 native SQLite（`bh db-migrate`）。
- **原生 Linux（Linux 唯一形态，`tools/bh/linux/native/`）**：systemd 用户实例
  `baihua-server@<user>.service`（8788）+ `baihua-webui@<user>.service`（5177），产物 `bh build` 发布到
  `$BAIHUA_HOME/bin/{server,webui}`（默认 `~/.baihua`），数据 `$BAIHUA_HOME/db/baihua.db`（SQLite 单文件）。
  命令：`bh install` / `bh build` / `bh start|stop|restart|status|logs` / `bh doctor` / `bh backup|restore` /
  `bh proxy install`（系统 nginx :80）/ `bh db-migrate`（PostgreSQL → SQLite）/ `bh dashboard`。
  `bh install` 会把单元里的 `%h/.baihua` 渲染成实际 `BAIHUA_HOME`；日志走 journald（`bh logs server`）。
  备份 = `bh backup`（冷备份数据目录），迁移与排障详见 `docs/LINUX_NATIVE.md`。
  **k3s/k8s 形态已退役（2026-11）**：单节点上 PG 崩溃恢复被存活探针反复打断、后端永远等不到数据库；
  改为原生后没有独立数据库进程与编排层。`k8s/` 清单、`bh-k3s` 入口、k3s 备份/恢复已删除；
  旧 PostgreSQL 数据用 `bh db-migrate` 一次性迁入 SQLite（源库只读）。
- **compose 对应关系**（可选形态）：`docker/docker-compose.yml`（服务名 `server` / `webui` / `nginx` /
  `openvino`（profile `inference`）/ `openobserve`（profile `observability`）；数据库为数据卷内的 SQLite 文件，无 postgres 服务）。
  Windows 已不走容器栈（native 唯一形态），compose 仅供 Linux 自行部署参考。
- 已退役：合并前 `family`/`ai`/`vault` 三容器、`docker-ai` profile、`host.docker.internal:8791` 跨进程访问、
  Windows Docker Desktop 形态（`bh docker`）、Windows wslc 容器栈（`bh wslc`，2026-11-25）、
  Linux 的 k3s/k8s 形态与 PostgreSQL 数据库；
  `tools/bh/linux/native` 由"备选 cell"升为 Linux 唯一形态，`tools/bh/win/native` 由"开发形态"升为 Windows 唯一形态。
- **OpenVINO 推理**与后端进程分离：Windows 用 wslc 容器 `ovms-gpu`（REST :8000，`bh ovms start|status|logs` 管理；
  `scripts/install-openvino-ovms-service.ps1` 仅作回滚到 Windows 服务的备选），Linux 用 compose `--profile inference`
  的 `openvino` 容器（:8000）或自行部署；
  后端经 `OpenVinoOms__BaseUrl` 访问（默认 `http://127.0.0.1:8000`，OpenAI 兼容路径 `/v1`）。
- **AI 对话页（open-webui）**：Windows 统一为 wslc 容器 `bh-open-webui`（:8080，`bh open-webui install|start|stop|status|logs`；
  镜像固定 `v0.13.3` 防自动升级破坏 webui.db，数据卷 `bh-openwebui-data` 保留 wslc 时代聊天记录）；
  Linux 不在默认部署里，Web 的 `/open-webui` 是入口页（打开链接 + 部署步骤），需要时自行 docker run。

**OpenObserve 凭据约定**（默认口令 `Complexpass#123` 已废弃，appsettings 中不再有默认值）：
- `openobserve` 为可选观测栈（compose profile `observability`）；
  `OPENOBSERVE_PASSWORD` **必填**（缺失时 compose 直接报错、不启动该服务），由 `docker/.env` 提供。
- 历史：native cell 曾由 `bh.ps1(win/native)` 从 `$BAIHUA_HOME\openobserve-password.txt` 注入 —— 该机制已随 native cell 恢复而重新启用。

**例外（名实相符，保留原名）**：
- `TaskRunner.Cloud` — 官网版（mdyj-cloud 仓库）的真实项目名，与本仓库无关
- 历史文档/测试报告中的旧名 — 记录当时的客观状态

## 移动端兼容

移动端（鸿蒙/安卓）通过 `http://<server>/`（**默认 80 端口，无显式端口号**）发现服务器并调用 API。
Windows native 开发形态用 `bh lan on` 设置宿主 `:80 -> :8788` portproxy（netsh）作为统一入口；
Linux 原生形态用系统 nginx（`bh proxy install`）监听 :80，`/mg/*`、`/pair` 等路径转发到 `Baihua.Server`（8788），
`/api/auth/` 转发到 WebUI（5177）；wslc 形态由 nginx 容器占 :80。
配对二维码的 `baseUrl` 由 `Baihua:PublicBaseUrl` 决定（`bh proxy install` 会按宿主默认路由 IP 写入
`$BAIHUA_HOME/config/server.env`；appsettings 里**留空**，没有注入的形态会按本机 IP + 8788 自动探测；
均可用 `BAIHUA_PUBLIC_BASE_URL` 覆盖）。
合并后**不再有跨服务转发**：原 family→vault / family→ai 两个转发中间件已删除，改为 `Baihua.Server`
内的**一个设备授权中间件**（见 `services/Baihua.Server/Program.cs`）——
先做 HMAC 签名校验，再要求"必须是已配对设备"：携带 `X-Device-Id` 且 `DeviceService` 中该设备已授权
（有 `AccessToken`），否则 401；对知识库同步路径在进程内注入 `Authorization: Bearer <accessToken>`
（等价于原转发行为）。因此 **移动端代码无需任何改动**，移动端契约（路径、字段、签名）保持不变。

授权与认证：
- 局域网发现/配对阶段通过 HMAC 签名（共享 `sharedSecret`）校验设备身份——HMAC 只证明"持有共享密钥"，不代表"已配对"。
- 设备授权中间件随后校验设备已配对；知识库同步/下载路径（`/mg/manifest`、`/mg/file`、`/mg/cards`、
  `/mg/vaults`、`/api/sync/*`、`/vault/*`、`/mobile-vaults/push` 等）与 `/api/ai/chat/*` 均走此校验，
  知识库路径由中间件注入 Bearer Token，供知识库模块的 `ISyncAuthorizationStrategy` 校验（原跨进程转发附加的 `Authorization` 头由此等价替代）。

## 测试

**后端配对服务测试**（测试项目名沿用 `Baihua.Family.Tests`，覆盖 `Baihua.Server` 宿主 + 各模块）：

```bash
dotnet test tests/Baihua.Family.Tests/Baihua.Family.Tests.csproj
```

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **baihua** (11747 symbols, 26540 relationships, 500 execution flows).

> Index stale? Run `node .gitnexus/run.cjs analyze --index-only` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? Bootstrap with `npx`, `bunx`, or `pnpm dlx` — e.g. `bunx gitnexus@latest analyze` (npm 11 npx crash; #1939).

## Always Do

- **MUST run impact before editing.** Use `impact({target: "symbolName", direction: "upstream"})` or `node .gitnexus/run.cjs impact "symbolName" --direction upstream --repo .`; report callers, processes, and risk. Never substitute grep for graph analysis.
- **MUST analyze graph changes before committing.** Use `detect_changes({scope: "all"})` (MCP) or `node .gitnexus/run.cjs detect-changes --scope all --repo .` (CLI fallback). `partial: true` or `truncated: true` is not a clean check — a zero means unseen, not unaffected; re-run it. For regression review: `detect_changes({scope: "compare", base_ref: "main"})` or `node .gitnexus/run.cjs detect-changes --scope compare --base-ref "main" --repo .`.
- MUST warn on HIGH/CRITICAL `risk` pre-edit; never use `riskSharedAxes` to waive a HIGH/CRITICAL `risk` warning. Compare File/symbol: MCP File omits axes; Graph-RAG expands File.
- **MUST treat `risk: UNKNOWN` as unresolved, not as low.** An empty caller set is not evidence the symbol is unused — it can also mean the callers are not resolvable by the index (plain-object property access, dynamic dispatch, cross-language calls). `impact` pairs `UNKNOWN` with a `riskNote` saying so. Confirm with a text search before treating the symbol as safe to change or delete; do not proceed on the strength of a zero.
- **MUST use `query({search_query: "concept"})` for concepts/flows, `context({name: "symbolName"})` for a named symbol, or `impact` for blast radius, on read-only callers, dependencies, imports, or execution flow.** Graph first; text search only for empty/`UNKNOWN`/literals.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method before MCP/CLI impact analysis.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis, and never read `UNKNOWN` as an all-clear — it means the walk could not answer, which is the one verdict that requires confirming by other means.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit before MCP/CLI graph change analysis.

## Resources

| Resource | Use for |
| --- | --- |
| `gitnexus://repo/baihua/context` | Codebase overview, check index freshness |
| `gitnexus://repo/baihua/clusters` | All functional areas |
| `gitnexus://repo/baihua/processes` | All execution flows |
| `gitnexus://repo/baihua/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
| --- | --- |
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
