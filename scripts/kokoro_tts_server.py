#!/usr/bin/env python3
"""Kokoro TTS Server - OpenAI-compatible /v1/audio/speech endpoint.

Uses optimum-intel to load the OpenVINO IR model directly, with misaki for G2P.
Supports Chinese (lang_code='z') and English (lang_code='a'/'b') via voice prefix.

Usage:
    python kokoro_tts_server.py [--port 8001] [--device cpu]

Environment:
    KOKORO_MODEL_PATH  Model directory (default: ~/.baihua/models/Kokoro-82M-int8-ov/1)
    KOKORO_TTS_PORT    Server port (default: 8001)
    KOKORO_TTS_DEVICE  Device: cpu/gpu (default: cpu)
"""

import os
os.environ.setdefault('HF_HUB_OFFLINE', '1')
os.environ.setdefault('TRANSFORMERS_OFFLINE', '1')

import argparse
import io
import json
import logging
import re
import sys
import time
from http.server import HTTPServer, BaseHTTPRequestHandler
from pathlib import Path
from urllib.parse import urlparse

import numpy as np
import soundfile as sf

logging.basicConfig(
    level=logging.DEBUG,
    format='%(asctime)s [%(levelname)s] %(message)s',
    stream=sys.stderr,
)
logger = logging.getLogger('kokoro-tts')

MODEL_PATH = os.environ.get(
    'KOKORO_MODEL_PATH',
    str(Path.home() / '.baihua' / 'models' / 'Kokoro-82M-int8-ov' / '1'),
)
VOICES_DIR = os.path.join(MODEL_PATH, 'voices_pt')
SAMPLE_RATE = 24000

_log_file = os.path.join(os.path.dirname(MODEL_PATH), 'kokoro_tts_server.log')
try:
    _fh = logging.FileHandler(_log_file, encoding='utf-8')
    _fh.setLevel(logging.DEBUG)
    _fh.setFormatter(logging.Formatter('%(asctime)s [%(levelname)s] %(message)s'))
    logging.getLogger().addHandler(_fh)
except Exception:
    pass
MAX_PHONEMES = 510

LANG_BY_PREFIX = {
    'a': 'a', 'b': 'b',
    'z': 'z',
    'e': 'e', 'f': 'f', 'h': 'h', 'i': 'i', 'p': 'p', 'j': 'j',
}

_model = None
_voice_cache = {}


def load_model():
    global _model
    from optimum.intel.openvino import OVModelForTextToSpeechSeq2Seq
    logger.info('Loading Kokoro model from %s', MODEL_PATH)
    logger.info('ENV: HF_HUB_OFFLINE=%s, TRANSFORMERS_OFFLINE=%s',
                os.environ.get('HF_HUB_OFFLINE'), os.environ.get('TRANSFORMERS_OFFLINE'))
    t0 = time.time()
    _model = OVModelForTextToSpeechSeq2Seq.from_pretrained(MODEL_PATH)
    logger.info('Model loaded in %.1fs', time.time() - t0)


def get_voice_path(voice: str) -> str | None:
    pt = os.path.join(VOICES_DIR, f'{voice}.pt')
    if os.path.exists(pt):
        return pt
    return None


def detect_lang_code(voice: str) -> str:
    if voice and voice[0] in LANG_BY_PREFIX:
        return LANG_BY_PREFIX[voice[0]]
    return 'a'


def split_text(text: str, lang_code: str) -> list[str]:
    text = text.strip()
    if not text:
        return []
    if lang_code == 'z':
        parts = re.split(r'(?<=[。！？；\n])', text)
    else:
        parts = re.split(r'(?<=[.!?\n])\s+', text)
    chunks = []
    cur = ''
    for p in parts:
        p = p.strip()
        if not p:
            continue
        if len(cur) + len(p) < 200:
            cur += p
        else:
            if cur:
                chunks.append(cur)
            cur = p
    if cur:
        chunks.append(cur)
    return chunks or [text]


