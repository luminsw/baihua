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

### 终端中文乱码修复（`dotnet build` 输出）

运行 `dotnet build` 时，.NET 输出的 UTF-8 中文可能被 `pwsh` 误解码为 GBK 导致乱码（如"鐢熸垚澶辫触"）。

**修复方式**（已写入 PowerShell Profile `C:\Users\lumin\Documents\PowerShell\profile.ps1`）：
```powershell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$PSDefaultParameterValues['*:Encoding'] = 'utf8'
```
配置后重启终端或重新打开 VS Code 即可生效。

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
| 百花 | `./tools/bh/linux/k8s/bh.sh`（或 `linux/native/bh.sh`） | `tools\bh\win\docker\bh.ps1`（或 `win/native/bh.ps1`） |
| 花阁 | `./hg` | `.\hg.ps1` |

> 当前架构已从单体后台拆分为 3 个独立后端服务：
> - **Baihua.Family** (8788) — 家庭/亲子功能（任务、成就、OpenClaw、设备配对）
> - **Baihua.AI** (8791) — AI 模型、聊天、配置管理
> - **Baihua.Vault** (8790) — 知识库、同步、搜索、索引
>
> 3 个服务各自使用独立的 PostgreSQL 数据库（family / vault / ai 三库，一服务一库，通过 `Baihua.Data`/`Baihua.Core` 共享数据层与实体；连接见 `Baihua.Data.DbConnections`，配置 `PG_HOST`/`PG_USER`/`PG_PASSWORD`）。

## 助手 / 自动化约定

- **执行任务时先检查当前是 Windows 还是 Linux**，再选用对应平台的命令与路径写法（PowerShell vs bash、反斜杠 vs 正斜杠、bh 的 win 与 linux 版本等），以免用错命令。
- **服务运行由用户手动按需启停**，助手不自动拉起/保持后台进程：
  - Baihua.Family **8788**、Baihua.AI **8791**、Baihua.Vault **8790**、Baihua.Web **5177**（Windows native 下 OpenVINO 由 OVMS 系统服务承载（服务名 `ovms`，REST :8000，安装见 `scripts/install-openvino-ovms-service.ps1`），`bh status` 展示状态）
  - 启停统一用 `bh start` / `bh stop`；开发调试可单独 `dotnet watch run`
  - 若某服务未监听，先询问用户是否需要启动，不要擅自拉起
- **WebUI 与后端之间的共享数据类型和 API 接口定义必须放在 `Baihua.Contracts`**，两边禁止各自重复定义。新增或修改 API 契约时，先更新 Contracts，再让两边引用同一版本。
- **共享业务服务（如 `VaultSettingsService`、`VaultNoteIndexer`）放在 `Baihua.Core`**，`Baihua.Family`、`Baihua.Vault` 和 `Baihua.AI` 均通过引用 `Baihua.Core` 使用，避免 HTTP 调用开销。

## JSON 序列化与反序列化规范（强制）

> 背景：曾因 `QRToolPanel` 用裸 `JsonSerializer.Deserialize`（默认 PascalCase + 大小写敏感）反序列化后端 camelCase 响应，导致主 AI API Key 二维码不显示；又因 `Baihua.Family` 历史遗留 `PropertyNamingPolicy=null`（PascalCase）与 AI/Vault（camelCase）不一致，导致移动端 `/mg/pair` 字段全部丢失、安卓 `AuthorizationWatcher` 授权判定失效。以下规范防止同类问题。

