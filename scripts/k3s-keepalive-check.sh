#!/bin/bash
# WSL 保活有效性诊断（只读）：发行版运行时长、容器启动时刻与重启计数
echo "=== 发行版 uptime（秒）==="
cut -d. -f1 /proc/uptime
echo "=== k3s 服务 ==="
systemctl is-active k3s
echo "=== 容器启动时刻 / 重启计数 ==="
k3s kubectl -n baihua get pods -o json > /tmp/_pods.json
python3 - <<'PY'
import json
d = json.load(open('/tmp/_pods.json'))
for p in sorted(d["items"], key=lambda x: x["metadata"]["name"]):
    name = p["metadata"]["name"]
    for cs in p.get("status", {}).get("containerStatuses", []) or []:
        started = cs.get("state", {}).get("running", {}).get("startedAt", "")
        print("%-32s restarts=%-4s startedAt=%s" % (name, cs["restartCount"], started))
PY
