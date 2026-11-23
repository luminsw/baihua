#!/bin/bash
# baihua - Linux + k3s CLI
# Cell of the matrix: OS=linux, deployment=k8s (k3s + containerd, 无 docker 依赖)
#
# 镜像构建用 nerdctl 直连 k3s 的 containerd socket（/run/k3s/containerd/containerd.sock），
# 构建完镜像直接落在 k3s 的 containerd 存储里，无需 docker build / docker save / ctr import。
# 前置：k3s 已安装运行（k3s 无法自动安装，见 k8s/README.md 前提条件）。
# 权限：containerd socket 与 k3s.yaml 仅 root 可访问——build/deploy 需 sudo（sudo bh-k3s build）；
#       status/logs/dashboard 等只读命令检测到 k3s 配置不可读时自动提权，无需手动 sudo。
#       脚本内部对 /usr/local/bin 与 /etc 的写入会自动用 sudo，非 root 直接跑也会尽量完成。
# nerdctl / buildkit（buildkitd+buildctl）缺失时 build 会自动下载安装（GitHub release → /usr/local/bin）。
#
# Usage: ./tools/bh/linux/k8s/bh.sh <command> [args]     （命令行名：bh-k3s；Linux 上 bh 同义）
#   build [img...]  nerdctl 构建镜像进 k3s containerd（默认 3 个；可指定部分，如: server webui）
#   deploy      kubectl apply k8s/ manifests + wait ready
#   up          全量重建 .NET 应用镜像 + deploy（openvino 镜像不在其中，需时单独 build openvino）
#   update      git pull + up（pull 以真实用户执行，build/deploy 自动提权，sudo 与否均可）
#   prune       清空 buildkit 构建缓存（释放磁盘，修复 nuget 包缓存损坏导致的构建失败）
#   status      pods / svc / pvc overview（--json 供 DSH 插件消费）
#   logs <svc> [n]   tail pod logs (default 50)
#   start <svc>     scale deployment to 1
#   stop <svc>      scale deployment to 0
#   restart <svc>   rollout restart deployment
#   backup [目录]   数据卷冷备份（缩容→tar hostPath→演练→按原副本数扩容回去）
#                  产物 <目录>/baihua-backup-<时间>/（默认仓库根 backup/）；日志/模型卷不备份
#   restore <备份目录> [-y]   从备份恢复（旧目录挪 *.pre-restore-<时间>；覆盖需输 yes 确认）
#   destroy     delete namespace baihua
#   dashboard   open browser with cli-token auto-login
#   openvino <on|off|status>   按需启停 Intel GPU 相关服务（10-intel-gpu-plugin + bh-openvino）
#                              deploy 时自动探测：无 Intel GPU 则跳过；可用 BAIHUA_ENABLE_OPENVINO=1/0 强制
#   doctor      只读体检：k3s/命名空间/Deployment/PVC/端口/AI 组件，逐项给下一步命令
#   help        this help
set -u

ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"  # tools/bh/linux/k8s → 仓库根
K8S_DIR="$ROOT/k8s"
IMAGE_DIR="$ROOT/k8s/images"   # Dockerfile 配方 + entrypoint 全在这里
NAMESPACE="baihua"

IMAGES="bh-server:latest bh-webui:latest bh-openvino:latest"

# k3s containerd socket（k3s 默认）
K3S_CONTAINERD_SOCK="/run/k3s/containerd/containerd.sock"

# nerdctl 封装：直连 k3s containerd（在 build 时才检查，help/status 等不依赖）
# -n k8s.io：k3s 的镜像 namespace（否则 nerdctl 默认 default，看不到 k3s 的镜像）
n() { nerdctl -a "$K3S_CONTAINERD_SOCK" -n k8s.io "$@"; }

# buildkitd socket（prune 用；nerdctl build 内部自动连接同一 daemon）
BUILDKIT_ADDR="unix:///run/buildkit/buildkitd.sock"

# 镜像名 → Dockerfile 映射（支持 "bh-server" 或 "server" 两种写法）
dockerfile_of() {
    case "${1#bh-}" in
        server)  echo "Dockerfile.server" ;;
        webui)   echo "Dockerfile.webui" ;;
        openvino) echo "Dockerfile.openvino-server" ;;
        *)       echo "" ;;
    esac
}

# 全部 .NET 应用镜像（Contracts/Data/Core/Modules 等共享库变更时全部受影响）
ALL_DOTNET="server webui"

# kubectl 封装：优先 k3s 自带 kubectl（k3s kubectl），再 PATH 里的 kubectl
# 惰性解析——help 等不实际用 kubectl 的命令在 k3s 缺失时也能跑
k() {
    # k3s.yaml 仅 root 可读：非 root 调用时自动提权（status/logs/dashboard 等只读命令无需手动 sudo）
    if [ "$(id -u)" != "0" ] && [ -f /etc/rancher/k3s/k3s.yaml ] && [ ! -r /etc/rancher/k3s/k3s.yaml ]; then
        if command -v sudo >/dev/null 2>&1 && command -v k3s >/dev/null 2>&1; then
            sudo k3s kubectl "$@"
            return $?
        fi
    fi
    if command -v k3s >/dev/null 2>&1; then
        if [ -z "${KUBECONFIG:-}" ] && [ -f /etc/rancher/k3s/k3s.yaml ]; then
            export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
        fi
        k3s kubectl "$@"
    elif command -v kubectl >/dev/null 2>&1; then
        kubectl "$@"
    else
        echo "[k8s] 未找到 k3s / kubectl（k3s 安装见 k8s/README.md 前提条件）" >&2
        return 1
    fi
}

help_text() {
    sed -n 's/^#   //p' "$0" | grep -E '^[a-z]'
}

# WSL2 判定：内核版本/ /proc/version 含 microsoft，或 systemd-detect-virt=wsl
# （不能拿 /dev/dxg 当依据——k8s hostPath DirectoryOrCreate 会在原生 Linux 上建出空目录）
is_wsl() {
    if [ -f /proc/sys/kernel/osrelease ] && grep -qi microsoft /proc/sys/kernel/osrelease 2>/dev/null; then
        return 0
    fi
    if [ -f /proc/version ] && grep -qi microsoft /proc/version 2>/dev/null; then
        return 0
    fi
    if command -v systemd-detect-virt >/dev/null 2>&1; then
        [ "$(systemd-detect-virt)" = "wsl" ] && return 0
    fi
    return 1
}

# Intel GPU 探测：决定是否部署/启动 openvino 相关服务（10-intel-gpu-plugin + 22a-openvino）
# 顺序：显式开关 BAIHUA_ENABLE_OPENVINO（1/0）> WSL2 GPU-PV（内核判定 + /dev/dxg 字符设备）> 真机 /dev/dri + lspci 厂商为 Intel
has_intel_gpu() {
    case "${BAIHUA_ENABLE_OPENVINO:-auto}" in
        1|true|yes|on)  echo "[gpu] BAIHUA_ENABLE_OPENVINO=on 强制启用 openvino 服务"; return 0 ;;
        0|false|no|off) echo "[gpu] BAIHUA_ENABLE_OPENVINO=off 强制停用 openvino 服务"; return 1 ;;
    esac
    # WSL2：GPU-PV 走 /dev/dxg（必须是字符设备；空目录是 k8s hostPath 建的，不作数）
    if is_wsl; then
        if [ -c /dev/dxg ]; then
            echo "[gpu] WSL2 GPU-PV：/dev/dxg 字符设备"
            return 0
        fi
        echo "[gpu] WSL2 未检测到 /dev/dxg 字符设备，继续查 /dev/dri"
    fi
    # 真机 / 直通：无 /dev/dri 渲染节点 → 无 GPU
    if [ ! -d /dev/dri ] || ! ls /dev/dri/renderD* >/dev/null 2>&1; then
        echo "[gpu] 未检测到 /dev/dri 渲染节点"
        return 1
    fi
    # Intel 厂商判断（lspci 可用时）；lspci 缺失时以 /dev/dri 存在为准
    if command -v lspci >/dev/null 2>&1; then
        if lspci | grep -qiE '(vga|3d|display).*intel'; then
            echo "[gpu] 检测到 Intel GPU（lspci）"
            return 0
        fi
        echo "[gpu] 有 /dev/dri 但非 Intel 显卡（openvino 需要 Intel GPU，跳过）"
        return 1
    fi
    echo "[gpu] 检测到 /dev/dri（无 lspci，按有 GPU 处理）"
    return 0
}

# 自动安装缺失的构建依赖（nerdctl / buildkitd）
# 策略：GitHub release 官方 tarball → /usr/local/bin（需 root；有 sudo 自动用，否则提示）
# 不能自动安装的（k3s 系统服务）在 k8s/README.md 前提条件章节有指引
NERDCTL_VERSION="2.3.5"
BUILDKIT_VERSION="0.32.2"
ARCH="$(uname -m | sed 's/x86_64/amd64/; s/aarch64/arm64/')"