### 后端（三个服务统一）
- **JSON 序列化统一 camelCase**（ASP.NET Core 默认）。`AddJsonOptions` 中**禁止**设 `PropertyNamingPolicy = null`；并显式加 `PropertyNameCaseInsensitive = true`（容错入参大小写）。`Baihua.Family`/`Baihua.AI`/`Baihua.Vault` 三服务保持一致。
- SignalR `PayloadSerializerOptions`：Family 当前保留 PascalCase（内部 WebUI 消费，case-insensitive 容错）；新增 hub 应 camelCase 统一，避免新坑。
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
| _（百花内置）_ | 百花 → 任意 MCP 客户端 | 标准 MCP（streamable-http `/mcp` 端点，挂在 `Baihua.Family`）：知识库 / 家庭能力（检索/列表/读笔记/创建知识库/写笔记 + 记账/任务），DSH 经 `dsh-mcp-client` 接入（工具名带 `mcp__baihua__` 前缀） | `Baihua.Family:8788/mcp` |

**agent 可直接调用的工具**（由上述插件注册）：

- `bh_status` / `bh_logs` / `bh_op_status` — 只读运维，直接用
- `bh_start` / `bh_stop` / `bh_restart` / `bh_build` / `bh_build_restart` / `bh_update` / `bh_git_commit_push` / `bh_dsh_restart` / `bh_bootstrap` — 变更类运维/宿主机操作，**执行前先询问用户**（插件侧已挂审批门：ask 策略时在 DSH 界面确认，never 时自动拒绝）；编译/更新为长操作，用返回的 `opId` 轮询 `bh_op_status`
- `baihua_draw` / `baihua_draw_video` — 经算力池绘图网关出图/出视频（txt2img / txt2video，支持跨机）
- `local_ai_small_task` — 小而有界的文本任务（短摘要/分类/取词/起标题/简短改写）交给本机 AI，省线上 token；**长文档 / 多步推理 / 写代码用远程模型**
- `mcp__baihua__*` — 百花数据工具：只读（知识库检索/列表/读笔记、记账汇总、任务列表）+ 写（`baihua_vault_create` 建知识库、`baihua_vault_write_note` 写笔记），统一经 `Baihua.Family` 内置 `/mcp` 端点暴露（`baihua-dsh-plugin` 不再注册数据工具）

> 插件配置在 `~/.dsh/cordis.patch.yml`（token / drawGatewayUrl / drawToken / comfyModelType 等）；
> 三个自研 DSH 插件已改**本地 link 方式**安装：`~/.dsh/profiles/web/package.json` 依赖为
> `link:../../../src/<repo>`（bundle 自动挂层），各仓库 `node_modules` 为 junction →
> `~/.dsh/profiles/node_modules`（复用 DSH 全局依赖层，版本与单例一致）；
> 改插件源码后**重启 DSH 即生效**（无需 git push/重装，`npx @deepseek-ai/dsh web`）。

## 目录

- `services/Baihua.Family/`：家庭版主后台（亲子功能、设备管理）
- `services/Baihua.AI/`：AI 微服务（模型、聊天、配置）
- `services/Baihua.Vault/`：知识库微服务（Vault、Sync、Search）
- `services/Baihua.Web/`：家庭版 Web 界面（Blazor Server）
- `services/Baihua.Contracts/`：共享 DTO 与接口契约
- `services/Baihua.Core/`：共享服务层（含 VaultSettingsService、DeviceService 等）
- `services/Baihua.Data/`：共享 EF Core 数据层
- `services/BaiHua.slnx`：服务端解决方案（包含所有 services/ 项目及 libs/MobileContract）
- `tools/bh/`：极简 CLI 工具（Linux: k8s/native，Windows: docker/native，均统一命令名 `bh`）
- `libs/BaihuaSdk/`：跨平台移动端 SDK（net9.0;net10.0，零 MAUI 依赖，主要 target net10.0）
- `libs/MobileContract/`：移动端契约（DTO、接口定义）
- `clients/Huapu/`：花圃（BaiHua.Nursery）— 移动端技术实验与验证工具（非正式发布 App，详见下方说明）
- `clients/Huapu.slnx`：花圃解决方案（包含 BaihuaSdk + MobileContract + Huapu）
- `docs/`：协议与架构文档
- `scripts/`：开发、发布、部署脚本
- `tests/Baihua.Family.Tests/`：后端配对服务测试
- `tests/Baihua.Sdk.Tests/`：SDK 单元测试与集成测试
- `tests/Huapu.Tests/`：MAUI DI 回归测试

