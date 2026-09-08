# 百花 AI 功能精减计划

> 创建时间：2026-09-08
> 状态：待审阅（计划文档，审阅通过后分阶段实施）
> 目标：百花 AI 只保留「不变 / 变化慢」的稳定能力；「变化快」的能力删除，或改由 DSH Agent 用提示词 + 工具动态完成，避免随软硬件迭代而过时。

---

## 一、背景与动机

软硬件（推理后端、模型型号、量化方式、GPU 生态）迭代极快。百花 AI 侧沉淀了大量与具体框架/硬件/厂商绑定的功能：
- 三种进程内推理后端（LlamaSharp GGUF / OnnxRuntimeGenAI / OpenVINO）
- 四种外部运行时管理（Ollama / LM Studio / llama.cpp / OpenVINO）
- 一套**静态硬编码**的模型推荐引擎 + 模型库
- 复杂分 Tab 的本地模型 WebUI

这些功能的维护成本随生态演进只增不减，且大部分已被 DSH + 三个插件以「探测 + 推理接入 + 环境运维」的形式接管。精减的核心判断：**协议/接口层稳定，具体技术/型号层易变**。保留前者，后者下沉给 Agent 用自然语言按当下最优解动态执行。

---

## 二、现状盘点（调查结论）

### 2.1 服务与插件职责现状

| 组件 | 位置 | 职责 |
|---|---|---|
| Baihua.AI（8791） | `services/Baihua.AI/` | AI 计算 + 配置管理，独占 `ai` 库 |
| Baihua.AI.Provider | `services/Baihua.AI.Provider/` | 本地推理接口 + 运行时服务（Ollama/LM Studio/llama.cpp） |
| Baihua.AI.Provider.OpenVino | `services/Baihua.AI.Provider.OpenVino/` | OpenVINO/OVMS 推理 + 运行时管理 |
| Baihua.Family（8788） | `services/Baihua.Family/` | 承载本地模型部署/算力池/MCP/绘图网关 |
| WebUI（5177） | `services/Baihua.Web/` | 本地模型页、AI 对话页、编程 Agent 页 |
| baihua-dsh-plugin | `~/src/baihua-dsh-plugin` | 桥接 + `bh_*` 运维 + `baihua_draw*` 绘图 + 病历本工具 |
| baihua-local-ai-dsh-plugin | `~/src/baihua-local-ai-dsh-plugin` | 探测 OVMS/shim/算力池 → `baihua-local` provider + `local_ai_small_task` |
| openvino-dsh-plugin | `~/src/openvino-dsh-plugin` | OpenVINO 环境诊断/扫描/状态/基准/INT4 转换 |

### 2.2 百花 AI 侧的功能清单（按稳定性分类）

**A 类：稳定协议/接口层（保留）**

| 功能 | 入口 | 关键文件 |
|---|---|---|
| OpenAI 兼容推理端点 | `mg/ai/v1`（AI 8791） | `services/Baihua.AI/Controllers/OpenAiCompatController.cs` |
| 聊天 API（移动端花记在用） | `api/ai/chat` | `services/Baihua.AI/Controllers/AiChatController.cs` |
| 云端提供商/API Key 配置 | `api/ai/config` | `services/Baihua.AI/Controllers/AiConfigController*.cs` |
| Embedding 配置 | `api/embedding/config` | `services/Baihua.AI/Controllers/EmbeddingConfigController.cs` |
| TTS 转发（Kokoro） | `api/ai/tts` | `services/Baihua.AI/Controllers/TtsController.cs` |
| AI 用量指标 | `api/ai/metrics` | `services/Baihua.AI/Controllers/AiMetricsController.cs` |
| 算力池（局域网共享算力） | `api/compute-pool`、`/mg/pool/v1`、`/mg/capabilities`、`/mg/model-store` | `services/Baihua.Family/Controllers/ComputePool/*` |
| 绘图网关 | `/mg/pool/v1/draw/*`、`api/comfy` | `services/Baihua.Family/Controllers/ComputePool/DrawGatewayController.cs`、`ComfyController.cs` |