install_tool() {
    # $1=工具名 $2=下载URL $3=tarball 内二进制名
    local name="$1" url="$2" bin="$3"
    echo "[deps] $name 缺失，自动下载安装（$url）..."
    local tmp
    tmp="$(mktemp -d)"
    # GitHub 直连失败时自动换镜像加速（国内网络友好）
    local ok=0
    if curl -fsSL -o "$tmp/tool.tar.gz" "$url"; then
        ok=1
    else
        for mirror in "https://mirror.ghproxy.com/" "https://ghfast.top/" "https://ghproxy.net/"; do
            echo "[deps] GitHub 直连失败，尝试镜像: $mirror"
            if curl -fsSL -o "$tmp/tool.tar.gz" "$mirror$url"; then
                ok=1
                break
            fi
        done
    fi
    if [ "$ok" != "1" ]; then
        echo "[deps] 下载失败（直连+镜像均不可达），请手动安装 $name 后重试（见 k8s/README.md）"
        rm -rf "$tmp"
        exit 1
    fi
    # 完整性校验（tarball 可能被截断）
    if ! tar -tzf "$tmp/tool.tar.gz" >/dev/null 2>&1; then
        echo "[deps] 下载的 tarball 损坏（网络截断），请手动安装 $name 后重试"
        rm -rf "$tmp"
        exit 1
    fi
    if ! tar -xzf "$tmp/tool.tar.gz" -C "$tmp" "$bin" 2>/dev/null; then
        tar -xzf "$tmp/tool.tar.gz" -C "$tmp" || true
    fi
    local found
    found="$(find "$tmp" -name "$bin" -type f | head -1)"
    local target="/usr/local/bin/$name"
    if command -v sudo >/dev/null 2>&1; then
        sudo install -m 0755 "$found" "$target"             || { echo "[deps] 安装失败（权限？），请手动安装 $name"; rm -rf "$tmp"; exit 1; }
    elif [ "$(id -u)" = "0" ]; then
        install -m 0755 "$found" "$target"             || { echo "[deps] 安装失败，请手动安装 $name"; rm -rf "$tmp"; exit 1; }
    else
        echo "[deps] 需要 root 权限安装到 /usr/local/bin，请手动执行:"
        echo "        sudo install -m 0755 $found $target"
        rm -rf "$tmp"
        exit 1
    fi
    rm -rf "$tmp"
    echo "[deps] $name 安装完成"
}

# buildkit 全家桶：buildkitd（守护进程）+ buildctl（客户端，nerdctl build 需要）同在一个 tarball
install_buildkit() {
    local url="https://github.com/moby/buildkit/releases/download/v${BUILDKIT_VERSION}/buildkit-v${BUILDKIT_VERSION}.linux-${ARCH}.tar.gz"
    echo "[deps] buildkit 缺失，自动下载安装（buildkitd + buildctl，$url）..."
    local tmp
    tmp="$(mktemp -d)"
    local ok=0
    if curl -fsSL -o "$tmp/tool.tar.gz" "$url"; then
        ok=1
    else
        for mirror in "https://mirror.ghproxy.com/" "https://ghfast.top/" "https://ghproxy.net/"; do
            echo "[deps] GitHub 直连失败，尝试镜像: $mirror"
            if curl -fsSL -o "$tmp/tool.tar.gz" "$mirror$url"; then
                ok=1
                break
            fi
        done
    fi
    if [ "$ok" != "1" ]; then
        echo "[deps] 下载失败（直连+镜像均不可达），请手动安装 buildkit（见 k8s/README.md）"
        rm -rf "$tmp"
        exit 1
    fi
    if ! tar -tzf "$tmp/tool.tar.gz" >/dev/null 2>&1; then
        echo "[deps] 下载的 tarball 损坏（网络截断），请手动安装 buildkit 后重试"
        rm -rf "$tmp"
        exit 1
    fi
    tar -xzf "$tmp/tool.tar.gz" -C "$tmp" 2>/dev/null || true
    local install_one
    install_one() {
        local bin="$1"
        local found
        found="$(find "$tmp" -name "$bin" -type f | head -1)"
        [ -z "$found" ] && { echo "[deps] tarball 里找不到 $bin"; return 1; }
        local target="/usr/local/bin/$bin"
        if command -v sudo >/dev/null 2>&1; then
            sudo install -m 0755 "$found" "$target"
        elif [ "$(id -u)" = "0" ]; then
            install -m 0755 "$found" "$target"
        else
            echo "[deps] 需要 root 权限安装到 /usr/local/bin，请手动执行:"
            echo "        sudo install -m 0755 $found $target"
            return 1
        fi
    }
    if ! install_one buildkitd || ! install_one buildctl; then
        rm -rf "$tmp"
        exit 1
    fi
    rm -rf "$tmp"
    echo "[deps] buildkitd + buildctl 安装完成"
}

ensure_deps() {
    # nerdctl：k3s 不附带，自动装
    if ! command -v nerdctl >/dev/null 2>&1; then
        install_tool nerdctl \
            "https://github.com/containerd/nerdctl/releases/download/v${NERDCTL_VERSION}/nerdctl-${NERDCTL_VERSION}-linux-${ARCH}.tar.gz" \
            "nerdctl"
    fi
    # buildkit：buildkitd（守护进程）+ buildctl（客户端，nerdctl build 需要），一次下载装俩
    if ! command -v buildkitd >/dev/null 2>&1 || ! command -v buildctl >/dev/null 2>&1; then
        install_buildkit
    fi
    # buildkitd 必须运行（socket 可达），否则给出启动指引
    if ! command -v buildkitd >/dev/null 2>&1; then return 0; fi
    if [ ! -S /run/buildkit/buildkitd.sock ]; then
        ensure_registry
        # systemd 环境：GitHub release 的 buildkit 不带 systemd 单元文件，脚本先写入 buildkit.service 再提示 systemctl 启动
        if [ -d /run/systemd/system ]; then
            if [ ! -f /etc/systemd/system/buildkit.service ]; then
                write_root /etc/systemd/system/buildkit.service << 'EOF'
[Unit]
Description=BuildKit
Documentation=https://github.com/moby/buildkit

[Service]
ExecStart=/usr/local/bin/buildkitd -config /etc/buildkit/buildkitd.toml
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
            fi
            if [ -f /etc/systemd/system/buildkit.service ]; then
                { systemctl daemon-reload || sudo systemctl daemon-reload; } 2>/dev/null || true
                echo "[deps] buildkitd 未运行。systemd 单元已就绪，请启动："
                echo "        sudo systemctl enable --now buildkit"
                echo "        （状态: systemctl status buildkit / 日志: journalctl -u buildkit -f）"
                exit 1
            fi
            echo "[deps] buildkitd 未运行且无法写入 systemd 单元（需要 root 权限）" >&2
            exit 1
        fi
        echo "[deps] buildkitd 未运行。无 systemd 环境请手动启动："
        echo "        nohup buildkitd -config /etc/buildkit/buildkitd.toml > /tmp/buildkitd.log 2>&1 &"
        exit 1
    fi
}

# 写 root 属主的配置文件：root 直接写；有 sudo 自动用（与 install_tool 策略一致）；否则提示手动执行
write_root() {
    local target="$1"
    local dir; dir="$(dirname "$target")"
    if [ "$(id -u)" = "0" ]; then
        mkdir -p "$dir" || return 1
        cat > "$target" || return 1
    elif command -v sudo >/dev/null 2>&1; then
        sudo mkdir -p "$dir" || return 1
        sudo tee "$target" > /dev/null || return 1
    else
        echo "[deps] 需要 root 权限写入 $target（无 sudo），请手动执行:"
        echo "        sudo mkdir -p $dir && sudo tee $target"
        return 1
    fi
}

# 自动配置国内镜像加速（幂等：已存在则不动）：
#   1. /etc/rancher/k3s/registries.yaml —— k3s containerd 拉镜像走 daocloud（实测 pause/nginx 直连 docker.io 超时）
#   2. /etc/buildkit/buildkitd.toml —— buildkitd 构建时拉基础镜像走 daocloud
ensure_registry() {
    local wrote=0
    # k3s registries.yaml
    if [ ! -f /etc/rancher/k3s/registries.yaml ]; then
        write_root /etc/rancher/k3s/registries.yaml << 'EOF'
mirrors:
  docker.io:
    endpoint:
      - "https://docker.m.daocloud.io"
      - "https://docker.1ms.run"
      - "https://registry-1.docker.io"
EOF
        if [ $? -eq 0 ]; then
            echo "[deps] 已写入 /etc/rancher/k3s/registries.yaml（daocloud 镜像加速）"
            echo "        k3s 重启后生效（systemctl restart k3s 或重新拉起 k3s server）"
            wrote=1
        else
            echo "[deps] 写入 /etc/rancher/k3s/registries.yaml 失败（需要 root）" >&2
        fi
    fi
    # buildkitd.toml（仅当 buildkitd 存在时写）
    if command -v buildkitd >/dev/null 2>&1 && [ ! -f /etc/buildkit/buildkitd.toml ]; then
        write_root /etc/buildkit/buildkitd.toml << 'EOF'
# 必须禁用 OCI(runc) worker：否则 nerdctl build -o type=image 导出到 buildkit 的 OCI 存储，
# 镜像进不了 k3s containerd 的 k8s.io namespace，后续 FROM bh/* 本地镜像解析失败（会去 docker.io 拉→403）
[worker.oci]
  enabled = false

[worker.containerd]
address = "/run/k3s/containerd/containerd.sock"
namespace = "k8s.io"
[registry."docker.io"]
mirrors = ["docker.m.daocloud.io", "docker.1ms.run"]
EOF
        if [ $? -eq 0 ]; then
            echo "[deps] 已写入 /etc/buildkit/buildkitd.toml（daocloud 镜像加速 + k8s.io namespace，禁用 OCI worker）"
            wrote=1
        else
            echo "[deps] 写入 /etc/buildkit/buildkitd.toml 失败（需要 root）" >&2
        fi
    fi
    return "$wrote"
}

