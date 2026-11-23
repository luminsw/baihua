"""Minimal OpenVINO API mock for K8s connectivity testing.

Responds to:
  GET  /health         → {"status":"ok","devices":["CPU","GPU","NPU"]}
  GET  /v1/models      → {"object":"list","data":[{"id":"qwen2.5-vl-7b","object":"model"}]}
  POST /v1/chat/completions → {"id":"mock","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"mock response"}}]}
"""
import json, os
from http.server import HTTPServer, BaseHTTPRequestHandler

class MockHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/health":
            self._json({"status": "ok", "devices": ["CPU", "GPU", "NPU"]})
        elif self.path == "/v1/models":
            self._json({"object": "list", "data": [

                {"id": "qwen2.5-vl-7b", "object": "model"},
            ]})
        else:
            self._json({"error": "not found"}, 404)

    def do_POST(self):
        if self.path == "/v1/chat/completions":
            self._json({"id": "mock", "object": "chat.completion",
                        "choices": [{"index": 0, "message": {"role": "assistant", "content": "mock response from bh-openvino"}}]})
        else:
            self._json({"error": "not found"}, 404)

    def _json(self, data, code=200):
        body = json.dumps(data).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        print(f"[openvino-mock] {self.address_string()} - {fmt % args}")

if __name__ == "__main__":
    port = int(os.environ.get("OPENVINO_LLM_PORT", "8000"))
    print(f"[openvino-mock] Listening on :{port}")
    HTTPServer(("0.0.0.0", port), MockHandler).serve_forever()
