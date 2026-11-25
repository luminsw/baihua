#!/bin/bash
# install-baihua.sh — 百花一键安装脚本（原生 Linux：systemd + SQLite）
#
# 用法（任一即可）：
#   curl -fsSL https://raw.githubusercontent.com/luminsw/baihua/main/scripts/install-baihua.sh | bash
#   curl -fsSL https://github.com/luminsw/baihua/releases/latest/download/install-baihua.sh | bash
#
# 脚本做的事：
#   1. 平台检测（Linux；WSL 需要已启用 systemd），确认 systemd / .NET 10 SDK
#   2. clone 仓库到 ~/src/baihua（或 --dir 指定；已存在则 git pull 更新）
#   3. bh install（装 CLI 定位器 + systemd 单元，并 enable）
#   4. bh build + bh start（发布 dotnet 产物到 ~/.baihua/bin 并启动）
#   5. 提示 bh status / bh dashboard / bh proxy install
#
# 不再安装 k3s / 容器运行时 / PostgreSQL：数据库是 ~/.baihua/db/baihua.db 单文件。
#
# 选项：
#   --release <tag>   clone 指定 git tag（默认 main）
#   --dir <path>      仓库目标目录（默认 ~/src/baihua）
#   --no-start        只安装 + 发布，不启动（稍后手动 bh start）
#   --self-contained  发布自包含产物（目标机不需要装 .NET 运行时，体积更大）
#   --help            显示帮助
#
# 设计：幂等（重跑不报错）、中文提示、普通用户执行（systemd 安装内部 sudo）。
set -euo pipefail

REPO="luminsw/baihua"
DEFAULT_DIR="$HOME/src/baihua"
RELEASE="main"
DIR="$DEFAULT_DIR"
NO_START=0

# ---------- 颜色 ----------
if [ -t 1 ]; then
    C_GREEN=$'\e[32m'; C_YELLOW=$'\e[33m'; C_RED=$'\e[31m'; C_CYAN=$'\e[36m'; C_RESET=$'\e[0m'
else
    C_GREEN=""; C_YELLOW=""; C_RED=""; C_CYAN=""; C_RESET=""
fi
info()  { printf '%s[info]%s %s\n'  "$C_CYAN"   "$C_RESET" "$*"; }
ok()    { printf '%s[ok]%s   %s\n'  "$C_GREEN"  "$C_RESET" "$*"; }
warn()  { printf '%s[warn]%s %s\n'  "$C_YELLOW" "$C_RESET" "$*"; }
die()   { printf '%s[err]%s %s\n'   "$C_RED"    "$C_RESET" "$*" >&2; exit 1; }

# ---------- 参数 ----------
while [ $# -gt 0 ]; do
    case "$1" in
        --release) RELEASE="${2:-}"; shift 2 ;;
        --dir)     DIR="${2:-}"; shift 2 ;;
        --no-start) NO_START=1; shift ;;
        # 兼容旧参数名
        --no-up)   NO_START=1; shift ;;
        --self-contained) export BAIHUA_PUBLISH_SELF_CONTAINED=1; shift ;;
        --help|-h) cat <<'EOF'
install-baihua.sh — 百花一键安装脚本（原生 Linux：systemd + SQLite）

用法（任一即可）：
  curl -fsSL https://raw.githubusercontent.com/luminsw/baihua/main/scripts/install-baihua.sh | bash
  curl -fsSL https://github.com/luminsw/baihua/releases/latest/download/install-baihua.sh | bash

脚本做的事：
  1. 平台检测（Linux；WSL 需要已启用 systemd），确认 systemd / .NET 10 SDK
  2. clone 仓库到 ~/src/baihua（或 --dir 指定；已存在则 git pull 更新）
  3. bh install（装 CLI 定位器 + systemd 单元，并 enable）
  4. bh build + bh start（发布 dotnet 产物到 ~/.baihua/bin 并启动）
  5. 提示 bh status / bh dashboard / bh proxy install

选项：
  --release <tag>   clone 指定 git tag（默认 main）
  --dir <path>      仓库目标目录（默认 ~/src/baihua）
  --no-start        只安装 + 发布，不启动（稍后手动 bh start）
  --self-contained  发布自包含产物（目标机不需要装 .NET 运行时）
  --help            显示帮助

