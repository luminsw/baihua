# 百花局域网算力池（LAN Compute Pool）设计方案

> 目标：同一局域网内的所有百花服务器组成一个"算力池"——在一台机器上就能
> 看到并使用全网每一台机器的 AI 模型，token 输出速度一目了然，按需选择，
> 打通物理壁垒。
>
> 文生图 / 文生视频的跨机共享（绘图网关）用法见 [COMPUTE_POOL_DRAW.md](COMPUTE_POOL_DRAW.md)。

## 1. 现状

| 能力 | 现状 | 缺口 |
|------|------|------|
| 服务器互联 | ✅ 已有（UDP 45678 发现 + `/mg/server-msg/inbox` 互发消息） | 只传消息，不传算力信息 |
| AI 提供方 | ✅ `AiProviderConfig`（OpenAI 兼容 baseUrl + 模型列表 + 分层 Tier） | 只能配本机视角的 URL，对端算力靠手动填 IP |
| 模型测速 | ✅ `ModelBenchmarkService` 实测每模型 token/s + `HardwareBenchmark` 硬件估算 | 只测本机，无跨机汇总 |
| 跨机访问 | ⚠️ 部分：本机 `.13:8788` 已可达（200）；本地模型服务（Ollama/llama.cpp/OpenVINO host）天然 OpenAI 兼容 | k8s 内 ClusterIP 不对局域网暴露；无统一网关 |

## 2. 总体架构（三层）

```
┌─────────────────────────── 百花算力池 ───────────────────────────┐
│                                                                  │
│  ┌─────────────┐   能力广播/心跳    ┌─────────────┐              │
│  │ 192.168.3.13│ ◄──────────────► │ 192.168.3.9 │  ... 更多机器  │
│  │  (桃夭馆)   │   /mg/capabilities │  (寻芳居)   │              │
│  └──────┬──────┘                   └──────┬──────┘              │
│         │ 本地 OpenAI 兼容               │ 本地 OpenAI 兼容       │
│  ┌──────▼──────┐                  ┌──────▼──────┐               │
│  │ 推理后端     │                  │ 推理后端     │               │
│  │ Ollama/llm.cpp/OpenVINO 等     │ (同左)       │               │
│  └─────────────┘                  └─────────────┘               │
│                                                                  │
│  WebUI /compute 总览 ──► 一键选用 ──► 本机 AiProviderConfig      │
│  统一推理网关（M3）──► 按模型名路由到最快机器 + 故障转移            │
└──────────────────────────────────────────────────────────────────┘
```

### 2.1 算力发现层（Capability Registry）

每台百花机器暴露一个只读能力端点（对端服务器鉴权，复用 `X-Server-Token`）：

```
GET /mg/capabilities
{
  "serverId": "srv-xxx",
  "name": "桃夭馆",
  "hostUrl": "http://192.168.3.13",
  "providers": [
    {
      "id": "local-openvino",
      "name": "OpenVINO (本机)",
      "models": [
        { "name": "Qwen2.5-14B-Instruct-INT4-OV", "tier": "Tier2", "isMain": true,
          "tokensPerSecond": 23.5, "contextWindow": 32768, "gpu": "Intel Arc" }
      ]
    }
  ],
  "gpu": { "name": "Intel Arc A770", "vramGb": 16, "utilization": 12 },
  "cpuCores": 8,
  "updatedAt": "..."
}
```

汇聚机制（沿用服务器互联的发现框架）：
- **HTTP 拉取为主**：发现对端（手动添加/局域网广播）后，定时 `GET /mg/capabilities` 缓存（`ServerPeer` 表加 `LastCapabilitiesJson` 字段，TTL 复用 5 分钟心跳语义）。
- **广播为辅**：native 部署继续走 UDP 45678；k8s pod 收不到广播（已验证）→ 手动添加或经宿主机转发。
- 不实时广播算力，只广播"我活着"；算力详情按需拉取，避免 UDP 报文过大。

### 2.2 跨机推理接入（两个阶段）

