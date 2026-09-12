# bh - 百花统一 CLI

百花只有**一种部署形态**：Linux k3s（PostgreSQL + 后端 + WebUI + OVMS 全部容器化）。
合并为单进程 + 单库后，原先的 native / docker cell 已删除；Windows 上经 WSL 调用同一套 k3s cell。

```
tools/bh/
├── bh.ps1          Windows 入口（经 WSL 路由到 Linux k3s cell）
├── bh.cmd          Windows cmd shim（让 cmd/PowerShell 都能直接 `bh`）
├── bh.sh           Linux 入口
├── locator.ps1/.sh 自包含定位器（安装到 PATH 用）
└── linux/k8s/      Linux k3s（containerd，nerdctl 构建）bh.sh —— 唯一 cell
```

## 用法

```
bh <command> [args]           执行命令
bh k8s <command> [args]       同上（显式写 cell，兼容旧习惯）
bh lan [on|off|status]        局域网入口（宿主 :80 → WSL k3s），默认 status
bh install / uninstall        安装到 PATH / 移除
```

> **Windows 侧的两个自动动作**（都在 `bh.ps1`，只读命令不动）：
> 1. **局域网入口**：`start`/`deploy`/`up`/`restart`/`dashboard` 后自动确保宿主 :80 → WSL 转发，
>    用「连通性」判断而非解析 `netsh` 输出；未就绪才弹一次 UAC（幂等，WSL 重启后自动重做）。
> 2. **配对地址校正**：`start`/`deploy`/`up`/`restart` 后把 ConfigMap 的 `Baihua__PublicBaseUrl`
>    校正为当前宿主 LAN IP（值变了才 patch + 滚动重启 `bh-server`），免得二维码扫出旧地址。

- Windows 上 `bh ...` 自动经 `wsl -u root` 路由到 Linux k3s cell（路径经 `wslpath` 转换）。
- Linux 上 `build`/`deploy`/`update` 需 root（containerd socket / k3s.yaml 仅 root 可读），
  `status`/`logs`/`dashboard` 等只读命令检测到配置不可读时自动提权，无需手动 sudo。
- 完整命令清单：`bh help`。

### k8s cell 命令速查

| 命令 | 说明 |
|------|------|
| `bh build [img...]` | 构建镜像进 k3s containerd；默认全部，可指定部分（如 `bh build server webui`） |
| `bh deploy` | `kubectl apply` k8s/ 清单（含 postgres）+ 滚动重启应用 |
| `bh up` | 按 git 变更只构建受影响镜像 + deploy（未变更镜像跳过）；`bh up --all` 强制全量 |
| `bh update` | `git pull` + `up` |
| `bh status` | pods / svc / pvc 总览（免 sudo） |
| `bh logs <svc> [n]` | tail pod 日志，默认 50 行（免 sudo）；svc: server / webui / openvino / postgres |
| `bh prune` | 清空 buildkit 构建缓存（释放磁盘、修复 nuget 缓存损坏导致的构建失败） |
| `bh dashboard` | 打开 WebUI（cli-token 自动登录）；Windows 侧自动用默认浏览器打开 |
| `bh lan [on\|off\|status]` | 局域网入口（宿主 :80 → WSL k3s Traefik）：查看 / 配置 / 撤销 |
| `bh openvino <on\|off\|status>` | Intel GPU 相关服务按需启停 |
| `bh destroy` | 删除 baihua 命名空间 |

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