数据目录：~/.baihua（BAIHUA_HOME 可覆盖）
  db/baihua.db   SQLite 数据库（备份 = 打包该目录）
  config/*.env   环境变量覆盖
  vaults/        知识库根目录
EOF
        exit 0 ;;
        *) die "未知参数: $1（用 --help 查看用法）" ;;
    esac
done

[ -n "$RELEASE" ] || die "--release 不能为空"
[ -n "$DIR" ]     || die "--dir 不能为空"

# ---------- 平台检测 ----------
case "$(uname -s)" in
    Linux) ;;
    Darwin) die "macOS 暂不支持（百花服务端按 Linux systemd 部署）" ;;
    *) die "不支持的系统: $(uname -s)" ;;
esac

is_wsl() {
    grep -qi microsoft /proc/sys/kernel/osrelease 2>/dev/null && return 0
    grep -qi microsoft /proc/version 2>/dev/null && return 0
    if command -v systemd-detect-virt >/dev/null 2>&1; then
        [ "$(systemd-detect-virt 2>/dev/null)" = "wsl" ] && return 0
    fi
    return 1
}

PLATFORM="原生 Linux"
is_wsl && PLATFORM="WSL"
info "检测到平台: $PLATFORM"

if [ "$(id -u)" = "0" ]; then
    die "请用普通用户执行本脚本（systemd 安装内部会 sudo；root 执行会让 clone 的文件归 root）"
fi

# ---------- systemd ----------
if [ ! -d /run/systemd/system ] || ! command -v systemctl >/dev/null 2>&1; then
    if [ "$PLATFORM" = "WSL" ]; then
        die "WSL 内未启用 systemd。请在 /etc/wsl.conf 写入：
  [boot]
  systemd=true
然后在 Windows 执行 wsl --shutdown 再进入 WSL。"
    fi
    die "未检测到运行中的 systemd，百花原生形态依赖 systemd 管理服务。"
fi
ok "systemd 可用"

# ---------- sudo ----------
if command -v sudo >/dev/null 2>&1; then
    SUDO="sudo"
    sudo -n true 2>/dev/null || warn "sudo 可能需要交互输入密码（安装 systemd 单元时）"
else
    die "未找到 sudo（安装 systemd 单元需要 root 权限）"
fi

# ---------- 依赖 ----------
need_cmd() { command -v "$1" >/dev/null 2>&1 || die "缺少命令: $1（请先安装）"; }
need_cmd git
need_cmd curl

if ! command -v dotnet >/dev/null 2>&1; then
    die "未安装 .NET SDK 10。安装方式（Ubuntu/Debian）：
  sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
或参考 https://learn.microsoft.com/dotnet/core/install/linux
（也可以在有 SDK 的机器上 BAIHUA_PUBLISH_SELF_CONTAINED=1 bh build 后拷贝产物）"
fi
DOTNET_MAJOR="$(dotnet --version 2>/dev/null | cut -d. -f1)"
[ "${DOTNET_MAJOR:-0}" -ge 10 ] 2>/dev/null || die "需要 .NET SDK 10，当前：$(dotnet --version 2>/dev/null)"
ok ".NET SDK $(dotnet --version 2>/dev/null)"

# ---------- clone / 更新仓库 ----------
clone_repo() {
    if [ -d "$DIR/.git" ]; then
        info "仓库已存在: $DIR（git pull 更新）"
        git -C "$DIR" fetch --quiet
        git -C "$DIR" checkout "$RELEASE" --quiet 2>/dev/null || \
            warn "checkout $RELEASE 失败（可能不存在该分支/tag），保持当前"
        git -C "$DIR" pull --quiet 2>/dev/null || true
        ok "仓库已更新"
    else
        info "clone 仓库到 $DIR（分支/tag: $RELEASE）..."
        mkdir -p "$(dirname "$DIR")"
        git clone --depth 1 --branch "$RELEASE" "https://github.com/$REPO.git" "$DIR" 2>/dev/null || \
            git clone --branch "$RELEASE" "https://github.com/$REPO.git" "$DIR" || \
            die "clone 失败（检查网络 / 分支名: $RELEASE）"
        ok "仓库已 clone"
    fi
    [ -f "$DIR/tools/bh/bh.sh" ] || die "仓库结构异常: 缺少 tools/bh/bh.sh"
}

# ---------- 安装 CLI + systemd 单元 ----------
install_bh() {
    info "安装 bh 与 systemd 单元..."
    bash "$DIR/tools/bh/bh.sh" install
    local bin="$HOME/.local/bin"
    case ":$PATH:" in
        *":$bin:"*) ;;
        *) export PATH="$bin:$PATH" ;;
    esac
    ok "bh 已安装"
}

# ---------- 构建 + 启动 ----------
do_start() {
    if [ "$NO_START" = "1" ]; then
        warn "--no-start 指定，跳过启动。稍后手动执行: bh build && bh start"
        return 0
    fi
    info "发布服务（bh build，首次约 1~3 分钟）..."
    bash "$DIR/tools/bh/bh.sh" build || \
        die "构建失败。看日志: bash $DIR/tools/bh/bh.sh doctor"
    info "启动服务..."
    bash "$DIR/tools/bh/bh.sh" start || \
        die "启动失败。看日志: sudo journalctl -u baihua-server@$(id -un).service -n 100"
    ok "服务已启动"
}

# ---------- 主流程 ----------
info "=== 百花一键安装（原生 Linux + SQLite）==="
info "仓库: https://github.com/$REPO  分支/tag: $RELEASE  目标: $DIR"

clone_repo
install_bh
do_start

echo ""
ok "=== 百花安装完成 ==="
echo ""
printf '%s下一步：%s\n' "$C_CYAN" "$C_RESET"
printf '  查看状态:      %sbh status%s\n' "$C_GREEN" "$C_RESET"
printf '  打开管理面板:  %sbh dashboard%s\n' "$C_GREEN" "$C_RESET"
printf '  查看后端日志:  %sbh logs server 100%s\n' "$C_GREEN" "$C_RESET"
printf '  局域网 :80:    %sbh proxy install%s（可选，需要 nginx）\n' "$C_GREEN" "$C_RESET"
printf '  更新并重启:    %sbh update%s\n' "$C_GREEN" "$C_RESET"
echo ""
echo "数据目录: ${BAIHUA_HOME:-$HOME/.baihua}（数据库 db/baihua.db，备份用 bh backup）"
if [ "$PLATFORM" = "WSL" ]; then
    warn "WSL 提示：未启用 systemd 的 WSL 无法跑本形态；本仓库不支持在 WSL 里开发。"
fi