# nerdctl 直接构建进 k3s containerd（构建即入库，无 docker）
# 标准构建：镜像 unpack 进 k3s containerd（-o type=image 导出不可靠——tag 不更新会部署到旧镜像）
# 非 root 自动提权：k3s containerd socket 仅 root 可读，exec sudo 重跑（无需手动 sudo）。
build_all() {
    if [ "$(id -u)" != "0" ]; then
        echo "[build] 需要 root 权限（k3s containerd socket $K3S_CONTAINERD_SOCK 仅 root 可读），自动提权..." >&2
        exec sudo "$(readlink -f "$0")" build "$@"
    fi
    ensure_deps
    ensure_registry
    if ! n info >/dev/null 2>&1; then
        echo "[build] 无法连接 k3s containerd（$K3S_CONTAINERD_SOCK）"
        echo "        请确认 k3s 已运行（k3s 安装见 k8s/README.md 前提条件）"
        exit 1
    fi
    # base-runtime：server/webui 的 FROM（运行时基础镜像）
    if ! n images | grep -qE 'bh/base-runtime\s+latest'; then
        n build -o type=image -f "$IMAGE_DIR/Dockerfile.base-runtime" -t bh/base-runtime:latest "$IMAGE_DIR" >/dev/null || exit 1
        echo "[build] bh/base-runtime"
    fi
    # sdk-offline：离线 SDK 基础镜像（nuget-local 包源经 build-context 沉底，一次性构建），.NET 镜像 build 阶段 FROM 它
    if ! n images | grep -qE 'bh/sdk-offline\s+latest'; then
        n build --build-context "nuget=$ROOT/nuget-local" -o type=image -f "$IMAGE_DIR/Dockerfile.sdk-offline" -t bh/sdk-offline:latest "$ROOT" >/dev/null || exit 1
        echo "[build] bh/sdk-offline"
    fi
    # 目标镜像：默认全部 3 个；可传参指定（bh-k3s build server webui）
    local targets
    if [ $# -gt 0 ]; then
        targets=""
        for a in "$@"; do
            if [ -z "$(dockerfile_of "$a")" ]; then
                echo "[build] 未知镜像: $a（可用: server webui openvino）" >&2
                exit 1
            fi
            targets="$targets ${a#bh-}"
        done
    else
        targets=" server webui openvino"
    fi

    # .NET 镜像：多阶段源码构建（容器内 dotnet publish，restore 走 sdk-offline 里的离线包源），context 需仓库根（services/ 源码）
    for img in $targets; do
        n build -f "$IMAGE_DIR/$(dockerfile_of "$img")" -t "bh-$img:latest" "$ROOT" >/dev/null || exit 1
        echo "[build] bh-$img"
    done
    echo "[build] done: ${targets# }"
}

deploy_all() {
    if [ "$(id -u)" != "0" ]; then
        echo "[deploy] 需要 root 权限（/etc/rancher/k3s/k3s.yaml 仅 root 可读），自动提权..." >&2
        exec sudo "$(readlink -f "$0")" deploy
    fi
    # 记录本次部署对应的源码 commit（供 bh-k3s status --json 判断运行代码是否最新）
    local git_commit="unknown"
    if command -v git >/dev/null 2>&1 && [ -d "$ROOT/.git" ]; then
        git_commit="$(cd "$ROOT" && git rev-parse --short HEAD 2>/dev/null || echo unknown)"
    fi
    # 基础清单（与 GPU 无关，始终部署）
    # 25-postgres 必须在这里：此前它被本列表遗漏（清单从未被任何部署路径应用）。
    for m in 00-namespace.yaml 01-configmap.yaml 02-secret.yaml 03-pvc.yaml \
             25-postgres.yaml 20-server.yaml 23-webui.yaml 26-open-webui.yaml; do
        echo "[deploy] $m"
        k apply -f "$K8S_DIR/$m" >/dev/null || exit 1
    done
    # 24-traefik.yaml 是 IngressRoute（traefik.io CRD）：集群未装 Traefik 时先跳过而不是整体失败，
    # 否则基础工作负载已经起来了却因为入口层报错中断，容易误判成"部署挂了"。
    if k get crd ingressroutes.traefik.io >/dev/null 2>&1; then
        echo "[deploy] 24-traefik.yaml"
        k apply -f "$K8S_DIR/24-traefik.yaml" >/dev/null || exit 1
    else
        echo "[deploy] 跳过 24-traefik.yaml：集群无 traefik.io CRD（未安装 Traefik）"
        echo "         移动端/外部入口需要 :80 —— k3s 默认自带 Traefik（若被 --disable traefik 关掉，需重新启用或自建入口）"
        echo "         临时入口可用: kubectl -n $NAMESPACE port-forward svc/bh-server 8788:8788 / svc/bh-webui 5177:5177"
    fi
    # k3s 的 svclb（klipper-lb）默认 externalTrafficPolicy=Cluster，会对来源做 SNAT，
    # 真实客户端 IP 在到达 Traefik 之前就丢了（X-Forwarded-For 只能是集群内地址）。
    # 改成 Local 后才谈得上"按客户端 IP"的策略（配合服务端 BAIHUA_TRUSTED_PROXY_NETS）。
    # 单节点 k3s 无副作用；集群没装 Traefik 时跳过。
    if k -n kube-system get svc traefik >/dev/null 2>&1; then
        local etp
        etp="$(k -n kube-system get svc traefik -o jsonpath='{.spec.externalTrafficPolicy}' 2>/dev/null || true)"
        if [ "$etp" != "Local" ]; then
            k -n kube-system patch svc traefik -p '{"spec":{"externalTrafficPolicy":"Local"}}' >/dev/null 2>&1 \
                && echo "[deploy] traefik Service externalTrafficPolicy: $etp -> Local（保留客户端源 IP）"
        fi
    fi
    # 给 deployment 打上 git commit 标注（供 bh-k3s status --json 判断运行代码是否最新）。
    # 标注语义是「这个工作负载最后一次是在哪个 commit 部署的」，不是「镜像由本仓库构建」，
    # 所以 postgres 这类上游镜像也一起打 —— 否则它永远是 upToDate:false，看着像待处理问题。
    for svc in bh-server bh-webui bh-openvino bh-postgres bh-open-webui; do
        k -n "$NAMESPACE" annotate deploy "$svc" "baihua.git-commit=$git_commit" --overwrite >/dev/null 2>&1 || true
    done
    echo "[deploy] 记录源码 commit: $git_commit"
    # GPU 按需：有 Intel GPU 才部署 intel-gpu-plugin（kube-system）+ bh-openvino
    if has_intel_gpu; then
        for m in 10-intel-gpu-plugin.yaml 22a-openvino.yaml; do
            echo "[deploy] $m"
            k apply -f "$K8S_DIR/$m" >/dev/null || exit 1
        done
    else
        echo "[deploy] 无 Intel GPU：跳过 openvino 相关服务（10-intel-gpu-plugin / 22a-openvino）"
        # 之前部署过的话停掉，避免在无 GPU 节点上空转/崩溃循环
        # （DaemonSet 不支持 scale，用 delete --ignore-not-found；on 时 apply 清单会重建）
        k -n kube-system delete ds intel-gpu-plugin --ignore-not-found >/dev/null 2>&1 && \
            echo "[deploy] intel-gpu-plugin 已删除（无 GPU）"
        k -n "$NAMESPACE" scale deploy bh-openvino --replicas=0 >/dev/null 2>&1 && \
            echo "[deploy] bh-openvino 已缩容至 0"
    fi
    echo "[deploy] 滚动重启应用新镜像（本地 :latest 镜像不重启不会生效）"
    # 显式列出应用 deployment，避免误重启 bh-postgres（数据库无需随应用重建而重启）
    k -n "$NAMESPACE" rollout restart deployment bh-server bh-webui bh-openvino >/dev/null 2>&1 || true
    echo "[deploy] 等待应用滚动完成（rollout status，确保新 pod 全部就绪）..."
    k -n "$NAMESPACE" rollout status deployment bh-server bh-webui bh-openvino --timeout=300s \
        || echo "[deploy] 部分 deployment 未在 300s 内就绪（可稍后 bh-k3s status 复查，或 bh-k3s logs <svc> 查看原因）"
    status_all
}

# openvino 按需启停：on 启动（无 GPU 时拒绝，除非 BAIHUA_ENABLE_OPENVINO=1 强制），off 缩容至 0，status 查看

# 变更检测（changed_images）已移除：一键更新/up 改为始终全量重建，避免"部署镜像与源码脱节、标注撒谎"的误导。



# up：始终全量重建 2 个 .NET 应用镜像 + deploy。
# 不再做源码变更检测（曾用 changed_images 决定重建哪些，导致"部署镜像与源码脱节、标注撒谎"这类误导），
# 全量重建 + buildkit 缓存未变更层（开销可控）始终把.NET 应用带到当前 HEAD，稳定可靠。
# 注：openvino 的 FROM 是外部 registry 镜像（openvino/model_server:latest-gpu），构建依赖代理/网络，
#     纳入自动更新会让一键更新随代理抖动而失败（如 hysteria 未运行→ connection refused），故不在此重建；
#     需要更新 openvino 时单独 bh-k3s build openvino。
up_all() {
    if [ "$(id -u)" != "0" ]; then
        echo "[up] 需要 root 权限（build/deploy），自动提权..." >&2
        exec sudo "$(readlink -f "$0")" up "$@"
    fi
    echo "[up] 全量构建 .NET 应用镜像: $ALL_DOTNET"
    build_all $ALL_DOTNET || return 1
    deploy_all
}

# 清空 buildkit 构建缓存（含 nuget 包缓存挂载）：释放磁盘、修复缓存损坏导致的构建失败
prune_cache() {
    if [ "$(id -u)" != "0" ]; then
        echo "[prune] 需要 root 权限（buildkitd socket 仅 root 可读），自动提权..." >&2
        exec sudo "$(readlink -f "$0")" prune
    fi
    if ! command -v buildctl >/dev/null 2>&1; then
        echo "[prune] 未找到 buildctl（请先运行 bh-k3s build 自动安装）" >&2
        exit 1
    fi
    echo "[prune] 清空 buildkit 构建缓存（下次构建将重新 restore，可修复 nuget 缓存损坏）..."
    buildctl --addr="$BUILDKIT_ADDR" prune --all
    echo "[prune] 完成"
}

# ---- 数据冷备份 / 恢复（k8s 形态；与 Windows wslc 形态的 bh backup/restore 对齐）----
# 数据在 hostPath PV 里（/opt/baihua/* 与 Vaults 目录），路径从 PV 对象动态解析，不硬编码清单值。
#   备份 = 缩容全部工作负载（冷备，防止写入中拷贝）→ tar 数据 PV 目录 → 导出 namespace 配置
#          （secret/configmap/PV/PVC）→ 恢复演练（解包 + pg_controldata 校验停机状态）→ 按原副本数扩容回去
#   恢复 = 缩容 → 旧目录挪 <目录>.pre-restore-<时间> → 解包覆盖 → 回放配置 → 扩容 → 健康检查
# 日志（baihua-logs-pv）与模型（baihua-models-pv，体积大且可重新拉取）不备份，与 wslc 形态口径一致。

BACKUP_PVS="baihua-postgres-pv baihua-data-pv baihua-openwebui-pv baihua-vaults-pv"
BACKUP_DEPLOYS="bh-server bh-webui bh-postgres bh-open-webui bh-openvino"

pv_hostpath() { k get pv "$1" -o jsonpath='{.spec.hostPath.path}' 2>/dev/null; }

deploy_replicas() { k -n "$NAMESPACE" get deploy "$1" -o jsonpath='{.spec.replicas}' 2>/dev/null; }

# 等命名空间内没有 Running/Pending/Terminating 的 pod（缩容完成的判据）；$1=超时秒
wait_ns_quiet() {
    local deadline=$(( $(date +%s) + ${1:-300} ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        if ! k -n "$NAMESPACE" get pods --no-headers 2>/dev/null |
            grep -qE 'Running|Pending|Terminating'; then
            return 0
        fi
        sleep 3
    done
    return 1
}

# 恢复演练：解包 postgres 归档 + pg_controldata 校验（一次性 pod 挂宿主临时目录）。
# pg_controldata 能确认控制文件完好、集群状态是干净的 shut down（tar 时仍在运行会显示 in production）。
# 一次性 pod 起不来时降级为"解包校验通过"（不否定已打包数据），pg_controldata 报脏状态则判失败。
verify_drill() {
    local dest_dir="$1" pg_image="$2"
    local tmp data pod phase out state n
    tmp="$(mktemp -d /tmp/bh-backup-verify-XXXXXXXX)" || return 1
    echo "[backup] 恢复演练: 解包 postgres 归档..."
    if ! tar -xzf "$dest_dir/baihua-postgres-pv.tgz" -C "$tmp" --numeric-owner; then
        echo "[backup] 演练失败: 解包 baihua-postgres-pv.tgz 出错" >&2
        rm -rf "$tmp"; return 1
    fi
    # pg18 布局: <pgdata 根>/18/docker（PGDATA=/var/lib/postgresql/18/docker，与 wslc 同款）
    data="$(find "$tmp" -type d -path '*/18/docker' | head -1)"
    if [ -z "$data" ] || [ ! -f "$data/PG_VERSION" ]; then
        echo "[backup] 演练失败: 解包后未找到 PG 数据目录（*/18/docker/PG_VERSION）" >&2
        rm -rf "$tmp"; return 1
    fi
    pod="bh-backup-verify-$(date +%s)"
    echo "[backup] 恢复演练: pg_controldata 校验（一次性 pod: $pod）..."
    if k run "$pod" --restart=Never --image="$pg_image" \
        --overrides "{\"spec\":{\"containers\":[{\"name\":\"verify\",\"image\":\"$pg_image\",\"command\":[\"pg_controldata\",\"$data\"],\"volumeMounts\":[{\"name\":\"v\",\"mountPath\":\"$tmp\"}],\"restartPolicy\":\"Never\"}],\"volumes\":[{\"name\":\"v\",\"hostPath\":{\"path\":\"$tmp\"}}]}}" \
        >/dev/null 2>&1; then
        n=0
        phase=""
        while [ "$n" -lt 40 ]; do
            phase="$(k get pod "$pod" -o jsonpath='{.status.phase}' 2>/dev/null)"
            case "$phase" in Succeeded|Failed) break ;; esac
            sleep 3; n=$((n+1))
        done
        out="$(k logs "$pod" 2>/dev/null)"
        k delete pod "$pod" --wait=false >/dev/null 2>&1
        if ! printf '%s\n' "$out" | grep -q "Database cluster state"; then
            echo "[backup] 演练失败: pg_controldata 无输出（pod phase=${phase:-unknown}）" >&2
            rm -rf "$tmp"; return 1
        fi
        state="$(printf '%s\n' "$out" | sed -n 's/^Database cluster state: *//p' | head -1)"
        case "$state" in
            "shut down"|"shut down in recovery")
                echo "[backup] 演练通过: Database cluster state = $state" ;;
            *)
                echo "[backup] 演练失败: Database cluster state = ${state:-?}（归档不是干净停机状态）" >&2
                rm -rf "$tmp"; return 1 ;;
        esac
    else
        echo "[backup] 演练降级: 一次性 pod 起不来（跳过 pg_controldata），归档完整性以 tar 解包成功为准"
    fi
    rm -rf "$tmp"
    return 0
}

