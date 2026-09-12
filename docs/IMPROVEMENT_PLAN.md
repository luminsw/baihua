# 百花插件体系优化计划

> 创建时间：2026-08-23  
> 状态：高优先级（1-5）、中优先级（6-9）、低优先级（10-12）均已执行完成  
> 入口说明：本文件供新会话继续执行，任务按优先级排序。已完成项带 ✅ 与执行记录。
>
> **2026-08-28 更新**：原独立 `baihua-mcp-server` 仓库已迁移到百花内置 `/mcp` 端点（streamable-http，`ModelContextProtocol.AspNetCore`），仓库已删除。下方涉及 `baihua-mcp-server` 的条目为迁移前历史记录，工具名 `mcp__baihua__*` 不变。
>
> **2026-09-12 补充**：`aa053f1` 三服务合一后，`/mcp` 端点由唯一后端 `Baihua.Server`(8788) 提供（文中「`Baihua.Family` 内置」为当时叫法）。

## 执行进度（2026-08-23）

- ✅ **1. `bh_git_commit_push` 仓库定位失败** — `ops.js` 改为 `detectRepoRoot()` 推断（常见路径
  `~/src/baihua`、BAIHUA_HOME、bhCommand 逐级），不再回退 `process.cwd()`；显式 `gitRepo`
  直接使用并经 `verifyGitRepo()`（`git rev-parse --show-toplevel` 校验仓库根）快速失败。
  已单测验证（自动定位/显式配置/子目录/不存在目录四种路径）。插件提交 `9027483`。
- ✅ **2. `bh_dsh_restart` 重启不可靠** — Windows 改为**计划任务方案**：写临时 ps1 →
  `Register-ScheduledTask` → `Start-ScheduledTask`（固定任务名 `dsh-web-restart`，脚本内自删）。
  detached 子进程方案（父进程被强杀时子进程不可靠）弃用。已端到端验证：重启后 DSH PID/
  启动时间确实变化，30 秒内恢复，桥接正常（`/dsh-bridge/status` 200）。
- ✅ **3. 插件安装规范化** — 三个插件已 `dsh plugin --profile web add github:luminsw/<repo>`
  安装（`profiles/web/package.json` 出现依赖，bundle 自动挂层）；`~/.dsh/cordis.patch.yml`
  三个 insert 改为按 id `config` 覆盖；`dsh plugin ls` 可列出/管理；`--dump-config` 正常；
  旧 `profiles/node_modules` 副本已清理。
- ✅ **4. 收紧 DSH 权限** — `~/.dsh/settings.yaml` 默认预设 `danger-full-access` →
  `workspace-write`（沙箱限定工作区 + 审批 ask，需全量权限时按会话临时切换）；插件侧为
  `bh_start/stop/restart`、`bh_build*`、`bh_update`、`bh_git_commit_push`、
  `bh_dsh_restart`、`bh_bootstrap` 挂 `tools/pre-execute` 审批门（ask 时 UI 确认，never 时自动拒绝）。
- ✅ **5. 更新过时文档** — `baihua-dsh-plugin/README.md`、`baihua/README.md`、`baihua/AGENTS.md`、
  `baihua/docs/DSH_INTEGRATION.md` 均已更新：插件职责=桥接/运维/绘图，数据工具统一指向
  `baihua-mcp-server`（`mcp__baihua__*`），补充桥接共享密钥与高危工具审批门说明。
- ✅ **6. `baihua-mcp-server` 支持远端鉴权** — `baihua.js` 支持 `BAIHUA_TOKEN`（及按服务
  `BAIHUA_VAULT_TOKEN`/`BAIHUA_FAMILY_TOKEN` 覆盖）；回环免 token 保持兼容，非回环未配置
  token 直接返回明确错误；请求头同时带 `X-Server-Token` + `Authorization: Bearer`；
  错误信息透出。已单测（回环/远端强制/头注入）。提交 `8980413`。
