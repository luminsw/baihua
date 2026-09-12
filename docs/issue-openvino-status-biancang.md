# Issue：本地模型页 OpenVINO 运行状态显示错误 —— BianCang 未显示为"运行中"

> 状态：待修复（由其他 AI 接手）
> 报告日期：2026-08-29
> 环境：Windows 11，Intel Arc 核显，OVMS（OpenVINO Model Server）常驻托管

> ⚠️ **路径为合并前快照**：文中 `services/Baihua.Family/…` 现为 `services/Baihua.Modules.Family/…`
> （commit `aa053f1` 三服务合一，后端统一到 `Baihua.Server`，单一 `baihua` 库）；
> `services/Baihua.AI.Provider.OpenVino/…` 路径不变。正文按当时状态保留。

---

## 一、问题现象

百花"本地模型"页（`/local-models`）中，OpenVINO 相关区域的模型运行状态显示与实际不符：

1. **概览页"▶️ 运行中模型"区域**：只显示 1 个模型 `Qwen2.5-VL-7B-Instruct (INT4)`。
   **扁仓 BianCang 缺失**（同时缺失的还有 OVMS 里注册的对话模型 `qwen2.5`、嵌入模型 `bge-small-zh`）。
2. **OpenVINO Tab 的模型目录表**：BianCang 行存在（没有从列表里消失），
   但状态徽标显示为 **"已下载"（未运行）**，而不是"运行中"。
3. **目录区状态非实时**：页面加载后不会自动轮询运行状态，
   必须手动点"刷新"或触发其他操作才会更新。

## 二、实际运行状态（实测证据，2026-08-29 11:58）

- OVMS Windows 服务（`ovms`）运行中，监听 `0.0.0.0:8000`。
- `GET http://127.0.0.1:8000/v1/models` 返回 **4 个已注册模型**：

  ```json
  { "data": [
    { "id": "bge-small-zh" },
    { "id": "biancang" },
    { "id": "qwen2.5" },
    { "id": "qwen2.5-vl-7b" }
  ] }
  ```

- `GET http://127.0.0.1:8000/v1/models/biancang` → 200，模型可用。
- OVMS 配置 `C:\Users\lumin\.baihua\models\config.json`：

  ```json
  {
    "model_config_list": [
      { "config": { "name": "qwen2.5",       "base_path": "Qwen2.5-7B-Instruct-int4-ov" } },
      { "config": { "name": "qwen2.5-vl-7b", "base_path": "Qwen2.5-VL-7B-Instruct-int4-ov" } },
      { "config": { "name": "bge-small-zh",  "base_path": "bge-small-zh-v1.5" } },
      { "config": { "name": "biancang",      "base_path": "BianCang-Qwen2.5-7B-Instruct" } }
    ]
  }
  ```

- 模型目录 `C:\Users\lumin\.baihua\models\BianCang-Qwen2.5-7B-Instruct\` 存在，
  `openvino_model.bin`（7.1 GB）齐全，目录最后修改 2026-08-29 05:53（当天刚转换/更新）。
- OVMS 懒加载：模型已注册、可推理，首次请求时才真正编译加载进内存。

**结论：BianCang 模型"在运行/可用"（OVMS 已托管），但页面判定逻辑把它显示为未运行。**

## 三、实际 API 输出（页面数据来源）

`GET http://127.0.0.1:8788/api/local-models/openvino/catalog`（bh-family 服务）关键字段：

| id | installed | isRunning | port | 说明 |
|---|---|---|---|---|
| biancang-instruct | **true** | **false** ❌ | null | 应为运行中（OVMS 已注册 biancang） |
| kokoro-82m | true | true | 8001 | 正确（独立 TTS 服务，端口探测） |
| qwen2.5-vl-7b | true | false ❌ | null | OVMS 已注册 qwen2.5-vl-7b，也应运行中 |
| qwen2.5-14b | true | false | null | 本机目录存在，未注册 OVMS，合理 |
| 其余 | — | — | — | 未下载/未注册，合理 |

`GET http://127.0.0.1:8788/api/local-models/running?forceRefresh=true` 只返回 1 条：