backup_cmd() {
    if [ "$(id -u)" != "0" ]; then
        echo "[backup] 需要 root 权限（k3s.yaml / hostPath 数据目录仅 root 可读），自动提权..." >&2
        exec sudo "$(readlink -f "$0")" backup "${1:-}"
    fi
    if ! k get ns "$NAMESPACE" >/dev/null 2>&1; then
        echo "[backup] 命名空间 $NAMESPACE 不存在（先: bh deploy）" >&2
        exit 1
    fi
    local ts dest_root dest_dir git_commit pg_image
    ts="$(date +%Y%m%d-%H%M%S)"
    dest_root="${1:-$ROOT/backup}"
    dest_dir="$dest_root/baihua-backup-$ts"
    git_commit="$(cd "$ROOT" 2>/dev/null && git rev-parse --short HEAD 2>/dev/null || echo unknown)"
    pg_image="$(k -n "$NAMESPACE" get deploy bh-postgres -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null)"
    if [ -z "$pg_image" ]; then
        echo "[backup] 未找到 deploy/bh-postgres（核心数据无法备份，先: bh deploy）" >&2
        exit 1
    fi
    mkdir -p "$dest_dir" && chmod 700 "$dest_dir"

    # 数据 PV → hostPath（postgres 不可用直接放弃；其余缺失告警跳过）
    local pv path pvs=() paths=()
    for pv in $BACKUP_PVS; do
        path="$(pv_hostpath "$pv")"
        if [ -z "$path" ] || [ ! -d "$path" ]; then
            if [ "$pv" = "baihua-postgres-pv" ]; then
                echo "[backup] $pv 的 hostPath 不可用（${path:-未绑定}）——核心数据，放弃备份" >&2
                exit 1
            fi
            echo "[backup] 跳过 $pv（hostPath 不可用: ${path:-未绑定}）"
            continue
        fi
        pvs+=("$pv"); paths+=("$path")
    done

    # 记录副本数 → 全部缩容（冷备）
    local d r deploys=() reps=()
    for d in $BACKUP_DEPLOYS; do
        r="$(deploy_replicas "$d")"
        [ -n "$r" ] || continue
        deploys+=("$d"); reps+=("$r")
    done
    echo "[backup] 缩容: ${deploys[*]:-（无运行中的工作负载）}"
    for d in "${deploys[@]}"; do
        k -n "$NAMESPACE" scale deploy "$d" --replicas=0 >/dev/null || exit 1
    done
    echo "[backup] 等待 pod 终止..."
    if ! wait_ns_quiet 300; then
        echo "[backup] 300s 仍有 pod 未终止，放弃本次备份并恢复原副本数" >&2
        for i in "${!deploys[@]}"; do
            k -n "$NAMESPACE" scale deploy "${deploys[$i]}" --replicas="${reps[$i]}" >/dev/null 2>&1
        done
        exit 1
    fi

    # tar 数据目录 + 导出配置（任一失败标记 fail，最后统一扩容回去再报错退出）
    local i arch fail=0 first out
    for i in "${!pvs[@]}"; do
        pv="${pvs[$i]}"; path="${paths[$i]}"
        arch="$dest_dir/$pv.tgz"
        echo "[backup] $pv -> $arch  ($path)"
        if ! tar -C "$(dirname "$path")" -czf "$arch" --numeric-owner "$(basename "$path")"; then
            echo "[backup] 打包 $pv 失败" >&2
            fail=1; break
        fi
    done
    if [ "$fail" = "0" ]; then
        k -n "$NAMESPACE" get secret,configmap -o yaml > "$dest_dir/k8s-config.yaml" 2>/dev/null || fail=1
        first=1
        : > "$dest_dir/k8s-pv.yaml"
        for pv in baihua-postgres-pv baihua-data-pv baihua-logs-pv baihua-models-pv baihua-vaults-pv baihua-openwebui-pv; do
            out="$(k get pv "$pv" -o yaml 2>/dev/null)" || continue
            [ -n "$out" ] || continue
            [ "$first" = "0" ] && echo "---" >> "$dest_dir/k8s-pv.yaml"
            printf '%s\n' "$out" >> "$dest_dir/k8s-pv.yaml"
            first=0
        done
        k -n "$NAMESPACE" get pvc -o yaml > "$dest_dir/k8s-pvc.yaml" 2>/dev/null
    fi
    if [ "$fail" = "0" ]; then
        {
            echo "form=k8s"
            echo "namespace=$NAMESPACE"
            echo "timestamp=$ts"
            echo "bhVersion=$git_commit"
            echo "pgImage=$pg_image"
            for i in "${!pvs[@]}"; do
                echo "archive ${pvs[$i]} ${paths[$i]}"
            done
            for i in "${!deploys[@]}"; do
                echo "replica ${deploys[$i]} ${reps[$i]}"
            done
        } > "$dest_dir/backup.meta"
        verify_drill "$dest_dir" "$pg_image" || fail=1
    fi

    # 按原副本数扩容回去（无论成败都恢复运行）
    echo "[backup] 按原副本数扩容: ${deploys[*]:-（无）}"
    for i in "${!deploys[@]}"; do
        k -n "$NAMESPACE" scale deploy "${deploys[$i]}" --replicas="${reps[$i]}" >/dev/null || fail=1
    done
    for d in "${deploys[@]}"; do
        k -n "$NAMESPACE" rollout status "deploy/$d" --timeout=300s >/dev/null 2>&1 \
            || echo "[backup] 提示: $d 未在 300s 内就绪（bh status / bh logs $d 查看）"
    done

    if [ "$fail" != "0" ]; then
        echo "[backup] 备份失败：$dest_dir 已保留现场（检查后可删除）；工作负载已尽力恢复原状" >&2
        exit 1
    fi
    echo "[backup] 完成: $dest_dir"
    du -sh "$dest_dir" 2>/dev/null
    echo "[backup] 内容: 各数据 PV 的 .tgz + k8s-config.yaml（含密码，目录权限 700）+ k8s-pv/pvc.yaml + backup.meta"
    echo "[backup] 恢复: bh restore $dest_dir"
}

