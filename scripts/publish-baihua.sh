#!/bin/bash
# publish-baihua.sh — 打包百花为 self-contained 单文件（server + webui）
#
# 用法:
#   bash scripts/publish-baihua.sh [linux-x64|win-x64]  # 默认 linux-x64
#
# 产出 dist/<rid>/ 下:
#   run-baihua.sh   一键启动脚本
#   server/bh-server  后端单文件（含 .NET 运行时）
#   webui/bh-webui    WebUI 单文件（含 .NET 运行时）
set -euo pipefail

RID="${1:-linux-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist/$RID"

echo "[publish] 打包百花 self-contained（RID=$RID）→ $OUT"

# Server：Release 锁 linux-x64，命令行 -r 覆盖
dotnet publish "$ROOT/services/Baihua.Server/Baihua.Server.csproj" \
    -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$OUT/server"

# WebUI
dotnet publish "$ROOT/services/Baihua.Web/Baihua.Web.csproj" \
    -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$OUT/webui"

# 桌面 App（Photino 窗口包 WebUI，输出到 $OUT 与 server/ webui/ 同级）
dotnet publish "$ROOT/clients/BaihuaDesktop/BaihuaDesktop.csproj" \
    -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$OUT"

# 启动脚本
cp "$ROOT/scripts/run-baihua.sh" "$OUT/run-baihua.sh"
chmod +x "$OUT/run-baihua.sh"

echo ""
echo "[ok] 打包完成: $OUT"
echo "     桌面 App: $OUT/baihua-desktop（双击打开窗口）"
echo "     命令行:   bash $OUT/run-baihua.sh"
echo "     WebUI: http://localhost:5177  后端: http://localhost:8788"