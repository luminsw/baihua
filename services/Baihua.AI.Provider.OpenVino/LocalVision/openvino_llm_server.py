#!/usr/bin/env python3
"""百花 OpenVINO OpenAI 兼容推理服务（openvino_genai LLM/VLMPipeline，常驻进程）

由 Baihua.Family 的 OpenClaw 本地 AI 配置「OpenVINO」卡片启动
（DetectAndStartOpenVinoAsync），提供 OpenAI 兼容端点，OpenClaw 可直接注册为
models.providers.openvino 使用。模型常驻内存，避免每次请求重新加载（冷加载 10-30s）。

端点:
  GET  /health              -> {"ok":true,"model":"...","device":"...","vl":true}
  GET  /v1/models           -> OpenAI 格式 {"object":"list","data":[{"id":"...","object":"model"}]}
  POST /v1/chat/completions -> OpenAI 格式（纯文本；VL 模型支持 image_url base64 图片）

命令行参数:
  --model <dir>       OpenVINO 模型目录（含 openvino_language_model.xml 的那一级）
  --device <dev>      推理设备: CPU / GPU / NPU / AUTO（默认 CPU）
  --port <port>       监听端口（默认 8000）
  --max-context-size <n>  最大上下文长度（尽力映射到 MAX_PROMPT_LEN，默认 4096）
  --max-tokens <n>    默认最大生成 token 数（默认 1024）

设计要点（踩坑经验固化，勿随意改动）:
  * VL 模型（目录含 openvino_vision_embeddings_model.xml）必须用 VLMPipeline；
    LLMPipeline 加载 VL 目录会报 "Port for tensor name input_ids was not found"
  * 纯文本对话同样走 VLMPipeline（Qwen2.5-VL 支持无图输入，已实测）
  * openvino-genai 2026.2 的 images 参数必须是 [ov.Tensor(NHWC uint8)] 扁平列表，
    不接受文件路径/PIL Image/嵌套列表（与 vision_server.py 同款实现）
  * 先 import numpy 再 import openvino（pybind 依赖顺序）
  * 兼容 .NET HttpClient 默认的 chunked 请求体（与 vision_server.py 同款实现）
"""
import base64
import io
import json
import os
import sys
import threading
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

try:
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
except Exception:
    pass


def parse_args():
    ap = argparse.ArgumentParser(description='OpenVINO OpenAI 兼容推理服务')
    ap.add_argument('--model', required=True, help='OpenVINO 模型目录')
    ap.add_argument('--device', default='CPU', help='推理设备: CPU/GPU/NPU/AUTO')
    ap.add_argument('--port', type=int, default=8000, help='监听端口')
    ap.add_argument('--max-context-size', type=int, default=4096, help='最大上下文长度')
    ap.add_argument('--max-tokens', type=int, default=1024, help='默认最大生成 token 数')
    ap.add_argument('--task', default='chat', choices=['chat', 'embedding'], help='任务类型: chat=对话/VL, embedding=向量嵌入（TextEmbeddingPipeline）')
    return ap.parse_args()


ARGS = parse_args()
MODEL_DIR = os.path.abspath(ARGS.model)
DEVICE = ARGS.device
PORT = ARGS.port
MAX_CONTEXT = ARGS.max_context_size
DEFAULT_MAX_TOKENS = ARGS.max_tokens

_pipe = None
_pipe_lock = threading.Lock()
OV_CORE = None  # openvino 模块引用（pybind 对象不能挂属性）
IS_VL = os.path.exists(os.path.join(MODEL_DIR, 'openvino_vision_embeddings_model.xml'))

# 嵌入模型：由启动方显式指定 --task embedding（bge 等 RAG 模型，用 TextEmbeddingPipeline）
IS_EMBEDDING = getattr(ARGS, 'task', '') == 'embedding'


def log(msg: str):
    print(f'[openvino-server] {msg}', flush=True)


def model_id() -> str:
    """稳定模型 id：目录名小写、点转横线（与 .NET Scan 侧一致）"""
    return os.path.basename(MODEL_DIR).replace('.', '-').lower()


def gpu_info():
    """尽力获取 GPU 设备名与可用显存（FULL_DEVICE_NAME / AVAILABLE_MEMORY）。"""
    name, vram = None, None
    try:
        import openvino as ov_core
        core = ov_core.Core()
        try:
            name = core.get_property(DEVICE, 'FULL_DEVICE_NAME')
        except Exception:  # noqa: BLE001
            pass
        try:
            vram = core.get_property(DEVICE, 'AVAILABLE_MEMORY')  # 单位字节（部分版本返回 int）
        except Exception:  # noqa: BLE001
            pass
        if isinstance(vram, str):
            try:
                vram = int(vram)
            except ValueError:
                vram = None
    except Exception:  # noqa: BLE001
        pass
    return name, vram