restore_cmd() {
    local dir="${1:-}" flag="${2:-}"
    if [ -z "$dir" ]; then
        echo "[restore] 用法: bh restore <备份目录> [-y]   （目录 = bh backup 产物，含 backup.meta）" >&2
        exit 1
    fi
    # 参数/产物校验放在提权之前：目录不存在或不是备份产物时，无需 root 就能报错
    dir="${dir%/}"
    if [ ! -f "$dir/backup.meta" ]; then
        echo "[restore] $dir 不是 bh backup 产物（缺 backup.meta）" >&2
        exit 1
    fi
    if [ "$(id -u)" != "0" ]; then
        echo "[restore] 需要 root 权限（k3s.yaml / hostPath 数据目录仅 root 可写），自动提权..." >&2
        exec sudo "$(readlink -f "$0")" restore "$dir" "$flag"
    fi
    if ! k get ns "$NAMESPACE" >/dev/null 2>&1; then
        echo "[restore] 命名空间 $NAMESPACE 不存在。新机恢复顺序: bh deploy → bh restore <备份目录>" >&2
        exit 1
    fi
    if [ "$flag" != "-y" ] && [ "$flag" != "yes" ]; then
        echo "[restore] 将用 $dir 覆盖现有数据（旧目录保留为 *.pre-restore-<时间>），确认请输 yes):"
        read -r reply
        [ "$reply" = "yes" ] || { echo "[restore] 已取消"; exit 1; }
    fi
    # 备份清单里的数据 PV 列表
    local pvs="" key a b
    while read -r key a b; do
        [ "$key" = "archive" ] && pvs="$pvs $a"
    done < "$dir/backup.meta"
    if [ -z "$pvs" ]; then
        echo "[restore] backup.meta 里没有 archive 记录" >&2
        exit 1
    fi
    # 缩容现有工作负载（副本数以恢复前的现场为准）
    local d r deploys=() reps=()
    for d in $BACKUP_DEPLOYS; do
        r="$(deploy_replicas "$d")"
        [ -n "$r" ] || continue
        deploys+=("$d"); reps+=("$r")
    done
    echo "[restore] 缩容: ${deploys[*]:-（无）}，等待 pod 终止..."
    for d in "${deploys[@]}"; do
        k -n "$NAMESPACE" scale deploy "$d" --replicas=0 >/dev/null || exit 1
    done
    if ! wait_ns_quiet 300; then
        echo "[restore] 300s 仍有 pod 未终止，已取消（未动任何数据）" >&2
        exit 1
    fi
    # 逐 PV 恢复：目标路径以**当前集群**的 PV 对象为准（备份记录仅作对照）；
    # 旧目录挪 .pre-restore-<时间> 保留，解包走临时目录再顶上（归档顶层目录名可能与现路径不同）
    local ts pv cur_path parent tmpex inner fail=0
    ts="$(date +%Y%m%d-%H%M%S)"
    for pv in $pvs; do
        if [ ! -f "$dir/$pv.tgz" ]; then
            echo "[restore] 缺 $dir/$pv.tgz，跳过 $pv" >&2
            continue
        fi
        cur_path="$(pv_hostpath "$pv")"
        if [ -z "$cur_path" ]; then
            echo "[restore] 集群里没有 $pv（先: bh deploy），跳过" >&2
            continue
        fi
        parent="$(dirname "$cur_path")"
        mkdir -p "$parent"
        tmpex="$parent/.bh-restore-$$-$RANDOM"
        mkdir -p "$tmpex"
        echo "[restore] $pv -> $cur_path"
        if ! tar -xzf "$dir/$pv.tgz" -C "$tmpex" --numeric-owner; then
            echo "[restore] 解包 $pv 失败" >&2
            rm -rf "$tmpex"; fail=1; break
        fi
        inner="$(find "$tmpex" -mindepth 1 -maxdepth 1 -type d | head -1)"
        if [ -z "$inner" ]; then
            echo "[restore] $pv 归档内容异常（无顶层目录）" >&2
            rm -rf "$tmpex"; fail=1; break
        fi
        if [ -e "$cur_path" ]; then
            mv "$cur_path" "$cur_path.pre-restore-$ts" || { fail=1; break; }
        fi
        mv "$inner" "$cur_path" || { fail=1; break; }
        rm -rf "$tmpex"
    done
    # 回放 secret/configmap（k apply 幂等；PV/PVC 清单仅新机重建用，集群里已有则不动）
    if [ -f "$dir/k8s-config.yaml" ] && [ "$fail" = "0" ]; then
        echo "[restore] 回放 secret/configmap..."
        k apply -f "$dir/k8s-config.yaml" >/dev/null 2>&1 \
            || echo "[restore] 提示: k8s-config.yaml 回放有告警（不阻塞数据恢复）"
    fi
    # 扩容 + 就绪等待 + postgres 健康检查
    echo "[restore] 按原副本数扩容..."
    for i in "${!deploys[@]}"; do
        k -n "$NAMESPACE" scale deploy "${deploys[$i]}" --replicas="${reps[$i]}" >/dev/null || fail=1
    done
    for d in "${deploys[@]}"; do
        k -n "$NAMESPACE" rollout status "deploy/$d" --timeout=300s >/dev/null 2>&1 \
            || echo "[restore] 提示: $d 未在 300s 内就绪（bh logs $d 查看）"
    done
    sleep 5
    if k -n "$NAMESPACE" exec deploy/bh-postgres -- pg_isready -h 127.0.0.1 -U baihua >/dev/null 2>&1; then
        echo "[restore] postgres 就绪（pg_isready 通过）"
    else
        echo "[restore] 提示: pg_isready 未通过（pod 可能仍在启动，稍后 bh logs postgres 查看）"
    fi
    if [ "$fail" != "0" ]; then
        echo "[restore] 有错误发生（见上方日志）；旧数据保留为 *.pre-restore-$ts" >&2
        exit 1
    fi
    echo "[restore] 完成。旧数据保留为 *.pre-restore-$ts（确认无误后可删除）"
}