**M1 阶段：提供方自动注册（最小可用）**

- 每台机器新增 `ComputeNodeService`：维护对端能力缓存。
- 把对端机器的推理端点**自动注册为本机只读 AI 提供方**（写入 `AiProviderConfig`，`Id` 加 `peer-` 前缀，`IsMain=false`，`Name` = "192.168.3.9 · Qwen2.5-14B"）。
- 本机 AiSettingsService 照常工作——聊天/拜师/OpenClaw 天然就能用对端模型，零协议改动。
- 对端机器的推理端点需要局域网可达：k8s 部署在 Traefik 上以 `/mg/ai/` 路由对外暴露推理端点
  （→ `bh-server`，8788；**合并后 AI 模块与家庭同进程**——原 `/mg/ai/` → `bh-ai:8791` 的独立后端已不存在，
  8790/8791 两个端口已随三服务合一消失）；本地模型服务（Ollama/llama.cpp/OpenVINO host）由 AI 模块代理转发。

**M3 阶段：统一推理网关（进阶）**

- 独立 `bh-gateway`（或并入 AI 服务）：统一 OpenAI 兼容入口 `/v1/chat/completions`。
- 路由策略：
  - 显式指定机器 → 直达；
  - 只给模型名 → 路由到"有该模型且最快"的机器；
  - 不指定 → 按任务类型推荐（见"额外点子 1"）；
  - 目标机失败 → **failover** 到次快节点（幂等重试，流式逐 token 转发）。
- 网关同时做鉴权（X-Server-Token）、指标上报（token 数、耗时 → 算力图表数据源）。

### 2.3 算力可视化层（Compute Dashboard）

新增页面 `/compute`：

```
┌─ 节点总览 ──────────────────────────────────────────────┐
│  [192.168.3.13 桃夭馆 🟢]  GPU Intel Arc · 3 模型 · 23.5 T/s │
│  [192.168.3.9  寻芳居 🟢]   CPU 8核    · 2 模型 · 8.1 T/s  │
└──────────────────────────────────────────────────────────┘
┌─ Token 输出速度（每机器 × 每模型）──────────────────────┐
│  柱状图：Qwen-14B  ████████████ 23.5/s   ← .13 OpenVINO  │
│          Qwen-14B  ████ 8.1/s           ← .9  CPU        │
│          1.5B      ████████████████████ 46/s  ← .13      │
│  （点击柱子 → 一键选用该模型 / 开始实时测速）              │
└──────────────────────────────────────────────────────────┘
┌─ 历史趋势 ──────────────────────────────────────────────┐
│  折线图：最近 N 次测速，观察过热降频/负载波动              │
└──────────────────────────────────────────────────────────┘
```

- 数据来源：`ModelBenchmarkService` 扩展为可跨机触发——对端暴露 `POST /mg/benchmark/run`（指定模型 → 跑一次标准 prompt → 回传 TPS），结果统一进本机 `BenchmarkRepository`。
- 图表库：WebUI 目前无图表库（仅 bootstrap-icons）。引入**本地化 ECharts**（离线打包进 wwwroot，沿用 app 的离线优先模式；不用 CDN）。移动端（鸿蒙/安卓）做简单柱状列表即可，不引重量级图表。
- 一键选用：图表点击 → 写本机 `AiProviderConfig`（设为该模型或新增提供方）→ 聊天/拜师立刻可用。

## 3. 数据与安全

- **鉴权**：跨机 `/mg/capabilities`、`/mg/benchmark/run`、AI 转发全部要求 `X-Server-Token`（复用消息互发的口令体系；每台机器可设置独立 token，WebUI 管理）。
- **信任分级**：提供方标注来源——`本机` / `局域网可信`（经 token 互认）/ `手动添加`。WebUI 可禁用某个对端的所有模型。
- **隐私**：聊天内容跨机 = 离开本机，UI 上明确标注"模型运行在 192.168.3.9"。

## 4. 实施计划

