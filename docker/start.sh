#!/usr/bin/env bash
set -euo pipefail

# ============================================
# 百花 Docker 一键启动脚本（容器全栈：postgres + server + webui + nginx）
# 用法：./start.sh [--build] [--inference]
#   --inference 额外启动 OVMS 容器（baihua 本地推理）
# ============================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "${SCRIPT_DIR}"

# 检查 .env 文件
if [[ ! -f .env ]]; then
    echo "警告：未找到 .env 文件，使用默认配置"
    echo "提示：cp .env.example .env 并按需修改（PG_PASSWORD 为必填）"
fi

# 确保宿主机目录存在
mkdir -p /opt/baihua/data /opt/baihua/logs \
         /opt/baihua/config/server /opt/baihua/config/webui /opt/baihua/config/nginx \
         /opt/baihua/data/postgres /opt/baihua/data/openobserve \
         /opt/baihua/models          # OpenVINO 模型仓库（OVMS 挂载，模型缺失则对应 servable 不加载）

# 构建参数
BUILD_FLAG=""
PROFILE_ARGS=()
for arg in "$@"; do
    case "$arg" in
        --build)     BUILD_FLAG="--build" ;;
        --inference) PROFILE_ARGS+=(--profile inference) ;;
        --observability) PROFILE_ARGS+=(--profile observability) ;;
    esac
done

echo "启动百花容器栈（postgres + server + webui + nginx）..."
docker compose "${PROFILE_ARGS[@]}" up -d ${BUILD_FLAG} --remove-orphans

echo ""
echo "等待服务就绪..."
sleep 5

docker compose ps

echo ""
echo "========================================"
echo "百花服务已启动"
echo "  后端（唯一）:      http://127.0.0.1:8788   （家庭 / AI / 知识库 三模块合一）"
echo "  WebUI:             http://127.0.0.1:5177"
echo "  Nginx (HTTP):      http://127.0.0.1:80"
echo "  PostgreSQL:        127.0.0.1:5432（库：${PG_DATABASE:-baihua}）"
echo "  OpenVINO (OVMS):   http://127.0.0.1:8000   （--inference 时启动）"
echo "  OpenObserve:       http://127.0.0.1:5082   （--observability 时启动）"
echo ""
echo "数据目录: /opt/baihua/data"
echo "日志目录: /opt/baihua/logs"
echo "配置目录: /opt/baihua/config"
echo "========================================"