def generate_audio(text: str, voice: str, speed: float = 1.0) -> bytes:
    lang_code = detect_lang_code(voice)
    voice_path = get_voice_path(voice)
    if not voice_path:
        raise ValueError(f'Voice not found: {voice}')

    logger.debug('generate_audio: lang=%s, voice=%s, voice_path=%s, text=%s',
                 lang_code, voice, voice_path, text[:50])
    chunks = split_text(text, lang_code)
    logger.debug('Chunks: %d', len(chunks))
    audio_segments = []
    for i, chunk in enumerate(chunks):
        logger.debug('Processing chunk %d: %s', i, chunk[:50])
        inputs = _model.preprocess_input(chunk, voice=voice_path, lang_code=lang_code)
        audio = _model.generate(**inputs)
        audio_np = audio.numpy() if hasattr(audio, 'numpy') else np.array(audio)
        logger.debug('Chunk %d audio: shape=%s, sum=%.4f', i, audio_np.shape, float(np.abs(audio_np).sum()))
        audio_segments.append(audio_np)

    if not audio_segments:
        return b''

    full_audio = np.concatenate(audio_segments) if len(audio_segments) > 1 else audio_segments[0]
    wav_buf = io.BytesIO()
    sf.write(wav_buf, full_audio, SAMPLE_RATE, format='WAV')
    return wav_buf.getvalue()


class TTSHandler(BaseHTTPRequestHandler):
    def _send_json(self, code: int, data: dict):
        body = json.dumps(data).encode()
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_bytes(self, code: int, data: bytes, ctype: str):
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = urlparse(self.path).path
        if path == '/v1/models':
            self._send_json(200, {
                'data': [{'id': 'kokoro', 'object': 'model', 'owned_by': 'baihua'}],
                'object': 'list',
            })
        elif path in ('/health', '/healthz'):
            self._send_json(200, {'status': 'ok' if _model else 'loading'})
        elif path == '/v1/voices':
            voices = []
            if os.path.isdir(VOICES_DIR):
                voices = sorted(
                    f.replace('.pt', '')
                    for f in os.listdir(VOICES_DIR)
                    if f.endswith('.pt')
                )
            self._send_json(200, {'voices': voices})
        else:
            self._send_json(404, {'error': 'Not found'})

    def do_POST(self):
        path = urlparse(self.path).path
        if path != '/v1/audio/speech':
            self._send_json(404, {'error': 'Not found'})
            return

        length = int(self.headers.get('Content-Length', 0))
        try:
            body = json.loads(self.rfile.read(length))
        except Exception:
            self._send_json(400, {'error': 'Invalid JSON'})
            return

        text = body.get('input', '').strip()
        voice = body.get('voice', '').strip()
        speed = float(body.get('speed', 1.0))

        if not text:
            self._send_json(400, {'error': 'input is required'})
            return
        if not voice:
            self._send_json(400, {'error': 'voice is required'})
            return
        if _model is None:
            self._send_json(503, {'error': 'Model not loaded yet'})
            return

        try:
            t0 = time.time()
            wav_bytes = generate_audio(text, voice, speed)
            elapsed = time.time() - t0
            logger.info('TTS: voice=%s, text=%d chars, audio=%d bytes, %.2fs',
                        voice, len(text), len(wav_bytes), elapsed)
            self._send_bytes(200, wav_bytes, 'audio/wav')
        except ValueError as e:
            self._send_json(400, {'error': str(e)})
        except Exception as e:
            logger.exception('TTS generation failed')
            self._send_json(500, {'error': str(e)})

    def log_message(self, fmt, *args):
        pass


def main():
    parser = argparse.ArgumentParser(description='Kokoro TTS Server')
    parser.add_argument('--port', type=int, default=int(os.environ.get('KOKORO_TTS_PORT', '8001')))
    parser.add_argument('--host', default='0.0.0.0')
    args = parser.parse_args()

    load_model()

    server = HTTPServer((args.host, args.port), TTSHandler)
    logger.info('Kokoro TTS server listening on %s:%d', args.host, args.port)
    logger.info('Model path: %s', MODEL_PATH)
    logger.info('Voices dir: %s', VOICES_DIR)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logger.info('Shutting down...')
        server.shutdown()


if __name__ == '__main__':
    main()