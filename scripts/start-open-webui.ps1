#Requires -Version 5.1
<#
  Open WebUI Windows 启动脚本（native cell）
  用 Python venv 在 Windows 上跑 Open WebUI，连接本机 OVMS (127.0.0.1:8000)。

  用法:
    .\scripts\start-open-webui.ps1              启动（首次自动创建 venv + 安装）
    .\scripts\start-open-webui.ps1 -Stop        停止
    .\scripts\start-open-webui.ps1 -Status      查看状态
    .\scripts\start-open-webui.ps1 -Install     仅安装（创建 venv + pip install）
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$Status,
    [switch]$Install,
    # 仅供计划任务内部自调用（见文件头说明）：真正的启动实例，不再二次脱离
    [switch]$Detached
)

$ErrorActionPreference = 'Stop'
$DataHome = if ($env:BAIHUA_HOME) { $env:BAIHUA_HOME } else { Join-Path $HOME '.baihua' }
$VenvDir = Join-Path $DataHome 'open-webui-venv'
$DataDir = Join-Path $DataHome 'open-webui-data'
$PidFile = Join-Path $DataHome 'open-webui.pid'
$LogFile = Join-Path $DataHome 'open-webui.log'
$Port = 8080
$OvmsUrl = if ($env:OPENVINO_OMS_URL) { $env:OPENVINO_OMS_URL } else { 'http://127.0.0.1:8000' }

function Test-PortOpen($port) {
    try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $port); $c.Dispose(); return $true } catch { return $false }
}

function Get-PythonExe {
    $py = Get-Command python -ErrorAction SilentlyContinue
    if ($py) { return $py.Source }
    $py = Get-Command python3 -ErrorAction SilentlyContinue
    if ($py) { return $py.Source }
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python311\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python313\python.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    Write-Error 'Python 未找到，请安装 Python 3.11+ (https://python.org)'
    exit 1
}

function Install-OpenWebUI {
    if (Test-Path (Join-Path $VenvDir 'Scripts\open-webui.exe')) {
        Write-Host '[open-webui] 已安装，跳过'
        return
    }
    New-Item -ItemType Directory -Force -Path $DataHome | Out-Null
    $py = Get-PythonExe
    Write-Host "[open-webui] 创建 venv: $VenvDir"
    & $py -m venv $VenvDir
    if ($LASTEXITCODE -ne 0) { throw "venv 创建失败" }

    $pip = Join-Path $VenvDir 'Scripts\pip.exe'
    Write-Host '[open-webui] 安装 open-webui（可能需要几分钟）...'
    & $pip install --upgrade pip wheel
    & $pip install open-webui
    if ($LASTEXITCODE -ne 0) { throw "pip install open-webui 失败" }
    Write-Host '[open-webui] 安装完成'
}

function Start-OpenWebUI {
    if (-not (Test-Path (Join-Path $VenvDir 'Scripts\open-webui.exe'))) {
        Install-OpenWebUI
    }

    if (Test-PortOpen $Port) {
        Write-Warning "[open-webui] 端口 $Port 已被占用，跳过"
        return
    }

    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

    $exe = Join-Path $VenvDir 'Scripts\open-webui.exe'
    $env:WEBUI_PORT = "$Port"
    $env:DATA_DIR = $DataDir
    $env:OPENAI_API_BASE_URL = "$OvmsUrl/v1"
    $env:OPENAI_API_KEY = 'dummy'
    $env:OLLAMA_BASE_URL = 'http://localhost:1'
    $env:HF_HUB_OFFLINE = '1'
    $env:TRANSFORMERS_OFFLINE = '1'
    $env:RAG_EMBEDDING_ENGINE = 'openai'
    $env:RAG_OPENAI_EMBEDDING_MODEL = 'qwen3-embedding-0.6b'
    $env:WEBUI_AUTH = 'false'

    $p = Start-Process -FilePath $exe -ArgumentList 'serve' -WorkingDirectory $VenvDir -RedirectStandardOutput $LogFile -RedirectStandardError "$LogFile.err" -PassThru -WindowStyle Hidden
    Set-Content -Path $PidFile -Value $p.Id
    Write-Host "[open-webui] started pid=$($p.Id) port=$Port log=$LogFile"
    Write-Host '[open-webui] waiting for health ...'
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if (Test-PortOpen $Port) { Write-Host "[open-webui] ready on $Port"; return }
        Start-Sleep -Milliseconds 1000
    }
    Write-Warning "[open-webui] 端口 $Port 90s 内未就绪，查看日志: $LogFile"
}

