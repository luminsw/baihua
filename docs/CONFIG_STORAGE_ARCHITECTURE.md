# 配置与存储架构

本文档汇总系统所有配置项的存储位置、读写方及设计依据。

## 目录结构

```
$BAIHUA_HOME/                    # 百花数据根目录
├── db/                          # 数据（密钥、WebUI 配置等本地文件）
│   ├── .baihua-key              # AES-256 加密密钥文件（自动生成）
│   ├── webui.settings.json      # WebUI 后端 URL 配置
│   └── user_preferences.json    # 用户偏好（字体、主题）
├── vaults/                      # 知识库文件
│   └── local/{行业}/{知识库名}/
└── logs/                        # 运行日志
```

**数据库**：**整个百花只有一个 PostgreSQL 库** —— 默认库名 `baihua`（`PG_DATABASE` 可覆盖），
一个 `public` schema；各模块保留自己的 `DbContext`（`FamilyDbContext` / `VaultDbContext` / `AIDbContext`），
表结构由 `Baihua.Data.DatabaseInitializer` 按上下文统一建表（不再 `EnsureCreated`）。
连接串统一由 `Baihua.Data.DbConnections.Baihua` 生成（`Baihua.Data.DbConnections.Build(name)` 仅供工具/脚本按库名拼串），
读取 `PG_HOST` / `PG_USER` / `PG_PASSWORD`（+ `PG_DATABASE`）。
k8s 部署由 configmap + Secret 注入；本地开发默认 `localhost` 上的 `baihua` 库。

（历史：曾用三个 SQLite 文件 → 2026-08 迁移为 `family` / `vault` / `ai` 三库（一服务一库）→ commit `aa053f1`
合并为单一 `baihua` 库。旧三库合并脚本：`scripts/migrate-to-single-db.ps1`（只读源库、不改旧库，
旧库保留在磁盘上可回滚；已跑通 39 张表）。）

**跨盘映射**：通过 OS 级 symlink/junction 实现，代码无感。
```powershell
cmd /c mklink /J "C:\Users\lumin\.baihua" "D:\BaihuaData"
```

## 环境变量

### 核心

| 变量 | 用途 | 默认值 |
|------|------|--------|
| `BAIHUA_HOME` | 数据根目录（db + vaults + logs） | `%USERPROFILE%\.baihua` (Win) / `~/.baihua` (Linux) |
| `BAIHUA_ENCRYPTION_KEY` | 手动指定 API Key 加密密钥（优先级高于 .baihua-key 文件） | 空（自动生成 .baihua-key） |
| `ASPNETCORE_URLS` | 服务监听地址 | `http://0.0.0.0:8788`（后端 `Baihua.Server`）；WebUI `http://0.0.0.0:5177` |
| `ASPNETCORE_ENVIRONMENT` | 运行环境 | `Production` |

> 合并前需要为 ai(8791) / vault(8790) / family(8788) 三个进程分别设置监听地址；现在只有 `Baihua.Server`(8788)
> 与 `Baihua.Web`(5177) 两个进程。

### 数据库（PostgreSQL，单库）

| 变量 | 用途 | 默认值 |
|------|------|--------|
| `PG_DATABASE` | 数据库名（**整个百花只有一个库**） | `baihua` |
| `PG_HOST` | PostgreSQL 主机 | `localhost` |
| `PG_USER` | 用户名 | `baihua` |
| `PG_PASSWORD` | 口令 | `Baihua2026Pg!`（生产请覆盖） |

### AI 请求参数

| 变量 | 用途 | 默认值 (appsettings.json) |
|------|------|--------------------------|
| `TASK_RUNNER_AI_REQUEST_TIMEOUT_MINUTES` | AI 请求超时（分钟） | `5` |
| `TASK_RUNNER_AI_REQUEST_MAX_ATTEMPTS` | AI 请求最大重试次数 | `3` |
| `TASK_RUNNER_AI_REQUEST_INITIAL_BACKOFF_MS` | 重试初始退避（毫秒） | `1000` |
| `TASK_RUNNER_AI_REQUEST_MAX_BACKOFF_MS` | 重试最大退避（毫秒） | `30000` |

> 历史遗留变量（**合并后语义已变，新部署不要设置**）：`BAIHUA_AI_URL` / `TASK_RUNNER_AI_API_URL`
> 是合并前指向独立 AI 服务(8791) 的 OpenAI 兼容 shim 地址，代码里仍有少量兼容读取点
> （如 `AiSettingsService.AiShimUrl`、算力池探测），其字面默认值仍是 `http://127.0.0.1:8791`——**该端口已不存在**。
> 合并后 AI 与家庭同进程，shim 就在同一个 8788 上，正常部署无需设置这些变量。

### 辅助

| 变量 | 用途 | 默认值 |
|------|------|--------|
| `BAIHUA_VAULT_URL` | 历史遗留：合并前指向独立 Vault 服务(8790) 的地址 | 代码字面默认 `http://127.0.0.1:8790`（**该端口已不存在**，新部署不要设置） |
| `BAIHUA_EMBEDDING_URL` | Embedding 服务地址 | 空（从 DB 配置读取） |
| `BAIHUA_EMBEDDING_MODEL` | Embedding 模型名 | 空（从 DB 配置读取） |
| `BAIHUA_LOCAL_MODEL_DIR` | 本地模型下载目录 | 空（使用 LocalAI 配置） |
| `WEBUI_CONFIG_DIR` | WebUI 配置文件目录 | `BAIHUA_HOME/db` |
| `USE_AVAHI` | Linux 下强制使用 Avahi mDNS | 空（自动检测） |
| `DOTNET_RUNNING_IN_CONTAINER` | Docker 环境检测（自动设置） | 空 |
| `OTEL_DEPLOYMENT_ENVIRONMENT` | OpenTelemetry 部署环境标识 | 空 |

