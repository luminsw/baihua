#!/usr/bin/env bash
# 百花 Docker 化部署脚本（远端 Linux 服务器，全容器栈）
#
# 合并前这里会分别构建 family / ai / vault / webui 四个镜像并逐个做健康检查；
# 合并为单进程 + 单库后，栈里只剩：postgres + server + webui + nginx（+ 可选 openvino/openobserve）。
#
# 用法:
#   ./scripts/deploy-docker.sh <user@host> [--skip-build]
#
# 前置：
#   1) 目标机已装 docker + docker compose plugin
#   2) 本机可免密 ssh 到目标机
#   3) docker/.env 中已填 PG_PASSWORD（compose 必填），该文件会随源码一并同步
#
# 说明：k8s 部署请改用 `bh deploy`（tools/bh/linux/k8s/bh.sh）——那是当前主推形态。
set -euo pipefail

SERVER="${1:-}"
SKIP_BUILD="${2:-}"

if [[ -z "$SERVER" ]]; then
    echo "用法: $0 <user@host> [--skip-build]"
    exit 1
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REMOTE_DIR="/opt/baihua/src"
COMPOSE_DIR="/opt/baihua/compose"

echo "=== 百花 Docker 部署 → ${SERVER} ==="

if [[ ! -f "${ROOT}/docker/.env" ]]; then
    echo "[!] 缺少 docker/.env（compose 需要 PG_PASSWORD）。请先: cp docker/.env.example docker/.env 并填写。"
    exit 1
fi

# 1) 远端目录
ssh "${SERVER}" "mkdir -p ${REMOTE_DIR} ${COMPOSE_DIR} /opt/baihua/data /opt/baihua/logs /opt/baihua/models"

# 2) 同步源码（排除构建产物与本地数据）
echo "[1/3] rsync 源码..."
rsync -az --delete \
    --exclude '.git' --exclude 'bin' --exclude 'obj' --exclude 'out' \
    --exclude 'node_modules' --exclude 'logs' \
    "${ROOT}/" "${SERVER}:${REMOTE_DIR}/"

# 3) 构建 + 启动（远端执行）
echo "[2/3] 远端构建并启动容器栈..."
BUILD_FLAG="--build"
[[ "$SKIP_BUILD" == "--skip-build" ]] && BUILD_FLAG=""
ssh "${SERVER}" "set -euo pipefail; cd ${REMOTE_DIR}/docker && ln -sfn \$(pwd) ${COMPOSE_DIR} && docker compose ${BUILD_FLAG} up -d --remove-orphans"

# 4) 健康检查（唯一后端 + WebUI）
echo "[3/3] 健康检查..."
ok=0
HOST_ONLY="${SERVER#*@}"
for i in $(seq 1 45); do
    server_code=$(ssh "${SERVER}" "curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:8788/health" 2>/dev/null || echo "000")
    webui_code=$(ssh "${SERVER}" "curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:5177/" 2>/dev/null || echo "000")
    if [[ "$server_code" == "200" && "$webui_code" == "200" ]]; then
        ok=1
        break
    fi
    echo "  等待就绪... server=$server_code webui=$webui_code ($i/45)"
    sleep 4
done

if [[ "$ok" != "1" ]]; then
    echo "[X] 健康检查未通过，最近日志："
    ssh "${SERVER}" "cd ${REMOTE_DIR}/docker && docker compose logs --tail 40"
    exit 1
fi

echo ""
echo "=== 部署完成 ==="
echo "  后端（唯一）: http://${HOST_ONLY}:8788"
echo "  WebUI:        http://${HOST_ONLY}:5177"
echo "  数据库:       ${HOST_ONLY}:5432（库 baihua）"
echo ""
echo "提示：首次部署若需迁移旧的 family/vault/ai 三库，请在目标机执行"
echo "      scripts/migrate-to-single-db.ps1（或在目标机手动 pg_dump/psql 导入 baihua 库）。"
