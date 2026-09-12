# OpenClaw 本地 AI 配置 - OpenVINO 集成
> 状态：✅ **真实环境端到端验证通过（2026-08-08 第二轮优化）**。第一版（bf7eef1）的 API 链路（Contracts → Core 服务 → Family 接口 → WebUI 前端）已打通；本轮基于真实机器（Intel Core Ultra 5 225H + Arc 130T + NPU，Python 3.12 + openvino_genai 2026.2.1）完成「检测 → 自动启动 → 健康检查 → 同步模型 → OpenAI 兼容推理」全链路实测。
> 本文档为功能设计、实现记录、问题与待完善项汇总。

> ⚠️ **文件清单为合并前快照**：表中 `Baihua.Family` 下的 `Services/OpenClaw/…` 现位于
> `services/Baihua.Modules.Family/Services/OpenClaw/…`，宿主的 DI/端点注册在 `services/Baihua.Server/`；
> `Baihua.AI.Provider*` 路径不变（commit `aa053f1` 三服务合一）。正文按当时记录保留。

---

## 1. 背景

百花 WebUI 的 OpenClaw 页面（`/openclaw`）已支持四类本地 AI Provider 的配置管理：

| Provider | 说明 | 独立配置文件 |
|----------|------|-------------|
| Ollama | 最常用的本地推理服务，自动检测端口 | 无（走 openclaw.json 主配置） |
| LM Studio | Mac/Win 主流桌面推理 GUI | 无（走 openclaw.json 主配置） |
| llama.cpp | 原生 C++ 推理，支持 CPU/GPU | `llama-config.json`（独立） |
| OpenVINO | Intel CPU/GPU/NPU 加速推理（OpenAI 兼容） | `openvino-config.json`（独立） |

OpenVINO 与 llama.cpp 架构对齐：**独立 JSON 配置文件 + CLI `config set` 同步双写**。

### 1.1 真实运行方式（重要修正）

第一版文档假设存在 `openvino-genai-server` 二进制（scoop shims），**该假设不成立**。真实环境（已实测）：
- openvino_genai 是 **Python 包**（`pip install openvino-genai`），**不附带任何 OpenAI 兼容 HTTP server**
- 百花随发布自带 **`openvino_llm_server.py`**（OpenAI 兼容推理服务，见 §4.4），由「检测」按钮自动拉起
- 模型为 OpenVINO 优化目录（含 `openvino_language_model.xml` / `openvino_tokenizer.bin` 等）

---

## 2. 修改文件清单

共 9 个文件（含本轮优化）：

| 项目 | 文件 | 改动 |
|------|------|------|
| **Baihua.Contracts** | `OpenClaw/OpenClawDtos.cs` | `OpenClawOpenVinoConfigDto`；扩展 `OpenClawLocalAiConfigDto.OpenVino` / `SaveOpenClawLocalAiConfigRequest.OpenVino`；`LocalAiServiceStatusDto` 增加 `Devices`（探测设备）、`CommandLine`（启动命令预览） |
| **Baihua.Family** | `Services/OpenClaw/OpenClawConfigService.cs` | OpenVINO 配置路径获取、JSON 解析、构建；**修复 CLI 同步根因**（cmd shim + batch-file，见 §6 #5） |
| **Baihua.Family** | `Services/OpenClaw/LocalAiConfigService.Scan.cs` | `ScanOpenVinoModelsAsync` 重写：服务探测 / 子目录多模型扫描 / config.json 元信息读取 |
| **Baihua.Family** | `Services/OpenClaw/LocalAiConfigService.Detect.cs` | `DetectAndStartOpenVinoAsync` 重写：真实启动命令、python 自动探测、设备枚举 |
| **Baihua.Family** | `Services/OpenClaw/LocalAiConfigService.Sync.cs` | 同步到 openclaw.json providers（走修复后的 CLI 调用） |
| **Baihua.Family** | `Services/OpenClaw/OpenClawModelProfileService.cs` | OpenVINO 模型收集改为复用 `ScanLocalModelsAsync`（多模型 + id 对齐） |
| **Baihua.Family** | `Baihua.Family.csproj` | 打包 `openvino_llm_server.py` 随发布拷贝 |
| **Baihua.AI.Provider** | `LocalVision/openvino_llm_server.py` | OpenAI 兼容推理服务（VLMPipeline 自动判别、懒加载、chunked 兼容） |
| **Baihua.AI.Provider** | `Baihua.AI.Provider.csproj` | 本地 AI 引擎（LlamaSharp/ONNX/OpenVINO），打包 py 脚本随发布拷贝 |
| **Baihua.Web** | `Pages/OpenClaw.razor` + `Localization/SharedResources*.resx` | OpenVINO 卡片：Device 下拉动态化、启动命令预览、文案修正 |

