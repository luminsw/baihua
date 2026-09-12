# 百花（Baihua）

家庭版智能服务 + 本地 AI 算力池 + DSH 插件生态，面向本地/局域网/家庭使用。

**定位**：百花是**能力提供方**（本地 AI 推理 / 算力池 / 知识库 / 家庭数据），
**DSH（DeepSeek Harness）是编排与交互面**——百花 Web 内嵌 DSH 官方 Web UI 作为 AI 消费入口，
运维/绘图/本地小任务经插件桥接给 DSH agent 调用。

## 功能总览

- **家庭/亲子**：任务、成就、拜师（Master）体系、家庭病历本/AI 诊断、打卡清单、Anki 卡片
- **AI 能力**：多 Provider 配置（DeepSeek/Anthropic 等）、本地推理（OpenVINO/llama.cpp/Ollama/LM Studio）、
  模型测速与推荐、OpenClaw 集成
- **知识库（Vault）**：多知识库管理、增量同步、全文/语义搜索、索引
- **算力池**：局域网内百花服务器组成算力池，跨机共享模型与绘图能力（ComfyUI 文生图/文生视频）
- **移动端**：BaihuaSdk（C#）跨平台协议层；鸿蒙（arkts）/ 安卓（kotlin）花记客户端；花圃（MAUI）技术验证工具
- **DSH 集成**：桥接/运维/绘图插件 + 本地 AI 插件 + MCP server（详见 [docs/DSH_INTEGRATION.md](docs/DSH_INTEGRATION.md)）

## 架构

```
services/
  Baihua.Server/         # 唯一后端宿主（8788：家庭 / AI / 知识库 三模块同一进程）
  Baihua.Modules.Family/ # 家庭模块（亲子功能、设备配对、算力池绘图网关、MCP 工具）
  Baihua.Modules.Ai/     # AI 模块（模型、聊天、配置管理、OpenAI 兼容端点）
  Baihua.Modules.Vault/  # 知识库模块（Vault、同步、搜索、索引）
  Baihua.Web/            # Web 界面（5177，Blazor Server，仍是独立进程）
  Baihua.Contracts/      # 共享 DTO 与接口契约
  Baihua.Core/           # 共享服务层（跨模块接口在 Baihua.Core/Modules/）
  Baihua.Data/           # 共享 EF Core 数据层（单库多 DbContext）
libs/
  BaihuaSdk/        # 跨平台移动端 SDK（net9.0;net10.0，零 MAUI 依赖）
  MobileContract/   # 移动端契约
clients/
  Huapu/            # 花圃（BaiHua.Nursery）— MAUI 技术验证客户端
scripts/            # 开发、发布、部署脚本
docker/             # Docker compose 配置（nginx / server / webui 镜像）
tools/bh/           # 极简 CLI（Windows/Linux，native/docker/k8s）
tests/              # 后端/ SDK / 花圃单元测试 + E2E
```

> 合并前是 `Baihua.Family`(8788) / `Baihua.AI`(8791) / `Baihua.Vault`(8790) 三个独立服务，
> 已在 commit `aa053f1` 合并为 `Baihua.Server` 单进程（业务代码拆为 `Baihua.Modules.*` 三个类库，
> 跨模块调用只经 `Baihua.Core.Modules` 下的接口在进程内直调，不再有服务间 HTTP）。

**数据库**：单一 PostgreSQL 库（默认 `baihua`，`PG_DATABASE` 可覆盖），一个 `public` schema，
各模块保留自己的 `DbContext`，表结构由 `Baihua.Data.DatabaseInitializer` 统一建表；
旧三库合并见 `scripts/migrate-to-single-db.ps1`。
连接与存储细节见 [docs/CONFIG_STORAGE_ARCHITECTURE.md](docs/CONFIG_STORAGE_ARCHITECTURE.md)。

## 快速启动

```bash
# 一键打开管理面板（自动启动 + 自动登录 WebUI）
bh dashboard

# 手动启动（只有 1 个后端进程 + 1 个 WebUI）
cd services/Baihua.Server && dotnet run
cd services/Baihua.Web && dotnet run
```

Windows 下用 `bh.ps1`（推荐 PowerShell 7，UTF-8 中文正常）；完整命令见 `tools/bh/README.md`。

## DSH 插件生态（DeepSeek Harness × 百花）

