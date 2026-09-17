# bh / bh-k3s - 百花 CLI

两个命令，两种部署形态，**前提条件互不重叠**（这是拆开的原因：一个命令名无法同时表达两套前提）。

| 命令 | 形态 | 跑在哪 | 依赖 | 默认命令 |
|------|------|--------|------|----------|
| `bh` | **native（Windows 默认）** | Windows 上的 dotnet 进程（`bh-server.exe` / `bh-webui.exe`） | .NET SDK 10、本机 PostgreSQL、可选 OVMS | `dashboard` |
| `bh-k3s` | **k3s（全容器化）** | WSL2 内的 Linux k3s（containerd，无 docker） | WSL2 + Linux 发行版 + k3s + Traefik | `status` |
| `bh`（Linux 上） | 同 `bh-k3s` | 同 `bh-k3s` | 同 `bh-k3s` | `status` |

> Linux 只有 k3s 一种形态，因此 Linux 上 `bh` 与 `bh-k3s` 是**同一实现的两个名字**；Windows 上两者才是不同形态。
> Windows 上传入已移除的旧写法 `bh k8s <cmd>` 会失败并提示改用 `bh-k3s <cmd>`。

```
tools/bh/
├── bh.ps1 / bh.cmd          Windows 入口：native（不含任何 WSL 逻辑）
├── bh-k3s.ps1 / bh-k3s.cmd  Windows 入口：k3s（经 WSL 转发 + Windows 侧代开浏览器/portproxy）
├── locator.ps1 / locator-k3s.ps1   Windows 自包含定位器（复制到 PATH 用）
├── bh.sh / bh-k3s.sh        Linux 入口（都指向 linux/k8s/bh.sh）
├── locator.sh / locator-k3s.sh     Linux 自包含定位器
├── win/native/              native 实现（build/start/stop/status/logs/dashboard…）
└── linux/k8s/               k3s 实现（build/up/deploy/status/logs/openvino…）
```

## 首次运行（必读）

两边的"从零到能用"路径不同，**不要混用**：

### 路线 A：native（Windows 本地，不开 WSL）

```powershell
# ① 装 .NET SDK 10（bh build 也会在缺失时用 winget 自动装）
winget install --id Microsoft.DotNet.SDK.10

# ② 装 PostgreSQL，并准备密码（bh 不管理 PG 的安装；默认口令不可用时会连不上）
#    - 装 Windows 服务 postgresql-x64-18
#    - 建库：CREATE DATABASE baihua;
#    - 后端读环境变量 PG_PASSWORD（PG_HOST=127.0.0.1 / PG_USER=postgres / PG_DATABASE=baihua）
#    - 给当前用户设一次即可：setx PG_PASSWORD "<你的口令>"

# ③ 装 bh 定位器并加入 PATH
.\tools\bh\bh.ps1 install        # 新开终端后 bh / bh-k3s 直接可用

# ④ 构建 + 启动（首次 publish 需要几分钟）
bh build
bh start
bh status                        # 期望：server 8788 / webui 5177 均 ready
bh dashboard                     # 打开 WebUI（cli-token 自动登录）
```

可选：IPv6/局域网访问用 `bh lan on`（宿主 `:80 -> :8788` portproxy）；本地推理用
`scripts/install-openvino-ovms-service.ps1` 装 OVMS（REST :8000）。

> native 侧目前**没有** `onboard` 体检命令：缺 PostgreSQL 或口令不对时，`bh start` 只会报
> "后端起不来"，需要自己按上面 ①②检查。`bh status` / `bh logs server` 是主要排查手段。

### 路线 B：k3s（全容器化，Windows 需要 WSL2）

k3s 是 Linux 运行时，**Windows 上必须经 WSL2**（没有原生 Windows k3s）。首次安装分三段：

```powershell
# ① Windows：装 WSL2 与发行版（管理员 PowerShell，装完需重启）
wsl --install -d Ubuntu
```

```bash
# ② WSL 内：一键装 k3s + clone 仓库 + bh install + 构建部署（几分钟到十几分钟）
wsl -d Ubuntu
curl -fsSL https://raw.githubusercontent.com/luminsw/baihua/main/scripts/install-baihua.sh | bash
#   选项：--release <tag> / --dir <path> / --skip-k3s / --no-up
#   详见 k8s/README.md「一键安装」
```

```powershell
# ③ Windows：装 bh-k3s 包装层（定位器，经 WSL 转发命令）
.\tools\bh\bh.ps1 install        # 一次装好 bh 与 bh-k3s

# ④ Windows：保活 + 登录自启（管理员终端；**必做**，否则见下方"稳定性"）
bh-k3s autostart on

bh-k3s status                    # 期望：5 个 pod 全 1/1 Running + entry: http://localhost/
bh-k3s dashboard                 # 自动用 Windows 默认浏览器打开（WSL 内无浏览器）
```