## 访问授权

```bash
# 一键打开管理面板（自动启动服务）
./bh dashboard
```

WebUI（5177）用 CLI Token Cookie 登录；管理 API（8788/8791/8790）默认仅允许 loopback 访问，容器/反向代理部署用 `BAIHUA_ADMIN_ALLOWED_NETS`（CIDR 列表）显式放行网段，`BAIHUA_TRUSTED_PROXY_NETS` 声明受信任代理网段；移动端走 `/mg/*` 公开端点 + HMAC 签名设备鉴权。

## 常用命令

```bash
# 开发模式（Linux/macOS，一键启动全部 3 个后台 + WebUI）
cd services && ./bh dashboard

# 或手动分别启动
# 终端 1
cd services/Baihua.AI && dotnet watch run --non-interactive --no-hot-reload --urls "http://0.0.0.0:8791"
# 终端 2
cd services/Baihua.Vault && dotnet watch run --non-interactive --no-hot-reload --urls "http://0.0.0.0:8790"
# 终端 3
cd services/Baihua.Family && dotnet watch run --non-interactive --no-hot-reload
# 终端 4
cd services/Baihua.Web && dotnet watch run --non-interactive

# 编译验证（推送前必须执行）
dotnet build services/BaiHua.slnx -c Release
```

## 端口

| 服务 | 端口 | 说明 |
|------|------|------|
| Baihua.Family | 8788 | HTTP API（家庭/亲子功能、设备管理） |
| Baihua.AI | 8791 | HTTP API（AI 模型与配置） |
| Baihua.Vault | 8790 | HTTP API（知识库、同步、搜索） |
| Baihua.Web | 5177 | HTTP Blazor Server |

## 命名约定（TaskRunner → Baihua 已全部统一）

> 项目早期名为 **TaskRunner**，现已按服务域全部统一为 **Baihua.***，**代码/配置/部署中不得再出现 TaskRunner**。
> 各层命名必须与下表一致：

| 层 | 主服务 (8788) | AI (8791) | Vault (8790) | Web (5177) |
|---|---|---|---|---|
| 命名空间 / 目录 | `Baihua.Family` | `Baihua.AI` | `Baihua.Vault` | `Baihua.Web` |
| Docker compose 服务名 | `family` | `ai` | `vault` | `webui` |
| 容器名 | `bh-family` | `bh-ai` | `bh-vault` | `bh-webui` |
| 可执行文件 / dll | `bh-family` | `bh-ai` | `bh-vault` | `bh-webui` |
| HttpClient / 配置键 | `FamilyApi` | `AiApi` | `VaultApi` | — |
| 环境变量前缀 | `BAIHUA_*` | `BAIHUA_*` | `BAIHUA_*` | — |
| 日志 / 指标服务名 | `Baihua.Family` | `Baihua.AI` | `Baihua.Vault` | `Baihua.Web` |
| Dockerfile | `Dockerfile.family` | `Dockerfile.ai` | `Dockerfile.vault` | `Dockerfile.webui` |
| 配置目录 | `/opt/baihua/config/family` | `/opt/baihua/config/ai` | `/opt/baihua/config/vault` | `/opt/baihua/config/webui` |
| 数据库（PostgreSQL） | `family` 库 | `ai` 库 | `vault` 库 | — |

**部署形态**：
- **Windows**（`tools/bh/win/docker/bh.ps1`）：`ai` 服务 **native 运行**（Windows 进程，直接访问 Arc GPU 做 OpenVINO 推理），`family`/`vault`/`webui`/`nginx` 走 docker compose；compose 里 `ai` 带 `profiles: ["docker-ai"]`（默认不启动容器），容器通过 `host.docker.internal:8791` 访问 native ai
- **Linux**（`deploy-docker.sh`）：全部容器化，`docker compose --profile docker-ai up -d` 启动含 ai

