#Requires -Version 5.1
<#
  bh docker — 百花 Windows Docker 形态（Docker Desktop + Linux 容器）

  与 native 形态（bh <cmd>，dotnet 进程直接跑在 Windows）并列：
    - native：不依赖 Docker，PostgreSQL 由用户自行安装；
    - docker：postgres / server / webui 全部跑在 Docker Desktop 里，宿主机只需 Docker。
  两种形态都用 8788（后端）与 5177（WebUI），不要同时启动。

  依赖：Docker Desktop（Linux 容器；WSL2 或 Hyper-V 后端均可）已安装并处于运行状态。
  本机 OVMS（Windows :8000 进程或服务）由容器经 host.docker.internal:8000 访问；
  局域网 :80 入口仍由 bh lan on（宿主 portproxy → 127.0.0.1:8788）提供。

  用法：bh docker <command> [args]
    build [server|webui]   构建镜像（首次或源码变更后）
    start [svc...]         启动（默认全部：postgres → server → webui）
    stop                   停止（保留数据）
    restart                停止 + 启动
    status                 容器状态 + 访问地址 + OVMS 探测
    logs <svc> [n]         查看日志（server|webui|postgres，默认 server 50 行）
    ps                     docker compose ps
    pull                   拉取镜像（postgres 等）
    update                 git pull + build + start
    dashboard              打开 WebUI（尝试 cli-token 自动登录）
    open                   打开 http://127.0.0.1:5177
    env                    查看/生成 docker/.env.windows（首次自动生成，含随机 PG_PASSWORD）
    help                   本帮助
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(Position = 1)][string]$Arg1 = '',
    [Parameter(Position = 2)][string]$Arg2 = ''
)

$ErrorActionPreference = 'Stop'

# tools/bh/win/docker -> 仓库根（docker → win → bh → tools → 仓库根）
$Root = Split-Path -Parent $PSCommandPath
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$DockerDir = Join-Path $Root 'docker'
$Compose = Join-Path $DockerDir 'docker-compose.windows.yml'
$EnvFile = Join-Path $DockerDir '.env.windows'

$Services = @('postgres', 'server', 'webui')

function Show-Help {
    Write-Host 'bh docker — 百花 Windows Docker 形态（Docker Desktop，Linux 容器）'
    Write-Host ''
    Write-Host '用法: bh docker <command> [args]'
    Write-Host '  build [server|webui]   构建镜像'
    Write-Host '  start [svc...]         启动（默认全部）'
    Write-Host '  stop / restart         停止 / 重启'
    Write-Host '  status                 容器状态 + 地址 + OVMS'
    Write-Host '  logs <svc> [n]         日志（server|webui|postgres）'
    Write-Host '  ps / pull / update     容器列表 / 拉镜像 / git pull+build+start'
    Write-Host '  dashboard / open       打开 WebUI'
    Write-Host '  env                    查看或生成 docker/.env.windows'
    Write-Host ''
    Write-Host '两种 Windows 形态互斥（都占 8788/5177）：native = bh <cmd>，docker = bh docker <cmd>'
    Write-Host '首次使用会生成 docker/.env.windows（数据目录、知识库目录、随机 PG_PASSWORD）'
}

function Ensure-Docker {
    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if (-not $docker) {
        Write-Host '[deps] 未找到 docker 命令。Windows Docker 形态需要 Docker Desktop：' -ForegroundColor Yellow
        Write-Host '        winget install --id Docker.DockerDesktop'
        Write-Host '        或 https://www.docker.com/products/docker-desktop/'
        Write-Host '        安装后启动 Docker Desktop（托盘变绿），再重试本条命令。'
        exit 1
    }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $null = & docker info --format '{{.ServerVersion}}' 2>&1
    $ok = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prev
    if (-not $ok) {
        Write-Host '[deps] Docker 引擎未就绪：请启动 Docker Desktop 并等待引擎启动完成（docker info 失败）。' -ForegroundColor Yellow
        Write-Host '        若刚安装：确认已切换为 Linux 容器（Windows 下百花镜像都是 Linux 的）。'
        exit 1
    }
}

