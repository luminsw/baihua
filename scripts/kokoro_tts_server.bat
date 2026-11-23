@echo off
set HF_HUB_OFFLINE=1
set TRANSFORMERS_OFFLINE=1
python "%~dp0kokoro_tts_server.py" %*