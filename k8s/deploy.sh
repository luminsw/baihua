#!/bin/bash
# 百花服务 K8s 部署脚本
# 用法:
#   ./deploy.sh build    # 构建 Docker 镜像
#   ./deploy.sh deploy   # 部署到 K8s 集群
#   ./deploy.sh status   # 查看部署状态
#   ./deploy.sh logs     # 查看日志
#   ./deploy.sh destroy  # 删除所有资源
#   ./deploy.sh all      # build + load + deploy + verify-gpu
set -euo pipefail

# ============================================================
# 配置
# ============================================================
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
K8S_DIR="$SCRIPT_DIR"
NAMESPACE="baihua"
REGISTRY="${REGISTRY:-}"  # 如有远程镜像仓库，设置 REGISTRY=registry.example.com/

# 镜像列表（本仓库构建的镜像；数据库镜像 postgres:18-alpine 是外部镜像，不在此构建/加载）
IMAGES=(
    "bh-server:latest"
    "bh-webui:latest"
    "bh-openvino:latest"
)

# ============================================================
# 颜色输出
# ============================================================
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BLUE='\033[0;34m'
NC='\033[0m'

log() { echo -e "${GREEN}[$(date '+%H:%M:%S')]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err() { echo -e "${RED}[ERROR]${NC} $*" >&2; }
info() { echo -e "${BLUE}[INFO]${NC} $*"; }

# ============================================================
# 1. 构建基础镜像
# ============================================================
build_base() {
    log "构建基础镜像 bh/base-runtime:latest ..."
    docker build -f "$K8S_DIR/images/Dockerfile.base-runtime" -t bh/base-runtime:latest "$PROJECT_ROOT"
    log "基础镜像构建完成"
}

# ============================================================
# 2. 构建 Docker 镜像
# ============================================================
build_images() {
    build_base

    log "构建服务镜像 ..."

    # 多阶段源码构建：.NET 服务在离线 SDK 镜像（bh/sdk-offline，含 nuget-local 包源）内现场 publish，
    # 宿主/发布机无需安装 .NET SDK；OpenVINO 镜像纯 Python 源码构建。所有镜像统一使用项目根作为构建上下文。

    # 离线 SDK 基础镜像（nuget-local 包源经 build-context 沉底，仅首次构建传输一次；需 Docker Buildx）
    log "  构建 bh/sdk-offline:latest（离线 SDK 基础镜像）..."
    docker buildx build --build-context "nuget=$PROJECT_ROOT/nuget-local" \
        -f "$K8S_DIR/images/Dockerfile.sdk-offline" -t bh/sdk-offline:latest "$PROJECT_ROOT"

    # Server（唯一后端：家庭 / AI / 知识库三模块同一进程，合并前是 vault+ai+family 三个镜像）
    log "  构建 bh-server:latest（唯一后端，OpenVINO 已拆分到独立容器）..."
    docker build -f "$K8S_DIR/images/Dockerfile.server" -t bh-server:latest "$PROJECT_ROOT"

    # WebUI
    log "  构建 bh-webui:latest ..."
    docker build -f "$K8S_DIR/images/Dockerfile.webui" -t bh-webui:latest "$PROJECT_ROOT"

    # OpenVINO 推理服务器（独立容器，含 GPU 支持）
    log "  构建 bh-openvino:latest（OpenVINO + Intel GPU 推理服务）..."
    docker build -f "$K8S_DIR/images/Dockerfile.openvino-server" -t bh-openvino:latest "$PROJECT_ROOT"

    log "所有镜像构建完成"
    docker images | grep -E "bh-(server|webui|openvino)" | head -10
}

# ============================================================
# 3. 加载镜像到集群（minikube；k3s 用 nerdctl 直连 containerd，无需 load）
# ============================================================
load_images() {
    if command -v minikube &>/dev/null; then
        log "检测到 minikube，加载镜像到集群 ..."
        for img in "${IMAGES[@]}"; do
            minikube image load "$img" 2>/dev/null && log "  已加载: $img" || warn "  加载失败: $img"
        done
    else
        info "未检测到 minikube，假设镜像已在节点上可用"
        info "如使用远程仓库，请先 docker push 镜像"
    fi
}

# ============================================================
# 4. 部署到 K8s
# ============================================================

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

# Intel GPU 探测：决定是否部署 openvino 相关服务（10-intel-gpu-plugin + 22a-openvino）
# 顺序：显式开关 BAIHUA_ENABLE_OPENVINO（1/0）> WSL2 GPU-PV（内核判定 + /dev/dxg 字符设备）> 真机 /dev/dri + lspci 厂商为 Intel
has_intel_gpu() {
    case "${BAIHUA_ENABLE_OPENVINO:-auto}" in
        1|true|yes|on)  log "Intel GPU: BAIHUA_ENABLE_OPENVINO=on 强制启用"; return 0 ;;
        0|false|no|off) log "Intel GPU: BAIHUA_ENABLE_OPENVINO=off 强制停用"; return 1 ;;
    esac
    # WSL2：GPU-PV 走 /dev/dxg（必须是字符设备；空目录是 k8s hostPath 建的，不作数）
    if is_wsl; then
        if [ -c /dev/dxg ]; then
            log "Intel GPU: WSL2 GPU-PV（/dev/dxg 字符设备）"
            return 0
        fi
        log "Intel GPU: WSL2 未检测到 /dev/dxg 字符设备，继续查 /dev/dri"
    fi
    if [ ! -d /dev/dri ] || ! ls /dev/dri/renderD* >/dev/null 2>&1; then
        log "Intel GPU: 未检测到 /dev/dri 渲染节点"
        return 1
    fi
    if command -v lspci >/dev/null 2>&1; then
        if lspci | grep -qiE '(vga|3d|display).*intel'; then
            log "Intel GPU: 检测到 Intel GPU（lspci）"
            return 0
        fi
        log "Intel GPU: 有 /dev/dri 但非 Intel 显卡（openvino 需要 Intel GPU，跳过）"
        return 1
    fi
    log "Intel GPU: 检测到 /dev/dri（无 lspci，按有 GPU 处理）"
    return 0
}

