#!/bin/bash
cd /mnt/c/Users/lumin/src/baihua/dist/linux-x64
rm -f /tmp/bh-test.db
export BAIHUA_DB_PROVIDER=sqlite SQLITE_PATH=/tmp/bh-test.db
nohup ./run-baihua.sh > /tmp/bh-run.log 2>&1 &
RUN_PID=$!
sleep 12
echo "=== health ==="
curl -sf http://127.0.0.1:8788/health 2>&1 | head -3
echo ""
echo "=== webui 首页 ==="
curl -sf -o /dev/null -w "HTTP %{http_code}" http://127.0.0.1:5177/ 2>&1
echo ""
echo "=== run log 尾部 ==="
tail -8 /tmp/bh-run.log
kill $RUN_PID 2>/dev/null; pkill -f bh-server 2>/dev/null; pkill -f bh-webui 2>/dev/null
sleep 1
echo "=== 已停止 ==="