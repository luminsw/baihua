# bh — 百花 CLI（Windows）

两种互斥形态，都占 8788/5177，同一时间只跑一种：

| 命令 | 形态 | 跑在哪 | 依赖 | 用途 |
|---|---|---|---|---|
| `bh <cmd>` | **wslc（默认）** | WSL3 容器：postgres / server / webui / nginx | WSL ≥ 2.9.3（自带 `wslc.exe`） | 日常使用 |
| `bh dev <cmd>` | native（开发） | Windows dotnet 进程 + 本机 PostgreSQL | .NET SDK 10、PostgreSQL | 写代码：VS 调试、秒级 publish |

`bh wslc <cmd>` 等价于 `bh <cmd>`（脚本兼容写法）。Linux 上 `bh` 只有 k3s 形态，见 `k8s/README.md`。

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
# 本机 PostgreSQL（服务 postgresql-x64-18）+ 库 baihua + setx PG_PASSWORD "<口令>"
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
| `bh update` | git pull + build + start |
| `bh autostart on\|off\|status` | 登录自启（写 `baihua-wslc.lnk`） |

**dev（`bh dev`）**

| 命令 | 说明 |
|---|---|
| `bh dev build [svc...]` / `build-restart` | dotnet publish 到 `out/native/`（可顺带重启） |
| `bh dev start [svc...]` / `stop` / `restart` | 服务启停（默认全部，server→webui 顺序） |
| `bh dev status [--json]` | 状态（`--json` 供 DSH 插件） |
| `bh dev doctor [--json] [--fix]` | 体检；`--fix` 自动修可修项（UAC 提权，`--no-elevate` 跳过） |
| `bh dev logs <svc> [n] [-f] [-e]` | 日志；`-f` 跟随，`-e` 看 stderr |
| `bh dev update` | git pull + build + start + 放行防火墙 |
| `bh dev autostart on\|off\|status` | 登录自启（写 `baihua-native.lnk`；别和 wslc 自启同开） |

**形态无关**

| 命令 | 说明 |
|---|---|
| `bh ovms status\|start\|stop\|restart\|logs [n]` | OVMS(:8000)：自动识别容器 / Windows 服务 / 外部进程 |
| `bh open-webui start\|stop\|status\|install` | AI 对话页后端（Windows venv，:8080） |
| `bh lan on\|off\|status` | 局域网 :80 → :8788 portproxy（仅 dev；wslc 由 nginx 占 :80） |
| `bh install` / `uninstall` | 安装 / 移除定位器 |

## 数据与环境

- wslc：命名卷 `bh-pgdata` / `bh-data` / `bh-logs`；配置 `docker/.env.wslc`（首次自动生成，含随机 `PG_PASSWORD`）
- dev：本机 PostgreSQL + `%BAIHUA_HOME%`（默认 `~/.baihua`）；读 `PG_HOST` / `PG_USER` / `PG_PASSWORD` / `PG_DATABASE`
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

## Linux

```bash
bash tools/bh/bh.sh install    # 普通用户 → ~/.local/bin；root → /usr/local/bin
bh doctor                      # k3s / service / namespace / Deployment / PVC / 端口 / AI pod
```

部署细节见 `k8s/README.md`；`bh` 在 Linux 上就是 k3s 形态（`tools/bh/linux/k8s/bh.sh`）。