- ✅ **7. ComfyUI 能力接口补全** — `ComfyUiClient` 新增 `GetModelNamesAsync`（通用 loader
  查询）+ UNET/CLIP/VAE 便捷方法；`ComfyDrawService.GetCapabilitiesAsync` 汇总能力；
  `/mg/pool/v1/draw/capabilities` 现返回图像/视频 checkpoint 全集与 UNet/CLIP/VAE 模型清单
  （已验证列出 `z_image_turbo_bf16`、`qwen_3_4b`、`ae` 等）；本机 `/api/draw/status` 同步补全；
  新增 DSH 工具 `baihua_draw_status` 展示模型清单（capabilities 响应 PascalCase→camelCase 归一化）。
  提交 `e2eb633`、`166a643`。
- ✅ **8. `baihua_draw` 高级参数增强** — `DrawImageRequest`/`DrawVideoRequest` 增加
  `Seed`/`Cfg`/`Sampler`/`Scheduler`（图片另有既有 `UnetName`/`ClipName`/`VaeName`）；
  `ComfyWorkflowBuilder` 三构建器透传（默认值：sd15=7/euler/normal，turbo=1/res_multistep/simple，
  视频=4/euler/sgm_uniform）；DSH 工具参数同步扩展。已端到端验证（固定 seed=42 出图成功）。
  提交 `e2eb633`、`85d5b5b`。
- ✅ **9. 清理死配置与输出结构** — 删除 `comfyUrl`（未使用）与 `vaultUrl`（数据工具移除后无用）；
  `callGateway` 输出统一 `files` 字段（图片/视频通用），移除语义错误的 `images`。
  提交 `85d5b5b`。
- ✅ **10. CI / 冒烟测试** — 四个仓库均新增 `.github/workflows/ci.yml`（`node --check` 语法检查）；
  `baihua-mcp-server` 新增 `test/`（`node:test`：鉴权逻辑 5 项 + MCP initialize/tools-list/tools-call
  冒烟 2 项，含 schema 校验）；`baihua-dsh-plugin` 新增 `test/smoke.test.mjs`（仓库定位快速失败、
  comfy 高级参数透传、files 字段、capabilities camelCase 归一化，6 项）。全部本地通过。
- ✅ **11. 观测性** — MCP server 每次工具调用输出 JSON Lines 日志到 stderr（tool/ok/ms/error）；
  `baihua-dsh-plugin` 挂 `tools/pre-execute`+`post-execute` 统计工具调用计数/失败数与耗时，
  经 `/dsh-bridge/status` 暴露 `toolStats`；百花后端 `ComfyDrawService` 生成完成时记录
  promptId/file/elapsed 结构化日志。
- ✅ **12. 密钥管理升级** — 确认真实 token 不进 git：`services/Baihua.Web/appsettings.json` 带
  **skip-worktree** 标记（本地 token 不进 git，历史从未含真实 token），`out/` 已 gitignore；
  新增 **pre-commit 防泄密钩子** `scripts/git-hooks/pre-commit`（扫描已知 token 清单
  `secrets-local`（gitignored）+ 64+ 十六进制长密钥形态，已本机安装并实测拦截）；
  `docs/DSH_INTEGRATION.md` 增补「密钥管理」节（token 存放位置/防泄密机制/轮换流程）。
  说明：桥接 token 保留在 `~/.dsh/cordis.patch.yml`（用户目录、非 git），未迁移环境变量
  （`!!js process.env` 存在 fail-open 风险）。

## 背景与当前状态

## 排障记录（2026-08-23 补充：工具层全灭事故）

- **现象**：某次重启后 DSH 所有工具调用（含内置 read/pwsh）报
  `Cannot read properties of undefined (reading 'kind')`，工具层完全瘫痪。