deploy() {
    log "部署到 K8s 集群 (namespace: $NAMESPACE) ..."

    # 基础清单（与 GPU 无关，始终部署）
    # 25-postgres 必须在这里：此前它被本列表遗漏（清单从未被任何部署路径应用），
    # 导致 bh-server 起来后连不上数据库。
    local manifests=(
        "00-namespace.yaml"
        "01-configmap.yaml"
        "02-secret.yaml"
        "03-pvc.yaml"
        "25-postgres.yaml"
        "20-server.yaml"
        "23-webui.yaml"
        "24-traefik.yaml"
    )

    for manifest in "${manifests[@]}"; do
        log "  应用 $manifest ..."
        kubectl apply -f "$K8S_DIR/$manifest" 2>&1 | sed 's/^/    /'
    done

    # GPU 按需：有 Intel GPU 才部署 intel-gpu-plugin（kube-system）+ bh-openvino
    if has_intel_gpu; then
        for manifest in "10-intel-gpu-plugin.yaml" "22a-openvino.yaml"; do
            log "  应用 $manifest ..."
            kubectl apply -f "$K8S_DIR/$manifest" 2>&1 | sed 's/^/    /'
        done
    else
        warn "无 Intel GPU：跳过 openvino 相关服务（10-intel-gpu-plugin / 22a-openvino）"
        # 之前部署过的话停掉，避免在无 GPU 节点上空转/崩溃循环
        # （DaemonSet 不支持 scale，用 delete --ignore-not-found；on 时 apply 清单会重建）
        kubectl -n kube-system delete ds intel-gpu-plugin --ignore-not-found >/dev/null 2>&1 && \
            warn "intel-gpu-plugin 已删除（无 GPU）"
        kubectl -n "$NAMESPACE" scale deploy bh-openvino --replicas=0 >/dev/null 2>&1 && \
            warn "bh-openvino 已缩容至 0"
    fi

    log "滚动重启应用新镜像（本地 :latest 镜像不重启不会生效）..."
    # 显式列出应用 deployment，避免误重启 bh-postgres（数据库无需随应用重建而重启）
    kubectl -n "$NAMESPACE" rollout restart deployment bh-server bh-webui bh-openvino >/dev/null 2>&1 || \
        warn "rollout restart 失败（首次部署可忽略）"

    log "等待应用滚动完成（rollout status，确保新 pod 全部就绪）..."
    kubectl -n "$NAMESPACE" rollout status deployment bh-server bh-webui bh-openvino --timeout=300s 2>&1 || \
        warn "部分 deployment 未在 300s 内就绪，请用 'status' 命令查看详情"

    log "部署完成！"
    status
}

# ============================================================
# 5. 查看状态
# ============================================================
status() {
    log "=== Pod 状态 ==="
    kubectl -n "$NAMESPACE" get pods -o wide

    echo ""
    log "=== Service 状态 ==="
    kubectl -n "$NAMESPACE" get svc

    echo ""
    log "=== PVC 状态 ==="
    kubectl -n "$NAMESPACE" get pvc

    echo ""
    log "=== Intel GPU 资源 ==="
    kubectl get nodes -o custom-columns=NAME:.metadata.name,GPU:.status.capacity.'intel\.com/gpu' 2>/dev/null || \
        info "无 Intel GPU 资源（Device Plugin 未部署或节点无 GPU）"

    echo ""
    log "=== 访问地址 ==="
    local NODE_IP=$(kubectl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}' 2>/dev/null)
    if [ -n "$NODE_IP" ]; then
        info "  WebUI:  http://$NODE_IP/   (Traefik :80)"
        info "  Server: http://$NODE_IP/   (Traefik /mg/* /api/* /vault/* /mcp 转发)"
        info "  OpenVINO (OVMS): http://bh-openvino:8000 (集群内, OpenAI 兼容 /v3)"
    fi
}

