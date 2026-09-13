#!/bin/bash
cd /mnt/c/Users/lumin/src/baihua/dist/linux-x64
chmod +x baihua-desktop
rm -f /tmp/bh-desktop-test.db
export SQLITE_PATH=/tmp/bh-desktop-test.db
timeout 25 ./baihua-desktop 2>&1 | head -20
pkill -f bh-server 2>/dev/null
pkill -f bh-webui 2>/dev/null
sleep 1
echo "=== 检查 health ==="
curl -sf http://127.0.0.1:8788/health 2>&1 || echo "(服务已停止，符合预期)"