| 里程碑 | 内容 | 状态 |
|--------|------|------|
| **M1** | `/mg/capabilities` + `ComputePoolService` + 对端提供方自动注册 + Traefik `/mg/ai/` 路由 + AI 模块 OpenAI 兼容 shim + WebUI `/compute` 页 | ✅ 已实施（见下） |
| **M2** | `/mg/benchmark/run` 跨机测速 + ECharts 趋势折线 + 一键选用完善 | ✅ 跨机测速已实施（见下） |
| **M2.5** | 局域网模型商店（模型去重共享、断点续传）+ **跨机布署（拉取 + 启动运行时）** | ✅ 模型商店 + 布署已实施（见下） |
| **M3** | 统一推理网关 /mg/pool/v1：模型名全网路由 + 速度优先（v1） | ✅ 已实施 |

### M1 已实施内容（commit 记录见 git）

- **契约**：`Baihua.Contracts/ComputePool/ComputePoolDtos.cs`（能力广播、算力池总览、选用请求）
- **本机能力端点**：`GET /mg/capabilities`（X-Server-Token 自校验，公开路径）——汇报
  ServerId/名称/入口/OpenAiBaseUrl（自动派生 `{入口}/mg/ai/v1`，可被
  `BAIHUA_PUBLIC_OPENAI_BASE_URL` 覆盖）/提供方/模型/本机实测 TPS（来自 ModelBenchmark 排行榜）/CPU 核数
- **算力池服务**：`ComputePoolService`（IHostedService，每 60s 拉取对端能力并缓存；
  自动把声明了 OpenAI 端点的对端注册为本机提供方 `peer-{ServerId}`，模型合并去重；
  模型一致则跳过写入）
- **管理端点**：`GET /api/compute-pool`、`POST /api/compute-pool/refresh`、`POST /api/compute-pool/select`
- **OpenAI 兼容 shim**：`Baihua.Modules.Ai`（同一 `Baihua.Server` 进程）的 `/mg/ai/v1/chat/completions`（非流式 + 流式 SSE）与
  `/mg/ai/v1/models`——按模型名路由到本机 AI 提供方（含本地 Ollama/llama.cpp/OpenVINO），
  鉴权 `Authorization: Bearer` 对 `BAIHUA_AI_EXTERNAL_TOKEN`（未配置则局域网信任）
- **Traefik**：`/mg/ai/`（priority 760）→ `bh-server`:8788（合并前 → `bh-ai`:`8791`，该后端已随三服务合一删除）
- **WebUI**：`/compute` 算力池页（节点卡片 + 模型×TPS 柱状图（CSS bar）+ 一键选用）+
  侧栏「算力池」入口

### 已决策项

1. 网关：M1 自动注册提供方先行，M3 统一网关为后续里程碑（架构预留）
2. 图表：M1 用零依赖 CSS bar 柱状图（暗色适配、立即可用）；M2 引入 ECharts 本地化做趋势折线
3. 模型商店：排进 M2.5
4. 模型选择：手动选择为主，M2 加任务类型推荐角标（"够用最快"），自动速度路由留给 M3

### M2 已实施内容

- **跨机快速测速**：（短提示 + max_tokens=64 + 无缓存 client，
  几秒出结果，避免长作文超时/命中分布式缓存）；
  对任意节点（本机/对端）测速，结果回写能力缓存；/compute 页每模型「⏱ 测速」按钮
- **ModelBenchmarkService 修复**：usage=0 时回退字符估算（本地端点常不上报 usage）
- **局域网模型商店**：（已下载模型清单）+ 
  （tar 流式下发，PAX 格式）；对端经
   拉取并解压到本机模型根；/compute 页模型商店区「⬇ 拉取」按钮
- 实测：本机 qwen2-5-vl-7b（GPU）快速测速 ≈ 3 t/s、稳定；模型商店 8.4GB Qwen-14B 可 tar 下发

### M3 已实施内容（v1）

