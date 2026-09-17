@echo off
rem bh-k3s - baihua k3s CLI shim (callable from cmd or PowerShell)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bh-k3s.ps1" %*
exit /b %errorlevel%