---

## 3. 数据结构

### 3.1 `OpenClawOpenVinoConfigDto`（Contracts）

```csharp
public class OpenClawOpenVinoConfigDto
{
    public bool   Enabled      { get; set; }            // 是否启用
    public string BinaryPath   { get; set; } = "";      // server 脚本路径（.py）或可执行命令；留空自动探测
    public string ModelPath    { get; set; } = "";      // 模型目录（含 .xml/.bin/tokenizer）
    public string BaseUrl      { get; set; } = "http://localhost:8000";
    public int    Port         { get; set; } = 8000;
    public string Device       { get; set; } = "CPU";   // CPU / GPU / NPU / AUTO（探测回填）
    public int    ContextSize  { get; set; } = 4096;
    public string ApiType      { get; set; } = "openai-completions";
    public string ExtraArgs    { get; set; } = "";      // 透传到 server 脚本的附加参数，如 --max-tokens 256
}
```

### 3.2 独立配置文件（落盘）

路径：`%USERPROFILE%\.openclaw\openvino-config.json`

```json
{
  "enabled": true,
  "binaryPath": "",
  "modelPath": "C:\\Users\\lumin\\.openclaw\\models\\Qwen2.5-VL-7B-Instruct-int4-ov",
  "baseUrl": "http://localhost:8000",
  "port": 8000,
  "device": "CPU",
  "contextSize": 4096,
  "apiType": "openai-completions",
  "extraArgs": "--max-tokens 256"
}
```

### 3.3 openclaw.json 同步结构（标准 provider schema）

`openclaw config set models.providers.openvino` 只写入 OpenClaw schema 认可的标准键（自定义字段留在 openvino-config.json，见 §6 #5）：

```json
{
  "baseUrl": "http://localhost:8000",
  "api": "openai-completions",
  "models": [
    {
      "id": "qwen2-5-vl-7b-instruct-int4-ov",
      "name": "Qwen2.5-VL-7B-Instruct-int4-ov",
      "input": ["text", "image"],
      "contextWindow": 4096
    }
  ]
}
```

---

## 4. 核心流程

### 4.1 保存（`POST /api/openclaw/local-ai`）

```
SaveOpenClawLocalAiConfigAsync
    │
    ├─ Ollama / LM Studio  ──► 直接 openclaw config set（无独立文件）
    │
    ├─ llama.cpp ──► ① 写 llama-config.json（必填）
    │                ② CLI config set（尽力而为，失败不影响返回）
    │
    └─ OpenVINO  ──► ① 写 openvino-config.json（必填）
                     ② CLI config set（尽力而为，失败不影响返回）
```

**关键修复（2026-08-08）**：有独立配置文件的 provider（llama.cpp、OpenVINO），**JSON 落盘成功即返回 HTTP 200**；CLI `openclaw config set` 失败仅记日志，不影响 HTTP 状态码。修复后 CLI 调用在真实环境中已能成功（见 §6 #5），此「尽力而为」降级逻辑保留作为兜底。

### 4.2 读取（`GET /api/openclaw/local-ai`）

从三份来源读取后组装为 `OpenClawLocalAiConfigDto`：
1. `openclaw.json` 主配置 → Ollama/LM Studio
2. `llama-config.json` → llama.cpp
3. `openvino-config.json` → OpenVINO（不存在则返回全部默认值 + `Enabled=false`）

### 4.3 模型扫描（`LocalAiConfigService.Scan.cs`）

