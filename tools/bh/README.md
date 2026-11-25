# bh — 百花 CLI

> **环境边界（2026-11-22 约定）**：只支持两种开发环境——**纯 Windows** 与 **纯 Linux**，**不考虑 WSL 开发环境**。
> Windows 有两种互斥运行方式：`bh`（wslc，默认）与 `bh dev`（native）；
> Linux 只有**原生 systemd + SQLite**一种（`linux/native/bh.sh`，`bh` 即入口，见 `docs/LINUX_NATIVE.md`）。
> wslc 依赖 WSL3 自带的 `wslc.exe` 作为运行时，这属于 Windows 侧实现，**不等于在 WSL 里开发**。

两种互斥形态，都占 8788/5177，同一时间只跑一种：

| 命令 | 形态 | 跑在哪 | 依赖 | 用途 |
|---|---|---|---|---|
| `bh <cmd>` | **wslc（默认）** | WSL3 容器：server / webui / nginx | WSL ≥ 2.9.3（自带 `wslc.exe`） | 日常使用 |
| `bh dev <cmd>` | native（开发） | Windows dotnet 进程 | .NET SDK 10 | 写代码：VS 调试、秒级 publish |

`bh wslc <cmd>` 等价于 `bh <cmd>`（脚本兼容写法）。Linux 上 `bh` 只有原生 systemd + SQLite 形态（见本文末尾「Linux」）。
（Windows 脚本里的 postgres 容器与 `PG_*` 是遗留项：服务端已改用 SQLite，数据库文件落在 `bh-data` 卷，清理它们不影响数据。）

## 首次运行

```powershell
# wslc（默认形态）
wsl --update                      # 需要 WSL 3
\tools\bh\bh.ps1 install          # 装定位器到 ~\.local\bin，加入用户 PATH
bh doctor                         # 体检：wslc/镜像/卷/容器/端口冲突/OVMS/AI 对话页
bh build                          # 构建镜像（首次慢；拉不到 SDK 镜像用 bh build -Prebuilt）
bh start                          # postgres → server → webui → nginx
bh dashboard                      # 打开 WebUI（自动登录）
bh autostart on                   # 登录自启（不需要管理员）
```

```powershell
# dev（开发形态）
winget install --id Microsoft.DotNet.SDK.10
# 数据库为 SQLite（%BAIHUA_HOME%\db\baihua.db），不再需要本机 PostgreSQL
bh stop                           # 先停 wslc（端口互斥）
bh dev build; bh dev start
bh dev doctor
```

## 命令

**wslc（`bh`）**

| 命令 | 说明 |
|---|---|
| `bh build [server\|webui] [-Prebuilt]` | 构建镜像 |
| `bh start [svc...]` | 启动（默认全部）；端口被 dev 占用会直接拒绝 |
| `bh stop [svc...]` / `restart` | 停止（可只停指定服务，命名卷保留）/ 重启 |
| `bh status [--json]` | 容器 + 访问地址 + OVMS + 端口占用者 |
| `bh doctor [--json]` | 体检 |
| `bh logs <svc> [n]` | 日志（server/webui/postgres/nginx） |
| `bh exec <svc> <cmd...>` | 容器内执行命令 |
| `bh pull` / `ps` / `env` | 拉 postgres 镜像 / 容器列表 / 查看 `.env.wslc` |
| `bh update` | 源码仓库：git pull + 本地构建；部署包：从 registry 拉镜像 + 重建容器（数据卷保留） |
| `bh backup [目录]` | 冷备份全部数据卷（bh-pgdata/bh-data/bh-openwebui-data）+ `docker/.env.wslc`；自动停栈、备份后做恢复演练、再重启。产物在 `<目录>/baihua-backup-<时间>/`（默认仓库根 `backup/`） |
| `bh restore <备份目录> [-y]` | 从备份恢复数据卷与配置（删卷重建 + 还原 `.env.wslc`）；覆盖现有数据，需输 `yes` 确认 |

两形态 backup/restore 的真机验收手册：Windows wslc 见 `docs/WSLC_BACKUP_RESTORE_TEST.md`，Linux k8s 见 `docs/K8S_BACKUP_RESTORE_TEST.md`。
| `bh autostart on\|off\|status` | 登录自启（写 `baihua-wslc.lnk`） |

**dev（`bh dev`）**

