# 百花 × DeepSeek Harness 集成（DSH 交互面）

> 架构定位：**百花 = 能力提供方**（算力池/本机模型/知识库/家庭数据），**DSH = 编排与交互面**。
> 百花 Web 内 AI 消费型功能（AI 对话 / 编程 Agent / 图片识别 / AI 绘图）已下线菜单入口，
> 统一走 DSH 智能体（`/dsh` 页 = DSH 控制台）。百花核心业务（知识库/家庭/任务/移动端）保留。

## 部署拓扑

```
┌─────────────── 宿主机（如 192.168.3.13，Linux/k3s） ───────────────┐
│  DSH web（手动启动，127.0.0.1:3080）                                │
│   ├─ baihua-dsh-plugin      桥接（agent 会话/事件流 + bh 运维 + 绘图）│
│   ├─ baihua-local-ai-dsh-plugin  LLM provider（本机 OVMS + 算力池网关）   │
│   └─ lanListen 0.0.0.0:3081  仅 /dsh-bridge/* 的局域网桥（token 鉴权）    │
│                                                                      │
│  k3s：server/webui/openvino/postgres（ClusterIP 直连免签名）                │
└───────────────────────────────────────────────────────────────────┘
        ▲ LAN :3081（token）                    ▲ 百花 Web(k8s) 经 DshApi__BaseUrl
```

## 1. 启动 DSH（手动，不常驻）

DSH 迭代期不稳定，**保持手动启动**：

```bash
# Node ≥ 22.19；首次会下载/使用 npx 缓存
npx @deepseek-ai/dsh web            # 监听 127.0.0.1:3080
# 关终端即停；需要后台时再自行选择 nohup/systemd（不推荐常驻）
```

> 不要用 `--host 0.0.0.0`——DSH 故意禁止（会把远程代码执行暴露到网络）。
> 局域网访问走插件的 `lanListen`（只暴露桥接口）。

## 2. 安装插件（每台跑 DSH 的机器一次）

两个插件仓库在 `/home/lumin/src/mdyj/`（org `luminsw`，public）：

```bash
dsh plugin --profile web add /home/lumin/src/mdyj/baihua-dsh-plugin
dsh plugin --profile web add /home/lumin/src/mdyj/baihua-local-ai-dsh-plugin
# 或从 GitHub：dsh plugin --profile web add github:luminsw/baihua-dsh-plugin
```

> **本机（Windows）现状**：已全部改用本地 link 方式安装，无需 add 命令——
> 修改 `~/.dsh/profiles/web/package.json` 依赖为 `link:../../../src/<repo>` 后
> 在该目录跑 `pnpm install`；依赖解析经各仓库 `node_modules` junction 复用
> `~/.dsh/profiles/node_modules`。改源码后重启 DSH 即生效。

## 3. 插件配置（~/.dsh/profiles/web/cordis.patch.yml）

```yaml
- id: dsh-baihua-bridge
  name: baihua-dsh-plugin
  config:
    token: '<共享密钥>'                                    # 必须（对外暴露时；与百花 DshApi__Token 同值）
    lanListen: '0.0.0.0:3081'                             # 局域网桥（仅 /dsh-bridge/*）
    bhCommand: '/home/lumin/src/mdyj/baihua/tools/bh/bh.sh'  # 运维 CLI（本机路径）
    drawGatewayUrl: 'http://127.0.0.1:8788'               # 绘图网关（/mg/pool/v1/draw/*，可跨机）
    drawToken: '<BAIHUA_AI_EXTERNAL_TOKEN>'               # 网关鉴权（本机已启用时必填）

- id: dsh-baihua-local-ai
  name: baihua-local-ai-dsh-plugin
  config:
    token: '<共享密钥>'
    poolUrl: 'http://127.0.0.1/mg/pool/v1'                # 算力池网关（全网路由+failover；宿主插件访问本机算力池用 127.0.0.1，跨机才填对方局域网 IP）
    # poolToken: '...'                                    # 网关配置了 BAIHUA_AI_EXTERNAL_TOKEN 时填写
```

> `baihua-dsh-plugin` 不再消费 `vaultUrl` / `familyUrl` / `comfyUrl`：知识库/家庭数据
> 由 `Baihua.Server` 内置 `/mcp` 端点提供（工具名 `mcp__baihua__*`），绘图统一经 `drawGatewayUrl` 网关。
> 插件行由各包 `dsh.bundle` 自动插入（`dsh plugin add` 后挂入 profile 组合层），
> 用户级补丁里**不要重复 `insert` 同名行**，只按 id 覆盖 `config` 即可。
> k8s ClusterIP 可用 `kubectl get svc -n baihua bh-server -o jsonpath='{.spec.clusterIP}'` 查询
> （合并前需分别查 `bh-family` / `bh-ai` / `bh-vault`，现在只有一个 `bh-server`）；
> 服务重建后可能变化，需同步更新。

## 4. 百花 Web（k8s）对接

