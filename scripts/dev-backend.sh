#!/usr/bin/env bash
# macOS/Linux 开发启动脚本
# 合并后只有两个进程：Baihua.Server（后端，8788）与 Baihua.Web（WebUI，5177）
# 数据库为单一 PostgreSQL 库（默认 baihua，可用 PG_DATABASE 覆盖）

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

echo "========================================"
echo "百花 - 开发环境启动 (macOS/Linux)"
echo "========================================"
echo ""

# 检查 dotnet
if ! command -v dotnet &> /dev/null; then
    echo "错误：未安装 .NET SDK"
    echo "请访问 https://dotnet.microsoft.com/download 安装"
    exit 1
fi

echo "正在启动服务..."
echo ""

# 清理编译服务器缓存，避免 VBCSCompiler 缓存导致 stale binary
echo "清理编译服务器缓存..."
dotnet build-server shutdown 2>/dev/null || true
echo ""

# 辅助函数：在终端中启动进程
launch_in_terminal() {
    local label="$1"
    local dir="$2"
    local port="$3"
    local cmd="cd '$dir' && dotnet watch run --non-interactive --no-hot-reload --urls 'http://0.0.0.0:$port'"

    echo "[$label] 启动 $label (端口 $port)..."
    osascript -e "tell application \"Terminal\" to do script \"$cmd\"" 2>/dev/null || \
        echo "  请手动在终端运行: cd \"$dir\" && dotnet watch run --non-interactive --no-hot-reload --urls 'http://0.0.0.0:$port'"
}

# 启动唯一后端（家庭 / AI / 知识库 三模块在同一进程内）
launch_in_terminal "Baihua.Server" "$ROOT/services/Baihua.Server" "8788"

# 启动 WebUI
echo "[WebUI] 启动 Baihua.Web (端口 5177)..."
osascript -e "tell application \"Terminal\" to do script \"cd '$ROOT/services/Baihua.Web' && dotnet watch run --non-interactive\"" 2>/dev/null || \
    echo "  请手动在终端运行: cd \"$ROOT/services/Baihua.Web\" && dotnet watch run --non-interactive"

echo ""
echo "========================================"
echo "服务启动中..."
echo "========================================"
echo ""
echo "后端服务:"
echo "  - Baihua.Server  http://localhost:8788   （家庭 / AI / 知识库 三模块合一）"
echo ""
echo "前端界面:"
echo "  - Baihua.Web     http://localhost:5177"
echo ""
echo "数据库: 单一 PostgreSQL 库（默认 baihua；PG_HOST/PG_USER/PG_PASSWORD/PG_DATABASE）"
echo "  · 首次使用可先跑 scripts/init-pg.ps1（建库）"
echo "  · 旧三库（family/vault/ai）迁移见 scripts/migrate-to-single-db.ps1"
echo ""
echo "提示: 使用 Ctrl+C 停止服务"
