# bh — 百花 CLI

> **环境边界（2026-11-22 约定）**：只支持两种开发环境——**纯 Windows** 与 **纯 Linux**，**不考虑 WSL 开发环境**。
> Windows 只有一种运行形态（2026-11-25 起）：**native**（dotnet 进程 + SQLite）；
> Linux 只有**原生 systemd + SQLite**一种（`linux/native/bh.sh`，`bh` 即入口，见 `docs/LINUX_NATIVE.md`）。
> wslc 容器栈已退役（2026-11）；可选容器组件（ovms-gpu / bh-open-webui）仍走 WSL3 自带 `wslc.exe`，
> 不需要 Docker Desktop，缺失仅对应功能降级。

## 首次运行

```powershell
winget install --id Microsoft.DotNet.SDK.10   # 部署包形态可跳过（apps\ 为自包含产物）
\tools\bh\bh.ps1 install          # 装定位器到 ~\.local\bin，加入用户 PATH
bh build                          # dotnet publish 到 out\native（部署包形态不可 build）
bh start                          # server(8788) → webui(5177)
bh doctor                         # 体检：SQLite / 产物 / 端口 / lan / OVMS / AI 对话页
bh dashboard                      # 打开 WebUI（自动登录）
bh lan on                         # 局域网入口：宿主 :80 → 127.0.0.1:8788（手机/其它设备）
bh autostart on                   # 登录自启（不需要管理员）
```

数据库为 SQLite（`%USERPROFILE%\.baihua\db\baihua.db`），无需本机 PostgreSQL；
旧 PostgreSQL 数据用 `bh db-migrate`（`tools/db-migrate`）迁入。

## 命令

| 命令 | 说明 |
|---|---|
| `bh build [svc...]` / `build-restart` | dotnet publish 到 `out/native/`（部署包布局 `apps\` 不可 build） |
| `bh start [svc...]` / `stop` / `restart` | 服务启停（默认全部，server→webui 顺序） |
| `bh status [--json]` | 状态（`--json` 供 DSH 插件） |
| `bh doctor [--json] [--fix]` | 体检；`--fix` 自动修可修项（UAC 提权，`--no-elevate` 跳过） |
| `bh logs <svc> [n] [-f] [-e]` | 日志；`-f` 跟随，`-e` 看 stderr |
| `bh update` | git pull + build + start + 放行防火墙（仅开发仓库形态） |
| `bh backup [目录]` | 冷备份数据目录（排除 bin/logs/models/venv）→ zip + .sha256；默认输出仓库根 `backup\`；需先 `bh stop` |
| `bh restore <zip> [-y]` | 恢复数据目录（旧目录改名 `.pre-restore-<时间>` 保留，可回滚）；需先 `bh stop` |
| `bh autostart on\|off\|status` | 登录自启（用户级 Startup，写 `baihua-native.lnk`） |
| `bh install` / `uninstall` | 安装 / 移除定位器与用户 PATH |
| `bh help` | 帮助 |

**可选容器组件（形态无关；需 WSL ≥ 2.9.3 自带 `wslc.exe`）**

| 命令 | 说明 |
|---|---|
| `bh ovms status\|start\|stop\|restart\|logs [n]` | 本地推理 OVMS(:8000)：自动识别 wslc 容器 / Windows 服务 / 外部进程 |
| `bh open-webui install\|start\|stop\|restart\|status\|logs [n]` | AI 对话页（wslc 容器 `bh-open-webui`，:8080；镜像固定 v0.13.3，数据卷 `bh-openwebui-data`） |

**局域网入口**

| 命令 | 说明 |
|---|---|
| `bh lan on\|off\|status` | 宿主 `:80` → `127.0.0.1:8788` portproxy + 防火墙（移动端默认 80 端口无显式端号） |

## 数据与环境

- 数据目录 `%USERPROFILE%\.baihua`（`BAIHUA_DATA_HOME` 可覆盖；服务进程内为 `BAIHUA_HOME`）
- 数据库 `db\baihua.db`（SQLite 单库，三模块共用）；知识库 `vaults\`；日志 `logs\`
- 可选组件数据：ovms 模型 `models\`、open-webui 聊天记录在 wslc 卷 `bh-openwebui-data`

## 部署包（最终用户）

`scripts/make-deploy-package.ps1` 生成 `dist\baihua-deploy-<version>.zip`：

- bh CLI + `apps\{server,webui}\`（self-contained win-x64 publish，无需 .NET）
- 镜像仓库结构：包根下 `tools\bh\` + `apps\`，native cell 自动识别（`$OutDir = apps\`）
- `.ps1` 一律 UTF-8 with BOM（bh.cmd 走 Windows PowerShell 5.1，无 BOM 按 GBK 读会解析崩）
- 安装：解压 → `bh.cmd install`（设 `BAIHUA_PKG_HOME` 定位包目录）→ `bh.cmd start`

## 注意

- **登录自启无窗口**：Startup 里只放 `.lnk` → `bh-launch.exe`（GUI 子系统）。不要往 Startup 放 `.cmd`，Explorer 跑 `.cmd` 必有控制台窗口
- **脚本编码**：`.ps1` 必须 UTF-8 with BOM（5.1 按 GBK 读无 BOM 文件会把中文注释的换行吞掉）；`.cmd` 必须纯 ASCII
- **管理 API 来源**：容器访问宿主要放行 `BAIHUA_ADMIN_ALLOWED_NETS`（wslc 容器网段 `169.254.0.0/16`）
- **OVMS 模型目录**：不能是 NTFS junction（容器内断链），需真实目录

## 自检

```powershell
pwsh -File scripts/verify-bh-native.ps1     # 入口 / 依赖 / status / doctor / autostart
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
