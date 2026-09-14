# bh - 百花统一 CLI

两种部署形态：**native（默认，Windows dotnet 进程）** 和 **k8s（可选，Linux k3s 全容器化）**。
native cell 不依赖 WSL/k3s，直接用 `dotnet publish` 产物跑在 Windows 上；k8s cell 经 WSL 调用 Linux k3s。

```
tools/bh/
├── bh.ps1          Windows 入口（默认路由到 native cell；bh k8s <cmd> 走 WSL）
├── bh.cmd          Windows cmd shim（让 cmd/PowerShell 都能直接 `bh`）
├── bh.sh           Linux 入口
├── locator.ps1/.sh 自包含定位器（安装到 PATH 用）
├── win/native/     Windows native cell（dotnet 进程）bh.ps1
└── linux/k8s/      Linux k3s（containerd，nerdctl 构建）bh.sh
```

## 用法

```
bh <command> [args]           执行命令（默认 native cell）
bh native <command> [args]    显式用 native cell
bh k8s <command> [args]       显式用 k8s cell（经 WSL）
bh lan [on|off|status]        局域网入口（native: 宿主 :80→:8788 portproxy；k8s: 宿主 :80→WSL）
bh install / uninstall        安装到 PATH / 移除
```

### native cell 命令速查（默认）

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

> native cell 不管 PostgreSQL 安装（用户自行安装 Windows 服务 `postgresql-x64-18`），
> OpenVINO 用 `ovms` 进程/系统服务（REST :8000，`scripts/install-openvino-ovms-service.ps1` 安装）。
> 环境变量：`PG_HOST`（默认 127.0.0.1）、`PG_USER`（默认 postgres）、`PG_PASSWORD`、`PG_DATABASE`（默认 baihua）。

### k8s cell 命令速查

| 命令 | 说明 |
|------|------|
| `bh k8s build [img...]` | 构建镜像进 k3s containerd；默认全部，可指定部分（如 `bh k8s build server webui`） |
| `bh k8s deploy` | `kubectl apply` k8s/ 清单（含 postgres）+ 滚动重启应用 |
| `bh k8s up` | 按 git 变更只构建受影响镜像 + deploy（未变更镜像跳过）；`bh k8s up --all` 强制全量 |
| `bh k8s update` | `git pull` + `up` |
| `bh k8s status` | pods / svc / pvc 总览（免 sudo） |
| `bh k8s logs <svc> [n]` | tail pod 日志，默认 50 行（免 sudo）；svc: server / webui / openvino / postgres |
| `bh k8s prune` | 清空 buildkit 构建缓存 |
| `bh k8s dashboard` | 打开 WebUI（cli-token 自动登录）；Windows 侧自动用默认浏览器打开 |
| `bh k8s lan [on\|off\|status]` | 局域网入口（宿主 :80 → WSL k3s Traefik） |
| `bh k8s openvino <on\|off\|status>` | Intel GPU 相关服务按需启停 |
| `bh k8s destroy` | 删除 baihua 命名空间 |

> 镜像与工作负载：`bh-server`（唯一后端，8788）、`bh-webui`（5177）、`bh-openvino`（8000）、`bh-postgres`。
> 合并前的 `bh-family` / `bh-ai` / `bh-vault` 已不存在。

## 安装

```powershell
# Windows（写入用户 PATH，新终端生效）
.\tools\bh\bh.ps1 install

# Linux / WSL
bash tools/bh/bh.sh install            # 普通用户 → ~/.local/bin/bh（~/.bashrc 已含该目录时直接可用）
sudo bash tools/bh/bh.sh install       # root → /usr/local/bin/bh（在 sudo secure_path 内，sudo bh 也可用）
```

> **目录改名/移动后不用重装。** Linux 安装的是自包含定位器（`locator.sh` 的副本，非软链）。
> 每次调用按 `$BAIHUA_HOME` → 常见路径（`~/src/mdyj/baihua`、`~/src/baihuagu` 等）→ 当前目录向上
> 的顺序自动定位仓库根，再转发到仓库内的 `bh.sh`。只要仓库还在常见位置、或在仓库目录内执行、
> 或设置了 `BAIHUA_HOME`，`bh` 都能用。想要重新定位只需 `export BAIHUA_HOME=<新路径>`。

> **sudo 下找不到 bh？** `sudo bh` 用 root 的 `secure_path`（不含 `~/.local/bin`）。
> 用 `sudo bash tools/bh/bh.sh install`（装到 `/usr/local/bin/bh`）即可，或直接
> `sudo /usr/local/bin/bh <cmd>` / `sudo bash <仓库>/tools/bh/bh.sh install`。

## 踩坑记录（实现注意）

- PowerShell 5.1/7 中 splatting `[string]` 变量会把字符串拆成字符数组（`@Arg1` = 每个字符一个参数），string 必须直接位置传参，只有数组才 splat。
- 强类型数组 `$arr[1..1]` 单元素范围索引返回裸元素而非数组，收尾用 `@($arr | Select-Object -Skip 1)` 强制数组。
- PowerShell 脚本（含中文）必须存 UTF-8 with BOM，否则 5.1 按 GBK 误读破坏结构；`.cmd` 文件必须纯 ASCII（cmd 按 ANSI 读）。
- `wsl.exe` 传参剥离反斜杠，Windows→WSL 路径先 `-replace '\\','/'` 再 `wsl wslpath -u`。
- `wsl -e sudo` 会卡密码提示（WSL 默认 sudo 要密码），用 `wsl -u root` 免密。