# 环境文件：首次运行生成；已有值一律保留（只补缺）
function Get-EnvMap {
    $map = @{}
    if (Test-Path $EnvFile) {
        foreach ($line in Get-Content $EnvFile) {
            if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
                $map[$matches[1]] = $matches[2].Trim()
            }
        }
    }
    $homeUnix = ($HOME -replace '\\', '/')
    $defaults = [ordered]@{
        BAIHUA_DATA_DIR = $homeUnix + '/.baihua'
        BAIHUA_LOG_DIR = $homeUnix + '/.baihua/docker-logs'
        VAULTS_DIR = $homeUnix + '/Vaults'
        PG_USER = 'baihua'
        PG_DATABASE = 'baihua'
        PG_DOCKER_PORT = '5433'
        BAIHUA_SERVER_PORT = '8788'
        BAIHUA_WEBUI_PORT = '5177'
        OPENOBSERVE_ENABLED = 'false'
    }
    $changed = $false
    foreach ($k in $defaults.Keys) {
        if (-not $map.ContainsKey($k) -or [string]::IsNullOrWhiteSpace($map[$k])) {
            $map[$k] = $defaults[$k]
            $changed = $true
        }
    }
    if (-not $map.ContainsKey('PG_PASSWORD') -or [string]::IsNullOrWhiteSpace($map['PG_PASSWORD'])) {
        $chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
        $pw = -join (1..24 | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
        $map['PG_PASSWORD'] = $pw
        $changed = $true
        Write-Host '[env] 已生成 PG_PASSWORD（写入 docker/.env.windows；容器首次初始化后不要再改，否则连不上旧库）'
    }
    if ($changed) {
        $lines = @('# 由 bh docker 生成/维护（Windows Docker 形态）。改端口/路径后 bh docker restart 生效。')
        foreach ($k in ($map.Keys | Sort-Object)) {
            if ([string]::IsNullOrWhiteSpace($map[$k])) { continue }
            $lines += ($k + '=' + $map[$k])
        }
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [IO.File]::WriteAllLines($EnvFile, $lines, $utf8)
    }
    return $map
}

function Invoke-Compose {
    param([string[]]$ComposeArgs)
    Ensure-Docker
    $null = Get-EnvMap
    Push-Location $DockerDir
    try {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & docker compose --env-file $EnvFile -f $Compose @ComposeArgs 2>&1
        $code = $LASTEXITCODE
        $ErrorActionPreference = $prev
        if ($code -ne 0) { throw "docker compose 失败（exit $code）：$($ComposeArgs -join ' ')" }
    } finally {
        Pop-Location
    }
}

function Test-PortOpen($port) {
    $c = New-Object Net.Sockets.TcpClient
    try { $c.Connect('127.0.0.1', [int]$port); return $true } catch { return $false } finally { $c.Dispose() }
}

function Wait-Port($port, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-PortOpen $port) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Resolve-SvcArgs {
    param([string]$First)
    if ([string]::IsNullOrWhiteSpace($First)) { return @() }
    return @($First)
}

function Invoke-Build {
    param([string]$Only)
    $targets = Resolve-SvcArgs $Only
    $composeArgs = @('build')
    if ($targets.Count -gt 0) { $composeArgs += $targets }
    Invoke-Compose $composeArgs
}

function Start-Stack {
    param([string]$Only)
    $envMap = Get-EnvMap
    $targets = Resolve-SvcArgs $Only
    $composeArgs = @('up', '-d', '--remove-orphans')
    if ($targets.Count -gt 0) { $composeArgs += $targets }
    Invoke-Compose $composeArgs
    $serverPort = $envMap['BAIHUA_SERVER_PORT']
    $webuiPort = $envMap['BAIHUA_WEBUI_PORT']
    Write-Host '[start] 等待服务就绪 ...'
    if (Wait-Port $serverPort 180) { Write-Host "  server ready: http://127.0.0.1:$serverPort" }
    else { Write-Warning "server 180s 内未就绪（bh docker logs server）" }
    if (Wait-Port $webuiPort 120) { Write-Host "  webui  ready: http://127.0.0.1:$webuiPort" }
    else { Write-Warning "webui 120s 内未就绪（bh docker logs webui）" }
    Write-Host '  局域网 :80 入口：bh lan on（宿主 portproxy -> 127.0.0.1:8788）'
}