## 优先级规则

```
环境变量 > 数据库/JSON 文件 > appsettings.json > 硬编码默认值
```

## 密钥与加密

### .baihua-key 密钥文件

| 属性 | 说明 |
|------|------|
| 位置 | `$BAIHUA_HOME/db/.baihua-key` |
| 内容 | 64 字符十六进制（256-bit AES 密钥） |
| 生成 | 首次启动自动生成，随机不可预测 |
| 权限 | Linux/macOS: 600（仅所有者读写）；Windows: 隐藏属性 |

### 加密/解密流程

```
API Key 明文
    ↓ 读 .baihua-key → SHA256 → HMAC-SHA256 派生 AES Key
    ↓ AES-256-GCM（随机 Nonce + 认证 Tag）
    ↓ Base64 + "A:" 前缀
存入 AiProviderSettings.EncryptedApiKey
```

### 密钥来源优先级

```
.baihua-key 文件  >  BAIHUA_ENCRYPTION_KEY 环境变量  >  机器指纹(OS级 MachineGuid)
```

**机器指纹**（仅在密钥文件丢失时兜底）：
- Windows: `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`
- Linux: `/etc/machine-id`
- macOS: `ioreg IOPlatformUUID`

## 存储机制一览

| 存储位置 | 数据内容 | 读写方 |
|----------|----------|--------|
| PostgreSQL `baihua` 库 · `FamilyDbContext` 实体 | 家庭任务、成就、设备授权、Onboarding 状态、记账、病历本等 | `Baihua.Modules.Family` |
| PostgreSQL `baihua` 库 · `VaultDbContext` 实体 | 知识库配置、同步状态、搜索索引 | `Baihua.Modules.Vault` |
| PostgreSQL `baihua` 库 · `AIDbContext` 实体 | AI Provider 配置（加密 API Key）、Embedding 配置、模型列表 | `Baihua.Modules.Ai` |
| `.baihua-key` | AES-256 加密密钥 | AiConfigService |
| `webui.settings.json` | WebUI 后端 URL 配置（唯一后端键 `BaihuaServer:BaseUrl`） | WebUI |
| `user_preferences.json` | 用户偏好（字体、主题） | WebUI |
| `appsettings.json` | 服务默认配置（端口、超时等） | `Baihua.Server` / `Baihua.Web` |
| 环境变量 | 部署级覆盖配置 | 两个进程 |

> 三个模块**共用同一个库、同一个 `public` schema**，按"实体归属哪个 DbContext"划分边界（不再是物理分库）。
> 合并前的 `family` / `vault` / `ai` 三库可保留在磁盘上作回滚，但新写入只进 `baihua` 库。

## 服务端口

| 服务 | 端口 | 说明 |
|------|------|------|
| Baihua.Server | 8788 | 唯一后端：家庭/亲子/设备管理 + AI + 知识库 API（含算力池绘图网关、MCP `/mcp`） |
| Baihua.Web | 5177 | Blazor Server 管理面板（独立进程） |
| OpenVINO Model Server | 8000 | 本地 OpenVINO 推理（OVMS，OpenAI 兼容） |

> 合并前的 `Baihua.AI`(8791) / `Baihua.Vault`(8790) 两个端口已随服务合并消失。

## 备份与恢复

### 数据库（PostgreSQL 单库）

整个百花只有一个库（默认 `baihua`），备份/恢复只需处理一个库：

```bash
# 备份（自定义格式，含 schema + 数据）
pg_dump -h "$PG_HOST" -U "$PG_USER" -d "${PG_DATABASE:-baihua}" -F c \
        -f "baihua_$(date +%Y%m%d_%H%M%S).dump"

# 恢复（覆盖目标库现有对象）
pg_restore -h "$PG_HOST" -U "$PG_USER" -d "${PG_DATABASE:-baihua}" --clean --if-exists <dump 文件>
```

- 从旧的**三库**部署升级：先执行 `pwsh scripts/migrate-to-single-db.ps1`
  （**只读**源库 `family` / `vault` / `ai`，新建并导入 `baihua`；旧库原样保留在磁盘上可回滚。
  目标库已存在时脚本默认拒绝执行，`-Force` 才先删后建）。
- 合并前需要为 `family` / `vault` / `ai` 各做一次备份与恢复；现在只需一个库。

### 备份格式（应用级 zip 备份）

```
baihua_backup_yyyyMMdd_HHmmss.zip
├── manifest.json          # 元数据
├── db/                    # 数据库 JSON 导出
├── config/                # WebUI 配置文件
└── vaults/                # 知识库文件
```

### API Key 安全

| 场景 | 处理方式 |
|------|----------|
| 有备份密码 | API Key 解密后用备份密码 AES-256-CBC 重加密 |
| 无备份密码 | API Key 明文导出（仅本地可信环境） |
| 恢复时 | 备份密码解密 → 用目标机器 .baihua-key 重加密 |

### 不恢复的数据

- `ServerInstanceId`：本机唯一标识，保留本机的
- 授权设备标记为 `PendingReauth`，需重新确认
- 内存会话令牌：临时数据，不备份