**WSL 前置要求**：WSL2 + 至少一个发行版（`wsl -l -v` 能看到、`Version` 为 2）。发行版停着也能用
（首次调用会自动冷启动，多等几秒）。k3s 必须在 WSL 内安装，`bh-k3s` 只负责转发和宿主侧收尾。

### k3s 稳定性：必须保活 WSL（否则集群会"莫名反复重启"）

**这是本机实测踩到的真坑，不是端口冲突**：WSL 发行版是"按需启动、空闲回收"的生命周期 ——
发行版里最后一个会话结束时，**整个 k3s 集群随之消失**，所有容器被 SIGTERM（退出码 143），
restart 计数一路累积（实测 `bh-postgres` 涨到 107 次、`bh-server` 61 次）。表象极像"服务起不来/端口冲突"，
真因却是运行时被回收。判断依据（两个都很快）：

```powershell
bh-k3s autostart status     # 看"保活进程 / 登录自启任务"两行
wsl -u root -e bash -lc "uptime"   # 发行版只启动了几十秒 → 就是被回收了
wsl -u root -e bash -lc "journalctl --list-boots"   # 大量 30~60 秒的短会话 = 反复重启
```

修复（`bh-k3s autostart on`，需管理员，幂等）：

| 做的事 | 说明 |
|---|---|
| 常驻 `wsl.exe` 保活进程 | `wsl.exe -d <发行版> -u root -e sleep infinity` 常驻（约 8 MB），发行版不再被回收 |
| 登录自启计划任务 | 计划任务 `Baihua-WSL-KeepAlive`（交互式登录会话），登录即保活并确认 k3s active |
| `.wslconfig` 资源上限 | 仅在**没有**该文件时写 `memory=12GB / swap=8GB / processors=8`；已存在则不动、只提示 |

> ⚠️ **反面做法（实测无效，勿用）**：`wsl ... -c "nohup sleep infinity &"`。命令一返回 WSL 就认为会话结束，
> 发行版立刻回收（实测随即变 `Stopped`），后台进程连同集群一起消失。必须让 **Windows 侧的 `wsl.exe` 进程本身**常驻。
> 同理，`install-baihua.sh` 在无 systemd 环境用 `nohup k3s server &` 启动的集群也活不过会话结束 ——
> 所以 Windows + WSL2 场景下 `bh-k3s autostart on` 是**必做项**，不是可选优化。

排查用的两个只读脚本（在 WSL 内执行）：

```bash
wsl -u root -e bash <仓库>/scripts/k3s-keepalive-check.sh   # 发行版 uptime + 容器启动时刻/重启计数
wsl -u root -e bash -lc "cd <仓库> && python3 scripts/k3s-probes.py"   # 各 deployment 探针/资源/环境变量
```

**WSL 重启后要重做的一步**：WSL 的 IP 会变，宿主 `:80` 转发会失效 —— 下次 `bh-k3s start`/`deploy`/`up`/
`dashboard` 会自动重建（`bh-k3s lan status` 可查看，`bh-k3s lan on` 手动重建）。
想免掉 UAC，可改用 WSL mirrored 网络（见 `k8s/README.md`）。

## 用法

```
bh <command> [args]           native 命令（Windows）；Linux 上等同 bh-k3s
bh lan [on|off|status]        局域网入口（native: 宿主 :80→:8788 portproxy）
bh install / uninstall        安装（bh + bh-k3s 定位器，加入用户 PATH）/ 移除

bh-k3s <command> [args]        k3s 命令（Windows 经 WSL；Linux 等同 bh）
bh-k3s lan [on|off|status]    局域网入口（宿主 :80 → WSL k3s Traefik）
bh-k3s install / uninstall     安装 / 移除定位器（与 bh install 等价）
```

### native 命令速查（`bh`）

| 命令 | 说明 |
|------|------|
| `bh build [svc...]` | dotnet publish 到 out/native/（默认全部，可指定 server/webui） |
| `bh build-restart [svc...]` | build + restart |
| `bh start [svc...]` | 启动服务（默认全部，按依赖顺序 server→webui） |
| `bh stop [svc...]` | 停止服务 |
| `bh restart [svc...]` | stop + start |
| `bh update` | git pull + build + start + 防火墙放行 |
| `bh status` | 端口/进程状态 |
| `bh status --json` | JSON 格式（供 DSH 插件） |
| `bh logs <svc> [n]` | tail 日志，默认 50 行 |
| `bh dashboard` | 打开 WebUI（cli-token 自动登录） |
| `bh lan [on\|off\|status]` | 局域网入口（宿主 :80 → :8788 portproxy） |

