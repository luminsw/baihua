@echo off
rem bh - baihua unified CLI shim (callable from cmd or PowerShell)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bh.ps1" %*
exit /b %errorlevel%