- **定位**：插件排除法（逐个卸载/装回 + 重启验证）确认元凶为 `baihua-dsh-plugin`。
  根因：其 `tools/post-execute` 监听器签名为 `(exec, result)`，**不调用 `next()` 也不返回
  值**。cordis `waterfall` 语义规定"不调用 `next()` 会 veto 整条链并返回监听器自己的值"
  （返回 `undefined`），DSH 工具管线（`dsh-tools` postExecute）随后读取 `decision.kind`
  崩溃，导致每一次工具调用全部失败。
- **修复**：`baihua-dsh-plugin/src/index.js` 的 post-execute 改为
  `(exec, result, next) => { ...; return next(); }`，与内置插件（spill-policy / tool-jobs）
  写法对齐。提交 `bfa2524`，冒烟测试 6 项全过。
- **教训**：挂 `tools/pre-execute` / `tools/post-execute` 的插件必须遵守 waterfall 链式
  约定——要么调用 `next()` 透传，要么显式返回合法决策（`{kind:"ask"}` 等）；纯旁路观测
  型监听器也要 `return next()`。
- **附带修复**：本地 link 方式安装 hysteria 缺 `@deepseek-ai/schemastery`（插件目录无
  node_modules）导致 DSH 启动失败；改用 `dsh plugin --profile web add github:luminsw/<repo>`
  GitHub 方式安装，依赖随包装入 profile，规避该问题。

## 背景与当前状态

- 百花当前有 4 个相关仓库：
  - `baihua`：百花主服务
  - `baihua-dsh-plugin`：DSH 桥接插件（百花 Web → DSH，以及 DSH 侧运维/绘图工具）
  - `baihua-local-ai-dsh-plugin`：DSH 本地 LLM 插件
  - `baihua-mcp-server`：标准 MCP server（DSH/任意客户端 → 百花非 AI 能力）
  - `hysteria-dsh-plugin`：DSH 代理管理插件

- 已完成的近期改动：
  - 文生图默认改为 Z-Image-Turbo 工作流，支持 `modelType` / `checkpoint` 参数。
  - 绘图网关文件下载改为短时签名 URL，真实 token 不再出现在链接中。
  - `baihua-dsh-plugin` 中的 5 个数据工具已移除，百花数据统一走 `baihua-mcp-server`。
  - DSH bridge 已配置共享 token，`/dsh-bridge/*` 除 `/status` 外均要求鉴权。
  - `baihua-mcp-server` 已接入 DSH，工具名为 `mcp__baihua__*`。

- 当前架构分工目标：
  - `baihua-dsh-plugin`：百花 Web → DSH 桥、`bh_*` 运维工具、`baihua_draw*` 绘图。
  - `baihua-local-ai-dsh-plugin`：本地 LLM 推理。
  - `baihua-mcp-server`：DSH/任意客户端 → 百花非 AI 只读能力。
  - `hysteria-dsh-plugin`：本机代理管理。

---

## 高优先级

### 1. 修复 `bh_git_commit_push` 仓库定位失败

- 问题：工具执行时在错误目录运行，报 `fatal: not a git repository`。
- 证据：曾手动调用 `bh_git_commit_push` 返回 exit 128；后续手动 `git commit/push` 完成。
- 建议：
  - 在 `baihua-dsh-plugin/src/ops.js` 中检查 `gitRepo` 配置与仓库根推断逻辑。
  - 若 `gitRepo` 为空，应从 `bhCommand` 所在路径或常见路径推断，而不是使用错误的当前工作目录。
  - 执行前先 `git rev-parse --show-toplevel` 验证。
- 涉及文件：`baihua-dsh-plugin/src/ops.js`、相关配置。
- 验收：`bh_git_commit_push` 在任意工作目录下都能定位到百花仓库并提交推送。

### 2. 修复 `bh_dsh_restart` 重启不可靠

- 问题：调用工具返回成功，但 DSH 进程实际未被重启（PID/启动时间未变化）。
- 证据：`ops.js` 的 Windows 重启脚本未生效；最终通过计划任务方式完成重启。
- 建议：
  - 调试 `baihua-dsh-plugin/src/ops.js` 中 `restartDsh()` 的 Windows 实现。
  - 确保 detached PowerShell 脚本真正执行，并能杀掉 `dsh web` 的完整进程树。
  - 可参考本次使用的计划任务方案：写临时 ps1 → `Register-ScheduledTask` → `Start-ScheduledTask`。
