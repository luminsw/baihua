#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""OpenVINO LLM server 托管服务
在宿主机(Windows/Linux)上管理 openvino_llm_server.py 子进程（多实例：对话/代码等），
对外提供 HTTP API 供百花 WebUI（k8s/compose 部署）调用。

API:
  GET  /health            -> {"ok": true}
  GET  /status            -> 所有已配置实例的状态
  POST /start             -> {"port": 8000, "model": "...", "device": "GPU", "name": "..."}
  POST /stop              -> {"port": 8000}
  GET  /instances         -> 配置清单
"""
import argparse
import json
import os
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# 默认实例配置（可被 --config json 覆盖；模型根目录跟随 BAIHUA_HOME）
_MODEL_ROOT = os.environ.get("BAIHUA_HOME") or os.path.join(os.path.expanduser("~"), ".baihua")
DEFAULT_INSTANCES = [
    {"port": 8000, "name": "对话模型 (Qwen2.5-7B-Instruct)",
     "model": os.path.join(_MODEL_ROOT, "models", "Qwen2.5-7B-Instruct-int4-ov"), "device": "GPU"},
    {"port": 8001, "name": "代码模型 (Qwen2.5-Coder-7B-Instruct)",
     "model": os.path.join(_MODEL_ROOT, "models", "Qwen2.5-Coder-7B-Instruct-int4-ov"), "device": "GPU"},
    {"port": 8002, "name": "视觉模型 (Qwen2.5-VL-7B-Instruct)",
     "model": os.path.join(_MODEL_ROOT, "models", "Qwen2.5-VL-7B-Instruct-int4-ov"), "device": "GPU"},
    {"port": 8003, "name": "嵌入模型 (bge-small-zh-v1.5)",
     "model": os.path.join(_MODEL_ROOT, "models", "bge-small-zh-v1.5"), "device": "CPU", "task": "embedding"},
]

SCRIPT = None  # openvino_llm_server.py 路径（自动探测）
PROCS = {}     # port -> {"proc": Popen, "started": float, "model": str, "port": int}
LOCK = threading.Lock()


def find_script():
    """定位 openvino_llm_server.py（首选同级目录，跨工程路径作发布布局兜底）

    当前布局（本脚本与 openvino_llm_server.py 同工程同目录）：
      services/Baihua.AI.Provider.OpenVino/LocalVision/openvino_host.py
      services/Baihua.AI.Provider.OpenVino/LocalVision/openvino_llm_server.py
    下面的跨工程路径仅作旧发布布局/其他打包方式的兜底。
    """
    here = os.path.dirname(os.path.abspath(__file__))
    candidates = [
        os.path.join(here, "openvino_llm_server.py"),
        # 兄弟工程 OpenVino Provider 的 LocalVision（旧源码布局兜底）
        os.path.normpath(os.path.join(here, "..", "Baihua.AI.Provider.OpenVino", "LocalVision", "openvino_llm_server.py")),
        # 发布布局：本脚本可能被拷贝到 openvino host 输出目录的 LocalVision 下
        os.path.normpath(os.path.join(here, "..", "..", "Baihua.AI.Provider.OpenVino", "LocalVision", "openvino_llm_server.py")),
        # 兜底：白花仓库根下 services 路径
        os.path.normpath(os.path.join(here, "..", "..", "..", "services", "Baihua.AI.Provider.OpenVino", "LocalVision", "openvino_llm_server.py")),
    ]
    for cand in candidates:
        if os.path.exists(cand):
            return cand
    return None


def find_python():
    if os.name == "nt":
        return sys.executable
    return sys.executable


def is_alive(port):
    """端口是否健康（openvino server /health）"""
    import urllib.request
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as r:
            return r.status == 200
    except Exception:
        return False


def instance_status(inst):
    port = inst["port"]
    with LOCK:
        p = PROCS.get(port)
    running = p is not None and p["proc"].poll() is None
    healthy = is_alive(port)
    return {
        "port": port,
        "name": inst.get("name", ""),
        "model": inst.get("model", ""),
        "device": inst.get("device", ""),
        "managed": running,
        "running": running or healthy,
        "healthy": healthy,
        "pid": p["proc"].pid if (p and p["proc"].poll() is None) else None,
        "startedAt": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(p["started"])) if p else None,
        "logFile": os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs", f"openvino_llm_{port}.log"),
    }


def start_instance(inst):
    port = inst["port"]
    with LOCK:
        p = PROCS.get(port)
        if p and p["proc"].poll() is None:
            return {"ok": True, "message": f"port {port} 已在运行 (pid={p['proc'].pid})"}
        if is_alive(port):
            PROCS[port] = None  # 外部进程占着端口，标记为未托管但运行
            return {"ok": True, "message": f"port {port} 已有外部服务在运行，无需启动"}

    script = SCRIPT
    if not script or not os.path.exists(script):
        return {"ok": False, "error": f"openvino_llm_server.py 不存在: {script}"}
    model = inst.get("model", "")
    if not os.path.isdir(model):
        return {"ok": False, "error": f"模型目录不存在: {model}"}

    log_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs")
    os.makedirs(log_dir, exist_ok=True)
    log_file = os.path.join(log_dir, f"openvino_llm_{port}.log")

    cmd = [find_python(), script, "--model", model, "--device", inst.get("device", "GPU"), "--port", str(port)]
    task = inst.get("task")
    if task:
        cmd += ["--task", str(task)]
    with open(log_file, "ab") as lf:
        proc = subprocess.Popen(cmd, stdout=lf, stderr=lf, cwd=os.path.dirname(script),
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    with LOCK:
        PROCS[port] = {"proc": proc, "started": time.time(), "model": model, "port": port}
    return {"ok": True, "message": f"已启动 port {port} (pid={proc.pid})", "pid": proc.pid}


def stop_instance(port):
    with LOCK:
        p = PROCS.get(port)
    if p and p["proc"].poll() is None:
        p["proc"].terminate()
        try:
            p["proc"].wait(timeout=8)
        except subprocess.TimeoutExpired:
            p["proc"].kill()
        with LOCK:
            PROCS[port] = None
        return {"ok": True, "message": f"已停止 port {port}"}
    # 端口被外部进程占用：尝试通过 server 自身关闭（无 API）→ 仅提示
    return {"ok": False, "error": f"port {port} 没有受管进程（可能由外部启动，无法停止）"}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        sys.stderr.write("[openvino-host] %s\n" % (fmt % args))

    def do_GET(self):
        if self.path == "/health":
            return self._send(200, {"ok": True, "service": "openvino-host"})
        if self.path == "/status":
            with LOCK:
                insts = list(INSTANCES)
            return self._send(200, {"instances": [instance_status(i) for i in insts]})
        if self.path == "/instances":
            with LOCK:
                insts = list(INSTANCES)
            return self._send(200, {"instances": insts})
        self._send(404, {"error": "not found"})

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(length).decode("utf-8")) if length else {}
        if self.path == "/start":
            # 按 port 或完整实例启动；port 匹配配置则用配置的 model/device
            port = int(body.get("port", 0))
            inst = next((i for i in INSTANCES if i["port"] == port), None)
            if inst is None:
                inst = {"port": port, "name": body.get("name", ""), "model": body.get("model", ""),
                        "device": body.get("device", "GPU")}
                if not inst["model"]:
                    return self._send(400, {"error": f"port {port} 不在配置中且未提供 model"})
            r = start_instance(inst)
            return self._send(200 if r.get("ok") else 409, r)
        if self.path == "/stop":
            port = int(body.get("port", 0))
            r = stop_instance(port)
            return self._send(200 if r.get("ok") else 409, r)
        self._send(404, {"error": "not found"})


def main():
    ap = argparse.ArgumentParser(description="OpenVINO LLM server 托管服务")
    ap.add_argument("--port", type=int, default=8866, help="托管服务监听端口")
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--config", default="", help="实例配置 json 文件（覆盖默认）")
    args = ap.parse_args()

    global INSTANCES, SCRIPT
    if args.config and os.path.exists(args.config):
        with open(args.config, encoding="utf-8") as f:
            INSTANCES = json.load(f).get("instances", [])
    else:
        INSTANCES = DEFAULT_INSTANCES
    SCRIPT = find_script()

    # 启动时认领已在运行的端口（如手动起的 8000/8001 → 标记 running 但未托管）
    for inst in INSTANCES:
        if is_alive(inst["port"]):
            with LOCK:
                PROCS[inst["port"]] = None
            print(f"[openvino-host] port {inst['port']} 已在运行（外部进程，只读认领）")

    print(f"[openvino-host] listening on {args.bind}:{args.port}, script={SCRIPT}, instances={[i['port'] for i in INSTANCES]}")
    ThreadingHTTPServer((args.bind, args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