> native 不管 PostgreSQL 安装（用户自行装 Windows 服务 `postgresql-x64-18`），
> OpenVINO 用 `ovms` 进程/系统服务（REST :8000，`scripts/install-openvino-ovms-service.ps1` 安装）。
> 环境变量：`PG_HOST`（默认 127.0.0.1）、`PG_USER`（默认 postgres）、`PG_PASSWORD`、`PG_DATABASE`（默认 baihua）。

### k3s 命令速查（`bh-k3s`）

| 命令 | 说明 |
|------|------|
| `bh-k3s build [img...]` | 构建镜像进 k3s containerd；默认全部，可指定部分（如 `bh-k3s build server webui`） |
| `bh-k3s deploy` | `kubectl apply` k8s/ 清单（含 postgres）+ 滚动重启应用 |
| `bh-k3s up` | 全量重建 .NET 应用镜像 + deploy（openvino 镜像需单独 build） |
| `bh-k3s update` | `git pull` + `up` |
| `bh-k3s status` | pods / svc / pvc 总览（免 sudo；`--json` 供 DSH 插件） |
| `bh-k3s logs <svc> [n]` | tail pod 日志，默认 50 行（免 sudo）；svc: server / webui / openvino / postgres |
| `bh-k3s start\|stop\|restart <svc>` | 单服务伸缩 / 滚动重启 |
| `bh-k3s prune` | 清空 buildkit 构建缓存 |
| `bh-k3s dashboard` | 打开 WebUI（cli-token 自动登录）；Windows 侧自动用默认浏览器打开 |
| `bh-k3s lan [on\|off\|status]` | 局域网入口（宿主 :80 → WSL k3s Traefik） |
| `bh-k3s autostart [on\|off\|status]` | WSL 保活 + 登录自启（避免发行版被空闲回收拖垮集群；见上方"稳定性"） |
| `bh-k3s openvino <on\|off\|status>` | Intel GPU 相关服务按需启停 |
| `bh-k3s destroy` | 删除 baihua 命名空间 |

> 镜像与工作负载：`bh-server`（唯一后端，8788）、`bh-webui`（5177）、`bh-openvino`（8000）、`bh-postgres`。
> 合并前的 `bh-family` / `bh-ai` / `bh-vault` 已不存在。

## 从 `bh k8s` 迁移

`bh k8s <cmd>` 已**移除**（k3s 从"子 cell"变成独立命令），对应关系：

| 旧 | 新 |
|----|----|
| `bh k8s build server webui` | `bh-k3s build server webui` |
| `bh k8s up` / `deploy` / `update` | `bh-k3s up` / `deploy` / `update` |
| `bh k8s status` / `logs webui 100` | `bh-k3s status` / `logs webui 100` |
| `bh k8s dashboard` | `bh-k3s dashboard` |
| `bh k8s lan on` | `bh-k3s lan on` |
| `bh k8s openvino on` | `bh-k3s openvino on` |
| `bh native <cmd>`（Windows） | `bh <cmd>`（native 就是默认，无需前缀） |

装完新定位器后重新 `bh install` 一次即可（`bh k8s` 会打印带迁移命令的提示并退出 1）。

## 安装

```powershell
# Windows（写入用户 PATH，新终端生效）—— 同时装 bh 与 bh-k3s
.\tools\bh\bh.ps1 install
```

```bash
# Linux / WSL（同时装 bh 与 bh-k3s）
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
- PowerShell 脚本（含中文）必须存 UTF-8 with BOM（`bh.ps1` / `bh-k3s.ps1` / `locator*.ps1` 都带 BOM），否则 5.1 按 GBK 误读破坏结构；`.cmd` 文件必须纯 ASCII（cmd 按 ANSI 读）。
- `wsl.exe` 传参剥离反斜杠，Windows→WSL 路径先 `-replace '\\','/'` 再 `wsl wslpath -u`。
- `wsl -e sudo` 会卡密码提示（WSL 默认 sudo 要密码），用 `wsl -u root` 免密。
- 跨 WSL 转发**必须经管道逐行转发**（`wsl ... 2>&1 | ForEach-Object { Write-Host $_ }`）：`wsl.exe` 是原生子进程，stdout 不是控制台时紧跟的 `exit` 会在输出刷出前终止进程，表现为"命令成功却没有任何输出"。