openvino_cmd() {
    local action="${1:-status}"
    case "$action" in
        on|start)
            if ! has_intel_gpu; then
                echo "[openvino] 未检测到 Intel GPU，拒绝启动。如确需强制请: BAIHUA_ENABLE_OPENVINO=1 $0 openvino on" >&2
                return 1
            fi
            k apply -f "$K8S_DIR/10-intel-gpu-plugin.yaml" -f "$K8S_DIR/22a-openvino.yaml" >/dev/null || exit 1
            k -n kube-system scale ds intel-gpu-plugin --replicas=1 >/dev/null 2>&1 || true
            k -n "$NAMESPACE" scale deploy bh-openvino --replicas=1 >/dev/null 2>&1
            echo "[openvino] 已启动，等待就绪（kubectl -n baihua rollout status deploy/bh-openvino）"
            ;;
        off|stop)
            k -n "$NAMESPACE" scale deploy bh-openvino --replicas=0 >/dev/null 2>&1
            # DaemonSet 不支持 scale，用 delete（on 时 apply 清单重建）
            k -n kube-system delete ds intel-gpu-plugin --ignore-not-found >/dev/null 2>&1
            echo "[openvino] 已停止（bh-openvino 缩容至 0、intel-gpu-plugin 已删除，可随时 on 恢复）"
            ;;
        status)
            echo "=== Intel GPU 探测 ==="
            has_intel_gpu
            echo ""
            echo "=== bh-openvino ==="
            k -n "$NAMESPACE" get deploy bh-openvino 2>/dev/null || echo "  (未部署)"
            k -n "$NAMESPACE" get pods -l app=bh-openvino 2>/dev/null || true
            echo ""
            echo "=== intel-gpu-plugin ==="
            k -n kube-system get ds intel-gpu-plugin 2>/dev/null || echo "  (未部署)"
            echo ""
            echo "=== 节点 Intel GPU 资源 ==="
            k get nodes -o custom-columns=NAME:.metadata.name,GPU:.status.capacity.'intel\.com/gpu' 2>/dev/null || echo "  (device-plugin 未注册)"
            ;;
        *)
            echo "用法: $0 openvino <on|off|status>"
            return 1
            ;;
    esac
}

status_all() {
    echo "=== pods ==="
    k -n "$NAMESPACE" get pods -o wide
    echo ""
    echo "=== svc ==="
    k -n "$NAMESPACE" get svc
    echo ""
    echo "=== pvc ==="
    k -n "$NAMESPACE" get pvc
    echo ""
    echo "entry: http://localhost/  (Traefik :80)"
}

# 机器可读状态（供 DSH 桥插件/运维界面消费）：每个应用 deployment 一行
# 服务名统一不带 bh- 前缀（server/webui/openvino/postgres）
# 额外输出 git 版本信息：HEAD + 各服务部署时记录的 commit（baihua.git-commit annotation），
# 用于判断当前运行的代码是否最新（gitHead == imageCommit 即最新；unknown 表示尚未部署标注）。
# 判断某服务在两个 commit 之间是否受影响：检查其专属源码目录 + 共享层（Contracts/Core/Data/Modules）。
# 仅文档（docs/、*.md 等）或其他服务的变更不算本服务受影响——避免误报"落后"。
# 参数：svc（server/webui/openvino/postgres）from to。输出 true/false。
service_affected() {
    local svc="$1" from="$2" to="$3"
    [ "$from" = "$to" ] && { echo false; return; }
    if ! git -C "$ROOT" rev-parse --verify "$from" >/dev/null 2>&1 || ! git -C "$ROOT" rev-parse --verify "$to" >/dev/null 2>&1; then
        echo false; return
    fi
    local paths=()
    case "$svc" in
        server)   paths=("services/Baihua.Server/" "services/Baihua.Modules.Family/" "services/Baihua.Modules.Ai/" "services/Baihua.Modules.Vault/" "services/Baihua.AI.Provider/" "services/Baihua.Core/" "services/Baihua.Contracts/" "services/Baihua.Data/" "libs/");;
        # webui 只引用 Baihua.Contracts（见 services/Baihua.Web/Baihua.Web.csproj），
        # 列上 Core/Data/libs 会误报"需要重建"（如只改 Baihua.Data 的 DbContext 时）。
        webui)    paths=("services/Baihua.Web/" "services/Baihua.Contracts/");;
        openvino) paths=("k8s/images/Dockerfile.openvino-server" "services/Baihua.AI.Provider.OpenVino/");;
        postgres) paths=("k8s/25-postgres.yaml");;
        # open-webui 用上游镜像（ghcr.io/open-webui/open-webui），源码不在本仓库，任何 commit 变化都不影响它
        open-webui) echo false; return;;
    esac
    local pat=""; for p in "${paths[@]}"; do pat="${pat:+"$pat|"}$p"; done
    if git -C "$ROOT" diff --name-only "$from".."$to" 2>/dev/null | grep -E "^(${pat})" | grep -q .; then
        echo true
    else
        echo false
    fi
}

status_json() {
    local git_head="unknown" git_branch="unknown" git_dirty="false"
    if command -v git >/dev/null 2>&1 && [ -d "$ROOT/.git" ]; then
        git_head="$(cd "$ROOT" && git rev-parse --short HEAD 2>/dev/null || echo unknown)"
        git_branch="$(cd "$ROOT" && git rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)"
        # 只看已跟踪文件的改动：未跟踪文件（本地实验产物、node_modules 之类）不影响
        # 「部署的镜像是不是 HEAD 构建的」，判成 dirty 只会误导。
        # 另外 root 下跑 git 需要 safe.directory（见 k8s/README「root 权限」节），
        # 且 root 的 core.autocrlf 要与 Windows 侧一致，否则 CRLF 工作区会被整片判成已修改。
        if [ -n "$(cd "$ROOT" && git status --porcelain --untracked-files=no 2>/dev/null)" ]; then
            git_dirty="true"
        fi
    fi
    local services="bh-server bh-webui bh-openvino bh-postgres bh-open-webui"
    local entries=""
    local first=1
    local ready_total=0 total=0
    for svc in $services; do
        local json phase ready replicas image age restarts image_commit up_to_date
        json="$(k -n "$NAMESPACE" get deploy "$svc" -o json 2>/dev/null)" || continue
        phase="$(printf '%s' "$json" | python3 -c 'import json,sys; d=json.load(sys.stdin); print((d.get("status",{}).get("conditions") or [{}])[-1].get("type",""))' 2>/dev/null)"
        ready="$(printf '%s' "$json" | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("status",{}).get("readyReplicas",0))' 2>/dev/null)"
        replicas="$(printf '%s' "$json" | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("spec",{}).get("replicas",0))' 2>/dev/null)"
        image="$(printf '%s' "$json" | python3 -c 'import json,sys; d=json.load(sys.stdin); c=(d.get("spec",{}).get("template",{}).get("spec",{}).get("containers") or [{}]); print(c[0].get("image",""))' 2>/dev/null)"
        age="$(printf '%s' "$json" | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("metadata",{}).get("creationTimestamp",""))' 2>/dev/null)"
        restarts="$(k -n "$NAMESPACE" get pods -l "app=$svc" -o jsonpath='{.items[*].status.containerStatuses[0].restartCount}' 2>/dev/null | tr ' ' '\n' | awk '{s+=$1} END {print s+0}')"
        image_commit="$(printf '%s' "$json" | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("metadata",{}).get("annotations",{}).get("baihua.git-commit","unknown"))' 2>/dev/null)"
        [ -z "$image_commit" ] && image_commit="unknown"
        # upToDate：image_commit 等于当前 HEAD，或 HEAD 前进但该服务源码未受影响（仅文档/其他服务变更）
        local svc_short="${svc#bh-}"
        if [ "$git_head" != "unknown" ] && [ "$image_commit" != "unknown" ] && [ "$image_commit" = "$git_head" ]; then
            up_to_date="true"
        elif [ "$git_head" != "unknown" ] && [ "$image_commit" != "unknown" ] && \
             [ "$(service_affected "$svc_short" "$image_commit" "$git_head")" = "false" ]; then
            up_to_date="true"
        else
            up_to_date="false"
        fi
        [ -z "$ready" ] && ready=0
        [ -z "$replicas" ] && replicas=0
        [ -z "$restarts" ] && restarts=0
        ready_total=$((ready_total + ready))
        total=$((total + replicas))
        local name="${svc#bh-}"
        [ "$first" = 0 ] && entries="$entries,"
        first=0
        entries="$entries{\"name\":\"$name\",\"ready\":$ready,\"replicas\":$replicas,\"image\":\"$image\",\"age\":\"$age\",\"restarts\":$restarts,\"phase\":\"$phase\",\"imageCommit\":\"$image_commit\",\"upToDate\":$up_to_date}"
    done
    printf '{"cell":"k8s","namespace":"%s","updatedAt":"%s","git":{"head":"%s","branch":"%s","dirty":%s},"services":[%s],"summary":{"ready":%s,"total":%s}}\n' \
        "$NAMESPACE" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$git_head" "$git_branch" "$git_dirty" "$entries" "$ready_total" "$total"
}

# 单个服务启停/重启（操作 deployment 副本数/滚动重启；服务名可不带 bh- 前缀）
scale_service() {
    local svc="${2:-}"
    [ -z "$svc" ] && { echo "[${1}] 用法: bh-k3s ${1} <svc>（server/webui/openvino/postgres/open-webui）" >&2; return 1; }
    case "$svc" in bh-*) ;; *) svc="bh-$svc" ;; esac
    if [ "$(id -u)" != "0" ]; then
        echo "[${1}] 需要 root 权限（k3s.yaml 仅 root 可读），自动提权..." >&2
        sudo "$0" "${1}" "$svc"
        return $?
    fi
    case "$1" in
        start)   k -n "$NAMESPACE" scale deploy "$svc" --replicas=1 && echo "[start] $svc 已扩容至 1" ;;
        stop)    k -n "$NAMESPACE" scale deploy "$svc" --replicas=0 && echo "[stop] $svc 已缩容至 0" ;;
        restart) k -n "$NAMESPACE" rollout restart deploy "$svc" && echo "[restart] $svc 已触发滚动重启" ;;
    esac
}