```json
{
  "toolId": "openvino", "modelName": "7b",
  "displayName": "Qwen2.5-VL-7B-Instruct (INT4)",
  "status": "running", "family": "Qwen2.5-VL"
}
```

**附带问题**：OVMS 注册的 7B 对话模型 `qwen2.5`（`Qwen2.5-7B-Instruct-int4-ov`，目录已下载）
在 `OpenVinoCatalog` 里**没有对应条目**（目录里的 qwen2.5 条目是 14B 版），
即存在"已托管运行但目录列表里找不到"的模型。

## 四、根因分析

### 根因 1：目录表运行状态只认"自起子进程"，不认 OVMS 注册

- `services/Baihua.Family/Controllers/AI/LocalModelDeploymentController.OpenVino.cs`
  `GetOpenVinoCatalog()`（约 10–60 行）：调用 `_openVinoRuntime.GetInstalledModels()`
  合并安装/运行状态。
- `services/Baihua.AI.Provider.OpenVino/OpenVinoRuntimeManager.cs`
  `GetInstalledModels()`（约 40–70 行）：扫描 `ModelRoot` 目录找
  `openvino_model.bin`/`openvino_language_model.bin`；
  `IsRunning` 只看内部 `_running` 字典（仅 `StartAsync()` 拉起的 Python 子进程）。
- `MergeRemoteServed()`（约 75–110 行）只有在环境变量
  `OPENVINO_LLM_URL`/`OPENVINO_HOST_URL` 设置且可达时才合并远端状态；
  **本地 OVMS 部署未设置该变量 → 合并逻辑不生效**。
- 背景：2026-08-29 前已把 OpenVINO 推理统一切到 OVMS 常驻托管
  （commit `1bce309`「扁仓模型路由到本地 OVMS」、`cff23ca`「扁仓 INT4_SYM 量化 + OVMS 模型路由检测」），
  但目录页状态判定仍是切换前的旧逻辑。

### 根因 2：概览"运行中模型"只枚举视觉模型

- `services/Baihua.AI.Provider.OpenVino/OpenVinoToolService.cs`
  `GetRunningModelsAsync()`（约 313–337 行）：
  遍历 `DistinctModels()`（配置 `OpenVinoToolOptions.Models`，默认只有
  `{ Id = "7b", Name = "Qwen2.5-VL-7B-Instruct (INT4)" }`），
  经 `OmsModelMap.VisionModelId()` 全部映射为 `qwen2.5-vl-7b`。
  → LLM（qwen2.5、biancang）与嵌入（bge-small-zh）永远不会出现在运行中列表。
- `GetOmsModelIdsAsync()`（约 232–254 行）已经具备探测 OVMS `/v1/models` 的能力，
  但没有被用于枚举全部注册模型。
- `services/Baihua.AI.Provider.OpenVino/OmsOptions.cs` `OmsModelMap`：
  已有 `ChatModelId => "qwen2.5"`、`EmbeddingModelId => "bge-small-zh"`，
  但没有 biancang 的映射。

### 根因 3：前端目录状态不轮询

- `services/Baihua.Web/Components/LocalModels/OpenVinoTab.razor`：
  `LoadOpenVinoAsync()` 仅在 Tab 打开时加载一次；
  `EnsureDownloadTimer()` 的 3 秒定时器**只在存在活跃下载任务时**运行，仅刷新下载进度。
- `services/Baihua.Web/Pages/LocalModels.razor`：
  `RefreshRunningModels()` 按需调用；`StartStatusPolling()` 的 5 秒定时器
  **只轮询部署任务**（`activeTasks`），不轮询运行模型。

## 五、建议修复方案

1. **目录状态对接 OVMS 注册**：
   `GetOpenVinoCatalog()`（或 `OpenVinoRuntimeManager.GetInstalledModels()`）中，
   探测 `{OmsOptions.BaseUrl}/v1/models`（复用 `GetOmsModelIdsAsync` 的写法），
   把 OVMS 注册的模型 id 与目录条目建立映射（目录名 ↔ OVMS id）：
   - `BianCang-Qwen2.5-7B-Instruct` ↔ `biancang`
   - `Qwen2.5-VL-7B-Instruct-int4-ov` ↔ `qwen2.5-vl-7b`
   - `Qwen2.5-7B-Instruct-int4-ov` ↔ `qwen2.5`（注意：此模型当前无目录条目，见"附带问题"）
   - `bge-small-zh-v1.5` ↔ `bge-small-zh`（目录表是否需要展示嵌入模型由产品决定）
   命中注册即 `IsRunning = true`，端口取 OVMS 端口（8000）。