- 涉及文件：`baihua-dsh-plugin/src/ops.js`。
- 验收：`bh_dsh_restart` 调用后，DSH 进程 PID/启动时间确实变化，且 30 秒内恢复。

### 3. 插件安装方式规范化

- 问题：`profiles/web/package.json` 的 `dependencies` 为空，`dsh.profile.bundles` 也没有列出三个自研 DSH 插件；目前靠 `~/.dsh/cordis.patch.yml` 手动 insert。
- 影响：`dsh plugin` 无法管理插件，升级/迁移容易遗漏；node_modules 改动不触发 HMR。
- 建议：
  - 对三个 DSH 插件执行：
    ```sh
    dsh plugin --profile web add github:luminsw/baihua-dsh-plugin
    dsh plugin --profile web add github:luminsw/baihua-local-ai-dsh-plugin
    dsh plugin --profile web add github:luminsw/hysteria-dsh-plugin
    ```
  - 将 `~/.dsh/cordis.patch.yml` 中对应条目改为对官方 id 的 config 覆盖：
    - `dsh-baihua-bridge`
    - `dsh-baihua-local-ai`
    - `dsh-hysteria-proxy`
  - 确认 `profiles/web/package.json` 中出现插件依赖，`dsh --profile web --dump-config` 正常。
- 涉及文件：`~/.dsh/profiles/web/package.json`、`~/.dsh/cordis.patch.yml`。
- 验收：`dsh plugin` 能列出/管理这三个插件；重装后配置仍然生效。

### 4. 收紧 DSH 权限预设

- 问题：`~/.dsh/settings.yaml` 当前为：
  ```yaml
  permission:
    defaultPreset: danger-full-access
  ```
  等于 agent 对全部工具（含 `bh_*`、git push、代理管理）全开。
- 建议：
  - 改为受限预设，或自定义 permission preset。
  - 仅放行必要工具：文件读写、代码执行、`baihua_draw*`、`mcp__baihua__*`、`local_ai_small_task` 等。
  - 高危操作如 `bh_build`、`bh_update`、`bh_git_commit_push`、`bh_dsh_restart` 保留确认/禁止。
- 涉及文件：`~/.dsh/settings.yaml`，可能新增 `~/.dsh/.agent-presets/` 自定义预设。
- 验收：DSH agent 默认不再拥有全量高危工具；需要高权限时按会话临时授权。

### 5. 更新过时文档

- 问题：代码已把数据工具从 `baihua-dsh-plugin` 移除，但文档仍宣称插件提供 `baihua_*` 数据工具。
- 需要更新：
  - `baihua-dsh-plugin/README.md`
  - `baihua/README.md`
  - `baihua/AGENTS.md`
  - `baihua/docs/DSH_INTEGRATION.md`
- 内容调整：
  - `baihua-dsh-plugin` 职责改为：桥接、运维、绘图。
  - 数据工具统一指向 `baihua-mcp-server` 和 `mcp__baihua__*`。
  - 补充 DSH bridge token 安全配置说明。
- 验收：文档与当前代码行为一致。

---

## 中优先级

### 6. `baihua-mcp-server` 支持远端鉴权

- 问题：`src/baihua.js` 直接 `fetch(vaultUrl/familyUrl)`，不带任何鉴权，只适合 loopback 信任面。
- 建议：
  - 支持环境变量/参数注入 `BAIHUA_TOKEN` 或 `Authorization`。
  - 在请求头中加入 `X-Server-Token` 或 Bearer。
  - 仅对非 loopback URL 强制要求鉴权；loopback 可保持兼容。
- 涉及文件：`baihua-mcp-server/src/baihua.js`、`src/index.js`、README。
- 验收：配置 token 后能访问远端 Vault/Family；未配置 token 访问远端时返回明确错误。