**B 类：变化快 / 与具体技术绑定（删除或下沉）**

| 功能 | 入口 | 关键文件 |
|---|---|---|
| 进程内推理后端（GGUF/ONNX） | `api/local-ai` | `services/Baihua.AI.Provider/LlamaSharpInference.cs`、`OnnxRuntimeGenAIInference.cs` |
| 本地对话/模型扫描/工具协议 | `api/local-ai` | `services/Baihua.AI/Controllers/LocalAIController.cs` |
| 本地图片识别 | `api/local-ai/vision` | `services/Baihua.AI/Controllers/LocalVisionController.cs`、`OpenVinoVisionService.cs` |
| 外部运行时管理（Ollama/LM Studio/llama.cpp） | 由 Family 注册 | `services/Baihua.AI.Provider/OllamaService*.cs`、`LmStudioService*.cs`、`LmStudioDownloadService.cs`、`LlamaCppService*.cs` |
| 本地模型部署/下载/工具状态 | `api/local-models/*` | `services/Baihua.Family/Controllers/AI/LocalModelDeploymentController*.cs`、`Services/AI/LocalModelDeploymentService*.cs`、`ModelDownloadService.cs` |
| OpenVINO 目录/下载/运行/注册 | `api/local-models/openvino/*` | `LocalModelDeploymentController.OpenVino.cs`、`OpenVinoRuntimeManager.cs`、`OpenVinoToolService.cs` |
| 模型推荐引擎 + 静态模型库 | `api/local-models/recommend`、`/api/benchmark/vram-tiers` | `Services/AI/ModelRecommendationEngine.cs`、`Baihua.Contracts/LocalModels/ModelDatabase.cs`、`Services/AI/OllamaLibraryClient.cs` |
| AI 编程 Agent（MAF 框架） | `api/ai/code`、`/code-agent` | `services/Baihua.AI/Controllers/CodeAgentController.cs`、`Services/CodeAgentService.cs`、`CodeAgentTools.cs` |

### 2.3 WebUI 本地模型页现状

`services/Baihua.Web/Pages/LocalModels.razor`（1497 行）+ `Components/LocalModels/OpenVinoTab.razor`（552 行）：
- 当前仅「概览」「OpenVINO」两个 Tab（不存在 NVIDIA/AMD Tab，它们只是 `ILocalRuntimeManager` 注释里的未来设想）
- 「概览」内 4 个折叠区：硬件概览、本地工具状态（Ollama/LM Studio/llama.cpp/OpenVINO 徽章）、模型推荐（场景/公司/显存档位分组）、部署状态
- 推荐数据来自静态 `ModelDatabase.cs`（内置具体型号）+ Ollama Library 在线缓存

### 2.4 OpenClaw 遗留

`services/Baihua.Family/Controllers/OpenClaw/OpenClawController.cs` 有 `local-ai-config`/`local-ai-models`/`local-ai-detect`/`sync-local-models` 端点，属本地 AI 检测/同步遗留逻辑，纳入精减评估。

---

## 三、精减原则（长期判断标准）

1. **协议/接口层保留，具体实现层删除**：OpenAI 兼容、MCP、配置管理、算力池网关这些「行业接口」不变；LlamaSharp/ONNX/静态模型库这些「具体技术选型」易变，删除。
2. **本地 AI 的「装什么、怎么装、跑什么」交给 DSH Agent**：由 Agent 结合当前硬件 + 在线模型生态，用提示词 + 工具（诊断/转换/注册/运行）按当下最优解执行，而不是百花代码里固化一套会过时的推荐逻辑。
3. **百花只当「能力提供方」**：暴露稳定能力端点（OVMS `:8000`、OpenAI shim、算力池、绘图网关、TTS、MCP），编排与决策全在 DSH。
4. **单一路径优于多后端**：本地文本/视觉推理统一走 OVMS（OpenVINO），砍掉 GGUF/ONNX 进程内推理与 Ollama/LM Studio/llama.cpp 三套外部工具管理，唯一例外是通用 OpenAI 兼容 shim（`mg/ai/v1`）转发外部端点，保留对任意工具的接入能力。

---

