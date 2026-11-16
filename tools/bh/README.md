# bh - 百花 CLI（Windows：wslc 默认 / dev 开发）

Windows 上有**两种互斥的部署形态**，都占 8788（后端）/ 5177（WebUI），同一时间只能跑一种。
**2026-11-16 起默认形态是 wslc**，native 降级为开发形态（`bh dev`）：

| 命令 | 形态 | 跑在哪 | 依赖 | 用途 |
|------|------|--------|------|------|
| `bh <cmd>` | **wslc（默认）** | WSL3 里的 Linux 容器（postgres / server / webui / nginx） | WSL >= 2.9.3（自带 `wslc.exe`） | 日常使用：环境隔离、生命周期由容器层管、一条命令起全栈 |
| `bh dev <cmd>` | **native（开发）** | Windows dotnet 进程（`bh-server.exe` / `bh-webui.exe`） | .NET SDK 10、本机 PostgreSQL | 只在写代码时用：VS F5、秒级 publish、WSL 出问题时的退路 |
| `bh`（Linux 上） | k3s | Linux k3s（containerd） | Linux + k3s | 与 Windows 无关 |

> **为什么这么分**：容器形态自带生命周期管理（重启状态、日志、卷、端口发布），native 要手工拼
> 计划任务 + GUI 启动器才能做到「关终端不死、不弹窗口」，日常维护成本明显更高；但 native 的开发内环
>（改一行 C# → publish → 起进程）比「publish → 打镜像 → 重建容器」快一个量级，所以保留为 `bh dev`。
>
> **两种形态互斥**：切换前 `bh stop`（wslc）或 `bh dev stop`（native）。`bh start` 检测到端口被
> native 的 `bh-*` 进程占着会直接拒绝并提示，不会出现半启动状态。
>
> 历史上的 `bh-k3s`（经 WSL 的全容器化形态）已随本机 WSL 卸载而退役（2026-10-27）；
> Windows 侧该入口与定位器已删除，Linux 侧 k3s 实现仍保留在 `linux/k8s/`。
> 传入旧写法 `bh k8s <cmd>` / `bh-k3s <cmd>` 会失败并给出迁移指引。
>
> **wslc 形态**用 Windows 11 新版 WSL 自带的容器 CLI（`wslc.exe`，WSL >= 2.9.3），
> **不需要 Docker Desktop**、不需要 WSL 里的 k3s——`docker/Dockerfile.server|webui` 直接基于官方 .NET 镜像。

```
tools/bh/
├── bh.ps1 / bh.cmd          Windows 入口：默认 wslc；bh dev <cmd> 落 native；bh wslc <cmd> 显式写法
├── locator.ps1              Windows 自包含定位器（复制到 PATH 用）
├── bh.sh / locator.sh       Linux 入口（指向 linux/k8s/bh.sh；Linux 上 bh 即 k3s 形态）
├── win/native/              native（dev）实现：build/start/stop/status/logs/doctor/autostart…
├── win/wslc/                wslc（默认）实现：网络/卷/容器 build/run/logs/status/doctor/autostart，docker/.env.wslc
└── linux/k8s/               k3s 实现（Linux 专用；Windows 上已退役）
```

## 首次运行（wslc 默认形态）

```powershell
# ① WSL 3（wslc.exe 随 WSL 提供；不需要 Docker Desktop、不需要 .NET SDK、不需要本机 PostgreSQL）
wsl --update
wsl --version                    # 需要 WSL 2.9.3+（本机为 3.0.x）

# ② 装 bh 定位器并加入 PATH（先装定位器，后面 bh 才能直接用）
.\tools\bh\bh.ps1 install        # 新开终端后 bh 直接可用（并清理旧版 bh-k3s 遗留）

# ③ 体检 → 构建镜像 → 启动
bh doctor                        # 逐项结论 + 失败项的下一步命令（只读）
bh build                         # 首次要拉 .NET 基础镜像，较慢；拉不到 SDK 镜像用 bh build -Prebuilt
bh start                         # postgres → server → webui → nginx，等健康检查
bh status                        # 期望：四个容器 RUNNING + OVMS
bh dashboard                     # 打开 WebUI（cli-token 自动登录）
```