### 7. ComfyUI 能力接口补全

- 问题：`ComfyUiClient.GetCheckpointsAsync` 只查 `CheckpointLoaderSimple`，看不到 `z_image_turbo`、`qwen_3_4b`、`ae` 等 diffusion model。
- 建议：
  - 增加查询 `UNETLoader`、`CLIPLoader`、`VAELoader` 的 object_info。
  - 在绘图能力接口中返回可用的 image/video 模型列表。
  - DSH 工具 `status()` 可展示当前可用模型。
- 涉及文件：`baihua/services/Baihua.Core/Services/ComfyUiClient.cs`、`DrawGatewayController.cs`、相关 DTO。
- 验收：`/mg/pool/v1/draw/capabilities` 能列出 Z-Image-Turbo 相关模型。

### 8. `baihua_draw` 高级参数增强

- 问题：目前只暴露 `modelType` / `checkpoint`，缺少 `seed`、`cfg`、`sampler`、`scheduler`、`unetName`、`clipName`、`vaeName`。
- 建议：
  - DTO 增加对应字段。
  - `baihua_draw` 工具参数同步扩展。
  - 后端为 SD1.5 和 Z-Image-Turbo 分别传递默认值。
- 涉及文件：`baihua-dsh-plugin/src/index.js`、`src/comfy.js`、`baihua/services/Baihua.Contracts/Draw/DrawDtos.cs`、`ComfyDrawService.cs`。
- 验收：可以通过 DSH 工具指定 seed/sampler 等参数生成图片。

### 9. 清理死配置与输出结构

- 问题：
  - `baihua-dsh-plugin` 的 `comfyUrl` 配置项未使用。
  - `baihua_draw_video` 返回结构里用 `images` 字段装视频，语义不对。
- 建议：
  - 删除或改为有效用途的 `comfyUrl`。
  - 将 `callGateway` 返回字段统一为 `files`，`images` 保留兼容或移除。
- 涉及文件：`baihua-dsh-plugin/src/index.js`、`src/comfy.js`。
- 验收：代码中无死配置；视频返回 `files` 字段语义正确。

---

## 低优先级

### 10. CI / 冒烟测试

- 为 `baihua-mcp-server` 和三个 DSH 插件添加：
  - `node --check`
  - MCP initialize + tools/list 冒烟测试
  - 基础工具参数 schema 测试
- 建议 GitHub Actions 或本地脚本。
- 验收：PR 自动跑语法检查与冒烟测试。

### 11. 观测性

- 为绘图网关、MCP server、DSH bridge 增加：
  - 成功/失败计数
  - 耗时统计
  - 结构化日志
- 验收：可通过日志/指标快速定位生成失败或调用异常。

### 12. 密钥管理升级

- 当前 token 以明文存在于 `~/.dsh/cordis.patch.yml` 和 `out/native/webui/appsettings.json`。
- 可选优化：
  - 环境变量注入
  - Windows DPAPI / 系统密钥环
  - 禁止把真实 token 提交到 git
- 验收：敏感 token 不落盘明文或至少不进入 git。

---

## 执行建议顺序

1. 修复 `bh_git_commit_push` 与 `bh_dsh_restart`（高优先级，都是已踩坑的 bug）。
2. 插件安装规范化。
3. 收紧 DSH 权限。
4. 更新文档。
5. 中低优先级按需排期。

---

## 关键文件索引

- `C:\Users\lumin\src\baihua`
- `C:\Users\lumin\src\baihua-dsh-plugin`
- `C:\Users\lumin\src\baihua-local-ai-dsh-plugin`

- `C:\Users\lumin\src\hysteria-dsh-plugin`
- `C:\Users\lumin\.dsh\cordis.patch.yml`
- `C:\Users\lumin\.dsh\settings.yaml`
- `C:\Users\lumin\.dsh\profiles\web\package.json`