## 四、目标架构（精减后）

```
DSH Agent（编排面，提示词驱动）
 ├─ 推理接入：baihua-local-ai-dsh-plugin（探测 OVMS/shim/算力池 → baihua-local provider）
 ├─ 环境运维：openvino-dsh-plugin（诊断/转换/注册/基准 + 新增「初始化+装模型」）
 ├─ 百花运维/绘图：baihua-dsh-plugin（bh_* 运维 + baihua_draw*）
 └─ 百花数据：mcp__baihua__*（Family 内置 /mcp）

百花（能力提供方，只暴露稳定端点）
 ├─ AI 8791：mg/ai/v1（OpenAI shim）、api/ai/chat、api/ai/config、api/ai/tts、
 │            api/embedding/config、api/ai/metrics
 ├─ Family 8788：算力池（/mg/pool/v1、/mg/capabilities、/mg/model-store、api/compute-pool）、
 │              绘图（/mg/pool/v1/draw）、/mcp、/api/dsh/config|pool
 └─ 本机 OVMS 系统服务（8000）：OpenVINO 推理承载（文本/视觉/嵌入）
```

本地模型 WebUI：删除概览 Tab 与模型推荐，收敛为**一张模型表**，列含「工具」标注（Intel OpenVINO / NVIDIA TensorRT 之类），仅呈现已安装/可运行的真实状态，不再分厂商 Tab。

---

## 五、分步实施计划

> 每步独立可验证；先低风险 UI 收敛，再大范围后删减，最后同步 DSH 插件与文档。

### 阶段 1：WebUI 本地模型页收敛（低风险，先做）

**1.1 本地模型页改为单表**
- 改 `services/Baihua.Web/Pages/LocalModels.razor`：
  - 删除「概览」Tab 及其 4 个折叠区（硬件概览 47-103 行、本地工具状态 105-145 行、模型推荐 147-230 行、部署状态 233-274 行）
  - 删除 Tab 切换逻辑（`_activeTab`、`_openVinoAvailable` 分支）
  - 页面主体改为一张统一模型表，列：模型名 / 参数 / 大小 / 用途 / 状态 / **工具**（新增，标注来源：Intel OpenVINO、NVIDIA TensorRT 等）/ 操作
  - 数据源改为聚合「已安装 + 运行中」真实模型（复用 `GET /api/local-models/available`、`/running`、`downloaded`），不再调用 `GetRecommendedModelsAsync`/`GetBenchmarkVramTiersAsync`
- 同步精简 `Components/LocalModels/OpenVinoTab.razor`：并入主表（或删除该组件，"工具"列标注 OpenVINO）

**1.2 删除「模型推荐」**
- 前端：删除 `LocalModels.razor` 中推荐相关 @code（`scenarioTags`/`companyTags`/`VramTierValues`/`FilteredModelsByTier`/`RenderModelCard`/`LoadRecommendationsAsync` 等，约第 1098-1346 行）
- 后端：删除 `GET /api/local-models/recommend`（`LocalModelDeploymentController.cs:140-182`）、`ModelRecommendationEngine.cs`、`Baihua.Contracts/LocalModels/ModelDatabase.cs`、`OllamaLibraryClient.cs`、`GET /api/benchmark/vram-tiers`（`ModelBenchmarkController.cs`）及 `ApiService.GetRecommendedModelsAsync`/`GetBenchmarkVramTiersAsync`（`ApiService.FamilyTools.cs:977-999、1549`）

**验收**：`/local-models` 页只显示一张模型表，无 Tab、无「概览」「模型推荐」；编译通过（`dotnet build services/BaiHua.slnx -c Release`）。

### 阶段 2：删除变化快的本地推理与运行时管理（后删减核心）

**2.1 砍掉进程内推理后端（GGUF/ONNX）**
- 删除 `Baihua.AI.Provider/LlamaSharpInference.cs`、`OnnxRuntimeGenAIInference.cs` 及 `Baihua.AI.csproj` 中对应 NuGet 引用（LLamaSharp、Microsoft.ML.OnnxRuntimeGenAI 等）
- 删除 `Program.cs` 中 `ILocalModelInference` 的 GGUF/ONNX 注册（`services/Baihua.AI/Program.cs:93-95`）

