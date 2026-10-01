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

### 2.1 升级 DSH 后必做的两件事（否则插件被跳过）

DSH 启动时用**运行时自身版本**校验每个插件的 `peerDependencies` 中所有
`@deepseek-ai/dsh*` 项（`cordis` 等非 dsh 包不校验），不匹配就整包跳过
（`skipping profile bundle`，插件行不加载）；profile 里的插件行同理
（`disabling profile plugin row`）。升级 `@deepseek-ai/dsh` 后：

1. **5 个自研插件仓库**（baihua-dsh-plugin / baihua-local-ai-dsh-plugin /
   hysteria-dsh-plugin / dsh-dev-workbench / openvino-dsh-plugin）的
   `package.json` → `peerDependencies` 中 `@deepseek-ai/dsh-*` 范围改成新版本
   （当前 `^0.2.0-rc.2`；`^0.1.0-rc.x` 不匹配 `0.2.x`，因为 caret 在 0.x 下不跨 minor）。
   注意 `workspace:^` 只在 workspace 成员内可用，link 安装的插件不能用。
2. **`~/.dsh/profiles/web/package.json`** 里 `@deepseek-ai/dsh-mcp-client` 改成同一
   版本（它承载 5 个 MCP 插件行：baihua / harmonyos / microsoft-learn /
   android-docs / gitnexus），再在该目录
   `pnpm install --no-frozen-lockfile`（版本变了必须放宽 frozen-lockfile）。

验证：`dsh --profile web --port 3099 --no-open`（换个端口，别打断在跑的实例），
输出里不应出现 `skipping profile bundle` / `disabling profile plugin row` /
`warning: N entries did not activate`；再用
`curl 127.0.0.1:3099/dsh-bridge/bh/status-ui`、`/dsh-openvino/status`、
`/dsh-dev-workbench/status`、`/dsh-bridge/proxy/status-ui` 应均为 200。

> **依赖解析机制**：link 安装的插件（真实路径在 `~/src/<repo>`）里 `import
> '@deepseek-ai/dsh-*'` 由 DSH 自己的 loader 拦截并指向**运行时安装副本**
> （`dsh-app-boot` 的 linked-root interception，按插件 `package.json` 的
> `peerDependencies` 名字匹配）。因此 `~/.dsh/profiles/node_modules` 这层手工
> junction 镜像只对**非 peer** 依赖（`ws`、`schemastery`）有意义；它随 dsh 安装
> 布局变化会大面积失效（0.2.0-rc.2 起包装在 `_npx/<hash>/node_modules` 平铺，
> 旧的 `.../dsh/node_modules/@deepseek-ai/*` 嵌套路径消失），但 peer 导入不受影响。

### 2.2 插件卡片/配置表单的客户端 API（0.2.x 迁移）

DSH 0.2.x 重做了「插件」页与设置表单，0.1.x 的客户端 API **整体删除**，5 个自研插件的
卡片在升级后一度全部不可见（注册到了已不存在的槽位）：

| 0.1.x（已删除） | 0.2.x 替代 |
|---|---|
| 槽位 `settings.plugin.item` | `plugins.bundle.config`（key = **包名**，渲染在「插件」页该 bundle 自己的页面上） |
| 同上（某个 Loader 条目/行的配置页） | `plugins.row.config`（key = **`<包名>#<条目 id>`**，该行出现「配置」控件，打开后渲染此槽位） |
| 客户端服务 `settingsScope.bind({namespace})` | 宿主经槽位 ownerProps 下发 `form = { state, mutate }`：`state` 是 `{status,value,base,user,revision,writable,mode}` 快照，写回用 `mutate([{op:'set',path:[field],value}])` / `{op:'unset',path:[field]}`（成功即写 profile patch，Loader 重放条目 → 插件里的 `config` 即最新值） |
| 宿主 `settings.installSection(ctx, ns, Schema, config, {setSource})` | 已删除；settings 服务改为**按 Loader 条目 id 自动投影** Config schema（`ctx.settings.configure({auto:true})` 只声明页面策略，默认 auto） |

各插件当前注册的 key：

| 插件 | bundle key | 行配置 key |
|---|---|---|
| baihua-dsh-plugin | `baihua-dsh-plugin` | `baihua-dsh-plugin#dsh-baihua-bridge` |
| baihua-local-ai-dsh-plugin | `baihua-local-ai-dsh-plugin` | `baihua-local-ai-dsh-plugin#dsh-baihua-local-ai` |
| hysteria-dsh-plugin | `hysteria-dsh-plugin` | `hysteria-dsh-plugin#dsh-hysteria-proxy` |
| dsh-dev-workbench | `dsh-dev-workbench` | —（卡片无表单） |
| openvino-dsh-plugin | `openvino-dsh-plugin` | —（卡片无表单） |