# ============================================================
# 6. 查看日志
# ============================================================
show_logs() {
    local service="${1:-bh-server}"
    local tail="${2:-50}"
    log "=== $service 日志 (最后 $tail 行) ==="
    kubectl -n "$NAMESPACE" logs -l app="$service" --tail="$tail" --all-containers=true
}

# ============================================================
# 7. 删除部署
# ============================================================
destroy() {
    warn "即将删除 namespace: $NAMESPACE 及其所有资源"
    read -p "确认删除? (y/N): " confirm
    if [ "$confirm" = "y" ] || [ "$confirm" = "Y" ]; then
        kubectl delete namespace "$NAMESPACE"
        log "已删除 namespace $NAMESPACE"
    else
        info "已取消"
    fi
}

# ============================================================
# 8. 验证 GPU 可用性
# ============================================================
verify_gpu() {
    log "=== 验证 Intel GPU 可用性 ==="

    # 检查 Device Plugin
    log "1. 检查 Intel GPU Device Plugin ..."
    if kubectl -n kube-system get ds intel-gpu-plugin &>/dev/null; then
        log "  Device Plugin 已部署"
    else
        err "  Device Plugin 未部署"
        return 1
    fi

    # 检查节点 GPU 资源
    log "2. 检查节点 GPU 资源 ..."
    local gpu_count=$(kubectl get nodes -o jsonpath='{.items[0].status.capacity}' 2>/dev/null | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('intel.com/gpu','0'))" 2>/dev/null || echo "0")
    if [ "$gpu_count" -gt 0 ]; then
        log "  节点有 $gpu_count 个 Intel GPU"
    else
        warn "  节点未注册 Intel GPU 资源"
        info "  请检查: 1) 节点有 /dev/dri/renderD128  2) Device Plugin Pod 正常运行"
    fi

    # 检查 bh-openvino Pod（OVMS 官方镜像，无 python；用 --version 确认）
    log "3. 检查 bh-openvino Pod（OVMS 版本） ..."
    kubectl -n "$NAMESPACE" exec deployment/bh-openvino -- ovms --version 2>&1 | head -2 || \
        err "  无法在 bh-openvino Pod 中执行 OVMS 检测"

    # 检查 OVMS 就绪（readiness 探针 = /v2/health/ready）
    log "4. 检查 OVMS（OpenVINO Model Server）就绪 ..."
    local ovms_ready
    ovms_ready=$(kubectl -n "$NAMESPACE" get pod -l app=bh-openvino -o jsonpath='{.items[0].status.conditions[?(@.type=="Ready")].status}' 2>/dev/null)
    if [ "$ovms_ready" = "True" ]; then
        log "  bh-openvino Ready（/v2/health/ready 通过）"
        kubectl -n "$NAMESPACE" exec deployment/bh-openvino -- bash -c 'command -v curl >/dev/null 2>&1 && curl -s http://localhost:8000/v1/models || echo "(OVMS 镜像无 curl，可从 Server 侧探测)"' 2>&1 | head -c 300 || true
    else
        warn "  bh-openvino 未就绪（首次加载 7B 模型编译较慢，需数分钟；稍后 bh status 复查）"
    fi

    # 检查 Server → OVMS 连通性（bh-server 镜像自带 curl）
    log "5. 检查 Server → OVMS 连通性 ..."
    kubectl -n "$NAMESPACE" exec deployment/bh-server -- curl -s -m 5 http://bh-openvino:8000/v1/models 2>&1 | head -c 200 || \
        warn "  Server 暂时无法连接到 bh-openvino:8000（OVMS 可能仍在启动）"
}

# ============================================================
# 主入口
# ============================================================
case "${1:-help}" in
    build)
        build_images
        ;;
    deploy)
        deploy
        ;;
    load)
        load_images
        ;;
    status)
        status
        ;;
    logs)
        show_logs "${2:-}" "${3:-50}"
        ;;
    destroy)
        destroy
        ;;
    verify-gpu)
        verify_gpu
        ;;
    all)
        build_images
        load_images
        deploy
        verify_gpu
        ;;
    *)
        echo "百花服务 K8s 部署工具"
        echo ""
        echo "用法: $0 <command> [args]"
        echo ""
        echo "命令:"
        echo "  build        构建 Docker 镜像（含独立 OpenVINO 容器）"
        echo "  deploy       部署到 K8s 集群"
        echo "  load         加载镜像到 minikube 集群"
        echo "  status       查看部署状态"
        echo "  logs <svc>   查看服务日志 (默认: bh-server)"
        echo "  verify-gpu   验证 Intel GPU + OpenVINO 服务可用性"
        echo "  destroy      删除所有 K8s 资源"
        echo "  all          build + load + deploy + verify-gpu"
        ;;
esac