- **统一推理网关** `POST /mg/pool/v1/chat/completions` + `GET /mg/pool/v1/models`：
  按模型名在"本机 AI 服务 + 对端广播"里找候选节点，本机优先，对端按实测 TPS 从快到慢
  路由（`FindBestNodeAsync`），请求/响应按字节流透传（支持流式 SSE）；全网无该模型 → 404。
  每台机器的对外推理端点统一为 `{入口}/mg/pool/v1`（capabilities 自动广播）。
- 路由示例：本机 qwen2-5-vl-7b → 本机 AI shim → GPU；对端有更快的同名模型 → 自动走对端。
- /compute 页：同名模型跨节点对比，"⚡最快"角标 + TPS 排序。
- 注：failover（首选节点失败自动降级）与任务级调度留待 M3 v2。

### M2.5 已实施内容（局域网模型商店 + 跨机布署）

- **模型商店**：`GET /mg/model-store/list`（已下载模型清单，含大小/文件数）、
  `GET /mg/model-store/download/{name}`（tar 流式下发，PAX 格式；条目带 `{name}/` 前缀，
  对端解压时自动剥掉一层，避免双重嵌套）。
- **拉取**：`POST /api/compute-pool/pull-model` → `PullPeerModelAsync`：对端 tar 流下载 +
  解压到本机模型根（临时目录 + 原子改名）。共享解压助手 `ModelTarTransfer.DownloadAndExtractAsync`。
- **跨机布署（一键推模型到对端跑）**：
  - 管理端点 `POST /api/compute-pool/deploy`（`DeployModelRequest{ServerId, ModelName, Device?}`）
    → `ComputePoolService.DeployPeerModelAsync`：校验本机已有该模型 → 算出本机对外下载地址
    （`BAIHUA_HOST_IP` 优先，否则 `GetLocalPublicBaseUrl`）→ 携带 `SourceServerId/Device` 请求对端。
  - 对端端点 `POST /mg/model-store/deploy`（`PeerDeployRequest{SourceServerId, SourceUrl, ModelName, Device}`）：
    SSRF 防护（仅回环/私网 IP + 必须 `/mg/model-store/download/` 路径且模型名一致）→ 按
    SourceServerId 解析来源机互联口令 → 拉取解压到本机模型根 → `ILocalRuntimeManager.StartAsync`
    启动常驻推理进程（OpenVINO：openvino_llm_server.py），GPU 失败自动回退 CPU，返回
    `DeployModelResultDto{Success, Device, Port, Endpoint}`。
  - WebUI /compute 页：本机模型行「📦 布署」→ 选择目标节点 → 执行（大模型需数分钟，60min 超时）。
- 语义：**布署 = 本机已有模型 → 对端拉取 + 对端启动运行时**；对端跑起来后其
  `/mg/capabilities` 自动广播该模型（含 TPS），本机即可经网关/选用直接调用。

### Provider 分层设计（跨机布署/推理的厂商扩展点）

> 目标：`Baihua.AI.Provider` 只保留"所有厂商都提供的最大公约数"接口；
> Intel 专有的高效实现独立成 `Baihua.AI.Provider.OpenVino`；NVIDIA/AMD 预留同接口接入。

- **厂商中立接口（Baihua.AI.Provider，新）**：
  - `ILocalModelTool`：工具信息 / 运行中模型 / 可下载目录 / 已下载清单 / 加载卸载模型 / 确保运行时就绪
  - `ILocalVisionInference`：视觉服务状态 / 识别图片 / 启停（`VisionStatusDto` 来自 Contracts）
  - `ILocalRuntimeManager`：模型根 / 已安装扫描 / 运行实例 / `StartAsync(modelPath, device)` /
    `StopAsync(port)` —— 跨机布署的对端启动即调用本接口
  - `ILocalModelInference`：本地模型流式推理（GGUF/ONNX 后端，原已有）
- **Intel 实现（Baihua.AI.Provider.OpenVino，新项目）**：`OpenVinoToolService`、`OpenVinoVisionService`、
  `OpenVinoRuntimeManager`、`OpenVinoChatInference`、`LocalVisionOptions` 全部在此；
  LocalVision 的 `vision_server.py` / `openvino_llm_server.py` 随发布拷贝。
