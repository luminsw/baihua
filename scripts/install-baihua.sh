#!/bin/bash
# install-baihua.sh — 百花一键安装部署脚本（WSL2 / 原生 Linux 通用）
#
# 用法（任一即可，需先开 WSL 终端或 Linux 终端）：
#   curl -fsSL https://raw.githubusercontent.com/luminsw/baihua/main/scripts/install-baihua.sh | bash
#   curl -fsSL https://github.com/luminsw/baihua/releases/latest/download/install-baihua.sh | bash
#
# 脚本做的事：
#   1. 平台检测（WSL2 / 原生 Linux；macOS 不支持）
#   2. 检测并安装 k3s（需 sudo；已装跳过）
#   3. clone 仓库到 ~/src/baihua（或 --dir 指定；已存在则 git pull 更新）
#   4. bh install（装到 ~/.local/bin）
#   5. bh up（构建镜像 + 部署到 k3s；长操作，几分钟到十几分钟）
#   6. 提示 bh dashboard 打开管理面板
#
# 选项：
#   --release <tag>   clone 指定 git tag（默认 main）
#   --dir <path>      仓库目标目录（默认 ~/src/baihua）
#   --skip-k3s        跳过 k3s 安装（已装时）
#   --no-up           只装不部署（装完手动 bh up）
#   --help            显示帮助
#
# 设计：幂等（重跑不报错）、中文提示、普通用户执行（k3s 安装内部 sudo）。
# 仓库文件归普通用户，后续 bh update（git pull）无需 sudo。
set -euo pipefail

REPO="luminsw/baihua"
DEFAULT_DIR="$HOME/src/baihua"
RELEASE="main"
DIR="$DEFAULT_DIR"
SKIP_K3S=0
NO_UP=0

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
        --skip-k3s) SKIP_K3S=1; shift ;;
        --no-up)   NO_UP=1; shift ;;
        --help|-h) cat <<'EOF'
install-baihua.sh — 百花一键安装部署脚本（WSL2 / 原生 Linux 通用）

用法（任一即可，需先开 WSL 终端或 Linux 终端）：
  curl -fsSL https://raw.githubusercontent.com/luminsw/baihua/main/scripts/install-baihua.sh | bash
  curl -fsSL https://github.com/luminsw/baihua/releases/latest/download/install-baihua.sh | bash

脚本做的事：
  1. 平台检测（WSL2 / 原生 Linux；macOS 不支持）
  2. 检测并安装 k3s（需 sudo；已装跳过）
  3. clone 仓库到 ~/src/baihua（或 --dir 指定；已存在则 git pull 更新）
  4. bh install（装到 ~/.local/bin）
  5. bh up（构建镜像 + 部署到 k3s；长操作，几分钟到十几分钟）
  6. 提示 bh dashboard 打开管理面板

选项：
  --release <tag>   clone 指定 git tag（默认 main）
  --dir <path>      仓库目标目录（默认 ~/src/baihua）
  --skip-k3s        跳过 k3s 安装（已装时）
  --no-up           只装不部署（装完手动 bh up）
  --help            显示帮助

设计：幂等（重跑不报错）、中文提示、普通用户执行（k3s 安装内部 sudo）。
仓库文件归普通用户，后续 bh update（git pull）无需 sudo。
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
    Darwin) die "macOS 暂不支持（百花部署依赖 k3s + containerd，推荐用 WSL2 或 Linux 服务器）" ;;
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
if is_wsl; then
    PLATFORM="WSL2"
    # WSL2 文件系统根挂载点检测（提示用，不影响逻辑）
    if grep -qi 'root=/dev/.*\bwsroot\b' /proc/cmdline 2>/dev/null || \
       ! readlink -f / >/dev/null 2>&1; then
        :
    fi
fi
info "检测到平台: $PLATFORM"

# ---------- root 检查 ----------
if [ "$(id -u)" = "0" ]; then
    die "请用普通用户执行本脚本（k3s 安装会内部 sudo；root 执行会导致 clone 的文件归 root，后续 bh update 不便）"
fi

# ---------- sudo 可用性 ----------
SUDO=""
if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
    SUDO="sudo"
elif command -v sudo >/dev/null 2>&1; then
    # 需要密码的 sudo 也接受，但提示用户可能要输密码
    warn "sudo 可能需要交互输入密码，脚本会在安装 k3s 时触发"
    SUDO="sudo"
else
    die "未找到 sudo，且当前非 root。k3s 安装需要 root 权限，请先配置 sudo 或用 root 执行（不推荐）"
fi

# ---------- 依赖命令 ----------
need_cmd() {
    command -v "$1" >/dev/null 2>&1 || die "缺少命令: $1（请先安装）"
}
need_cmd curl
need_cmd git
need_cmd tar