2. **运行中模型枚举扩展**：
   `OpenVinoToolService.GetRunningModelsAsync()` 改为遍历 OVMS `/v1/models` 全部注册模型，
   映射出显示名（Qwen2.5-VL-7B、Qwen2.5-7B、BianCang、bge-small-zh），
   而不是只枚举 `_options.Models` 里的视觉模型。
   注意保持 `RunningModelDto` 字段完整（SizeBytes 可从目录算，或为 OVMS 托管模型传 0）。

3. **目录状态实时化**：
   OpenVINO Tab 可见期间加一个低频轮询（如 15–30 秒）刷新 `openVinoCatalog` 的运行状态，
   或复用现有 `_downloadTimer` 机制扩展到"有 OVMS 运行状态需要显示"时也轮询。
   概览页运行中模型同理（可纳入 5 秒轮询或独立低频轮询）。

4. **补充目录条目**（可选但建议）：
   在 `services/Baihua.Contracts/LocalModels/OpenVinoCatalog.cs` 增加
   `Qwen2.5-7B-Instruct-int4-ov`（OVMS 对话模型 `qwen2.5`）条目，
   否则该模型"已托管运行但目录不可见"。

## 六、验收标准

- [ ] OpenVINO Tab 目录表中：BianCang 显示"运行中"（绿色徽标，端口 8000 或 OVMS 标识）；
- [ ] 概览页"运行中模型"包含 BianCang（如产品需要，也包含 qwen2.5 对话模型 / bge 嵌入模型）；
- [ ] OVMS 注册变化（新增/移除模型）在页面无需手动刷新即可在 30 秒内反映；
- [ ] 回归：Qwen2.5-VL-7B 仍显示运行中；Kokoro TTS（8001）状态不受影响；
      下载/停止/删除、下载进度轮询功能正常；
- [ ] `dotnet build` 通过（services 解决方案），无新增警告/错误。

## 七、相关文件清单

| 文件 | 作用 |
|---|---|
| `services/Baihua.Family/Controllers/AI/LocalModelDeploymentController.OpenVino.cs` | 目录/已安装/下载 API；`GetOpenVinoCatalog` 状态合并 |
| `services/Baihua.AI.Provider.OpenVino/OpenVinoRuntimeManager.cs` | 模型目录扫描 + 进程级运行状态；`MergeRemoteServed` |
| `services/Baihua.AI.Provider.OpenVino/OpenVinoToolService.cs` | OVMS 探测（`GetOmsModelIdsAsync`）、`GetRunningModelsAsync`（仅视觉） |
| `services/Baihua.AI.Provider.OpenVino/OmsOptions.cs` | `OmsModelMap`：vision/chat/embedding id 映射（缺 biancang） |
| `services/Baihua.Contracts/LocalModels/OpenVinoCatalog.cs` | 静态目录条目（含 biancang-instruct；缺 7B 对话模型） |
| `services/Baihua.Web/Components/LocalModels/OpenVinoTab.razor` | OpenVINO Tab 前端；目录状态非实时 |
| `services/Baihua.Web/Pages/LocalModels.razor` | 概览页；运行中模型按需刷新 |
| `services/Baihua.Web/Services/ApiService.FamilyTools.cs` | 前端 API 调用封装 |

## 八、复现步骤（修复后验证用）

1. 启动 OVMS 服务（Windows 服务 `ovms`，端口 8000）与百花服务（`out/native` 下
   `bh-family.exe` → 8788、`bh-webui.exe` → 5177）。
2. 浏览器打开 `http://127.0.0.1:5177/local-models`。
3. 观察概览页"运行中模型"：预期出现 BianCang，实际只有 Qwen2.5-VL-7B。
4. 切到 OpenVINO Tab：BianCang 行状态为"已下载"，预期为"运行中"。
5. 保持页面 1 分钟不操作：状态不会自动变化（无轮询）。