- **NVIDIA / AMD 预留**：新项目 `Baihua.AI.Provider.Cuda` / `Baihua.AI.Provider.Rocm` 分别实现
  上述四个接口即可接入（`Baihua.Server` 宿主 / WebUI 零改动）：
  - CUDA：`ILocalRuntimeManager` → vLLM / TensorRT-LLM 进程；`ILocalVisionInference` → VL 模型服务
  - ROCm：`ILocalRuntimeManager` → vLLM-ROCm / llama.cpp ROCm；`ILocalModelTool.Id` = `"cuda"` / `"rocm"`
- DI 注册（`services/Baihua.Server/Program.cs`，合并前三服务各自注册）：`AddSingleton<ILocalModelTool, OpenVinoToolService>()`、
  `AddSingleton<ILocalRuntimeManager, OpenVinoRuntimeManager>()`。
- 命名空间迁移：`LocalAiOptions` 移至 `Baihua.Core`（各模块共用）；其余 OpenVINO 类型命名空间
  改为 `Baihua.AI.Provider.OpenVino`。

### 待办（收尾）

- 本机本地模型接入 OpenAI 提供方（把已下载的 OpenVINO 模型注册为指向 bh-openvino:8000 的提供方），
  让本机算力真正可被对端选用
- AI 模块各提供方 API Key 需在「AI 配置」页确认有效（shim 会透传其存储的 key）
- k8s 部署（如桃夭馆 .13）的 `bh-server` 容器内无 OpenVINO 运行时 → 对端布署到此类节点会返回
  "未找到可用 Python/设备不支持"；需把布署目标指向 native（Windows/Linux 本机）或先实现
  OpenVINO 服务 pod 代理后接 runtime 接口

## 5. 额外点子（按性价比排序）

1. **任务类型推荐**：按任务自动选"够用最快"模型——闲聊/代码补全用 1.5B，长文/推理用 14B，视觉走视觉模型；WebUI 显示推荐理由。省电且体验更好。
2. **模型仓库去重（局域网模型商店）**：8.4GB 的 Qwen-14B 全网只需下载一次。已下载的机器把模型目录登记为"可借出"（`/mg/model-store/`），对端断点续传拉取（复用现有 `.downloading` + `.part` 机制）。扫码即可借用。
3. **任务级调度（无服务器百花）**：OpenClaw 委派、拆笔记、批处理生成等异步任务提交到算力池，调度器按"谁快谁干"选机器执行，结果经 `/mg/server-msg/inbox` 回传。长任务不用占着本机。
4. **唤醒/休眠省电**：检测到算力池无任务时，向空闲机器发 WOL 让它休眠；任务到达再唤醒。电网级的省电，适合长期开机的小主机群。
5. **瓶颈识别**：除 token/s 外，同时上报首 token 延迟（TTFT）、显存占用、并发队列——区分"算力不够"还是"带宽不够"（如大模型跨机走千兆网 vs 本机 NVLink 差异明显）。
6. **云端兜底混合路由**：算力池没有能跑的模型或全部过载 → 自动 fallback 到 Tier3 云端大模型，WebUI 明示成本与归属。
7. **信任与安全隔离**：对端模型运行在对方的容器里，本机不碰对端文件；模型下载只发生在"被选中的那台机器"，杜绝恶意模型扩散。
8. **一键迁移已下载模型**：把本机已下载的 OpenVINO/Llama 模型目录压缩成可迁移包（`/mg/model-store/export`），对端导入即可用，省去重新下载。

## 6. 关键决策点（待确认）

1. 网关形态：M1 的"自动注册提供方"是否够用？还是直接上 M3 网关（更多代码，但体验统一）？
2. 图表库：ECharts 本地化（Web）可接受？移动端只做列表不做图表？
3. 模型共享：是否值得做"局域网模型商店"（M2.5），还是先各机自行下载？
4. 调度策略：先做"手动选择"，还是直接上"速度优先自动路由"？