**2.2 删除本地对话与视觉端点**
- 删除 `Baihua.AI/Controllers/LocalAIController.cs`（`api/local-ai`）、`LocalVisionController.cs`（`api/local-ai/vision`）
- 删除 `Baihua.AI.Provider/ILocalModelInference.cs`、`ILocalVisionInference.cs`、`OpenVinoVisionService.cs`（视觉改由 OVMS 直连，不经 AI 服务中转）
- 注意：`OpenVinoChatInference.cs` 若仅供 `LocalAIController` 使用则一并删除（OVMS 由 DSH 插件直连；如需保留 AI 服务内 OpenVINO 转发，则改由 `mg/ai/v1` shim 完成，本条目在步骤 1 落实施工时核实调用方后定）

**2.3 删除外部运行时管理（Ollama/LM Studio/llama.cpp）**
- 删除 `Baihua.AI.Provider/OllamaService*.cs`、`LmStudioService*.cs`、`LmStudioDownloadService.cs`、`LlamaCppService*.cs`
- 删除 `Baihua.Family/Program.cs:288-291` 的注册
- 删除 `LocalModelDeploymentService` 中 `DeployToOllamaAsync`/`DeployToLmStudioAsync`/`configure` 等路径（`Services/AI/LocalModelDeploymentService*.cs`）

**2.4 删除本地模型部署控制器与 OpenVINO 管理**
- 删除 `Baihua.Family/Controllers/AI/LocalModelDeploymentController*.cs`（`api/local-models/*`：deploy/tools/openvino catalog/download/run/register 等）
- 删除 `Services/AI/ModelDownloadService.cs`、`OpenVinoRuntimeManager.cs`、`OpenVinoToolService.cs`、`LocalModelDeploymentService*.cs`
- 删除 `ILocalRuntimeManager.cs`、`ILocalModelTool` 接口
- 保留：`GET /api/local-models/running`/`available`/`downloaded`/`details` 中「只读真实状态」的子集，供阶段 1 的模型表查询（或改由 OVMS `/v1/models` + 算力池能力表聚合，实施时定）

**2.5 OpenClaw 本地 AI 端点清理**
- 评估并删除 `OpenClawController.cs` 的 `local-ai-config`/`local-ai-models`/`local-ai-detect`/`sync-local-models`（若 OpenClaw 功能已停用）

**验收**：`dotnet build services/BaiHua.slnx -c Release` 通过；`services/` 中无上述已删除符号引用；本地 OVMS `:8000` 仍可被 DSH 插件直连推理。

### 阶段 3：删除编程 Agent（CodeAgent）

- 删除 `Baihua.AI/Controllers/CodeAgentController.cs`、`Services/CodeAgentService.cs`、`CodeAgentTools.cs` 及 `Program.cs` 注册
- 删除 `Baihua.Data` 中 `CodeAgentSession` 实体与 `AIDbContext` 对应 DbSet/映射
- 删除 WebUI `Pages/CodeAgent.razor`、`Components/StreamingCodeBlock.razor`（若无其他引用）、`FamilyNavMenu.razor:113` 菜单项、相关 `ApiService` 方法与本地化资源（`Codes/LocalStrings*`）
- 编程能力改由 DSH Agent 完成

**验收**：`/code-agent` 无入口；编译通过；`ai` 库 `CodeAgentSessions` 表可保留历史数据不再写入。

### 阶段 4：DSH 插件更新（配合精减）

**4.1 openvino-dsh-plugin：补「初始化 + 装模型」能力**
- 新增/整合工具 `baihua_ai_bootstrap`（或 `openvino_env_init`）：组合「探测硬件 → 扫描已装模型 → 从 HF/Ollama 选适配型号 → `optimum-cli` INT4 转换 → 注册 OVMS → 起服务」，使「给 DSH 发提示词『初始化本机 AI 环境、装好通用文本/编程/文生图/文生视频/音频转文本/文本转语音并运行』」可端到端完成
- 修复硬编码路径：`benchScriptPath` 默认值 `C:/Users/lumin/src/baihua/scripts/openvino_benchmark.py`、导出目录 `C:/Users/lumin/.baihua/models`（`src/index.js`）改为环境/配置推断
- 文生图/文生视频（ComfyUI）、TTS（Kokoro）不在 OpenVINO 插件内实现，由 `baihua-dsh-plugin` 绘图工具 + 百花 TTS 端点承载

