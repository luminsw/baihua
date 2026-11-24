# DSH 开发环境配置（新机器 / 新开发者建议清单)

> **定位**：本文回答"换一台机器（或新同事）要把 DSH + 百花插件生态跑起来，需要配哪些东西"。
> 插件生态的功能与架构总览见 [DSH_INTEGRATION.md](DSH_INTEGRATION.md)；本文只讲**安装与配置**。
> 以下内容提取自主开发机（Windows）的实际配置，路径按你的用户目录替换。

## 1. 插件清单（web profile）

DSH 安装在 `~/.dsh/`，插件以**本地 link** 方式装进 web profile
（`~/.dsh/profiles/web/package.json`）——改插件源码后**重启 DSH 即生效**，无需发布/重装。

| 包名 | 用途 | clone（org `luminsw`，分支） |
|---|---|---|
| `@luminsw/dsh-plugins` | 自研插件聚合 bundle | `dsh-plugins`（main） |
| `@luminsw/icon-standard` | 统一图标库 | `qihuang-icon-standard`（main） |
| `baihua-dsh-plugin` | 百花桥接：agent 会话 HTTP/WS（`/dsh-bridge/*`）、`bh_*` 运维工具、`baihua_draw*` 绘图、状态卡片 | `baihua-dsh-plugin`（master） |
| `baihua-local-ai-dsh-plugin` | 百花本地 AI：注册 `baihua-local` provider + `local_ai_small_task` 小任务工具 | `baihua-local-ai-dsh-plugin`（master） |
| `dsh-dev-workbench` | 开发工作台（`dev_verify` 等工具） | `dsh-dev-workbench`（main） |
| `hysteria-dsh-plugin` | Hysteria 2 代理管理（状态卡片 + `proxy_*` 工具） | `hysteria-dsh-plugin`（master） |
| `openvino-dsh-plugin` | OVMS 本地推理状态 | `openvino-dsh-plugin`（main） |
| `win-shell-dsh-plugin` | Windows shell 工具集 | `win-shell-dsh-plugin`（main） |

## 2. 安装步骤

### 2.1 前置

- Node.js（含 npm）、pnpm；DSH 本体：`npm i -g @deepseek-ai/dsh`
- （可选，见 2.4）全局包：`npm i -g android-docs-mcp gitnexus`

### 2.2 clone 插件仓库

按上表把 8 个仓库 clone 到固定路径（本机惯例：`~/src/<repo>`；图标库在
`~/game/dsh-plugins/icon-standard`——路径随意，但下一步 package.json 里的 `link:` 要对得上）：

```powershell
git clone https://github.com/luminsw/dsh-plugins.git                 ~/src/dsh-plugins
git clone https://github.com/luminsw/baihua-dsh-plugin.git           ~/src/baihua-dsh-plugin
# ...其余同理
```

### 2.3 写 web profile 的 package.json

`~/.dsh/profiles/web/package.json`（新机器没有就新建；DSH 生成过骨架则替换 dependencies/bundles）：

```json
{
  "name": "dsh-profile-web",
  "private": true,
  "dsh": {
    "profile": {
      "bundles": [
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
        "@luminsw/dsh-plugins",
        "win-shell-dsh-plugin",
        "hysteria-dsh-plugin"
      ]
    }
  },
  "dependencies": {
    "@luminsw/dsh-plugins": "link:C:/Users/<你>/src/dsh-plugins",
    "@luminsw/icon-standard": "link:C:/Users/<你>/game/dsh-plugins/icon-standard",
    "baihua-dsh-plugin": "link:C:/Users/<你>/src/baihua-dsh-plugin",
    "baihua-local-ai-dsh-plugin": "link:C:/Users/<你>/src/baihua-local-ai-dsh-plugin",
    "dsh-dev-workbench": "link:C:/Users/<你>/src/dsh-dev-workbench",
    "hysteria-dsh-plugin": "link:C:/Users/<你>/src/hysteria-dsh-plugin",
    "openvino-dsh-plugin": "link:C:/Users/<你>/src/openvino-dsh-plugin",
    "win-shell-dsh-plugin": "link:C:/Users/<你>/src/win-shell-dsh-plugin"
  }
}
```

然后在该目录执行 `pnpm install`：`link:` 依赖被解析为 junction，各插件仓库的
`node_modules` 复用 DSH 全局依赖层（`~/.dsh/profiles/node_modules`），版本与单例一致。

> `desktop` / `headless` 两个 profile 保持干净（无插件），不要动。

### 2.4 （可选）MCP 用到的全局 npm 包

若需要安卓文档 / GitNexus 两个 MCP（配置见 2.5），先：

```powershell
npm i -g android-docs-mcp gitnexus
```

### 2.5 两层 cordis.patch.yml（配置核心）

DSH 配置分两层，都是 YAML patch，**同一 id 在两层都 insert 会报
`duplicate loader entry id`——insert 只放一层，另一层只做按 id 的 config 覆盖**：

**用户级 `~/.dsh/cordis.patch.yml`**（对所有 profile 生效）——放与 profile 无关的东西：
通用 MCP（鸿蒙文档 / Microsoft Learn / 安卓文档 / GitNexus）、插件 config 覆盖、HMR watch root。

模板（token 一律占位，见第 3 节"机器特定项"）：