**OpenObserve 凭据约定**（默认口令 `Complexpass#123` 已废弃，appsettings 中不再有默认值）：
- native 部署：`bh.ps1(win/native)` 启动时从 `$BAIHUA_HOME\openobserve-password.txt` 注入 `OpenObserve__Password`（文件缺失则该配置为空）
- compose 部署：`OPENOBSERVE_PASSWORD` 环境变量必填（`bh.ps1(win/docker)` 与 `deploy-docker.sh` 会自动生成并写入 `docker/.env`）

**例外（名实相符，保留原名）**：
- `TaskRunner.Cloud` — 官网版（mdyj-cloud 仓库）的真实项目名，与本仓库无关
- 历史文档/测试报告中的旧名 — 记录当时的客观状态

## 移动端兼容

移动端（鸿蒙/安卓）通过 `http://<server>/`（**默认 80 端口，无显式端口号**）发现服务器并调用 API。
k8s 部署下 **Traefik**（IngressRoute，svclb 绑定宿主 :80）作为统一入口，`/mg/*`、`/pair` 等路径由
Traefik 转发到 `Baihua.Family`（8788）；配对二维码的 `baseUrl` 由 `Baihua:PublicBaseUrl` 决定
（k8s 已注入 `http://<节点IP>`，无端口）。
`Baihua.Family` 在 8788 上保留了**转发中间件**，将移动端调用的 Vault 域 API 路径（如 `/mg/manifest`、`/mg/file`、`/mg/cards`、`/mg/vaults` 等）透明转发到 `Baihua.Vault`（8790）。因此 **移动端代码无需任何改动**。

授权与认证：
- 局域网发现/配对阶段通过 HMAC 签名（共享 `sharedSecret`）校验设备身份。
- 转发到 `Baihua.Vault` 时，`Baihua.Family` 会为已授权设备自动附加 `Authorization: Bearer <accessToken>`，Vault 侧校验 Bearer Token 或本机回环请求。

## BaihuaSdk（跨平台移动端 SDK）

**位置**: `libs/BaihuaSdk/` — 纯 C# `net9.0;net10.0` 类库，零 MAUI 依赖。

封装了与百花服务器通信的全部协议层：

| 模块 | 说明 |
|------|------|
| `Signing/` | HMAC-SHA256 请求签名（与 Kotlin `RequestSigner.kt` 算法一致） |
| `Transport/` | HttpClient 封装、签名注入、HTTPS→HTTP 降级、错误中文映射 |
| `Services/SyncServiceImpl.cs` | 知识库同步（manifest → 文件下载 → 本地写入） |
| `Services/PairingServiceImpl.cs` | QR 码解析、多地址格式、设备注册 |
| `Services/LogServiceImpl.cs` | 批量缓冲日志上报 |
| `Services/QuotaServiceImpl.cs` | 配额/购买 API |
| `Push/PushWebSocketService.cs` | WebSocket 实时推送 + HTTP 轮询降级 |
| `Storage/` | ISecureStore / IServerConfigStore 接口（平台层实现） |

```bash
# 运行 SDK 单元测试
dotnet test tests/Baihua.Sdk.Tests/

# 运行集成测试（需要百花服务器）
export BaiHua_TEST_URL=http://192.168.3.x:8788
export BaiHua_TEST_SECRET=<shared-secret>
export BaiHua_TEST_VAULT_ID=<vault-id>
dotnet test tests/Baihua.Sdk.Tests/ --filter Integration
```

## 花圃 / BaiHua.Nursery（移动端技术实验与验证工具）

**位置**: `clients/Huapu/` — .NET MAUI Blazor Hybrid App。
**解决方案**: `clients/Huapu.slnx`（包含 BaihuaSdk + MobileContract + Huapu）

