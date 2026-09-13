#!/bin/bash
# run-baihua.sh — 启动百花 self-contained（SQLite，无需外部依赖）
#
# 环境变量（可选覆盖）:
#   SQLITE_PATH           SQLite 文件路径（默认 ./baihua.db）
#   BAIHUA_SERVER_PORT    后端端口（默认 8788）
#   BAIHUA_WEBUI_PORT     WebUI 端口（默认 5177）
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"

# 嵌入式 DB（无需 PostgreSQL）
export BAIHUA_DB_PROVIDER=sqlite
export SQLITE_PATH="${SQLITE_PATH:-$DIR/baihua.db}"

SERVER_PORT="${BAIHUA_SERVER_PORT:-8788}"
WEBUI_PORT="${BAIHUA_WEBUI_PORT:-5177}"

# WebUI → 后端
export BaihuaServer__BaseUrl="http://127.0.0.1:$SERVER_PORT/"
# 后端公网地址（配对二维码用）
export Baihua__PublicBaseUrl="${Baihua__PublicBaseUrl:-http://127.0.0.1:$SERVER_PORT}"

echo "[baihua] 启动 self-contained（SQLite: $SQLITE_PATH）"
echo "[baihua] 后端 :$SERVER_PORT  WebUI :$WEBUI_PORT"

# 起 server
"$DIR/server/bh-server" --urls "http://0.0.0.0:$SERVER_PORT" &
SERVER_PID=$!

# 等后端就绪再起 WebUI（避免 WebUI 首次请求失败）
for i in $(seq 1 15); do
    if curl -sf "http://127.0.0.1:$SERVER_PORT/health" >/dev/null 2>&1; then
        echo "[baihua] 后端就绪"
        break
    fi
    sleep 1
done

# 起 webui
"$DIR/webui/bh-webui" --urls "http://0.0.0.0:$WEBUI_PORT" &
WEBUI_PID=$!

trap "
echo ''
echo '[baihua] 停止...'
kill $WEBUI_PID 2>/dev/null || true
kill $SERVER_PID 2>/dev/null || true
wait 2>/dev/null || true
echo '[baihua] 已停止'
" EXIT INT TERM

echo "[baihua] WebUI 就绪: http://localhost:$WEBUI_PORT"
echo "[baihua] 按 Ctrl+C 停止"
wait