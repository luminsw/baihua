#!/usr/bin/env python3
"""百花本地视觉推理服务（Qwen2.5-VL 7B INT4 + OpenVINO，常驻进程）

由 Baihua.AI 启动并调用。模型常驻内存，避免每次识别都重新加载（冷加载 10-30s）。

端点:
  GET  /health            -> {"ok":true,"models":[{id,name,path}],"loaded":[...]}
  POST /v1/vision         -> JSON {image_base64, prompt, model} -> {"text":...}
  POST /v1/vision/reload  -> 预加载指定模型（可选）

环境变量:
  VISION_PORT      端口（默认 8801）
  VISION_MODEL_7B  Qwen2.5-VL-7B OpenVINO 目录
"""
import base64
import io
import json
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

try:
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
except Exception:
    pass

PORT = int(os.environ.get('VISION_PORT', '8801'))
HOME = os.path.expanduser('~')
MODELS = {

    '7b': {
        'name': 'Qwen2.5-VL-7B-Instruct (INT4)',
        'path': os.environ.get('VISION_MODEL_7B', os.path.join(HOME, '.openclaw', 'models', 'Qwen2.5-VL-7B-Instruct-int4-ov')),
    },
}
DEVICE = os.environ.get('VISION_DEVICE', 'GPU')

_pipes = {}
_pipes_lock = threading.Lock()
OV_CORE = None  # openvino 模块引用（pybind 对象不能挂属性）


def log(msg: str):
    print(f'[vision-server] {msg}', flush=True)


def get_pipe(model_id: str):
    """懒加载并缓存 VLMPipeline"""
    with _pipes_lock:
        if model_id in _pipes:
            return _pipes[model_id]
    info = MODELS.get(model_id)
    if info is None:
        raise ValueError(f'unknown model id: {model_id}')
    path = info['path']
    if not os.path.isdir(path):
        raise FileNotFoundError(f'model directory not found: {path}')
    log(f'loading model {model_id} from {path} (device={DEVICE}) ...')
    import numpy as np  # noqa: F401  确保 numpy 先导入（openvino 依赖）
    import openvino_genai as ov
    import openvino as ov_core
    global OV_CORE
    OV_CORE = ov_core
    pipe = ov.VLMPipeline(path, DEVICE)
    with _pipes_lock:
        _pipes[model_id] = pipe
    log(f'model {model_id} loaded')
    return pipe


def generate(model_id: str, image_bytes, prompt: str, max_tokens: int = 512):
    pipe = get_pipe(model_id)
    import openvino_genai as ov

    cfg = ov.GenerationConfig()
    cfg.max_new_tokens = max_tokens
    cfg.do_sample = False
    if image_bytes:
        from PIL import Image
        import numpy as np
        img = Image.open(io.BytesIO(image_bytes)).convert('RGB')
        tensor = OV_CORE.Tensor(np.array(img))
        result = pipe.generate(prompt, images=[tensor], generation_config=cfg)
    else:
        # 纯文本对话（Qwen2.5-VL 支持无图输入）
        result = pipe.generate(prompt, generation_config=cfg)
    return str(result)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):  # 静默访问日志
        pass

    def _send_json(self, status: int, obj: dict):
        body = json.dumps(obj, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if urlparse(self.path).path == '/health':
            self._send_json(200, {
                'ok': True,
                'models': [
                    {'id': mid, 'name': info['name'], 'path': info['path'],
                     'exists': os.path.isdir(info['path'])}
                    for mid, info in MODELS.items()
                ],
                'loaded': list(_pipes.keys()),
            })
        else:
            self._send_json(404, {'error': 'not found'})

    def _read_body(self):
        """兼容 Content-Length 与 chunked 两种编码（.NET HttpClient 默认用 chunked）"""
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            data = b''
            while True:
                line = self.rfile.readline().strip()
                if not line:
                    break
                try:
                    size = int(line, 16)
                except ValueError:
                    break
                if size == 0:
                    self.rfile.readline()  # 末尾 CRLF
                    break
                data += self.rfile.read(size)
                self.rfile.readline()  # chunk 后的 CRLF
            return data
        length = int(self.headers.get('Content-Length', '0'))
        return self.rfile.read(length)

    def do_POST(self):
        path = urlparse(self.path).path
        try:
            raw = self._read_body()
            log(f'POST {path} body_len={len(raw)}')
            req = json.loads(raw.decode('utf-8')) if raw else {}
            if path == '/v1/vision':
                model = str(req.get('model', '3b'))
                prompt = str(req.get('prompt', '请详细描述这张图片的内容。'))
                image_b64 = req.get('image_base64') or req.get('imageBase64') or ''
                log(f'vision request: model={model} image_b64_len={len(image_b64)} prompt_len={len(prompt)}')
                image_bytes = base64.b64decode(image_b64)
                text = generate(model, image_bytes, prompt)
                self._send_json(200, {'text': text, 'model': model})
            elif path == '/v1/chat':
                # 纯文本对话（无图，Qwen2.5-VL）
                model = str(req.get('model', '3b'))
                prompt = str(req.get('prompt', ''))
                if not prompt.strip():
                    self._send_json(400, {'error': 'prompt required'})
                    return
                max_tokens = int(req.get('max_tokens', 1024))
                log(f'chat request: model={model} prompt_len={len(prompt)}')
                text = generate(model, None, prompt, max_tokens=max_tokens)
                self._send_json(200, {'text': text, 'model': model})
            elif path == '/v1/vision/reload':
                model = str(req.get('model', '3b'))
                get_pipe(model)
                self._send_json(200, {'ok': True, 'model': model})
            elif path == '/v1/vision/unload':
                model = str(req.get('model', '3b'))
                with _pipes_lock:
                    _pipes.pop(model, None)
                log(f'model {model} unloaded')
                self._send_json(200, {'ok': True, 'model': model})
            else:
                self._send_json(404, {'error': 'not found'})
        except FileNotFoundError as e:
            log(f'400 FileNotFound: {e}')
            self._send_json(400, {'error': str(e)})
        except ValueError as e:
            log(f'400 ValueError: {e}')
            self._send_json(400, {'error': str(e)})
        except Exception as e:  # noqa: BLE001
            log(f'error: {e}')
            import traceback
            traceback.print_exc()
            self._send_json(500, {'error': str(e)})


if __name__ == '__main__':
    log(f'starting vision server on port {PORT}, device={DEVICE}')
    log('models: ' + json.dumps({k: v['path'] for k, v in MODELS.items()}, ensure_ascii=False))
    server = ThreadingHTTPServer(('127.0.0.1', PORT), Handler)
    server.serve_forever()