show_logs() {
    local svc="${1:-bh-server}"
    # 自动补 bh- 前缀：logs webui → app=bh-webui
    case "$svc" in
        bh-*) ;;            # 已带前缀
        *) svc="bh-$svc" ;;
    esac
    k -n "$NAMESPACE" logs -l "app=$svc" --tail="${2:-50}" --all-containers=true
}

open_dashboard() {
    # URL 主机优先级：BAIHUA_PUBLIC_HOST（手机/局域网访问用的宿主 IP）> 本机首个 IP（WSL 里就是 WSL IP）
    # 说明：Windows 上 bh 是经 WSL 调用的，浏览器打不开只能由 Windows 侧代开，
    #       因此支持 BAIHUA_DASHBOARD_PRINT_ONLY=1：只取 token 并输出一行 URL=（供 Windows 包装层捕获）。
    local host="localhost"
    if [ -n "${BAIHUA_PUBLIC_HOST:-}" ]; then
        host="$BAIHUA_PUBLIC_HOST"
    else
        local lanip
        lanip="$(hostname -I 2>/dev/null | awk '{print $1}')"
        if [ -n "$lanip" ] && [[ "$lanip" =~ ^[0-9.]+$ ]]; then
            host="$lanip"
        fi
    fi
    local url="http://$host"
    local token="" attempt
    # 服务可能刚滚动完还在热身（openvino 加载模型约 1 分钟），cli-token 获取失败自动重试
    for attempt in 1 2 3 4 5; do
        token=$(curl -s -m 5 -X POST "http://$host/api/auth/cli-token" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
        [ -n "$token" ] && break
        echo "[dashboard] cli-token 获取失败（第 $attempt/5 次，服务可能仍在热身），3 秒后重试 ..."
        sleep 3
    done
    if [ -n "$token" ]; then
        url="http://$host/?cli-token=$token"
        echo "[dashboard] cli-token 获取成功（5 分钟内可重复打开）"
    else
        echo "[dashboard] cli-token 获取失败（traefik :80 未就绪？先打开无 token URL）"
    fi
    echo "[dashboard] URL: $url"

    # 只输出 URL（Windows 包装层用：WSL 里没有浏览器，由 Windows 侧 Start-Process 打开）
    if [ "${BAIHUA_DASHBOARD_PRINT_ONLY:-}" = "1" ]; then
        echo "URL=$url"
        return 0
    fi

    # root/sudo 下 xdg-open 无法直接访问用户桌面（X/Wayland 授权），
    # 尝试以原用户身份打开；失败则只打印 URL。
    if [ "$(id -u)" = "0" ] && [ -n "${SUDO_USER:-}" ]; then
        local uid xdgrt home
        uid="$(id -u "$SUDO_USER" 2>/dev/null || echo 0)"
        xdgrt="/run/user/$uid"
        home="$(getent passwd "$SUDO_USER" 2>/dev/null | cut -d: -f6 || echo "/home/$SUDO_USER")"
        if [ -d "$xdgrt" ]; then
            # 首选桌面 portal（GNOME/Wayland 打开 URL 的标准路径，最可靠）：
            # 直接经会话总线调 org.freedesktop.portal.OpenURI，绕开 xdg-open 的桌面探测。
            if sudo -u "$SUDO_USER" env HOME="$home" \
                DBUS_SESSION_BUS_ADDRESS="unix:path=$xdgrt/bus" \
                gdbus call --session --dest org.freedesktop.portal.Desktop \
                --object-path /org/freedesktop/portal/desktop \
                --method org.freedesktop.portal.OpenURI.OpenURI \
                "" "$url" {} >/dev/null 2>&1; then
                echo "[dashboard] 已以 $SUDO_USER 身份在桌面打开浏览器"
                return 0
            fi
            # 兜底：xdg-open（必须带全桌面环境变量，且加超时防挂死——
            # sudo 下缺 DBUS_SESSION_BUS_ADDRESS/XDG_CURRENT_DESKTOP 时 xdg-open 会阻塞）
            if sudo -u "$SUDO_USER" env HOME="$home" DISPLAY="${DISPLAY:-:0}" \
                WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
                XDG_RUNTIME_DIR="$xdgrt" \
                DBUS_SESSION_BUS_ADDRESS="unix:path=$xdgrt/bus" \
                XDG_CURRENT_DESKTOP="${XDG_CURRENT_DESKTOP:-ubuntu:GNOME}" \
                timeout 10 xdg-open "$url" >/dev/null 2>&1; then
                echo "[dashboard] 已以 $SUDO_USER 身份在桌面打开浏览器"
                return 0
            fi
        fi
        echo "[dashboard] 无法代开浏览器（root 无桌面授权），请复制上面 URL 手动打开，或退出 sudo 重跑: bh dashboard"
        return 0
    fi

    if command -v xdg-open >/dev/null 2>&1 && xdg-open "$url" >/dev/null 2>&1; then
        echo "[dashboard] 已在默认浏览器打开"
    else
        echo "[dashboard] 无法自动打开浏览器（无 GUI 或未设 DISPLAY），请复制上面 URL 手动打开"
    fi
}

# 找到真实用户的 SSH agent socket（sudo 默认剥离 SSH_AUTH_SOCK，而 GitHub 密钥常只存在于 agent）
find_ssh_auth_sock() {
    local uid sock
    [ -n "${SSH_AUTH_SOCK:-}" ] && [ -S "${SSH_AUTH_SOCK:-}" ] && { echo "$SSH_AUTH_SOCK"; return 0; }
    uid="$(id -u "${SUDO_USER:-$(id -un)}" 2>/dev/null || echo 1000)"
    for sock in "/run/user/$uid/gcr/ssh" "/run/user/$uid/keyring/ssh" "/run/user/$uid/gnupg/S.gpg-agent.ssh" /tmp/ssh-*/agent.*; do
        [ -S "$sock" ] && { echo "$sock"; return 0; }
    done
    return 1
}

# update：git pull + build + deploy
# 关键：git pull 必须以真实用户身份执行（root 的 SSH 密钥通常无 GitHub 权限，会 Permission denied）；
#       build/deploy 需要 root（k3s containerd socket / k3s.yaml 仅 root 可读），非 root 自动提权。
# 因此 update 无论 sudo 与否都能跑通：sudo bh-k3s update / bh-k3s update 均可。
update_all() {
    if [ "$(id -u)" = "0" ] && [ -n "${SUDO_USER:-}" ]; then
        local sock
        sock="$(find_ssh_auth_sock)" || {
            echo "[update] 找不到 $SUDO_USER 的 ssh-agent socket，git pull 无法认证" >&2
            echo "        请改用: bh-k3s update（不带 sudo，脚本会自动提权 build/deploy）" >&2
            return 1
        }
        echo "[update] git pull 以 $SUDO_USER 身份执行（agent: $sock）"
        sudo -u "$SUDO_USER" SSH_AUTH_SOCK="$sock" -H git -C "$ROOT" pull origin main || { echo "[update] git pull 失败"; return 1; }
    else
        git -C "$ROOT" pull origin main || { echo "[update] git pull 失败"; return 1; }
    fi
    local self
    self="$(readlink -f "$0")"
    if [ "$(id -u)" != "0" ]; then
        echo "[update] build/deploy 需要 root，自动提权执行"
        sudo "$self" up || return 1
    else
        up_all || return 1
    fi
    echo "[update] 完成"
}

# annotate：把应用 deployment 的 baihua.git-commit 标注更新为当前仓库 HEAD。
# 场景：build-restart 走 bh-k3s build + bh-k3s restart（不经 deploy_all），deploy_all 只在 deploy/update 时打标注，
#       导致"镜像已重建为新 commit、但标注仍滞后"，bh-k3s status 会误报"落后"。此处单独补一次标注，
#       仅供"镜像重建并已滚动重启成功"后调用（见 DSH 插件 build-restart 流程）。
annotate_commit() {
    local svc="${1:-}"
    [ -z "$svc" ] && { echo "[annotate] 用法: bh-k3s annotate <svc>（server/webui/openvino/postgres）" >&2; return 1; }
    case "$svc" in bh-*) ;; *) svc="bh-$svc" ;; esac
    if [ "$(id -u)" != "0" ]; then
        echo "[annotate] 需要 root 权限（k3s.yaml 仅 root 可读），自动提权..." >&2
        sudo "$0" annotate "${svc#bh-}"
        return $?
    fi
    local git_commit="unknown"
    if command -v git >/dev/null 2>&1 && [ -d "$ROOT/.git" ]; then
        git_commit="$(cd "$ROOT" && git rev-parse --short HEAD 2>/dev/null || echo unknown)"
    fi
    if k -n "$NAMESPACE" annotate deploy "$svc" "baihua.git-commit=$git_commit" --overwrite >/dev/null 2>&1; then
        echo "[annotate] $svc -> baihua.git-commit=$git_commit"
    else
        echo "[annotate] 更新 $svc 标注失败（可稍后 bh-k3s status 复查）" >&2
        return 1
    fi
}

