# 百花（Baihua）

家庭智能服务 + 本地 AI 算力池，面向本机/局域网/家庭使用。

- **百花 = 能力提供方**：本地推理、算力池、知识库、家庭数据
- **DSH = 编排与交互面**：百花 Web 内嵌 DSH Web UI，运维/绘图/本地小任务经插件桥接给 agent

## 功能

| 模块 | 内容 |
|---|---|
| 家庭/亲子 | 任务、成就、拜师、家庭病历本/AI 诊断、打卡、Anki 卡片、题库学习 |
| AI | 多 Provider（DeepSeek/Anthropic…）、本地推理（OVMS/llama.cpp/Ollama/LM Studio）、模型测速与推荐 |
| 知识库 | 多库管理、增量同步、全文/语义搜索、索引 |
| 算力池 | 局域网多机共享模型与绘图能力（ComfyUI 文生图/视频），跨机测速与布署 |
| 移动端 | 鸿蒙（arkts）/ 安卓（kotlin）花记客户端 |
| DSH 集成 | 桥接/运维/绘图插件、本地 AI 插件、内置 MCP `/mcp` |

## 结构

```
services/
  Baihua.Server/         唯一后端宿主（8788）
  Baihua.Modules.Family/ 家庭/亲子、设备配对、算力池绘图网关、MCP 工具
  Baihua.Modules.Ai/     AI：模型、聊天、配置、OpenAI 兼容端点
  Baihua.Modules.Vault/  知识库：同步、搜索、索引
  Baihua.Web/            Web 界面（5177，Blazor Server）
  Baihua.Contracts/ Baihua.Core/ Baihua.Data/   共享契约 / 服务层 / EF Core
libs/                MobileContract（移动端契约，服务端适配层在用）
docker/             容器配方（可选；Linux 主推原生 systemd）
tools/bh/            百花 CLI（见 tools/bh/README.md）
scripts/ tests/      脚本 / 测试
```

数据库：单一 SQLite 文件（默认 `$BAIHUA_HOME/db/baihua.db`，`BAIHUA_DATABASE_PATH` 可覆盖），
各模块各自 `DbContext`，建表由 `Baihua.Data.DatabaseInitializer` 统一完成。
从旧 PostgreSQL 迁移用 `bh db-migrate`；存储细节见 [docs/LINUX_NATIVE.md](docs/LINUX_NATIVE.md) 与
[docs/CONFIG_STORAGE_ARCHITECTURE.md](docs/CONFIG_STORAGE_ARCHITECTURE.md)。

## 启动（Windows）

两种互斥形态，都占 8788/5177，同一时间只跑一种：

```powershell
bh doctor                   # 体检：wslc/镜像/卷/容器/端口冲突/OVMS/AI 对话页
bh build; bh start          # 默认：wslc 容器（WSL3 自带 wslc.exe，无需 Docker Desktop / .NET SDK / 本机 PG）
bh dev build; bh dev start  # 开发：dotnet 进程（需 .NET SDK 10），改代码秒级
bh autostart on             # 登录自启（用户级 Startup，不需要管理员）
bh dashboard                # 打开 WebUI（自动登录）
```

Linux：原生 systemd + SQLite（无 k8s / PostgreSQL）：

```bash
bh install          # 装 CLI + systemd 单元
bh build            # 发布到 ~/.baihua/bin
bh start            # 启动 server → webui
bh status / bh doctor / bh logs server 100
```

详见 [docs/LINUX_NATIVE.md](docs/LINUX_NATIVE.md)；命令与排障见 `tools/bh/README.md`。

## 端口

| 端口 | 服务 |
|---|---|
| 8788 | 后端（家庭 / AI / 知识库 + 算力池网关 + MCP `/mcp`） |
| 5177 | Web 界面（Blazor Server） |
| 8000 | OVMS 本地推理（OpenAI 兼容 `/v1`） |
| 8080 | Open WebUI（AI 对话页后端，独立应用） |
| 80 | 局域网统一入口（Windows wslc 的 nginx 容器 / Linux 系统 nginx） |

## 访问授权

- WebUI（5177）：CLI Token Cookie，`bh dashboard` 自动授权
- 管理 API（8788）：默认仅本机；局域网设备走 `/mg/*` + HMAC 设备鉴权
- 容器/反代：非 loopback 访问需放行 `BAIHUA_ADMIN_ALLOWED_NETS`、`BAIHUA_TRUSTED_PROXY_NETS`

## 本地推理（OVMS）

Intel OVMS 承载本地推理，OpenAI 兼容（`/v1/chat/completions`、`/v1/embeddings`）。

- Windows：`bh ovms status|start|stop|restart|logs`（容器 `ovms-gpu`，见 `%BAIHUA_HOME%\ovms\README.md`）
- Linux：用 compose `--profile inference` 的 `openvino` 容器（或自行部署 OVMS，:8000）

## 算力池绘图

本地 ComfyUI 接入算力池，支持 Z-Image-Turbo、SD1.5、LTX Video，可跨机调用（`baihua_draw` / `baihua_draw_video`）。
用法见 [docs/COMPUTE_POOL_DRAW.md](docs/COMPUTE_POOL_DRAW.md)。

## DSH 插件

| 插件 | 作用 |
|---|---|
| [baihua-dsh-plugin](https://github.com/luminsw/baihua-dsh-plugin) | 桥接 agent 会话（HTTP+WS `/dsh-bridge/*`）、`bh_*` 运维工具、`baihua_draw*`、设置页服务状态卡片 |
| [baihua-local-ai-dsh-plugin](https://github.com/luminsw/baihua-local-ai-dsh-plugin) | 注册 `baihua-local` provider + 本地小任务工具 |
| 百花内置 | 标准 MCP `/mcp`（知识库 / 家庭只读能力） |
| [hysteria-dsh-plugin](https://github.com/luminsw/hysteria-dsh-plugin) | DSH 内管理 Hysteria 2 代理 |

部署与配置：[docs/DSH_INTEGRATION.md](docs/DSH_INTEGRATION.md)。

## 文档

- [docs/README.md](docs/README.md) — 文档索引
- `tools/bh/README.md` — CLI 与排障
- [docs/LINUX_NATIVE.md](docs/LINUX_NATIVE.md) — Linux 原生部署（systemd + SQLite）与 PostgreSQL 迁移
- [AGENTS.md](AGENTS.md) — 开发约定（架构 / 命名 / 测试）