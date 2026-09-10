# 本地模型页推倒重来：MCP 注册表架构计划

> 状态：待用户确认
> 日期：2026-09-10
> 原则：沿用 AI 精减总原则——百花只保留稳定层（存储 + 展示 + 协议接口），「变化快」的探测/硬编码/选型逻辑全部交由 Agent。

## 一、目标架构

```
┌─────────────────┐   提示词驱动    ┌──────────────────────┐
│ 用户（粘贴提示词）│ ─────────────→ │ DSH / OpenClaw Agent │
└─────────────────┘                └──────────┬───────────┘
                                              │ ①诊断硬件 ②装后端+模型 ③启动服务 ④验证
                                              │ ⑤调用百花 MCP 注册
                                              ▼
                              ┌──────────────────────────────┐
                              │ Baihua.Family /mcp           │
                              │  baihua_local_model_register │
                              │  baihua_local_model_list     │
                              │  baihua_local_model_unregister│
                              └──────────────┬───────────────┘
                                             ▼
                              ┌──────────────────────────────┐
                              │ family 库 local_model_registry│ ← 唯一事实来源
                              └──────────────┬───────────────┘
                                             ▼
                              ┌──────────────────────────────┐
                              │ WebUI 本地模型页（纯展示）     │
                              │  · 注册表一张表               │
                              │  · 最佳提示词卡片（一键复制）  │
                              └──────────────────────────────┘
```

百花不再：探测 OVMS/目录、维护 OmsModelMap 硬编码映射、推断显示名/参数量/用途、管理下载/删除。
百花只做：**存（MCP 写入）→ 查（页面展示）**。模型是什么、跑在哪、怎么装，全部由 Agent 动态决定。

## 二、数据库（family 库新增 1 表）

实体 `Baihua.Data\Entities\Family\LocalModelRegistry.cs`：

| 列 | 类型 | 说明 |
|---|---|---|
| id | serial PK | |
| tool | varchar(64) | 推理后端：openvino / tensorrt-llm / vllm / llama.cpp / …（Agent 填） |
| model_id | varchar(128) | 后端内模型标识（如 qwen3-4b） |
| display_name | varchar(128) | 显示名（Agent 填） |
| endpoint | varchar(256) | 推理端点（OpenAI 兼容 base url，如 http://127.0.0.1:8000/v3） |
| parameter_size | varchar(32) | 4B / 7B / …（Agent 填，可空） |
| quantization | varchar(32) | INT4 / INT8 / …（可空） |
| usage | varchar(32) | 对话 / 嵌入 / 绘图 / TTS / 视觉…（可空） |
| size_bytes | bigint | 模型占用（可空） |
| capabilities | varchar(256) | 逗号分隔：chat,vision,embedding,…（可空） |
| notes | varchar(512) | Agent 备注（可空） |
| registered_by | varchar(64) | dsh / openclaw / manual |
| registered_at / updated_at | timestamptz | 默认 now() |

- 唯一索引 `(tool, model_id)` → MCP 注册走幂等 upsert
- `FamilyDbContext` 加 DbSet + 配置；`StartupOrchestratorHostedService` 补幂等 DDL（沿用 EnsureMedicalTables 模式）

## 三、MCP 工具（Baihua.Family）

新增工具类 `BaihuaLocalModelTools`（`Services\Mcp\BaihuaMcpTools.cs` 内）+ `Program.cs` `.WithTools<>()` 注册，共 3 个工具：

| 工具 | 参数 | 行为 |
|---|---|---|
| `baihua_local_model_register` | tool, model_id, display_name, endpoint, parameter_size?, quantization?, usage?, size_bytes?, capabilities?, notes?, registered_by? | upsert（tool+model_id 唯一）；返回注册后 JSON |
| `baihua_local_model_list` | tool?（可选过滤） | 列出注册表全部/按 tool 过滤 |
| `baihua_local_model_unregister` | tool, model_id | 删除一条；返回剩余数 |

注册表服务 `LocalModelRegistryService` 放 `Baihua.Core\Services\`（Core 已有 DbConnections/实体引用，Family 引用 Core 即可；Settings.razor 合并逻辑也要用）。

## 四、最佳提示词（页面内置 + docs 存档）

页面顶部卡片「一键复制提示词」，草稿：

```text
请帮我初始化本机 AI 大模型环境，要求：

1. 环境诊断：检测本机 CPU/GPU/NPU/内存/磁盘与已装推理运行时（OpenVINO、CUDA、
   Vulkan 等），先调用百花 MCP 的 baihua_local_model_list 查看已注册模型，避免重复安装。
2. 方案选型：根据硬件选择最合适的推理后端（如 Intel GPU → OpenVINO/OVMS，
   NVIDIA → TensorRT-LLM/vLLM，纯 CPU → llama.cpp/OVMS）和模型规模（文本对话 /
   嵌入 / 绘图 / 语音，按我能承受的磁盘与显存预算）。
3. 安装启动：安装后端、下载模型、启动服务（OpenAI 兼容端点优先），确认服务就绪
   （/v1/models 可访问、首次推理成功）。
4. 注册到百花：对每个成功运行的模型调用 baihua_local_model_register，逐项填写：
   tool（后端名）、model_id、display_name、endpoint（OpenAI 兼容 base url）、
   parameter_size、quantization、usage（用途）、size_bytes、capabilities、notes、
   registered_by（填你的 agent 名）。