> 改客户端 `client.js` 后必须**重启 DSH**（客户端 bundle 的 rev 在启动时登记，浏览器刷新
> 才会拿到新 bundle）；服务端插件代码同样必须重启（见 §7 的 HMR 实测结论）。

### 2.3 Agent 预设（preset）：0.2.x 与 0.1.x 完全不同

**0.1.x**：用户 preset 是**目录式**的，放在 `~/.dsh/.agent-presets/<id>/`，目录里
`preset.yml`（name/description）+ `agent.cordis.yml`（组合条目列表），由运行时扫描该目录。

**0.2.x**：**目录扫描机制已删除**（0.2.0-rc.2 全树搜不到 `.agent-presets`），preset 改为
**声明式 Loader 行**：

```yaml
- insert:
    - id: preset-<id>                       # 任意 id
      name: '@deepseek-ai/dsh-agent-preset'
      config:
        id: <id>                            # 必填；preset 身份（会话里选中的就是它）
        name: 百花中医                        # 可选的显示名
        description: ...
        order: 50
        plugins: [ ... ]                    # 必填：内联的 Cordis 条目列表（原 agent.cordis.yml）
```

官方自带的 `standard` / `ptc` / `minimal` / `cordis` 就是这个形状，定义在
`@deepseek-ai/dsh-web-app/presets/*.patch.yml`。注意：

- **`@deepseek-ai/dsh-persona` 的配置键变了**：0.1.x 用 `config.text`，0.2.x 只有
  `{ prefix(必填), suffix?, complete?, includeRuntimeContext? }` —— 直接搬旧组合会因未知键
  而挂掉，`text` 要改写为 `prefix`；`suffix` 省略会「遮蔽」deployment 后缀（即不再出现
  “You are a coding agent...” 那类框架文案）。
- **叠加顺序**：`bundle → profile → home → CLI`（见 `--dump-config-schema` 头注释），且
  「A patch config replaces the whole config」。preset 的**默认定义放 bundle**（随插件分发），
  **按 id 的覆盖放 profile patch**：Web 端「Agent 预设」编辑器对某行的改动就是按 id 写进
  profile patch 的（profile 层更晚 → 你的改动生效）；定义放 home patch
  （`~/.dsh/cordis.patch.yml`）则会因层序更靠后把 UI 的改动压掉。
  同一个 id **不能 insert 两次**（否则 `duplicate loader entry id`）。
- 「百花中医」preset 已从旧目录格式移植，并**随 `baihua-dsh-plugin` 的 bundle 分发**：
  文件 `baihua-dsh-plugin/presets/baihua-tcm.patch.yml`（已加入该包 `dsh.bundle.patch` 数组），
  行 id `preset-baihua-tcm`、`config.id: baihua-tcm`；组合 = persona（原 `text` → `prefix`）+
  `tool-web(fetch:false)` + `tool-ask-user` + compaction 组（含 tool-result-pruner）。
  **装了该插件就有这个预设**，profile patch 里不要再 insert 同名 id（只写覆盖）。
  旧目录 `~/.dsh/.agent-presets/baihua-tcm/` 保留为历史副本，**不再被读取**。
- 找不到 preset 时先看合成结果：`dsh --profile web --dump-config | Select-String preset-`
  （行上方会标注来自哪一层，如 `# == baihua-dsh-plugin`）；界面里的列表走 RPC（不在 HTML 里），
  改完 patch 或 bundle 需重启 DSH。

## 3. 插件配置（~/.dsh/profiles/web/cordis.patch.yml）

```yaml
- id: dsh-baihua-bridge
  name: baihua-dsh-plugin
  config:
    token: '<共享密钥>'                                    # 必须（对外暴露时；与百花 DshApi__Token 同值）
    lanListen: '0.0.0.0:3081'                             # 局域网桥（仅 /dsh-bridge/*）
    bhCommand: '/home/lumin/src/mdyj/baihua/tools/bh/bh.sh'  # 运维 CLI 入口（Linux/k3s；Windows 侧为 ~/.local/bin/bh.cmd）
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
底层是 `bh status --json` / `bh start|stop|restart <svc>`（Linux/k3s 下 `bh` 与 `bh-k3s` 同义；实现见 `tools/bh/linux/k8s/bh.sh`）。

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