> `bh doctor` 覆盖：`wslc.exe`/版本、镜像是否齐全、命名卷、容器状态、8788/5177/80 端口占用（含
> native/dev 冲突）、OVMS 形态与模型、AI 对话页后端（open-webui:8080）、`docker/.env.wslc`。
> 每项 `[ OK ] / [WARN] / [FAIL]`，FAIL 下面直接给命令；`--json` 给工具用（退出码 0 = 无 FAIL）。

### 登录自启（推荐）

```powershell
bh autostart on       # 用户级 Startup 写 baihua-wslc.cmd（登录时 bh start，幂等；不需要管理员）
bh autostart status   # 是否启用 / 入口 / 是否还留着 native 自启项
bh autostart off      # 删掉自启文件（不影响正在跑的服务）
```

> `autostart on` 会把旧的 `baihua-native.cmd` 改名成 `.disabled`（避免登录时两种形态抢 8788/5177）。

### 首次运行（dev / native 形态，仅供开发）

```powershell
# ① .NET SDK 10（bh dev build 也会在缺失时用 winget 自动装）
winget install --id Microsoft.DotNet.SDK.10

# ② 本机 PostgreSQL（dev 形态不管理 PG 安装）
#    - Windows 服务 postgresql-x64-18；建库：CREATE DATABASE baihua;
#    - 后端读环境变量 PG_PASSWORD（PG_HOST=127.0.0.1 / PG_USER=postgres / PG_DATABASE=baihua）
#    - 给当前用户设一次：setx PG_PASSWORD "<你的口令>"

# ③ 停掉 wslc（端口互斥）→ 构建 → 启动
bh stop
bh dev build                     # dotnet publish 到 out/native/（增量很快）
bh dev start
bh dev status                    # 期望：server 8788 / webui 5177 RUNNING (release)
bh dev doctor                    # dev 形态体检（含 psql 真实登录、产物完整性标签…）
```

> dev 形态的 `bh dev autostart` 也能开，但**不要和 wslc 的自启同时开**（两个都会抢 8788/5177）。
> 日常请只开一个：推荐 `bh autostart on`（wslc）。

