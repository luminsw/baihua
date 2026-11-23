# ============================================================
# 安装/卸载 百花 Kokoro TTS 服务（Windows 计划任务）
# 用法（管理员 PowerShell）:
#   powershell -ExecutionPolicy Bypass -File install-kokoro-tts-service.ps1
#   powershell -ExecutionPolicy Bypass -File install-kokoro-tts-service.ps1 -Remove
# 说明:
#   - 任务名 KokoroTTS，Python TTS 服务监听 port 8001
#   - 使用 optimum-intel + misaki 做中英文 G2P，OpenVINO IR 模型推理
#   - 依赖: pip install kokoro "misaki[en]" "misaki[zh]" soundfile optimum-intel
# ============================================================
param(
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'

$TaskName = 'KokoroTTS'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServerScript = Join-Path $ScriptDir 'kokoro_tts_server.py'
$Port = 8001

# --- 卸载 ---
if ($Remove) {
    Write-Host '[0/1] 停止并删除计划任务 KokoroTTS ...'
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host '[OK] 已删除计划任务 KokoroTTS'
    } else {
        Write-Host '[OK] 计划任务 KokoroTTS 不存在，无需删除'
    }
    exit 0
}

# --- 0. 管理员检查 ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '[X] 需要管理员权限！请右键 PowerShell -> 以管理员身份运行' -ForegroundColor Red
    exit 1
}

# --- 1. 检查依赖 ---
if (-not (Test-Path $ServerScript)) {
    Write-Host "[X] TTS 脚本不存在: $ServerScript" -ForegroundColor Red
    exit 1
}

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    Write-Host '[X] Python 未安装或不在 PATH' -ForegroundColor Red
    exit 1
}
$pythonExe = $python.Source
Write-Host "[1/4] Python: $pythonExe"

# 检查 TTS 端口是否已被占用
$existing = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[!] 端口 $Port 已被占用（PID $($existing.OwningProcess)），将先停止"
    Stop-Process -Id $existing.OwningProcess -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# --- 2. 创建计划任务 ---
Write-Host '[2/4] 创建计划任务 KokoroTTS ...'

$action = New-ScheduledTaskAction `
    -Execute $pythonExe `
    -Argument "`"$ServerScript`" --port $Port" `
    -WorkingDirectory $ScriptDir

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Days 9999)

$envSet = "HF_HUB_OFFLINE=1;TRANSFORMERS_OFFLINE=1"
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "百花 Kokoro TTS 服务 (port $Port)" `
    -Force | Out-Null

Write-Host '[3/4] 启动任务 ...'
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 8

# --- 3. 验证 ---
$conn = Test-NetConnection -ComputerName localhost -Port $Port -WarningAction SilentlyContinue
if ($conn.TcpTestSucceeded) {
    Write-Host "[4/4] TTS 服务已在 port $Port 启动" -ForegroundColor Green

    # 快速验证
    try {
        $models = Invoke-RestMethod -Uri "http://localhost:$Port/v1/models" -TimeoutSec 5
        Write-Host "      模型: $($models.data.id -join ', ')"
    } catch {
        Write-Host "      [!] 模型列表查询失败（服务可能仍在加载）"
    }
} else {
    Write-Host "[4/4] [!] TTS 服务未在 port $Port 监听，请检查日志" -ForegroundColor Yellow
    Write-Host "      手动测试: python `"$ServerScript`" --port $Port"
}

Write-Host ''
Write-Host '管理命令:'
Write-Host "  启动: Start-ScheduledTask -TaskName $TaskName"
Write-Host "  停止: Stop-ScheduledTask -TaskName $TaskName"
Write-Host "  卸载: powershell -File `"$PSCommandPath`" -Remove"