# 配置与存储架构

本文档汇总系统所有配置项的存储位置、读写方及设计依据。

## 目录结构

```
$BAIHUA_HOME/                    # 百花数据根目录（默认 ~/.baihua）
├── bin/                         # bh build 发布的 server/ webui 产物
├── db/                          # 数据库与本地文件
│   ├── baihua.db                # 唯一数据库（SQLite 单文件，WAL）
│   ├── .baihua-key              # AES-256 加密密钥文件（自动生成）
│   ├── prompt-templates.json    # 提示词模板
│   └── vault-index-snapshots.json
├── config/                      # systemd EnvironmentFile（server.env / webui.env）
├── vaults/                      # 知识库文件（允许软链到已有目录）
│   └── local/{行业}/{知识库名}/
├── webui.settings.json          # WebUI 后端 URL 配置
└── logs/                        # 运行日志（journald 之外的第二份）
```

**数据库**：**整个百花只有一个 SQLite 文件** —— 默认 `$BAIHUA_HOME/db/baihua.db`（`BAIHUA_DATABASE_PATH` 可覆盖）；
各模块保留自己的 `DbContext`（`FamilyDbContext` / `VaultDbContext` / `AIDbContext`），
表结构由 `Baihua.Data.DatabaseInitializer` 按上下文统一建表/补表。
连接串统一由 `Baihua.Data.DbConnections.Baihua` 生成（`DbConnections.Build(path)` 供工具/脚本指向别的文件），
启动时确保 `journal_mode=WAL`；`busy_timeout=15000` 与 `synchronous=NORMAL` 由连接拦截器
（`SqlitePragmasInterceptor`）在每个连接上设置；外键由连接串 `ForeignKeys=true` 打开。

（历史：曾用三个 SQLite 文件 → 2026-08 迁移为 `family` / `vault` / `ai` 三个 PostgreSQL 库 → commit `aa053f1`
合并为单一 `baihua` 库 → 2026-11 放弃 PostgreSQL 与 k8s，改为 SQLite 单文件（Linux 用原生 systemd 部署）。
旧 PostgreSQL 数据用 `bh db-migrate` 一次性迁移：只读源库、逐表摘要校验、支持 `--rewrite-prefix` 重写路径。
详见 [LINUX_NATIVE.md](LINUX_NATIVE.md)。)

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

### 数据库（SQLite，单文件）

| 变量 | 用途 | 默认值 |
|------|------|--------|
| `BAIHUA_DATABASE_PATH` | 数据库文件路径（**整个百花只有一个库**） | `$BAIHUA_HOME/db/baihua.db` |

> PostgreSQL 相关变量（`PG_HOST` / `PG_USER` / `PG_PASSWORD` / `PG_DATABASE`）已不再被服务端读取；
> 仅迁移工具 `tools/db-migrate` 用它们连接旧库。

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
| SQLite `baihua.db` · `FamilyDbContext` 实体 | 家庭任务、成就、设备授权、Onboarding 状态、记账、病历本等 | `Baihua.Modules.Family` |
| SQLite `baihua.db` · `VaultDbContext` 实体 | 知识库配置、同步状态、搜索索引 | `Baihua.Modules.Vault` |
| SQLite `baihua.db` · `AIDbContext` 实体 | AI Provider 配置（加密 API Key）、Embedding 配置、模型列表 | `Baihua.Modules.Ai` |
| `.baihua-key` | AES-256 加密密钥 | AiConfigService |
| `webui.settings.json` | WebUI 后端 URL 配置（唯一后端键 `BaihuaServer:BaseUrl`） | WebUI |
| `user_preferences.json` | 用户偏好（字体、主题） | WebUI |
| `appsettings.json` | 服务默认配置（端口、超时等） | `Baihua.Server` / `Baihua.Web` |
| 环境变量 | 部署级覆盖配置 | 两个进程 |

> 三个模块**共用同一个 SQLite 文件**，按"实体归属哪个 DbContext"划分边界（不再是物理分库）。
> 旧 PostgreSQL 库可保留在磁盘上作回滚，但新写入只进 `baihua.db`。

## 服务端口

| 服务 | 端口 | 说明 |
|------|------|------|
| Baihua.Server | 8788 | 唯一后端：家庭/亲子/设备管理 + AI + 知识库 API（含算力池绘图网关、MCP `/mcp`） |
| Baihua.Web | 5177 | Blazor Server 管理面板（独立进程） |
| OpenVINO Model Server | 8000 | 本地 OpenVINO 推理（OVMS，OpenAI 兼容） |

> 合并前的 `Baihua.AI`(8791) / `Baihua.Vault`(8790) 两个端口已随服务合并消失。

## 备份与恢复

### 数据库（SQLite 单文件）

```bash
# Linux 原生形态：冷备份整个数据目录（db / config / vaults / logs；排除可重建的 bin）
bh stop
bh backup ~/backup        # 产物 ~/backup/baihua-backup-<时间>.tar.gz
bh start

# 只要数据库文件时（服务已停止，WAL 已合并）
cp ~/.baihua/db/baihua.db ~/baihua-$(date +%Y%m%d).db
# 注意：.baihua-key 必须一起备份，否则库里的加密 API Key 解不开
```

- 运行中不要 `cp` 数据库文件（WAL 可能不一致）；在线备份用 `sqlite3 baihua.db ".backup out.db"`。
- 从旧 PostgreSQL 升级：`bh db-migrate`（只读源库，详见 [LINUX_NATIVE.md](LINUX_NATIVE.md)）。

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