# ---------- k3s 检测/安装 ----------
install_k3s() {
    if command -v k3s >/dev/null 2>&1 && [ -f /etc/rancher/k3s/k3s.yaml ]; then
        ok "k3s 已安装"
        # 检查是否在运行
        if $SUDO k3s kubectl get nodes >/dev/null 2>&1; then
            ok "k3s 集群就绪"
            return 0
        fi
        warn "k3s 已装但未运行，尝试启动..."
        if command -v systemctl >/dev/null 2>&1 && $SUDO systemctl list-unit-files k3s.service >/dev/null 2>&1; then
            $SUDO systemctl start k3s || die "k3s 启动失败（用 $SUDO systemctl status k3s 查原因）"
        else
            # 无 systemd（WSL 未开 systemd）：后台启动
            warn "无 systemd，后台启动 k3s（nohup）"
            $SUDO nohup k3s server --disable=traefik >/tmp/k3s.log 2>&1 &
        fi
        wait_k3s
        return 0
    fi

    if [ "$SKIP_K3S" = "1" ]; then
        die "--skip-k3s 指定但未检测到 k3s，无法继续"
    fi

    info "安装 k3s（需 root，可能要输 sudo 密码）..."
    # k3s 默认自带 Traefik（百花 IngressRoute 依赖）
    curl -sfL https://get.k3s.io | $SUDO sh -
    ok "k3s 安装完成"
    wait_k3s
}

wait_k3s() {
    info "等待 k3s 就绪（最多 120 秒）..."
    local i
    for i in $(seq 1 120); do
        if $SUDO k3s kubectl get nodes >/dev/null 2>&1; then
            ok "k3s 就绪（${i}s）"
            # 修复 k3s.yaml 权限，让 bh 能读（bh 内部也会 sudo，但放宽权限避免每次 sudo）
            $SUDO chmod 644 /etc/rancher/k3s/k3s.yaml 2>/dev/null || true
            return 0
        fi
        sleep 1
    done
    die "k3s 120 秒内未就绪。请手动检查: $SUDO systemctl status k3s 或 /tmp/k3s.log"
}

# ---------- clone 仓库 ----------
clone_repo() {
    if [ -d "$DIR/.git" ]; then
        info "仓库已存在: $DIR（git pull 更新）"
        git -C "$DIR" fetch --quiet
        git -C "$DIR" checkout "$RELEASE" --quiet 2>/dev/null || {
            warn "checkout $RELEASE 失败（可能不存在该分支/tag），保持当前"
        }
        git -C "$DIR" pull --quiet 2>/dev/null || true
        ok "仓库已更新"
    else
        info "clone 仓库到 $DIR（分支/tag: $RELEASE）..."
        mkdir -p "$(dirname "$DIR")"
        # 用 HTTPS（无需 SSH key），clone 完后续 bh update 走 HTTPS
        git clone --depth 1 --branch "$RELEASE" "https://github.com/$REPO.git" "$DIR" 2>/dev/null || \
            git clone --branch "$RELEASE" "https://github.com/$REPO.git" "$DIR" || \
            die "clone 失败（检查网络 / 分支名: $RELEASE）"
        ok "仓库已 clone"
    fi

    # 校验 bh.sh 存在
    [ -f "$DIR/tools/bh/bh.sh" ] || die "仓库结构异常: 缺少 tools/bh/bh.sh"
}

# ---------- bh install ----------
install_bh() {
    info "安装 bh 到 PATH..."
    bash "$DIR/tools/bh/bh.sh" install
    # bh install 装到 ~/.local/bin，确认在 PATH
    local bin="$HOME/.local/bin"
    case ":$PATH:" in
        *":$bin:"*) ;;
        *) export PATH="$bin:$PATH" ;;
    esac
    ok "bh 已安装"
}

# ---------- bh up（构建+部署） ----------
do_up() {
    if [ "$NO_UP" = "1" ]; then
        warn "--no-up 指定，跳过部署。稍后手动执行: sudo bh up"
        return 0
    fi
    info "开始构建镜像 + 部署（bh up，长操作，预计 5~15 分钟）..."
    info "首次构建会下载 nerdctl/buildkit + 基础镜像，耗时较长；请耐心等待"
    # bh up 需要 root（k3s.yaml + containerd socket）
    $SUDO bash "$DIR/tools/bh/bh.sh" up || die "bh up 失败。用 $SUDO bash $DIR/tools/bh/bh.sh status 查状态，或 $SUDO bash $DIR/tools/bh/bh.sh logs server 看日志"
    ok "部署完成"
}

# ---------- 主流程 ----------
info "=== 百花一键安装部署 ==="
info "仓库: https://github.com/$REPO  分支/tag: $RELEASE  目标: $DIR"

install_k3s
clone_repo
install_bh
do_up

echo ""
ok "=== 百花部署完成 ==="
echo ""
printf '%s下一步：%s\n' "$C_CYAN" "$C_RESET"
printf '  打开管理面板:  %sbh dashboard%s\n' "$C_GREEN" "$C_RESET"
printf '  查看服务状态:  %sbh status%s\n' "$C_RESET"
printf '  查看后端日志:  %sbh logs server%s\n' "$C_RESET"
printf '  更新并重部署:  %ssudo bh update%s\n' "$C_RESET"
echo ""
if [ "$PLATFORM" = "WSL2" ]; then
    warn "WSL2 提示：Windows 浏览器访问需配置局域网入口（bh lan on），或直接用 bh dashboard 自动打开"
fi
warn "首次部署：移动端配对密钥等 Secret 是占位值，需后续编辑 k8s/02-secret.yaml 并 sudo bh deploy 更新"