def get_pipe():
    """懒加载并缓存 pipeline（VL 必须 VLMPipeline，嵌入模型用 TextEmbeddingPipeline，纯文本用 LLMPipeline）"""
    global _pipe, OV_CORE
    if _pipe is not None:
        return _pipe
    with _pipe_lock:
        if _pipe is not None:
            return _pipe
        if not os.path.isdir(MODEL_DIR):
            raise FileNotFoundError(f'model directory not found: {MODEL_DIR}')
        log(f'loading model from {MODEL_DIR} (device={DEVICE}, vl={IS_VL}, embedding={IS_EMBEDDING}) ...')
        import numpy as np  # noqa: F401  确保 numpy 先导入（openvino 依赖）
        import openvino_genai as ov
        import openvino as ov_core
        OV_CORE = ov_core
        if IS_EMBEDDING:
            _pipe = ov.TextEmbeddingPipeline(MODEL_DIR, DEVICE)
        else:
            _pipe = ov.VLMPipeline(MODEL_DIR, DEVICE) if IS_VL else ov.LLMPipeline(MODEL_DIR, DEVICE)
        try:
            _pipe.set_property('MAX_PROMPT_LEN', MAX_CONTEXT)
            log(f'MAX_PROMPT_LEN set to {MAX_CONTEXT}')
        except Exception as e:  # noqa: BLE001
            log(f'MAX_PROMPT_LEN 设置失败（忽略）: {e}')
        log(f'model loaded ok (id={model_id()})')
        return _pipe


def build_config(req: dict):
    import openvino_genai as ov
    cfg = ov.GenerationConfig()
    cfg.max_new_tokens = int(req.get('max_tokens', DEFAULT_MAX_TOKENS))
    temp = req.get('temperature')
    if temp is not None:
        cfg.temperature = float(temp)
        cfg.do_sample = cfg.temperature > 0
    else:
        cfg.do_sample = False
    top_p = req.get('top_p')
    if top_p is not None:
        cfg.top_p = float(top_p)
    return cfg


def extract_text_and_image(messages):
    """从 OpenAI messages 中提取纯文本 prompt 与图片 bytes（VL 用）"""
    text_parts = []
    image_bytes = None
    for msg in messages:
        role = msg.get('role', 'user')
        content = msg.get('content')
        if isinstance(content, str):
            if role == 'user':
                text_parts.append(content)
            continue
        if isinstance(content, list):
            for part in content:
                if not isinstance(part, dict):
                    continue
                ptype = part.get('type')
                if ptype == 'text':
                    if role == 'user':
                        text_parts.append(part.get('text', ''))
                elif ptype == 'image_url' and image_bytes is None:
                    url = part.get('image_url', {}).get('url', '')
                    if url.startswith('data:'):
                        _, b64 = url.split(',', 1)
                        image_bytes = base64.b64decode(b64)
                    elif url:
                        import urllib.request
                        with urllib.request.urlopen(url, timeout=30) as resp:
                            image_bytes = resp.read()
    return '\n'.join(text_parts), image_bytes


def generate(prompt: str, image_bytes, cfg):
    pipe = get_pipe()
    if image_bytes:
        from PIL import Image
        import numpy as np
        img = Image.open(io.BytesIO(image_bytes)).convert('RGB')
        tensor = OV_CORE.Tensor(np.array(img))
        return str(pipe.generate(prompt, images=[tensor], generation_config=cfg))
    return str(pipe.generate(prompt, generation_config=cfg))


