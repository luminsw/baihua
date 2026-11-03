# bh - 百花 CLI（Windows native）

**native 是 Windows 上唯一的部署形态**（2026-10-27 起）：dotnet 进程直接跑在 Windows 上，
**不依赖 WSL / Docker / k3s**。历史上的 `bh-k3s`（经 WSL 的全容器化形态）已随本机 WSL 卸载而退役——
Windows 侧入口与定位器已删除；Linux 侧 k3s 实现仍保留在 `linux/k8s/`（供 Linux 机器独立使用，与 Windows `bh` 无关）。

| 命令 | 形态 | 跑在哪 | 依赖 | 默认命令 |
|------|------|--------|------|----------|
| `bh`（Windows） | **native（唯一）** | Windows dotnet 进程（`bh-server.exe` / `bh-webui.exe`） | .NET SDK 10、本机 PostgreSQL、可选 OVMS | `dashboard` |
| `bh`（Linux 上） | k3s | Linux k3s（containerd） | Linux + k3s | `status` |

> Windows 上传入已移除的旧写法 `bh k8s <cmd>` / `bh-k3s <cmd>` 会失败并给出迁移指引。

```
tools/bh/
├── bh.ps1 / bh.cmd          Windows 入口：native（不含任何 WSL 逻辑；install 时清理旧版 bh-k3s 遗留定位器）
├── locator.ps1              Windows 自包含定位器（复制到 PATH 用）
├── bh.sh / locator.sh       Linux 入口（指向 linux/k8s/bh.sh；Linux 上 bh 即 k3s 形态）
├── win/native/              native 实现（build/start/stop/status/logs/dashboard…）
└── linux/k8s/               k3s 实现（Linux 专用；Windows 上已退役）
```

## 首次运行（必读）

```powershell
# ① 装 .NET SDK 10（bh build 也会在缺失时用 winget 自动装）
winget install --id Microsoft.DotNet.SDK.10

# ② 装 PostgreSQL，并准备密码（bh 不管理 PG 的安装；默认口令不可用时会连不上）
#    - 装 Windows 服务 postgresql-x64-18
#    - 建库：CREATE DATABASE baihua;
#    - 后端读环境变量 PG_PASSWORD（PG_HOST=127.0.0.1 / PG_USER=postgres / PG_DATABASE=baihua）
#    - 给当前用户设一次即可：setx PG_PASSWORD "<你的口令>"

# ③ 装 bh 定位器并加入 PATH
.\tools\bh\bh.ps1 install        # 新开终端后 bh 直接可用（并清理旧版 bh-k3s 遗留）

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

### k3s 形态（已从 Windows 退役）

k3s 需要 Linux 运行时（Windows 上曾经 WSL2 转发，**本机 WSL 已于 2026-10-27 卸载**）。
Windows 侧 `bh-k3s` 入口已删除；如将来需要 k8s 能力：

1. 重装 WSL2（`wsl --install -d Ubuntu`），并按 `linux/k8s/README.md` 一键安装；
2. `bh lan on` 会自动清理指向已消失 WSL IP 的遗留 portproxy 规则并重建 native 转发。

Linux 机器不受影响：`bh`（= k3s 形态）照常可用。

## 用法

```
bh <command> [args]           native 命令（Windows）
bh lan [on|off|status]        局域网入口（宿主 :80→:8788 portproxy）
bh install / uninstall        安装 / 移除定位器（加入用户 PATH）
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

### Linux（k3s 形态）

在 Linux 机器上 `bh` 等同 k3s 形态（`linux/k8s/bh.sh`）：build/deploy/up/status/logs 等，
命令速查见 `linux/k8s/README.md`。

## 从 `bh k8s` / `bh-k3s` 迁移（Windows）

k3s 形态已从 Windows 退役，对应关系：

| 旧 | 新 |
|----|----|
| `bh-k3s status` / `logs` / `dashboard` | `bh status` / `bh logs <svc>` / `bh dashboard`（native） |
| `bh-k3s build server webui` / `up` | `bh build` / `bh build-restart`（native dotnet publish） |
| `bh-k3s lan on` | `bh lan on`（会清理 WSL 遗留 portproxy） |
| `bh k8s <cmd>` | 同上（旧子命令已移除） |
| `bh native <cmd>` | `bh <cmd>`（native 就是默认，无需前缀） |

装完新定位器后重新 `bh install` 一次即可（install 会顺带清理旧版 `bh-k3s.ps1` / `bh-k3s.cmd` 遗留定位器）。

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