`ScanOpenVinoModelsAsync` 按以下优先级：
1. 若 `BaseUrl` 可访问 → 调 `/v1/models` 获取实时模型列表（OpenAI 兼容 server）
2. 否则若 `ModelPath` 目录存在 → **扫描目录本身 + 一级子目录**，凡含 `openvino_language_model.xml` 的目录均识别为一个模型（支持多模型父目录）；从 `config.json` 读 `model_type` / `architectures`（VL 判定）、`openvino_config.json` 读 `dtype`（精度），组装可读名称
3. 都不可用 → 空列表

**模型 id 一致性约定**：本地推导的 id 与 `openvino_llm_server.py` 的 `model_id()` 完全一致（目录名小写、`.` → `-`），保证同步到 openclaw.json 后 OpenClaw 实际请求能找到模型。

### 4.4 OpenAI 兼容推理服务（`openvino_llm_server.py`，新增）

随百花发布的 Python 常驻服务（复用 `vision_server.py` 已验证的实现经验）：

```
启动（由 Detect 自动拉起或手动）：
  python openvino_llm_server.py --model <模型目录> --device <CPU/GPU/NPU> \
         --port <端口> --max-context-size <上下文> [--max-tokens <默认生成上限>]

端点:
  GET  /health              -> {"ok":true,"model":"...","device":"...","vl":true}
  GET  /v1/models           -> OpenAI 格式模型列表
  POST /v1/chat/completions -> OpenAI 格式（纯文本；VL 模型支持 image_url base64）
```

设计要点（踩坑经验固化）：
- **VL 模型必须用 `VLMPipeline`**：目录含 `openvino_vision_embeddings_model.xml` 时自动选用；`LLMPipeline` 加载 VL 目录会报 `Port for tensor name input_ids was not found`（已实测）
- 纯文本对话同样走 VLMPipeline（Qwen2.5-VL 支持无图输入，已实测）
- openvino-genai 2026.2 的 `images` 参数必须是 `[ov.Tensor(NHWC uint8)]` 扁平列表，不接受文件路径/PIL/嵌套列表（与 vision_server.py 同款实现）
- 先 `import numpy` 再 `import openvino`（pybind 依赖顺序）
- 兼容 .NET HttpClient 默认的 chunked 请求体
- 模型懒加载 + 常驻内存（冷加载 10-30s，之后每次请求毫秒级排队）

### 4.5 检测与启动（`LocalAiConfigService.Detect.cs`）

`DetectAndStartOpenVinoAsync`：
1. 探测可用推理设备（`python -c "import openvino as ov; print(ov.Core().available_devices)"`）→ 回填 `Devices`，前端下拉框动态显示
2. 探测端口健康（`GET {BaseUrl}/v1/models`）
3. 未启动则构造命令行：
   - BinaryPath 留空 → 自动探测：`python` 可用且能 `import openvino_genai` → 用随发布拷贝的 `openvino_llm_server.py`
   - BinaryPath 为 `.py` → 用 python 运行；其他 → 按可执行文件/命令处理
4. 启动后轮询就绪（先等 8s 冷加载，再每秒探测最多 60s）
5. 返回 `DetectionResult { Running, Endpoint, Models, Devices, CommandLine, Error }`

### 4.6 同步到 OpenClaw 可用模型

`SyncOpenVinoToOpenClawAsync` 调用 `openclaw config set models.providers.openvino <标准schema JSON>`，`OpenClawModelProfileService` 收集后前端默认模型下拉框以 `openvino/{modelId}` 格式出现。**CLI 同步修复后真实可用**（见 §6 #5）。

---

## 5. 前端 UI

`OpenClaw.razor` 中 OpenVINO 卡片（llama.cpp 卡片下方，结构对称）：

| 控件 | 绑定字段 | 说明 |
|------|---------|------|
| 开关 | `_openVinoConfig.Enabled` | 启用/禁用 |
| 状态徽标 | 自动计算 | 绿色「已检测到服务」/ 黄色「未检测到」/ 红色「启动失败」 |
| BinaryPath 输入框 | `_openVinoConfig.BinaryPath` | server 脚本（.py）或命令；留空自动探测随发布脚本 |
| ModelPath 输入框 + 浏览 | `_openVinoConfig.ModelPath` | 模型目录（含 openvino_language_model.xml 的那一级） |
| Device 下拉 | `_openVinoConfig.Device` | **动态**：检测到设备（如 CPU/GPU/NPU）+ 静态兜底（AUTO） |
| Port / BaseUrl / ContextSize | 对应字段 | 默认 8000 / localhost:8000 / 4096 |
| ExtraArgs 文本框 | `_openVinoConfig.ExtraArgs` | 附加参数，如 `--max-tokens 256` |
| 启动命令预览 | `status.CommandLine` | 检测后显示实际执行命令，可复制到 cmd 手动运行排查 |
| 「检测」按钮 | 调 `detect-openvino` API | 探测服务状态 + 设备枚举，未启动则自动拉起 |
| 「同步模型」按钮 | 调 `sync-models/openvino` API | 扫描并写入 openclaw.json |