5. 收尾：调用 baihua_local_model_list 确认注册完整，向我汇报安装清单与端点地址。

注意：所有信息以实际探测为准，不要臆测；安装失败要换方案重试；完成后模型清单以
百花注册表为准。
```

存档于 `docs/LOCAL_MODEL_BOOTSTRAP_PROMPT.md`，页面从常量渲染（提示词本身属于"给 Agent 的通用指令"，不含硬编码选型表，符合精减原则）。

## 五、删除清单（推倒重来部分）

| 目标 | 处置 |
|---|---|
| `OpenVinoToolService.cs`（759 行，含显示名/参数量/用途硬编码、config.json 改写） | **删除** |
| `OmsOptions.cs` 中 `OmsModelMap` 静态映射 | **删除**（保留 `OmsOptions.BaseUrl`？不需要——探测没了，一并删；`Baihua.AI` 仅 Configure 无消费者，同步摘引用） |
| `ILocalModelTool.cs` | **删除**（唯一实现即 OpenVinoToolService） |
| `LocalModelDeploymentService` 3 个 partial（含 `_tasks` 死代码） | **删除** |
| `LocalModelDeploymentController` 4 个 partial（含 deploy 死端点） | **删除**，新写极简 `LocalModelRegistryController`：`GET /api/local-models/registry`（只读列表） |
| `LocalModelsCacheWarmupService` + Program.cs 注册 | **删除** |
| `ApiService` local-models 4 方法（downloaded/available/delete/sources） | **删除**，新增 `GetLocalModelRegistryAsync()` |
| `LocalModels.razor` @code 探测/删除弹窗逻辑 | **重写**为纯展示 + 提示词卡片 |
| resx 100+ `LocalModels_*` 死 key | **清理**（保留页面新用 key） |
| `UnloadModelAsync` 3 个业务调用点（AIController.Chat.Stream:146、ChatCompletionsController.Streaming:104、TasksController.Actions:74） | **删除调用点**——本地 provider 现为外部 OpenAI 兼容端点，百花不再管理模型进程生命周期 |

## 六、必须保留（调查确认的共享依赖）

| 依赖 | 原因 |
|---|---|
| `HardwareInfoService`（Core） | CapabilityService 特性开关共用；只解除 Controller/Warmup 依赖 |
| `ILocalRuntimeManager` / `OpenVinoRuntimeManager` | 算力池 ModelStoreController（/mg/model-store/deploy）、MedicalAiService 在用 |
| `ILocalAiConfigService` 链 + `OpenClawTaskService/ConfigService` | HealthController.Fix 与任务系统在用（OpenClaw 服务层存活，仅页面已删） |
| `LocalModelSettingsService` | 下载目录设置（Settings.razor 显示）+ StartupOrchestrator 启动加载 + 测试引用 |

## 七、连带修改

1. **Settings.razor / GetAiConfigProvidersAsync**：原逻辑调 `/available` 把本地模型合并进 AI Provider 列表（端口识别 8000/11434/1234）。改为：从**注册表**读 endpoint 生成 provider 项（注册了才有，Agent 负责准确性）。
2. **openvino-dsh-plugin**（另一仓库）：其 `DIR_TO_OMS`/`OMS_TO_DIR` 硬编码映射与后端 OmsModelMap 是重复维护；改造后插件语义变为「注册表优先、目录扫描辅助」。本仓库内仅留下集成说明，插件侧改动建议后续单独一轮（登记到计划的"后续"）。
3. **AGENTS.md**：更新本地模型段说明（MCP 注册制 + 提示词入口）。

## 八、分阶段实施（每阶段编译验证，页面阶段含 playwright）

| 阶段 | 内容 | 验证 |
|---|---|---|
| 1 | 数据层：实体 + DbContext + 幂等 DDL + `LocalModelRegistryService`（Core） | 编译 + 单元测试 |
| 2 | MCP：3 个工具 + Program.cs 注册 | 编译；用 curl/HTTP 调 /mcp 实测 register→list→unregister |
| 3 | 删探测层：删除清单全部执行 + UnloadModelAsync 调用点清理 + Baihua.AI 引用摘除 | 编译 + Family.Tests + Sdk.Tests 全量 |
| 4 | 页面重写：LocalModels.razor 纯展示 + 提示词卡片 + RegistryController + Settings.razor provider 合并改造 | 编译 + **playwright**（页面渲染/提示词复制按钮/表格显示注册数据） |
| 5 | 文档：提示词存档 + AGENTS.md 更新 + DSH 插件对齐说明；全量编译 + 测试 | 提交推送 |

## 九、风险与边界

- **注册表为空时页面**：显示引导文案（"尚无注册模型，复制提示词交给 Agent 初始化"），不留旧探测回退。
- **数据可信度**：模型信息由 Agent 填写，百花不做校验（最多格式校验 endpoint URI）；页面标注"由 Agent 注册"。
- **嵌入模型消费方**：RAG/嵌入调用目前走 `EmbeddingModelId => "qwen3-4b"` 等 OmsModelMap 常量——需确认真实嵌入调用路径（Baihua.AI 侧）是否受删映射影响，实施阶段 3 时逐一核对，若被引用则改为从注册表按 usage=embedding 查询 endpoint。
- **OpenClaw shim 链**：LocalAiConfigService.Sync 与 openclaw provider 配置沿用现状不动。