> 定位：**百花 = 能力提供方**（算力池 / 本机模型 / 知识库 / 家庭数据），**DSH = 编排与交互面**。
> 部署与配置总文档见 [docs/DSH_INTEGRATION.md](docs/DSH_INTEGRATION.md)。

| 插件 | 方向 | 作用 |
|---|---|---|
| [baihua-dsh-plugin](https://github.com/luminsw/baihua-dsh-plugin) | 百花 Web → DSH | 桥接：agent 会话驱动（HTTP+WS `/dsh-bridge/*`）、`bh_*` 运维工具、`baihua_draw*` 绘图、设置页「百花服务状态」卡片 |
| [baihua-local-ai-dsh-plugin](https://github.com/luminsw/baihua-local-ai-dsh-plugin) | DSH → 百花本地 AI | 探测 OVMS/shim/算力池，注册 `baihua-local` LLM provider + `local_ai_small_task` 小任务工具（省线上 token） |
| _（百花内置）_ | 百花 → 任意 MCP 客户端 | 标准 MCP（streamable-http `/mcp` 端点，挂在 `Baihua.Server`）：知识库 / 家庭只读能力（DSH 内工具名 `mcp__baihua__*`） |
| [hysteria-dsh-plugin](https://github.com/luminsw/hysteria-dsh-plugin) | 本机代理 | DSH 内管理 Hysteria 2 代理（`proxy_*` 工具 + `proxy_retry` 失败兜底） |

百花 Web 的 `/dsh` 页内嵌 DSH 官方 Web UI（AI 消费型交互统一交给 DSH 智能体），
右上角「🧰 运维」保留百花服务运维面板。

## 访问授权

- **WebUI（5177）**：CLI Token Cookie 登录，`./bh dashboard` 一键授权（Token 5 分钟有效）。
- **管理 API（8788）**：默认仅允许本机（loopback）；局域网设备走移动端公开端点
  （`/mg/*`、配对/同步），需 HMAC 签名设备鉴权。
- **容器/反向代理**：非 loopback 来源访问管理 API 需显式放行：
  - `BAIHUA_ADMIN_ALLOWED_NETS`：允许网段（CIDR 逗号分隔）
  - `BAIHUA_TRUSTED_PROXY_NETS`：受信任反向代理网段

## 端口

| 服务 | 端口 | 说明 |
|------|------|------|
| Baihua.Server | 8788 | 唯一后端：家庭/亲子、AI、知识库三模块 + 算力池绘图网关 + MCP `/mcp` |
| Baihua.Web | 5177 | Blazor Server 管理后台（独立进程） |
| OpenVINO Model Server | 8000 | 本地 OpenVINO 推理（OVMS，OpenAI 兼容） |

> 合并前的 `Baihua.AI`(8791) / `Baihua.Vault`(8790) 已随服务合并消失，不再监听。

## 本地 OpenVINO 推理（OVMS）

百花本地推理由 **Intel OVMS（OpenVINO Model Server）** 统一承载
（OpenAI 兼容 `/v3/chat/completions`、`/v3/embeddings`，模型：`qwen2.5` /
`qwen2.5-vl-7b` / `bge-small-zh`）：

- **Windows**：管理员运行 `scripts/install-openvino-ovms-service.ps1` 一键安装为系统服务（`ovms`，:8000）
- **Linux k8s**：`bh-openvino` Deployment（官方 `openvino/model_server` 镜像），见 `k8s/README.md`

## 算力池绘图（ComfyUI 网关）

百花把本地 ComfyUI 的文生图/文生视频接入算力池，局域网内其它百花服务器或 DSH 可跨机调用
（`baihua_draw` / `baihua_draw_video` / `baihua_draw_status`）。支持 Z-Image-Turbo（1024、8 步）
与 SD1.5、LTX Video，高级参数（seed/cfg/sampler/scheduler/模型选择）齐全。
用法见 [docs/COMPUTE_POOL_DRAW.md](docs/COMPUTE_POOL_DRAW.md)。

## 文档

- [docs/README.md](docs/README.md) — 文档中心索引
- [docs/DSH_INTEGRATION.md](docs/DSH_INTEGRATION.md) — DSH 插件生态集成
- [AGENTS.md](AGENTS.md) — 开发助手说明（架构 / 命名约定 / 测试）
- `k8s/README.md`（部署）、`tools/bh/README.md`（bh CLI）