标题：**本地 AI 配置（Ollama / LM Studio / llama.cpp / OpenVINO）**。

---

## 6. 遇到的问题与修复

| # | 问题 | 现象 | 根因 | 修复 |
|---|------|------|------|------|
| 1 | **Save 返回 HTTP 400 误报** | 前端输入合法保存却报失败；数据已落盘 | `SaveLocalAiConfigAsync` 把 CLI 调用失败当致命错误 | 有独立 JSON 的 provider（llama.cpp、OpenVINO）JSON 写成功即返回 true；CLI 失败仅记日志 |
| 2 | **默认模型下拉框点不开** | 首次进入无本地模型，AvailableModels 为空数组 | 模型收集漏了云端 provider 和 OpenVINO | 补云端解析 + OpenVINO 分支 + 前端空状态引导 |
| 3 | **Playwright 访问被重定向到登录** | 打开 `/openclaw` 跳 `/login` | 管理面板基于 OS 权限授权 | 先调 `/api/auth/cli-token` 取一次性 token 拼 URL |
| 4 | **暗色模式下部分卡片亮色硬编码** | 暗色下亮白底刺眼 | 组件直接写 `bg-light` / `bg-white` | `<style>` 中加 `[data-bs-theme="dark"]` 覆盖，走全局暗色调色板 |
| 5 | **`openclaw config set` 始终失败（误判为 Node 版本低）** | 日志 `Win32Exception: 系统找不到指定的文件`；第一版文档记为「Node 版本不足」 | ① `openclaw` 是 npm 安装的 **.cmd shim**，.NET `Process.Start(UseShellExecute=false)` 无法直接启动 batch 文件（CreateProcess 不解析 .cmd/.bat）；② 内联 JSON 经 cmd 传参时引号转义不可靠（`\"` / `^"` 均破坏 JSON） | ① 用 `cmd.exe /c` 包装启动；② 改 **`--batch-file` 临时文件**传递 JSON（完全避开引号转义）；非 Windows 保留 ArgumentList 直启。修复后 sync 实测成功 |
| 6 | **`openclaw config set` schema 校验拒绝自定义键** | `Unrecognized keys: "modelPath", "binaryPath", "device"...` | `models.providers.<id>` 有严格 schema，只接受 `baseUrl`/`api`/`models[]` | `BuildOpenVinoProviderJson` / `BuildLlamaCppProviderJson` 只写标准键；自定义字段留在独立配置文件（llama-config.json / openvino-config.json） |
| 7 | **第一版启动命令虚构（`openvino-genai-server` 不存在）** | 检测按钮必然启动失败 | 文档假设了不存在的二进制；openvino_genai 是 Python 包且无内置 HTTP server | 新增随发布 `openvino_llm_server.py`；Detect 自动探测 python+openvino_genai 并正确组装命令；启动命令预览辅助排查 |
| 8 | **LLMPipeline 加载 VL 模型报错** | `Port for tensor name input_ids was not found` | VL 模型（含视觉编码器）必须用 `VLMPipeline` | server 脚本按目录是否含 `openvino_vision_embeddings_model.xml` 自动判别 pipeline 类型（已实测） |
| 9 | **单模型扫描**（第一版 P1） | 父目录含多个 OV 子模型时只识别 1 个 | 只按目录名推导 | 扫描目录 + 一级子目录，凡含 `openvino_language_model.xml` 均识别；读 config.json 补名称/类型/精度 |
| 10 | **设备静态枚举**（第一版 P1） | 下拉只有 CPU/GPU/NPU 固定 3 项 | 未探测真实设备 | Detect 时 `ov.Core().available_devices` 探测并回填；前端动态渲染 + 静态兜底 |