> **定位说明**：花圃（BaiHua.Nursery）是百花服务对移动端支持的**技术验证工具**，用于验证 BaihuaSdk 协议、配对流程、同步功能等在真实移动设备上的表现。它**不是正式发布的 App**，不具备产品级功能完整性。花记的正式移动端是鸿蒙端（ArkUI）和安卓端（Jetpack Compose），它们功能远超花圃。
>
> 花圃的职责边界：
> - ✅ 验证百花服务端 API 对移动端的兼容性
> - ✅ 验证 BaihuaSdk 的配对/同步/签名协议
> - ✅ 作为 .NET MAUI 技术实验平台
> - ✅ 对鸿蒙/安卓端花记的功能创意起到互相启发的作用
> - ❌ 不承担正式移动客户端角色
> - ❌ 不与鸿蒙/安卓端功能完全对齐
>
> **与花记的关系**：花圃参考鸿蒙/安卓花记的 UI/UX 设计（底部 Tab 导航、暗色模式、品牌色等），但功能范围远小于花记。花圃可作为新功能的快速验证平台，验证通过后再移植到鸿蒙/安卓端。

**UI 设计参考**（对齐鸿蒙/安卓花记）：
- 底部 3 Tab 导航：首页 / 获取知识 / 我的
- 品牌色：红色 `#FF2442`（与鸿蒙花记一致）
- 完整暗色模式支持（CSS 变量 + `data-theme` 属性）
- 语义化颜色系统（19 个 CSS 变量，亮/暗双主题）

**页面结构**：
| 页面 | 路由 | 说明 |
|------|------|------|
| 首页 | `/` | 快捷入口、已配对服务器、搜索入口 |
| 获取知识 | `/knowledge` | 百花同步 + 配对（子 Tab 切换） |
| 我的 | `/profile` | 设备信息、数据概览、功能菜单 |
| 搜索 | `/search` | 全文搜索已同步知识库 |
| 配对 | `/pairing` | 扫码/手动配对服务器 |
| 同步 | `/sync` | 知识库获取（独立页面入口） |
| 已获取 | `/vaults` | 文件浏览器、Markdown 预览 |
| 设置 | `/settings` | 暗色模式切换、数据管理、关于 |

**组件拆分**：
- `SyncContent.razor` / `PairingContent.razor`：可复用内容组件，供 KnowledgePage 和独立页面共用

**功能边界说明**：
- 花圃包含完整的**拜师（Master）功能**（约 2600 行：MasterService + 4 页 + 模型/缓存），功能远超"验证工具"典型范围——这是有意保留的完整产品功能（与 Web 端拜师体验对齐），非技术验证范畴；新增移动端功能时不必与花圃完全对齐，但拜师功能是例外（已正式纳入，不要裁剪）。
- 花圃不承担正式移动客户端角色，不与鸿蒙/安卓端功能完全对齐（拜师除外）。

- **Android**: `dotnet build clients/Huapu.slnx -f net9.0-android -c Release` → APK 在 `clients/Huapu/bin/Release/net9.0-android/com.lumin.BaiHua-Signed.apk`
- **iOS**: 需要 macOS + Xcode（GitHub Actions CI 已配置 `.github/workflows/ci.yml`）