ConfigMap `baihua-config`（`baihua` 命名空间）注入：

```yaml
DshApi__BaseUrl: http://192.168.3.13:3081   # 宿主机 lanListen 地址
DshApi__Token: <与插件相同的 token>
```

改后 `kubectl rollout restart deploy bh-webui -n baihua`。`/dsh` 页显示"DSH 在线"即通。

## 5. 桥接接口一览（/dsh-bridge/*，除 /status 外均需 token）

| 端点 | 说明 |
|---|---|
| `GET /status` | 健康检查（不鉴权） |
| `GET /sessions` · `POST /chat` · `GET /sessions/{id}/history` · `WS /stream` | agent 会话驱动（历史桥接接口；百花 /dsh 页已改为内嵌 DSH 官方 Web UI） |
| `GET /baihua/open-url` | 「打开百花」入口：申请 cli-token 并返回自动登录首页 URL（仅 127.0.0.1、免鉴权，供 DSH 设置页卡片调用） |
| `GET /bh/status` · `POST /bh/action` · `GET /bh/ops[/{id}]` · `GET /bh/logs` | 百花服务运维（启停/编译/更新/日志） |
| `GET /bh/status-ui` | **只读状态**（仅 127.0.0.1、免鉴权，供 DSH 设置页卡片拉取；LAN 不暴露） |
| DSH 工具 | `bh_*`（运维，含 `bh_build_restart` 编译并重启）、`baihua_draw` / `baihua_draw_video`（绘图）；**数据工具不再由本插件注册**，统一走 `mcp__baihua__*`（见 6.5） |

## 6. 运维界面

百花 → DSH 智能体（`/dsh`）→ 右上「🧰 运维」：服务状态表 + 启停/重启/编译并重启/编译/更新/部署/日志（打开后每 10s 自动刷新）。
底层是 `bh status --json` / `bh start|stop|restart <svc>`（已并入 `tools/bh/linux/k8s/bh.sh`）。

**DSH 设置页卡片**：DSH Web UI → 设置 → 插件 →「百花服务状态」卡片，只读展示百花各服务状态并自动刷新（`baihua-dsh-plugin` 的浏览器侧客户端模块，数据源 `/dsh-bridge/bh/status-ui`）。

## 6.5 百花能力 MCP server（标准对外通道，内置 /mcp 端点）

百花在 `Baihua.Server`（单一后端进程，8788）内置了标准 MCP server（`ModelContextProtocol.AspNetCore` 2.2.0，
streamable-http，`/mcp` 端点），把只读能力暴露给**任意** MCP 客户端（DSH / Claude Desktop /
Cursor 等）。实现见 `services/Baihua.Modules.Family/Services/Mcp/BaihuaMcpTools.cs`，注册见
`services/Baihua.Server/Program.cs` 的 `AddMcpServer().WithHttpTransport(Stateless).WithTools<...>()`
与 `app.MapMcp("/mcp")`。

- 工具（DSH 侧 `mcp__baihua__*` 前缀，与原独立 MCP server 名称一致无缝切换）：
  `baihua_vault_search` / `baihua_vault_list` / `baihua_vault_read_note` / `baihua_budget_summary` / `baihua_tasks_list`
- 调用路径：`vault_list` / `budget_summary` / `tasks_list` 直接调 `Baihua.Core` 服务层（零 HTTP 跳，强类型契约）；
  `vault_search` / `vault_read_note` 走 `Baihua.Core.Modules.IVaultQueryService`（知识库模块实现）
  ——**合并后是同进程直调，不再有 k8s 跨 pod HTTP**；检索逻辑（语义 / FTS5 / 文件扫描）与
  WebUI、移动端共用同一实现，单一来源。
- 鉴权：复用 `DshController` 模式——回环 + `BAIHUA_ADMIN_ALLOWED_NETS` 免鉴权；
  否则要求 `BAIHUA_AI_EXTERNAL_TOKEN`（Bearer / X-Server-Token / ?token=）
- 会话模式：`Stateless`（工具无状态，无需 session 亲和性，水平扩展友好）

DSH 接入（profile patch，需先 `dsh plugin --profile web add @deepseek-ai/dsh-mcp-client`）：

```yaml
- insert:                      # 新建行必须用 insert 包裹（裸 - id: 只能覆盖已有行）
    - id: mcp-baihua
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: baihua
        transport: streamable-http
        url: 'http://<server-clusterip>:8788/mcp'
        # headers:                # 远端部署启用 BAIHUA_AI_EXTERNAL_TOKEN 时填写
        #   Authorization: 'Bearer <token>'
```

DSH 里工具名带 `mcp__baihua__` 前缀（如 `mcp__baihua__baihua_vault_search`）。
其他 MCP 客户端（Claude Desktop / Cursor 等）以 streamable-http 方式指向
`http://<baihua-host>/mcp` 即可。

## 7. 已下线页面