---

## 7. 待完善项

| 优先级 | 项 | 说明 |
|--------|----|------|
| **P1** | 模型大小/精度徽标展示 | Scan 已读取 `openvino_config.json` 的 `dtype`（如 int4），前端模型表可加「精度」列（当前已解析未展示） |
| **P1** | NPU 驱动检测 | 启用 NPU 前检测 Intel AI Boost 驱动（WMI `Win32_PnPEntity`），未安装给出指引 |
| **P2** | 下载管理集成 | 与 Ollama 的 `LocalAI.DownloadDirectory` 对齐，OV 模型「下载 → 校验 → 自动放 ModelPath」一体化 |
| **P2** | 图片输入 WebUI 演示 | server 已支持 image_url base64（VL），OpenClaw 卡片可加「测试对话」面板验证多模态 |
| **P3** | GPU 显存不足自动降级 | 7B INT4 约 4.8GB 可入 Arc 16GB；更大模型需在 GPU OOM 时自动回退 CPU/混合 |
| **P3** | 进程生命周期管理 | 目前 Detect 启动的 python 进程独立于 Family 存活；可记录 PID 并在停用时提供「停止服务」 |

---

## 8. API 快速参考

| 方法 | 路径 | 说明 |
|------|------|------|
| GET  | `/api/openclaw/default-model` | 获取默认模型（CurrentModel + AvailableModels） |
| POST | `/api/openclaw/default-model` | 保存默认模型（写入 `agents.defaults.model.primary`） |
| GET  | `/api/openclaw/local-ai` | 获取本地 AI 配置（Ollama/LM/llama/OpenVINO 四个 DTO） |
| POST | `/api/openclaw/local-ai` | 保存本地 AI 配置（按 §4.1 规则双写） |
| POST | `/api/openclaw/local-ai/detect-openvino` | 检测 OpenVINO 服务状态，未启动则自动拉起（返回 Devices/CommandLine） |
| POST | `/api/openclaw/local-ai/sync-models/openvino` | 扫描模型并同步到 openclaw.json providers |
| GET  | `/api/auth/cli-token` | 获取一次性 CLI 登录 token（调试/自动化用） |

---

## 9. 验证记录

### 9.1 第一轮（bf7eef1，2026-08-08）：API 链路

**RoundTrip 全字段验证（PASS）**：
- POST 任意合法 OpenVINO 配置 → HTTP 200；GET 读取 9 字段与写入值逐字节一致
- 可用模型列表包含 `openvino/qwen25-7b-int4-ov`；CurrentModel 保持 `deepseek/deepseek-v4-flash`
- 环境恢复验证（PASS）：删除测试假文件后无残留

### 9.2 第二轮（本轮，2026-08-08）：真实环境端到端

真实机器：Intel Core Ultra 5 225H / Arc 130T 16GB / NPU，Python 3.12.10 + openvino_genai 2026.2.1，模型 `Qwen2.5-VL-7B-Instruct-int4-ov`。

| 验证项 | 结果 |
|--------|------|
| `openvino_llm_server.py` 启动 + `/health` + `/v1/models` | ✅ PASS（vl=true, model id 正确） |
| `/v1/chat/completions` 纯文本推理 | ✅ PASS（`2+2` → `4`；`Reply with exactly: OK` → `OK`） |
| VLMPipeline vs LLMPipeline 判别 | ✅ PASS（LLMPipeline 加载 VL 报错已复现并规避） |
| Detect API（POST local-ai-detect） | ✅ PASS（16.9s 完成：探测→自动启动→就绪；返回 Devices=[CPU,GPU,NPU] + 完整启动命令） |
| 模型扫描 API | ✅ PASS（运行中走 /v1/models；未运行走目录扫描） |
| 同步 API + `openclaw config set` | ✅ PASS（batch-file 方案；openclaw.json 写入标准 schema） |
| RoundTrip 读取 | ✅ PASS（openvino-config.json 全字段 + openclaw.json 标准结构并存） |
| 测试套件 | ✅ PASS（Family.Tests 867/867） |
| 环境恢复 | ✅ PASS（测试 provider/文件已清理，openclaw.json 恢复原状，config validate 通过） |