# 脱离调用者进程树的启动：经一次性计划任务在独立上下文里再跑一次自己（-Detached）。
# 直接 Start-Process 会把服务留在调用者的 job object 里——卡片「一键更新」/bh update
# 的调用方就是 DSH，DSH 一重启整棵树被关闭，服务静默消失（2026-09-17 实测）。
# 做法与 dsh-baihua-bridge 重启 DSH 相同：任务由 Task Scheduler 持有，父进程不是调用者。
function Start-OpenWebUIDetached {
    if (Test-PortOpen $Port) {
        Write-Warning "[open-webui] 端口 $Port 已被占用，跳过"
        return
    }
    $taskName = 'baihua-open-webui-start'
    $self = $PSCommandPath
    $register = @(
        "`$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$self`" -Detached'"
        "`$t = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(2)"
        "Register-ScheduledTask -TaskName '$taskName' -Action `$a -Trigger `$t -Force | Out-Null"
        "Start-ScheduledTask -TaskName '$taskName'"
    ) -join '; '
    Write-Host '[open-webui] 经计划任务启动（脱离调用者进程树）...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $register
    if ($LASTEXITCODE -ne 0) {
        Write-Error '[open-webui] 计划任务注册/启动失败'
        return
    }
    Write-Host '[open-webui] waiting for health ...'
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if (Test-PortOpen $Port) {
            Write-Host "[open-webui] ready on $Port"
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            return
        }
        Start-Sleep -Milliseconds 1000
    }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Warning "[open-webui] 端口 $Port 90s 内未就绪，查看日志: $LogFile"
}

function Stop-OpenWebUI {
    if (Test-Path $PidFile) {
        $pid2 = [int](Get-Content $PidFile)
        $proc = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
        if ($proc) {
            Stop-Process -Id $pid2 -Force -ErrorAction SilentlyContinue
            Write-Host "[open-webui] stopped pid=$pid2"
        }
        Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
    }
    if (Test-PortOpen $Port) {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($conn) { Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue; Write-Host "[open-webui] stopped by port pid=$($conn.OwningProcess)" }
    }
}

function Show-OpenWebUIStatus {
    $portOpen = Test-PortOpen $Port
    $pidAlive = $false
    if (Test-Path $PidFile) {
        $pid2 = [int](Get-Content $PidFile)
        $pidAlive = [bool](Get-Process -Id $pid2 -ErrorAction SilentlyContinue)
    }
    $state = if ($portOpen -and $pidAlive) { 'RUNNING' }
             elseif ($portOpen) { 'PORT-OPEN (foreign)' }
             elseif ($pidAlive) { 'PROC-ALIVE' }
             else { 'stopped' }
    $installed = Test-Path (Join-Path $VenvDir 'Scripts\open-webui.exe')
    Write-Host ("{0,-12} port={1,-5} {2}  installed={3}" -f 'open-webui', $Port, $state, $installed)
}

if ($Status) { Show-OpenWebUIStatus; exit 0 }
if ($Stop) { Stop-OpenWebUI; exit 0 }
if ($Install) { Install-OpenWebUI; exit 0 }
# 默认（无开关）：先经计划任务脱离调用者进程树，再由 -Detached 实例真正启动
if (-not $Detached) { Start-OpenWebUIDetached; exit 0 }
Start-OpenWebUI