AI 对话（/messages）、编程 Agent（/code-agent）、图片识别（/image-recognition）、AI 绘图（/ai-drawing）
——菜单隐藏后，其 Web 页面、API 客户端方法、后端控制器/服务与相关 DTO 已作为死代码整体删除
（AI 对话的 `/api/ai/chat/*` 后端保留：移动端花记客户端仍经 `Baihua.Server` 的移动端签名 +
设备授权中间件使用。另：单进程后助手域路由已由 `api/AI/*` 改名为 `api/assistant/*`，
`api/ai/chat/*` 属 AI 模块、路径不变）。
AI 实验室场景首页改指 `/dsh`。

## 8. 插件更新后的重启

三个自研插件经 `dsh plugin --profile web add github:luminsw/<repo>` 安装（bundle 挂层，
`dsh plugin --profile web list` 可管理）。**改插件源码 → push 到 GitHub → 重装**，
或对本地 `file:`/`link:` 安装直接重启 DSH 即生效：

```bash
pkill -f "dsh web"; npx @deepseek-ai/dsh web
# Windows 也可用 DSH 工具 bh_dsh_restart（计划任务方式重启，约 10-30 秒恢复）
```

> 注意：当前 DSH 由本机手动启动（不常驻）。重启会重新加载插件源码与 profile patch
> （含 mcp-baihua 等 insert 行）。

## 9. 安全基线

- **桥接共享密钥**：`baihua-dsh-plugin` 的 `token` 必须与百花 `DshApi__Token` 同值；
  启用后除 `/status` 外所有 `/dsh-bridge/*` 接口要求 Bearer/`?token=`，WebSocket 升级要求 `?token=`。
- **高危运维工具审批门**：`bh_start/stop/restart`、`bh_build*`、`bh_update`、
  `bh_git_commit_push`、`bh_dsh_restart`、`bh_bootstrap` 挂 `tools/pre-execute` 审批门；
  默认权限预设为 `workspace-write`（沙箱限定工作区 + 审批 ask），需要全量权限时按会话
  临时切换 `danger-full-access`。

### 密钥管理（token 不落 git）

真实 token（桥接共享密钥、绘图网关 `drawToken`/`BAIHUA_AI_EXTERNAL_TOKEN`）存放位置：

| 位置 | 说明 |
|---|---|
| `~/.dsh/cordis.patch.yml` | DSH 侧插件 config（桥接 token / drawToken），用户目录、非 git 仓库 |
| `services/Baihua.Web/appsettings.json`（本地工作区） | `DshApi__Token` 本地值；该文件带 **skip-worktree** 标记，本地改动不进入 git（git 内版本恒为 `""`） |
| `out/native/webui/appsettings.json` | 构建产物注入，`out/` 已被 .gitignore 忽略 |
| `k8s/02-secret.yaml` → `baihua-secret` → `BAIHUA_AI_EXTERNAL_TOKEN` | **跨机**算力池/绘图/AI shim 鉴权；设置后跨机需 token，本机(10.0.0.0/8)仍免鉴权；留空=局域网信任。经 `bh-server` 的 `envFrom` 自动注入 |

> 本机 DSH 零配置：三个插件 apply/启动时调用 `/api/dsh/config` 自举拓扑（本机免鉴权），`/api/dsh/pool`
> 返回 peer 能力目录，`baihua_draw(target=节点名)` 即可跨机按名调用。若要启用「跨机需 token」，在
> `k8s/02-secret.yaml` 填 `BAIHUA_AI_EXTERNAL_TOKEN` 并 `bh deploy`；DSH 侧会自动从 `/api/dsh/config`
> 拿到该 token（`drawToken`/`poolToken`）使用，本机仍免鉴权。

防泄密机制：

1. **skip-worktree**：`git ls-files -v services/Baihua.Web/appsettings.json` 显示小写标记即生效；
   改本地 token 后 `git status` 不会出现该文件。
2. **pre-commit 钩子**（`scripts/git-hooks/pre-commit`，本地已安装到 `.git/hooks/`）：
   扫描已暂存内容，命中 `scripts/git-hooks/secrets-local`（gitignored 本地清单）中的已知 token，
   或 64+ 位连续十六进制（长密钥形态）时阻止提交。新机器接入后执行
   `cp scripts/git-hooks/pre-commit .git/hooks/pre-commit` 安装。
3. **CI 语法/冒烟**：DSH 插件与 MCP 仓库的 GitHub Actions 在 PR 上跑 `node --check` 与
   `node --test`（见各仓库 `.github/workflows/ci.yml`）。

轮换：改 `BAIHUA_AI_EXTERNAL_TOKEN`（后端）→ 同步 `~/.dsh/cordis.patch.yml` 的
`drawToken` → 同步 `DshApi__Token`/桥接 `token` → 重启 server 与 DSH。

百花侧改动（Web 页面/后端）走 `bh build <svc> && bh restart <svc>`（或 /dsh 页「🧰 运维」）。