# ---- doctor：只读体检（与 Windows 侧 bh doctor 对齐的输出格式） -------------------
# 目的：一条命令说清「k3s/部署/端口/AI 组件哪一环有问题 + 下一步敲什么」。
# doctor --json：把收集到的检查项转成 JSON（字段与 Windows 侧 bh doctor --json 同构，cell=k8s）
doctor_json() {
    local okc="$1" warn="$2" fail="$3"
    python3 - "$DOCTOR_REC" "$okc" "$warn" "$fail" <<'PY'
import json, sys, datetime
path, ok, warn, fail = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
checks = []
if path:
    try:
        with open(path, encoding='utf-8') as f:
            for line in f:
                line = line.rstrip('\n')
                if not line:
                    continue
                parts = line.split('\t')
                while len(parts) < 4:
                    parts.append('')
                checks.append({'name': parts[0], 'status': parts[1], 'detail': parts[2], 'fix': parts[3]})
    except FileNotFoundError:
        pass
doc = {
    'cell': 'k8s',
    'updatedAt': datetime.datetime.utcnow().strftime('%Y-%m-%dT%H:%M:%SZ'),
    'ok': fail == 0,
    'summary': {'passed': ok, 'warned': warn, 'failed': fail},
    'checks': checks,
}
print(json.dumps(doc, ensure_ascii=False, indent=2))
PY
}

doctor_cmd() {
    # 用法: bh doctor [--json]；--json 的输出与 Windows 侧 doctor --json 同构（cell=k8s），
    # 字段: cell / updatedAt / ok / summary{passed,warned,failed} / checks[{name,status,detail,fix}]
    local mode="${1:-}"
    local okc=0 warn=0 fail=0 runtime_ok=1
    DOCTOR_REC="$(mktemp -t bh-doctor.XXXXXX 2>/dev/null || true)"
    _rec() { [ -n "$DOCTOR_REC" ] && printf '%s\t%s\t%s\t%s\n' "$1" "$2" "${3:-}" "${4:-}" >> "$DOCTOR_REC"; return 0; }
    _dok()  { okc=$((okc+1));  _rec "$1" ok '' ''; [ "$mode" = '--json' ] || printf '[ OK ] %s\n' "$1"; }
    _dwarn(){ warn=$((warn+1)); _rec "$1" warn '' "${2:-}"; [ "$mode" = '--json' ] || { printf '[WARN] %s\n' "$1"; [ -n "${2:-}" ] && printf '       -> %s\n' "$2"; } ; }
    _dfail(){ fail=$((fail+1)); _rec "$1" fail '' "${2:-}"; [ "$mode" = '--json' ] || { printf '[FAIL] %s\n' "$1"; [ -n "${2:-}" ] && printf '       -> %s\n' "$2"; } ; return 0; }
    _finish() {
        if [ "$mode" = '--json' ]; then
            doctor_json "$okc" "$warn" "$fail"
        else
            printf '\n体检完成：%s 项通过 / %s 项警告 / %s 项失败\n' "$okc" "$warn" "$fail"
        fi
        [ -n "$DOCTOR_REC" ] && rm -f "$DOCTOR_REC"
        [ "$fail" -gt 0 ] && return 1 || return 0
    }

    [ "$mode" = '--json' ] || printf 'bh doctor —— k3s 形态体检（Linux）\n\n'

    # 1) k3s 运行时
    if command -v k3s >/dev/null 2>&1; then
        _dok "k3s: $(k3s --version 2>/dev/null | head -1)"
    else
        _dfail "没有找到 k3s 可执行文件" "按 k8s/README.md 安装（curl -sfL https://get.k3s.io | sh -）"; runtime_ok=0
    fi
    if systemctl is-active --quiet k3s 2>/dev/null; then
        _dok 'k3s.service: active'
        if systemctl is-enabled --quiet k3s 2>/dev/null; then
            _dok 'k3s 开机自启: enabled（Linux 侧不需要 autostart 子命令）'
        else
            _dwarn 'k3s 未设置开机自启' 'sudo systemctl enable k3s'
        fi
    else
        _dfail 'k3s.service 未运行' 'sudo systemctl start k3s'; runtime_ok=0
    fi

    # 2) API 可达 + namespace
    if ! k get --raw /version >/dev/null 2>&1; then
        _dfail 'kubectl 连不上 k3s API（kubeconfig 权限？）' 'sudo chmod 644 /etc/rancher/k3s/k3s.yaml 或 sudo bh doctor'; runtime_ok=0
    elif k get namespace "$NAMESPACE" >/dev/null 2>&1; then
        _dok "namespace $NAMESPACE 存在"
    else
        _dwarn "namespace $NAMESPACE 不存在（还没部署）" 'bh deploy'
        _finish
        return $?
    fi

    # k3s 没跑起来就别继续刷屏了：后面的部署项会全部报「不存在」，纯噪声
    if [ "$runtime_ok" = 0 ]; then
        _finish
        return $?
    fi

    # 3) Deployment 就绪（按服务逐个看）
    local svc ready reps
    for svc in bh-server bh-webui bh-postgres bh-open-webui bh-openvino; do
        if ! k -n "$NAMESPACE" get deploy "$svc" >/dev/null 2>&1; then
            _dwarn "deploy/$svc 不存在" 'bh deploy（openvino 由 deploy 自动探测是否启用）'
            continue
        fi
        ready="$(k -n "$NAMESPACE" get deploy "$svc" -o jsonpath='{.status.readyReplicas}' 2>/dev/null)"
        reps="$(k -n "$NAMESPACE" get deploy "$svc" -o jsonpath='{.spec.replicas}' 2>/dev/null)"
        ready=${ready:-0}; reps=${reps:-0}
        if [ "$reps" = "0" ]; then
            _dwarn "deploy/$svc 已停（replicas=0）" "bh start ${svc#bh-}"
        elif [ "$ready" -ge "$reps" ] 2>/dev/null; then
            _dok "deploy/$svc ready $ready/$reps"
        else
            _dfail "deploy/$svc 未就绪（$ready/$reps）" "bh logs ${svc#bh-} 100"
        fi
    done

    # 4) PVC
    local pvc_phases
    pvc_phases="$(k -n "$NAMESPACE" get pvc -o jsonpath='{range .items[*]}{.metadata.name}={.status.phase}{"\n"}{end}' 2>/dev/null)"
    if [ -z "$pvc_phases" ]; then
        _dwarn '没有 PVC' 'bh deploy 会创建（数据在 hostPath，别手删）'
    elif printf '%s' "$pvc_phases" | grep -qv '=Bound'; then
        _dfail "有 PVC 未 Bound: $(printf '%s' "$pvc_phases" | grep -v '=Bound' | tr '\n' ' ')" 'kubectl -n baihua describe pvc 看原因'
    else
        _dok 'PVC 全部 Bound'
    fi

    # 5) 端口（8788/5177 由 Traefik 暴露，:80 是统一入口）
    local port
    for port in 80 8788 5177; do
        if timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port" >/dev/null 2>&1; then
            _dok "端口 $port 可连"
        else
            _dwarn "端口 $port 不通" "kubectl -n $NAMESPACE get svc；Traefik/LoadBalancer 是否就绪"
        fi
    done

    # 6) AI 组件（可选）
    if k -n "$NAMESPACE" get pods -l app=bh-openvino --no-headers 2>/dev/null | grep -q Running; then
        _dok 'OVMS pod Running（:8000）'
    else
        _dwarn 'OVMS pod 未运行（AI 功能降级）' 'bh openvino status / bh openvino on'
    fi
    if k -n "$NAMESPACE" get pods -l app=bh-open-webui --no-headers 2>/dev/null | grep -q Running; then
        _dok 'open-webui pod Running（AI 对话页后端）'
    else
        _dwarn 'open-webui pod 未运行（/open-webui 页会空白）' 'bh start open-webui'
    fi

    _finish
}
case "${1:-help}" in
    build)     build_all "${@:2}" ;;
    deploy)    deploy_all ;;
    up)        up_all "${2:-}" ;;
    update)    update_all ;;
    prune)     prune_cache ;;
    backup)    backup_cmd "${2:-}" ;;
    restore)   restore_cmd "${2:-}" "${3:-}" ;;
    status)    if [ "${2:-}" = "--json" ]; then status_json; else status_all; fi ;;
    start|stop|restart) scale_service "${1}" "${2:-}" ;;
    annotate)  annotate_commit "${2:-}" ;;
    openvino)  openvino_cmd "${2:-status}" ;;
    doctor)    doctor_cmd "${2:-}" ;;
    logs)      show_logs "${2:-bh-server}" "${3:-50}" ;;
    destroy)   k delete namespace "$NAMESPACE"; echo "[destroy] done" ;;
    dashboard) open_dashboard ;;
    k8s|native|dev|wslc|container)
        # Windows 侧才有「wslc 默认 / dev native」两种形态；Linux 只有 k3s 一种 ——
        # 这些词在 Linux 上没有意义，明确报错，而不是落到 help 里让人猜。
        case "$1" in
            dev|wslc|container)
                echo "[bh] Linux 上只有 k3s 形态：没有 '$1' 这个子命令（那是 Windows 侧的多形态命名）。" >&2
                echo "     直接用: bh <cmd>  例: bh status / bh start server / bh doctor" >&2
                ;;
            *)
                echo "[bh] '$1' 不再需要：Linux 上 bh 就是 k3s 形态，直接用 bh <cmd>。" >&2
                echo "     例: bh ${*:2}" >&2
                ;;
        esac
        exit 1
        ;;
    help)      help_text ;;
    *)         help_text ;;
esac