function Stop-Stack {
    Invoke-Compose @('down')
    Write-Host '[stop] 已停止（数据卷 baihua-win-pgdata 保留）'
}

function Show-Status {
    $envMap = Get-EnvMap
    Ensure-Docker
    Invoke-Compose @('ps')
    Write-Host ''
    Write-Host ("后端:   http://127.0.0.1:{0}" -f $envMap['BAIHUA_SERVER_PORT'])
    Write-Host ("WebUI:  http://127.0.0.1:{0}" -f $envMap['BAIHUA_WEBUI_PORT'])
    $ov = if (Test-PortOpen 8000) { 'RUNNING (port 8000)' } else { 'stopped' }
    Write-Host ("OVMS:   {0}（容器内经 host.docker.internal:8000 访问）" -f $ov)
}

function Show-Logs {
    param([string]$Svc, [int]$Count)
    if ([string]::IsNullOrWhiteSpace($Svc)) { $Svc = 'server' }
    if ($Services -notcontains $Svc) {
        Write-Host "未知服务：$Svc（可选：$($Services -join '|')）"
        return
    }
    Invoke-Compose @('logs', '--tail', "$Count", $Svc)
}

function Open-WebUI {
    $envMap = Get-EnvMap
    $port = $envMap['BAIHUA_WEBUI_PORT']
    try {
        $resp = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/api/auth/cli-token" -f $port) -Method POST -UseBasicParsing -TimeoutSec 10
        $token = ($resp.Content | ConvertFrom-Json).token
        Start-Process ("http://127.0.0.1:{0}/?cli-token={1}" -f $port, $token)
        Write-Host '[dashboard] opened with cli-token'
    } catch {
        Write-Host ("[dashboard] cli-token 获取失败（{0}），打开普通地址" -f $_.Exception.Message)
        Start-Process ("http://127.0.0.1:{0}" -f $port)
    }
}

function Update-Stack {
    Write-Host '[update] git pull ...'
    & git -C $Root pull origin main
    if ($LASTEXITCODE -ne 0) { throw 'git pull 失败，请检查网络/代理' }
    Invoke-Build ''
    Start-Stack ''
}

switch ($Command.ToLower()) {
    'help'      { Show-Help }
    'build'     { Invoke-Build $Arg1 }
    'start'     { Start-Stack $Arg1 }
    'up'        { Start-Stack $Arg1 }
    'stop'      { Stop-Stack }
    'down'      { Stop-Stack }
    'restart'   { Stop-Stack; Start-Stack '' }
    'status'    { Show-Status }
    'ps'        { Invoke-Compose @('ps') }
    'logs'      { $count = 50; if ($Arg2) { $count = [int]$Arg2 }; Show-Logs $Arg1 $count }
    'pull'      { Invoke-Compose @('pull') }
    'update'    { Update-Stack }
    'dashboard' { Open-WebUI }
    'open'      { $m = Get-EnvMap; Start-Process ("http://127.0.0.1:{0}" -f $m['BAIHUA_WEBUI_PORT']) }
    'env'       {
        $m = Get-EnvMap
        Write-Host "[env] $EnvFile"
        foreach ($k in ($m.Keys | Sort-Object)) {
            $v = $m[$k]
            if ($k -eq 'PG_PASSWORD') { $v = $v.Substring(0, [Math]::Min(4, $v.Length)) + '****' }
            Write-Host ("  {0}={1}" -f $k, $v)
        }
    }
    default {
        Write-Warning "[bh docker] 未知命令：$Command"
        Show-Help
        exit 1
    }
}
