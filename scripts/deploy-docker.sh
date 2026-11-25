#!/usr/bin/env bash
# 百花 Docker 化部署脚本（远端 Linux 服务器，全容器栈）
#
# 合并前这里会分别构建 family / ai / vault / webui 四个镜像并逐个做健康检查；
# 合并为单进程 + 单文件库后，栈里只剩：server + webui + nginx（+ 可选 openvino/openobserve）。
# 数据库是 SQLite 单文件（/opt/baihua/data/db/baihua.db），没有独立数据库容器。
#
# 用法:
#   ./scripts/deploy-docker.sh <user@host> [--skip-build]
#
# 前置：
#   1) 目标机已装 docker + docker compose plugin
#   2) 本机可免密 ssh 到目标机
#   3) docker/.env 可选（端口 / 知识库路径 / OpenObserve 口令等），随源码一并同步
#
# 说明：Linux 主推形态是原生 systemd + SQLite：`bh deploy`（tools/bh/bh.sh）。
#      compose 形态仅作为可选方案保留。
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
    echo "[!] 未找到 docker/.env，将使用默认配置（需要时 cp docker/.env.example docker/.env 后修改）。"
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
echo "  数据库:       /opt/baihua/data/db/baihua.db（SQLite 单文件）"
echo ""
echo "提示：从旧 PostgreSQL 迁移数据请在目标机执行："
echo "      PG_PASSWORD=... bh db-migrate   （或 tools/db-migrate 直接指定 --target）"
