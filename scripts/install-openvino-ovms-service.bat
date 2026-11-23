@echo off
REM 百花 OpenVINO Model Server (OVMS) Windows 服务安装入口
REM 实际逻辑在 install-openvino-ovms-service.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-openvino-ovms-service.ps1" %*