def generate_stream(prompt: str, image_bytes, cfg, on_token):
    """SSE 流式生成：on_token(subword) 逐 token 回调（openvino-genai streamer）"""
    pipe = get_pipe()

    def streamer(subword: str) -> bool:
        on_token(subword)
        return False  # False=继续生成

    if image_bytes:
        from PIL import Image
        import numpy as np
        img = Image.open(io.BytesIO(image_bytes)).convert('RGB')
        tensor = OV_CORE.Tensor(np.array(img))
        pipe.generate(prompt, images=[tensor], generation_config=cfg, streamer=streamer)
    else:
        pipe.generate(prompt, generation_config=cfg, streamer=streamer)


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
        path = urlparse(self.path).path
        if path == '/health' or path == '/status':
            _dev_name, _vram = gpu_info()
            self._send_json(200, {
                'ok': True,
                'model': model_id(),
                'device': DEVICE,
                'deviceName': _dev_name,
                'vramBytes': _vram,
                'vl': IS_VL,
                'embedding': IS_EMBEDDING,
                'modelPath': MODEL_DIR,
                'port': PORT,
                'pid': os.getpid(),
            })
        elif path == '/v1/models':
            self._send_json(200, {
                'object': 'list',
                'data': [
                    {'id': model_id(), 'object': 'model', 'owned_by': 'openvino'}
                ],
            })
        else:
            self._send_json(404, {'error': 'not found'})

    def _read_body(self):
        """兼容 Content-Length 与 chunked 两种编码（.NET HttpClient 默认 chunked）"""
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
            req = json.loads(raw.decode('utf-8')) if raw else {}
            if path == '/v1/embeddings':
                if not IS_EMBEDDING:
                    self._send_json(400, {'error': f'模型 {model_id()} 不是嵌入模型（无 TextEmbeddingPipeline 支持）'})
                    return
                input_data = req.get('input', '')
                if isinstance(input_data, str):
                    texts = [input_data]
                elif isinstance(input_data, list):
                    texts = [t if isinstance(t, str) else str(t) for t in input_data]
                else:
                    self._send_json(400, {'error': 'input 必须是字符串或字符串数组'})
                    return
                if not texts or not any(t.strip() for t in texts):
                    self._send_json(400, {'error': 'input content required'})
                    return
                import openvino_genai as ov
                pipe = get_pipe()
                log(f'embedding request: model={model_id()} inputs={len(texts)}')
                vectors = []
                for t in texts:
                    if not t.strip():
                        continue
                    vec = pipe.embed_query(t.strip())
                    vectors.append({'object': 'embedding', 'embedding': vec, 'index': len(vectors)})
                self._send_json(200, {
                    'object': 'list',
                    'model': model_id(),
                    'data': vectors,
                    'usage': {'prompt_tokens': 0, 'total_tokens': 0},
                })
            elif path == '/v1/chat/completions':
                messages = req.get('messages', [])
                prompt, image_bytes = extract_text_and_image(messages)
                if not prompt.strip():
                    self._send_json(400, {'error': 'messages content required'})
                    return
                cfg = build_config(req)
                log(f'chat request: model={model_id()} prompt_len={len(prompt)} '
                    f'image={"yes" if image_bytes else "no"} max_tokens={cfg.max_new_tokens} '
                    f'stream={bool(req.get("stream"))}')
                if req.get('stream'):
                    # SSE 流式（OpenAI 兼容：data: {...} 块 + [DONE]）
                    self.send_response(200)
                    self.send_header('Content-Type', 'text/event-stream; charset=utf-8')
                    self.send_header('Cache-Control', 'no-cache')
                    self.send_header('X-Accel-Buffering', 'no')
                    self.end_headers()

                    def emit(chunk_text: str):
                        data = json.dumps({
                            'id': 'chatcmpl-openvino',
                            'object': 'chat.completion.chunk',
                            'model': model_id(),
                            'choices': [{'index': 0, 'delta': {'content': chunk_text}, 'finish_reason': None}],
                        }, ensure_ascii=False)
                        self.wfile.write(f'data: {data}\n\n'.encode('utf-8'))
                        self.wfile.flush()

                    generate_stream(prompt, image_bytes, cfg, emit)
                    final = json.dumps({
                        'id': 'chatcmpl-openvino',
                        'object': 'chat.completion.chunk',
                        'model': model_id(),
                        'choices': [{'index': 0, 'delta': {}, 'finish_reason': 'stop'}],
                    }, ensure_ascii=False)
                    self.wfile.write(f'data: {final}\n\ndata: [DONE]\n\n'.encode('utf-8'))
                    self.wfile.flush()
                    return
                text = generate(prompt, image_bytes, cfg)
                self._send_json(200, {
                    'id': 'chatcmpl-openvino',
                    'object': 'chat.completion',
                    'model': model_id(),
                    'choices': [{
                        'index': 0,
                        'message': {'role': 'assistant', 'content': text},
                        'finish_reason': 'stop',
                    }],
                    'usage': {'prompt_tokens': 0, 'completion_tokens': 0, 'total_tokens': 0},
                })
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
    if not os.path.isdir(MODEL_DIR):
        log(f'FATAL: model directory not found: {MODEL_DIR}')
        sys.exit(1)
    log(f'starting openvino server on port {PORT}, device={DEVICE}, vl={IS_VL}')
    log(f'model: {MODEL_DIR}')
    server = ThreadingHTTPServer(('0.0.0.0', PORT), Handler)
    server.serve_forever()