```bash
# Android Release 编译
dotnet build clients/Huapu.slnx -f net9.0-android -c Release

# 安装到手机
adb install clients/Huapu/bin/Release/net9.0-android/com.lumin.BaiHua-Signed.apk```
```

### 花圃 Honor/部分 Android 设备 .NET 10 兼容性

**已知问题**: 2026-06 期间，Honor 真机（`ADNQUT5813009383`）安装 .NET 10 Preview APK 后启动崩溃：
```
java.lang.IllegalArgumentException: No view found for id 0x7f0800ff
for fragment NavigationRootManager_ElementBasedFragment
```
这是 MAUI 10 Preview 在部分 Android 设备上的已知框架问题（[dotnet/maui#32029](https://github.com/dotnet/maui/issues/32029)）。

**当前状态（2026-06-27）**:
- 为规避 Honor 设备兼容性问题，Android 目标框架已回退至 **.NET 9 LTS**（`net9.0-android`）
- MAUI workload `9.0.x`，`ZXing.Net.Maui.Controls` 降级至 `0.6.0`
- Debug + Release 构建成功（0 错误 0 警告）
- 单元测试全部通过（155 + 9）
- **✅ 真机验证通过**: Honor `ADNQUT5813009383` 安装 .NET 9 APK 后启动正常，MainActivity 可见，无崩溃

**csproj 关键防御配置**（已启用）:
```xml
<AndroidEnableFastDeployment>false</AndroidEnableFastDeployment>
<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>
<AndroidStoreUncompressedFileExtensions>.so;.dll</AndroidStoreUncompressedFileExtensions>
<AndroidEnableCompressionInNativeLibraries>false</AndroidEnableCompressionInNativeLibraries>
```

**后续若需升级 .NET 10**: 需先在 Honor/相关设备上重新验证 MAUI 10 Fragment 兼容性，确认无崩溃后再将 `TargetFrameworks` 改回 `net10.0-android`。

### 花圃 Debug TLS 证书宽松

Debug 构建跳过 TLS 证书验证（方便本地自签名证书开发），Release 构建严格校验证书。见 `MauiProgram.cs` 中的 `#if DEBUG` 条件判断。


## 测试

### BaihuaSdk 测试

**单元测试**（无需服务器，覆盖核心算法和逻辑）：

```bash
dotnet test tests/BaihuaSdk.Tests/BaihuaSdk.Tests.csproj --filter Unit
```

覆盖模块：
- `Signing/RequestSigner`：签名算法、密钥管理、SHA256/HMAC 验证
- `Transport/HttpTransport`：URL 规范化、错误提取、HTTP 状态码映射
- `Services/SyncServiceImpl`：文件类型判断、路径安全验证
- `Services/PairingServiceImpl`：QR 码解析（新旧格式）、服务器地址提取

**集成测试**（需要运行中的百花服务器）：

```bash
export BaiHua_TEST_URL=http://192.168.x.x:8788
export BaiHua_TEST_SECRET=<shared-secret>
export BaiHua_TEST_VAULT_ID=<vault-id>
dotnet test tests/BaihuaSdk.Tests/BaihuaSdk.Tests.csproj --filter Integration
```

测试完整流程：配对 → 获取知识库列表 → 获取 manifest → 同步文件

### 花圃测试

**DI 回归测试**（确保所有服务可正确构造）：

```bash
dotnet test tests/MobileApp.Maui.Tests/MobileApp.Maui.Tests.csproj
```

### Baihua.Family 测试

**后端配对服务测试**：

```bash
dotnet test tests/Baihua.Family.Tests/Baihua.Family.Tests.csproj
```

## 已知限制

- **华为/荣耀手机**: .NET 10 Preview 存在 `NavigationRootManager_ElementBasedFragment` 崩溃。当前 Android 目标框架已回退至 .NET 9 LTS 规避该问题，待 MAUI 10 兼容性验证通过后再考虑升级。详见上方「花圃 → Honor 兼容性」。
- **Android 模拟器**: 需要 KVM 硬件加速（`sudo modprobe kvm_intel`，BIOS 中启用 VT-x）。

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **baihua** (11125 symbols, 23882 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "main"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({search_query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method without first running `impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit changes without running `detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/baihua/context` | Codebase overview, check index freshness |
| `gitnexus://repo/baihua/clusters` | All functional areas |
| `gitnexus://repo/baihua/processes` | All execution flows |
| `gitnexus://repo/baihua/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
