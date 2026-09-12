#!/usr/bin/env bash
set -euo pipefail

# ============================================
# 百花 Docker 一键停止脚本（postgres + server + webui + nginx [+ openvino/openobserve]）
# 用法：./stop.sh [--clean]  (--clean 同时清理镜像)
# ============================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "${SCRIPT_DIR}"

echo "停止百花 Docker 服务..."
docker compose down --remove-orphans

if [[ "${1:-}" == "--clean" ]]; then
    echo "清理 Docker 镜像..."
    docker compose down --rmi local --remove-orphans
    echo "镜像已清理"
fi

echo "百花 Docker 服务已停止"
