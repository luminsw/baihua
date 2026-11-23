#!/usr/bin/env python3
"""检查 baihua namespace 各 deployment 的探针/资源/镜像（只读）。"""
import json
import subprocess

NS = "baihua"
SVCS = ["bh-server", "bh-webui", "bh-openvino", "bh-postgres", "bh-open-webui"]


def kget(*args):
    r = subprocess.run(["k3s", "kubectl", *args], capture_output=True, text=True)
    return r.stdout, r.stderr, r.returncode


for svc in SVCS:
    out, err, rc = kget("-n", NS, "get", "deploy", svc, "-o", "json")
    if rc != 0:
        print(f"--- {svc}: 未部署 ({err.strip()[:80]})")
        continue
    d = json.loads(out)
    spec = d["spec"]["template"]["spec"]
    print(f"--- {svc}  replicas={d['spec'].get('replicas')} ready={d['status'].get('readyReplicas')} available={d['status'].get('availableReplicas')}")
    for c in spec.get("containers", []):
        print(f"    container={c['name']} image={c.get('image')}")
        for pk in ("startupProbe", "readinessProbe", "livenessProbe"):
            p = c.get(pk)
            if p:
                probe = json.dumps({k: v for k, v in p.items() if k != "httpGet"} | ({"httpGet": p.get("httpGet")} if p.get("httpGet") else {}), ensure_ascii=False)
                print(f"      {pk}: {probe}")
        res = c.get("resources")
        if res:
            print(f"      resources: {json.dumps(res, ensure_ascii=False)}")
        if c.get("env"):
            names = [e.get("name") for e in c["env"]]
            print(f"      env: {', '.join(names)}")
    print()