| 命令 | 说明 |
|---|---|
| `bh dev build [svc...]` / `build-restart` | dotnet publish 到 `out/native/`（可顺带重启） |
| `bh dev start [svc...]` / `stop` / `restart` | 服务启停（默认全部，server→webui 顺序） |
| `bh dev status [--json]` | 状态（`--json` 供 DSH 插件） |
| `bh dev doctor [--json] [--fix]` | 体检；`--fix` 自动修可修项（UAC 提权，`--no-elevate` 跳过） |
| `bh dev logs <svc> [n] [-f] [-e]` | 日志；`-f` 跟随，`-e` 看 stderr |
> 镜像来源由 `docker/.env.wslc` 的 `BAIHUA_IMAGE_SOURCE` 决定：`auto`（仓库内有源码→本地构建；瘦部署包→registry）、
> `local`（只本地构建）、`registry`（只拉镜像）；`BAIHUA_IMAGE_NAMESPACE` / `BAIHUA_IMAGE_TAG` 指定镜像空间与版本，
> `BAIHUA_IMAGE_REGISTRY` 指定 registry（默认=华为云 SWR，国内匿名拉取已验证；留空=Docker Hub 直连，国内常超时；也可填阿里云 ACR 域名，仓库需公开）。
> CI 可同时推送 Docker Hub / 阿里云 ACR / 华为云 SWR（配置对应 GitHub Variables/Secrets 即自动启用）。
> 最终用户部署包由 `scripts/make-deploy-package.ps1` 生成（不含源码）。

| `bh dev update` | git pull + build + start + 放行防火墙 |
| `bh dev autostart on\|off\|status` | 登录自启（写 `baihua-native.lnk`；别和 wslc 自启同开） |

**形态无关**

| 命令 | 说明 |
|---|---|
| `bh ovms status\|start\|stop\|restart\|logs [n]` | OVMS(:8000)：自动识别容器 / Windows 服务 / 外部进程 |
| `bh open-webui start\|stop\|status\|logs [n]\|install` | AI 对话页后端（wslc 形态为容器 `bh-open-webui`；native/dev 仍为 Windows venv，:8080） |
| `bh lan on\|off\|status` | 局域网 :80 → :8788 portproxy（仅 dev；wslc 由 nginx 占 :80） |
| `bh install` / `uninstall` | 安装 / 移除定位器 |

## 数据与环境

- wslc：命名卷 `bh-data`（SQLite + 配置）/ `bh-logs` / `bh-openwebui-data`；配置 `docker/.env.wslc`（首次自动生成）
- dev：`%BAIHUA_HOME%`（默认 `~/.baihua`），数据库 `db\baihua.db`
- 两种形态**数据不互通**，切换不会搬数据

## 注意

- **端口互斥**：8788/5177 同时只归一种形态；切换前 `bh stop` 或 `bh dev stop`
- **登录自启无窗口**：Startup 里只放 `.lnk` → `%BAIHUA_HOME%\bin\bh-launch.exe`（GUI 子系统）。不要往 Startup 放 `.cmd`，Explorer 跑 `.cmd` 必有控制台窗口
- **脚本编码**：`.ps1` 必须 UTF-8 with BOM（5.1 按 GBK 读无 BOM 文件会把中文注释的换行吞掉）；`.cmd` 必须纯 ASCII
- **宿主 :80 被占**（IIS/http.sys 等）：在 `docker/.env.wslc` 设 `BAIHUA_NGINX_PORT=8080` 后 `bh restart`
- **管理 API 来源**：容器访问宿主要放行 `BAIHUA_ADMIN_ALLOWED_NETS`（wslc 已含 `169.254.0.0/16`）
- **OVMS 模型目录**：不能是 NTFS junction（容器内断链），需真实目录

## 自检

```powershell
pwsh -File scripts/verify-bh-native.ps1     # dev：入口 / 依赖 / dev status / doctor / autostart
pwsh -File scripts/verify-bh-wslc.ps1       # wslc：默认路由 / doctor / autostart / 静态检查
```

只读脚本，改了 `tools/bh/*.ps1` 后跑一遍（`dotnet test` 覆盖不到 PowerShell 入口）。

## Linux（原生 systemd + SQLite）

```bash
bash tools/bh/bh.sh install --no-enable   # 装 CLI + systemd 单元（不 enable 便于先迁移数据）
bh build                                  # 发布到 ~/.baihua/bin（会先停对应服务）
bh start                                  # 启动 baihua-server@<user> / baihua-webui@<user>
bh status [--json]                        # 状态 + HTTP 健康 + 数据库大小（--json 供 DSH 插件）
bh logs server 100 [-f]                   # journald 日志
bh doctor                                 # systemd/dotnet/单元/端口/磁盘/数据库/FTS5/nginx
bh backup ~/backup                        # 冷备份数据目录（要求服务已停止）
bh restore <archive> [-y]                 # 恢复（旧目录改名保留；恢复后需 bh build）
bh proxy install                          # 系统 nginx :80（/api/auth/ 转发到 5177）
bh db-migrate --rewrite-prefix /app/data/vaults=$HOME/.baihua/vaults   # PostgreSQL → SQLite
```

- 数据目录 `~/.baihua`（`BAIHUA_HOME` 覆盖）；`bh install` 会把单元里的 `%h/.baihua` 渲染成实际路径；
- 端口由 appsettings 的 Kestrel 端点决定，改端口在 `config/server.env` 写
  `Kestrel__Endpoints__Http__Url=http://0.0.0.0:<port>`（`ASPNETCORE_URLS` 对已配置端点的应用不生效）；
- 更完整的迁移/备份/排障：`docs/LINUX_NATIVE.md`。