**4.2 baihua-local-ai-dsh-plugin：清理死配置**
- `src/index.js:96-103` 的 bootstrap 使用 `familyUrl`，但 `Config` 未声明该字段（恒 `undefined`，实际回落 `127.0.0.1`）——补声明或删除冗余分支
- 确认探测源仍覆盖 OVMS `:8000`、AI shim `mg/ai/v1`、算力池 `/mg/pool/v1`

**4.3 baihua-dsh-plugin：基本不动**
- 确认 `bh_*` 运维、`baihua_draw*` 绘图不依赖被删的本地模型端点（调查表明不依赖，仅 `/api/dsh/config|pool` + 绘图网关）

**验收**：三插件 `node --check` 通过；重启 DSH 后 `baihua-local` provider 仍可列出 OVMS/shim/pool 模型；`baihua_ai_bootstrap`（或等价提示词）能完成初始化。

### 阶段 5：文档与收尾

- 更新 `docs/DSH_INTEGRATION.md`、`AGENTS.md`（AI 服务职责、本地模型架构描述）
- 更新 `docs/LAN_COMPUTE_POOL.md` 与 `docs/openclaw-openvino-integration.md`（若涉及删除项）
- 全量 `dotnet build services/BaiHua.slnx -c Release` + 跑 `tests/Baihua.Family.Tests`、`tests/Baihua.Sdk.Tests`
- 更新 `Baihua.Data` 迁移（若删实体/表）

---

## 六、关键决策点（请审阅时确认）

1. **本地推理唯一后端=OpenVINO/OVMS**：是否同意删掉 GGUF(LlamaSharp) 与 ONNX 进程内推理，及 Ollama/LM Studio/llama.cpp 三套外部工具管理，本地文本/视觉统一走 OVMS？——建议同意（与当前 Windows native「OVMS 系统服务承载」现状一致）。
2. **算力池保留**：算力池（局域网算力共享 + 模型商店跨机分发）是百花「能力提供方」定位的核心，建议整体保留；仅删其中「本地推荐」相关部分。
3. **编程 Agent 删除**：`/code-agent` 页面与 `api/ai/code` 后端是否整体删除？（DSH 已能代劳编程；CodeAgent 依赖 MAF 框架，属变化快型）
4. **OpenClaw 本地 AI 端点**：`OpenClawController` 的 `local-ai-*`/`sync-local-models` 是否随 OpenClaw 功能一并停用？
5. **本地模型页聚合方式**：阶段 1 的「一张表」数据源，建议直接读 OVMS `/v1/models` + 算力池能力表（真实状态）而非百花自行维护「可下载目录」；如需保留「可下载/推荐」列则需另行确认。

---

## 七、关键文件索引

- `services/Baihua.AI/`（AI 服务 + 全部 Controller + `Program.cs`）
- `services/Baihua.AI.Provider/`、`services/Baihua.AI.Provider.OpenVino/`
- `services/Baihua.Family/Controllers/AI/LocalModelDeploymentController*.cs`、`Controllers/ComputePool/*`、`Services/AI/*`
- `services/Baihua.Web/Pages/LocalModels.razor`、`Components/LocalModels/*`、`Pages/CodeAgent.razor`、`Shared/FamilyNavMenu.razor`
- `services/Baihua.Contracts/LocalModels/ModelDatabase.cs`
- `services/Baihua.Data/Entities/AI/*`、`AIDbContext.cs`
- `~/src/baihua-dsh-plugin`、`~/src/baihua-local-ai-dsh-plugin`、`~/src/openvino-dsh-plugin`
- `docs/DSH_INTEGRATION.md`、`docs/LAN_COMPUTE_POOL.md`、`AGENTS.md`