> `bh dev doctor` 是 dev 形态的体检入口，覆盖：.NET SDK 10、构建产物、产物完整性标签、
> PostgreSQL 服务/端口/**真实登录**（psql `select 1`，不弹口令提示）、8788/5177 占用、
> OVMS 形态与模型、数据目录可写、定位器与 PATH、防火墙 8788、仓库是否脏。
> 每项输出 `[ OK ] / [WARN] / [FAIL]`，FAIL 下面直接给命令；`--json` 给工具用
> （退出码 0 = 没有 FAIL 项）。`bh dev start` 也会在 PostgreSQL 不可达/口令未设时先警告。

### dev 专属：一键自动修 `bh dev doctor --fix`

只修「确定性、可回滚、不需要人判断」的项：产物完整性标签（Low → Medium）、PostgreSQL 服务启动、缺库建库、
防火墙 8788、bh 定位器、OVMS 容器拉起，以及产物缺失时跑 `bh dev build`（构建前在跑的服务会在构建完拉回）。
需要管理员的两项（防火墙 / 系统服务）会弹 UAC；无人值守场景用 `--no-elevate` 明确跳过（不弹窗）。
口令错误、数据目录权限这类只报不修 —— 修了也不会对。

### dev 专属：登录自启

`bh dev autostart on` 会往 Startup 写 `baihua-native.cmd`（登录时 `bh dev start`）。
**日常别开**：它和 wslc 的自启抢 8788/5177，只有整天写代码的机器才考虑。

### k3s 形态（已从 Windows 退役）

k3s 需要 Linux 运行时（Windows 上曾经 WSL2 转发，**本机 WSL 已于 2026-10-27 卸载**）。
Windows 侧 `bh-k3s` 入口已删除；如将来需要 k8s 能力：

1. 重装 WSL2（`wsl --install -d Ubuntu`），并按 `linux/k8s/README.md` 一键安装；
2. `bh lan on` 会自动清理指向已消失 WSL IP 的遗留 portproxy 规则并重建 native 转发。

Linux 机器不受影响：`bh`（= k3s 形态）照常可用。

## 用法

```
bh                           状态总览（= bh status，wslc）
bh <command> [args]          wslc 命令（Windows 默认形态）
bh dev <command> [args]      native 命令（开发/调试形态）
bh doctor [--json]           体检当前形态（默认 wslc）；native 用 bh dev doctor
bh autostart [on|off|status] 登录自启（默认写 wslc 的自启项）
bh ovms [status|start|stop|restart|logs]   本地模型服务 OVMS(:8000)，两种形态通用
bh open-webui <start|stop|status|install>  AI 对话页后端（Windows venv，两种形态共用）
bh lan [on|off|status]       局域网入口（仅 dev/native：宿主 :80→:8788 portproxy）
bh install / uninstall       安装 / 移除定位器（加入用户 PATH）
bh wslc <command> [args]     wslc 的显式写法（= bh <command>，脚本兼容用）
```

### dev 命令速查（`bh dev`，开发形态）

| 命令 | 说明 |
|------|------|
| `bh dev build [svc...]` | dotnet publish 到 `out/native/`（默认全部，可指定 server/webui） |
| `bh dev build-restart [svc...]` | build + restart |
| `bh dev start [svc...]` | 启动服务（默认全部，按依赖顺序 server→webui），后台静默、关终端不死 |
| `bh dev stop [svc...]` | 停止服务 |
| `bh dev restart [svc...]` | stop + start |
| `bh dev update` | git pull + build + start + 防火墙放行 |
| `bh dev status [--json]` | 状态总览（端口/进程/OVMS 形态/访问地址）；`--json` 供 DSH 插件（`cell: native`） |
| `bh dev doctor [--json] [--fix]` | dev 体检：.NET / PostgreSQL 登录 / 产物 / 端口 / OVMS / PATH；`--fix` 自动修可修项 |
| `bh dev autostart [on\|off\|status]` | dev 登录自启（写 `baihua-native.cmd`）；**别和 wslc 自启同开** |
| `bh dev logs <svc> [n] [-f] [-e]` | tail 日志（默认 50 行）；`-f` 跟随，`-e` 看 stderr 日志 |
| `bh dev dashboard` / `open` | 打开 WebUI（dashboard 走 cli-token 自动登录） |

> dev 形态不管 PostgreSQL 安装（自行装 Windows 服务 `postgresql-x64-18`）。
> 环境变量：`PG_HOST`（默认 127.0.0.1）、`PG_USER`（默认 postgres）、`PG_PASSWORD`、`PG_DATABASE`（默认 baihua）。

**OVMS（本地模型服务，REST :8000）不是 native 的一部分**，`bh` 只负责「按当前形态启停它」：

| 形态 | 何时出现 | `bh ovms start/stop` 做什么 |
|------|----------|------------------------------|
| `wslc` | 2026-11-15 起的现行形态：容器 `ovms-gpu`（WSL3 自带 `wslc.exe`） | 优先调用 `%BAIHUA_HOME%\ovms\start-ovms-wslc.ps1`（内含端口占用检查 + 等模型就绪），没有就用 `wslc start/stop ovms-gpu` |
| `service` | 历史形态：Windows SCM 服务 `ovms`（本机 2026-11-15 已卸载，仍兼容） | `Start-Service` / `Stop-Service`（需要管理员） |
| `process` | 只是个外来进程占着 :8000 | 只报告 pid，不代为 kill |

```powershell
bh ovms status      # 形态 + 容器状态 + /v1/models 里的模型名
bh ovms start       # 容器没起就拉起，等到模型就绪（最多 120s）
bh ovms restart     # 跑久了 tok/s 衰减时的一键恢复
bh ovms logs 100    # wslc logs --tail 100 ovms-gpu
```

> 运行手册（重建镜像、常见坑、为何不用 Windows 服务）：`%BAIHUA_HOME%\ovms\README.md`。
> 没有 OVMS 也能跑百花，只是本地 AI 功能降级；`bh doctor` 会把这种情况标成 `[WARN]` 而不是失败。
> 旧入口 `bh start|stop|restart|status openvino` 仍可用（= `bh ovms ...`），文案里说的 `openvino` 就是 OVMS。

### wslc 命令速查（`bh`，Windows 默认形态）

前置：**WSL 3**（`wsl --version` 看版本，`wsl --update` 升级；`wslc.exe` 随 WSL 提供，**不需要 Docker Desktop**）。
架构对应关系：`postgres` + `server`（8788）+ `webui`（5177）+ `nginx`（宿主 :80，浏览器与花记移动端统一入口）四个容器，
用户自定义网络 `bh-net`，镜像用 `docker/Dockerfile.server|webui` 由 `wslc build` 构建。

> 旧写法 `bh wslc <cmd>` 仍然可用（= `bh <cmd>`），用于脚本兼容；新代码/文档一律写 `bh <cmd>`。

| 命令 | 说明 |
|------|------|
| `bh build [server\|webui]` | 构建镜像（首次会拉 .NET SDK 镜像，较慢；源码变更后重跑） |
| `bh build -Prebuilt` | 预编译路径：宿主机 `dotnet publish -r linux-x64` → 打进 `aspnet:10.0` 运行时镜像（拉不到 SDK 镜像时用） |
| `bh start [svc...]` | 启动（默认全部：postgres → server → webui → nginx），等 8788/5177 就绪；**端口被 dev/native 占用会直接拒绝** |
| `bh stop [svc...]` / `restart` | 停止并删除容器（可只停指定服务；命名卷保留数据）/ 重启 |
| `bh status [--json]` | 容器列表 + 访问地址 + OVMS + 端口占用者；`--json` 给 DSH 插件（`cell: wslc`） |
| `bh doctor [--json]` | wslc 体检：wslc/版本、镜像、命名卷、容器、端口冲突、OVMS、AI 对话页后端、.env.wslc |
| `bh autostart [on\|off\|status]` | 登录自启：写 `baihua-wslc.cmd`（登录时 `bh start`）；会自动停用旧的 native 自启项 |
| `bh logs <svc> [n]` | 日志（server / webui / postgres / nginx） |
| `bh exec <svc> <cmd...>` | 在运行中的容器里执行命令（如 `bh exec postgres psql -U baihua -d baihua`） |
| `bh ps` / `pull` | 容器列表 / 拉取 postgres 镜像 |
| `bh update` | git pull + build + start |
| `bh dashboard` / `open` | 打开 WebUI（dashboard 尝试 cli-token 自动登录） |
| `bh env` | 查看/生成 `docker/.env.wslc`（首次自动生成，含随机 `PG_PASSWORD`） |

要点：
- **数据**：命名卷 `bh-pgdata`（PostgreSQL，挂 `/var/lib/postgresql`，pg18 布局）、`bh-data`（`/app/data`，密钥/配置）、
  `bh-logs`（`/app/logs`）；知识库目录 `VAULTS_DIR`（默认 `%USERPROFILE%\Vaults`）以 Windows 路径绑定挂载到 `/vaults`。
- **PostgreSQL** 只绑 `127.0.0.1:5433`（避开宿主 5432 上可能已装的 PostgreSQL）。
- **镜像源**：Docker Hub 在国内常直连超时，`docker/.env.wslc` 的 `BAIHUA_WSL_MIRROR` 默认 `docker.m.daocloud.io`，
  postgres 经它拉取；.NET 基础镜像走 `mcr.microsoft.com`（可直连）。留空则用官方 registry。
- **OVMS 是独立容器 `ovms-gpu`**（:8000，不属本栈）：`bh start` 会逐个探测宿主网卡地址，把可达的
  `OpenVinoOms__BaseUrl` 传进 server 容器（探测不到用 `docker/.env.wslc` 的 `OVMS_HOST_IP` 指定）。
  `bh ovms status|start|stop` 管它（两形态通用）。
- **局域网入口**：由 nginx 容器发布到宿主 `0.0.0.0:<BAIHUA_NGINX_PORT>`（默认 80，配置 `docker/nginx/wslc.conf`）：
  `/mg/*`、`/api/*`、`/pair`、`/ws/*`、`/mcp` → server，其余 → WebUI。**wslc 形态不需要 `bh lan on`**
  （执行会提示）；之前建过 portproxy 的先 `bh lan off`，否则 nginx 无法绑定。
  **宿主 :80 常被 http.sys/IIS/其它服务占着**：`bh doctor` 会 `[WARN]` 报出来，此时在 `docker/.env.wslc` 里
  设 `BAIHUA_NGINX_PORT=8080` 再 `bh restart` 即可绕过（8788/5177 直连始终可用）。
- **与 dev(native) 互斥**：两种形态都占 8788/5177，切换前 `bh stop`（wslc）或 `bh dev stop`（native）；`bh start` 会检测并拒绝。
- **重启后**：`wslc` 没有 restart policy，容器不会自己回来 —— 用 `bh autostart on` 让登录时跑 `bh start`；
  OVMS 容器另有自己的开机自启项 `ovms-wslc.cmd`，不受影响。
- **镜像构建拉不到 SDK 时**：本机实测 `wslc pull mcr.microsoft.com/dotnet/sdk:*` 会卡住（`aspnet:*` 正常），
  用 `bh build -Prebuilt`（宿主机 publish + 预构建 Dockerfile）即可绕开 SDK 镜像。
- **管理 API 来源网段**：容器模式下 server 的 `BAIHUA_ADMIN_ALLOWED_NETS` 额外放行 `169.254.0.0/16`
  （wslc 的 127.0.0.1 端口代理在容器内表现为这个源地址，不放行会让宿主机访问 `/api/*` 返回 403）。

## 自检（回归）

```powershell
pwsh -File scripts/verify-bh-native.ps1          # dev(native)：bh-k3s 残留 / 无 docker·kubectl·WSL 发行版依赖 / bh dev status(--json) / doctor / autostart / ovms / 旧写法
pwsh -File scripts/verify-bh-wslc.ps1            # wslc(默认)：默认路由 cell=wslc / doctor / autostart / bh dev 路由 / 只用 wslc 的静态检查
pwsh -File scripts/verify-bh-native.ps1 -Quiet   # 只输出失败与汇总
```

只读脚本（不 start/stop 服务），改了 `tools/bh/*.ps1` 后建议先跑一遍——
`dotnet test` 覆盖不到 PowerShell 入口。

### Linux（k3s 形态 —— 也是默认，而且只有这一种）

Linux 机器上 `bh` **就是** k3s 形态（`linux/k8s/bh.sh`）：没有第二个形态可切换，所以不存在
「默认切谁」的问题 —— 与 Windows 的差别只在命令面：

| 平台 | 默认 | 第二形态 | 自启 |
|------|------|----------|------|
| Windows | `bh` = wslc 容器 | `bh dev` = native（开发） | `bh autostart on`（用户级 Startup） |
| Linux | `bh` = k3s（systemd 服务） | 无 | `systemctl enable k3s`（k3s 本身是服务，Pod 由 Deployment 自愈） |

命令：`build` / `deploy` / `up` / `update` / `prune` / `status [--json]` / `start|stop|restart <svc>` /
`logs <svc> [n]` / `destroy` / `dashboard` / `openvino <on|off|status>` / **`doctor`（新增）** / `help`。

```bash
bh doctor          # 只读体检：k3s/版本、k3s.service 是否 active+enabled、API 可达、
                   # namespace、五个 Deployment 的 ready 数、PVC 是否 Bound、:80/8788/5177、
                   # OVMS/open-webui pod —— 输出格式与 Windows 侧 bh doctor 一致
bh doctor --json   # 同构 JSON：cell="k8s" / summary{passed,warned,failed} / checks[{name,status,detail,fix}]
```

> k3s 没装或没跑时 `doctor` 会**短路**：只报运行时那三项（k3s/service/API），不再刷五条「deploy 不存在」的噪声。

> Windows 的 `bh dev` / `bh wslc` 在 Linux 上会明确报错（「Linux 上只有 k3s 形态」），不会静默落到 help。
> `status --json` 的 `cell` 值是 `k8s`（Windows 侧是 `native` / `wslc`），DSH 插件按 cell 区分展示。

命令速查与运维细节见 `linux/k8s/README.md`。

## 从旧写法迁移

### 2026-11-16：默认形态调换（native → wslc）

| 旧习惯（当时是 native） | 现在等价写法 |
|----|----|
| `bh start` / `bh stop` / `bh status` / `bh logs` | `bh dev start` / `bh dev stop` / `bh dev status` / `bh dev logs` |
| `bh build`（dotnet publish） | `bh dev build` |
| `bh wslc start`（显式容器形态） | `bh start`（wslc 已是默认） |
| `bh native <cmd>` | `bh dev <cmd>`（`bh native` 仍可用，会提示改名） |
| `bh autostart on`（写 native 自启） | `bh autostart on` 现在写 **wslc** 自启；native 用 `bh dev autostart on` |

> 混淆成本最低的判断法：**要改代码 → `bh dev`；要用百花 → `bh`**。

### 更早：从 `bh k8s` / `bh-k3s` 迁移（k3s 已从 Windows 退役）

| 旧 | 新 |
|----|----|
| `bh-k3s status` / `logs` / `dashboard` | `bh status` / `bh logs <svc>` / `bh dashboard` |
| `bh-k3s build server webui` / `up` | `bh build` / `bh build-restart`（现在是 wslc 镜像构建；native 用 `bh dev build`） |
| `bh-k3s lan on` | `bh lan on`（仅 dev/native；wslc 由 nginx 容器占入口） |
| `bh k8s <cmd>` | 同上（旧子命令已移除） |

装完新定位器后重新 `bh install` 一次即可（install 会顺带清理旧版 `bh-k3s.ps1` / `bh-k3s.cmd` 遗留定位器）。

### 数据没跟着搬

两种形态的数据是**分开的**：wslc 用命名卷（`bh-pgdata` / `bh-data` / `bh-logs`），native 用本机 PostgreSQL + `~/.baihua`。
`bh` 切换形态**不会迁移数据**。要搬家需要单独做 `pg_dump` → `psql` 恢复 + vault/密钥目录同步（还没做成命令，需要时手工或另写脚本）。

## 安装

```powershell
# Windows（写入用户 PATH，新终端生效）
.\tools\bh\bh.ps1 install
```

```bash
# Linux（装 k3s 形态入口）
bash tools/bh/bh.sh install            # 普通用户 → ~/.local/bin
sudo bash tools/bh/bh.sh install       # root → /usr/local/bin（在 sudo secure_path 内）
```

> **目录改名/移动后不用重装。** 安装的是自包含定位器（`locator*.sh/.ps1` 的副本，非软链）。
> 每次调用按 `$BAIHUA_HOME` → 常见路径 → 当前目录向上 的顺序自动定位仓库根，再转发到仓库内脚本。
> 想重新定位只需 `export BAIHUA_HOME=<新路径>`（Windows: `$env:BAIHUA_HOME`）。

> **sudo 下找不到 bh？** `sudo bh` 用 root 的 `secure_path`（不含 `~/.local/bin`）。
> 用 `sudo bash tools/bh/bh.sh install`（装到 `/usr/local/bin/bh`）即可。

## 踩坑记录（实现注意）

- PowerShell 5.1/7 中 splatting `[string]` 变量会把字符串拆成字符数组（`@Arg1` = 每个字符一个参数），string 必须直接位置传参，只有数组才 splat。
- 强类型数组 `$arr[1..1]` 单元素范围索引返回裸元素而非数组，收尾用 `@($arr | Select-Object -Skip 1)` 强制数组。
- PowerShell 脚本（含中文）必须存 UTF-8 with BOM（`bh.ps1` / `locator*.ps1` 都带 BOM），否则 5.1 按 GBK 误读破坏结构；`.cmd` 文件必须纯 ASCII（cmd 按 ANSI 读）。
- `bh lan status/on` 的 portproxy 规则校验**只认 `:80 → 127.0.0.1:8788`**：k3s 时代遗留的 `:80 -> <WSL IP>:80` 规则指向已消失的 WSL IP，连接被接受但永不响应（HTTP 挂死超时）——`lan on` 会先 delete 再 add 清理这类遗留。
- `Get-HostLanIp` 排除 `WSL|Hyper-V|vEthernet|Loopback` 虚拟网卡：只按私有地址正则会把 WSL 的 vEthernet（172.x）误当成宿主 IP。
- （历史，WSL 已卸载）`wsl.exe` 传参剥离反斜杠、`wsl -e sudo` 卡密码、跨 WSL 转发必须经管道逐行——详见 git 历史的 bh-k3s.ps1。
- **`wslc list --format json` 的输出不是合法 JSON**：`Command` 字段被截断成含孤立代理字符的字符串，`ConvertFrom-Json` 必抛错。判断容器状态用 `wslc container inspect <name>` 的文本（正则取 `"Status": "running"`），别解析 list 的 JSON。
- **端口探测不要用 `TcpClient.Connect`**：有防火墙/WFP 过滤时，对未监听的回环端口不返回 RST 而是等到超时（实测 ~2.05s/次），`bh status` 探 4 个端口会卡 8s。改用 `GetIPGlobalProperties().GetActiveTcpListeners()` 枚举（~30ms，native 与 wslc 两处都已改）。
- **`bh` 不带参数 = 状态总览**：旧 help 写「打开管理面板」但实现是打印 help（不一致）。现在统一为 `status`——不会突然弹浏览器，只读且附带下一步提示。
- **服务启动是静默的**（2026-11-15）：计划任务在用户会话里直接起控制台程序会分配一个**可见**控制台窗口，而 `start-<svc>.cmd` 是同步等 exe 的，窗口就挂在服务整个生命周期（两个服务 = 两个黑框）。
  现在任务动作是 `out\native\bin\bh-launch.exe <start-<svc>.cmd> <logs 目录>`：**GUI 子系统**的小启动器（自身没有控制台，没有窗口可闪），内部 `Process.Start(cmd.exe /c <cmd>, CreateNoWindow=true)` 并 `WaitForExit`。
  为什么不用 `powershell -WindowStyle Hidden`：它只是"先创建控制台再隐藏"，实测仍有 0.1~0.3s 闪；也不用 `wscript`+VBS（VBScript 已被微软列入弃用路线）。
  启动器由 `bh start` 首次使用时用 `Add-Type -OutputAssembly -OutputType WindowsApplication` 编译（4KB，落在 `out\native\bin\`，`out` 被 gitignore）；编译失败则依次退回 PowerShell 隐藏外壳、直接 cmd。
  无论哪一层，外壳都**同步等** cmd 结束 —— 任务实例必须继续持有服务进程，否则任务结束会把它连坐杀掉（2026-11-04 实测）。旧版本留下的 `start-<svc>.ps1` / `.vbs` 会被自动清掉。
- **关掉调用方终端不会杀掉服务**：服务不是终端的子进程（父链是 计划任务 → wscript → cmd → exe），而且 cmd 的隐藏控制台与你的终端无关。反过来说，`Ctrl+C`/关窗口只影响 `bh` 命令本身。
- `bh doctor` 里的 PostgreSQL 登录检查必须带 `psql -w`（never prompt）：否则口令为空时 psql 会等 stdin，体检直接挂住。