```yaml
- insert:
    - id: mcp-harmonyos-knowledge
      name: '@deepseek-ai/dsh-mcp-client'
      config: { serverName: harmonyos_developer_knowledge, transport: streamable-http,
                url: https://connect-api.cloud.huawei.com/api/developerknowledge/mcp }
    - id: mcp-microsoft-learn
      name: '@deepseek-ai/dsh-mcp-client'
      config: { serverName: microsoft_learn, transport: streamable-http,
                url: https://learn.microsoft.com/api/mcp }
    - id: mcp-android-docs
      name: '@deepseek-ai/dsh-mcp-client'
      config: { serverName: android_docs, transport: stdio, command: node,
                args: ['<全局 npm 前缀>/node_modules/android-docs-mcp/dist/index.js'] }
    - id: mcp-gitnexus
      name: '@deepseek-ai/dsh-mcp-client'
      config: { serverName: gitnexus, transport: stdio, command: node,
                args: ['<全局 npm 前缀>/node_modules/gitnexus/dist/cli/index.js', 'mcp'],
                env: { GITNEXUS_SEMANTIC_EXACT_SCAN_LIMIT: '50000' } }

# 插件 config 覆盖（只按 id 覆盖，不重复 insert）
- id: dsh-baihua-bridge
  config:
    fallbackModel: 'deepseek-flash'
    token: <与百花 DshApi__Token 同值>
    drawGatewayUrl: 'http://127.0.0.1'      # 百花统一入口（nginx/Traefik :80），不要写 :8788
    comfyModelType: 'z-image-turbo'
    comfyCheckpoint: 'v1-5-pruned-emaonly.safetensors'
    drawToken: <与百花 BAIHUA_AI_EXTERNAL_TOKEN 同值>
- id: dsh-baihua-local-ai
  config:
    ovmsUrl: ''
    baihuaShimUrl: 'http://127.0.0.1/mg/ai/v1'
    visionUrl: ''
- id: dsh-hysteria-proxy          # 机器上没有 Hysteria 代理就整段删掉（默认路径不对会 ENOENT 刷屏）
  config:
    home: '~/myhysteria'
    hysteriaBin: '~/myhysteria/hysteria-windows-amd64.exe'

- id: hmr                         # 覆盖默认 hmr 的 watch root，指向插件源码目录
  config:
    root:
      - C:/Users/<你>/src/baihua-dsh-plugin
      - C:/Users/<你>/src/baihua-local-ai-dsh-plugin
      - C:/Users/<你>/src/hysteria-dsh-plugin
      - C:/Users/<你>/src/dsh-dev-workbench
      - C:/Users/<你>/src/openvino-dsh-plugin
    ignored: ['**/node_modules', '**/.git/**', '**/.*']
    debounce: 100
```

**profile 级 `~/.dsh/profiles/web/cordis.patch.yml`**——放 web profile 专属的东西：
百花 MCP（只在此层 insert，不要挪到用户层）、UI/默认模型/权限、本地 LLM provider：

```yaml
- insert:
    - id: mcp-baihua
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: baihua
        transport: streamable-http
        url: 'http://127.0.0.1/mcp'        # 百花统一入口 + /mcp；跨机指向目标机器
- id: agent-default-model
  name: "@deepseek-ai/dsh-agent-default-model"
  config: { provider: deepseek-official, model: deepseek-flash, reasoningEffort: high }
- id: permission
  name: "@deepseek-ai/dsh-permission-presets"
  config:
    presets:
      read-only: { sandbox: read-only, approval: ask }
      workspace-write: { sandbox: workspace-write, approval: ask }
      danger-full-access: { sandbox: danger-full-access, approval: never }
    defaultPreset: danger-full-access
```

### 2.6 启动

```powershell
npx @deepseek-ai/dsh web        # 首次会引导登录（凭据存 ~/.dsh/.credentials.yaml，勿跨机复制）
```

## 3. 机器特定项（照抄模板后必改）

| 项 | 哪里 | 怎么取值 |
|---|---|---|
| `dsh-baihua-bridge.token` | 用户层 | 与目标百花服务端 `DshApi__Token` 同值 |
| `dsh-baihua-bridge.drawToken` | 用户层 | 与目标百花 `BAIHUA_AI_EXTERNAL_TOKEN` 同值 |
| `drawGatewayUrl` / `baihuaShimUrl` / `mcp-baihua.url` | 两层 | 指向你实际访问的那台百花：本机 `http://127.0.0.1`，跨机 `http://<百花机器 IP>`；统一入口（nginx/Traefik :80，无端口后缀），**不要写 :8788** |
| 全局 npm 前缀路径（android-docs-mcp / gitnexus） | 用户层 | `npm prefix -g` 的结果；嫌路径麻烦可改用 `npx` 写法 |
| `dsh-hysteria-proxy` 覆盖段 | 用户层 | 没装 Hysteria/aliyun CLI 的机器整段删除；装了的按实际路径覆盖 `home`/`hysteriaBin`（不覆盖默认值会 spawn ENOENT 刷屏） |
| `hmr.config.root` | 用户层 | 全部替换成本机插件源码目录 |
| llm-pi-ai 本地 provider（`:8000/v3`） | profile 层 | 只在本机跑 OVMS/兼容网关时保留，否则删掉 |

**敏感信息边界**：token/密码一律放配置文件并保证该文件不入 git；代理服务器地址、
云平台安全组 ID 等只存于本机配置，**不写入任何公开文档**。

## 4. 实战坑位记录（照抄前必读）

1. **服务端插件代码改了必须重启 DSH**——HMR 的 watch root 只覆盖前端卡片，
   `apply()`/工具处理器模块不热生效；改完一律重启再验证。
2. **`mcp-baihua` 只在 profile 层 insert**——挪到用户层会与 profile 层重复，
   报 `duplicate loader entry id: mcp-baihua`。
3. **百花 MCP 的 URL 指向统一入口**（`:80`/nginx/Traefik + `/mcp`），
   写 `:8788` 的旧口径在容器化部署后连不上（工具列表里没有 `mcp__baihua__*`）。
4. `git push` 失败时的代理流程见 `AGENTS.md`「DSH 插件 / 集成」节。
