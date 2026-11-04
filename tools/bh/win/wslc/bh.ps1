#Requires -Version 5.1
<#
  bh wslc — 百花 Windows 容器形态（WSL 3 自带的 wslc.exe，无需 Docker Desktop）

  与 native 形态（bh <cmd>，Windows 上直接跑 dotnet 进程）并列：
    - native：不依赖容器，PostgreSQL 由用户自行安装；
    - wslc：postgres / server / webui 跑在 WSL 容器里（wslc.exe，WSL >= 2.9.3）。
  两种形态都占 8788（后端）/ 5177（WebUI），不要同时启动。

  前置：
    * WSL 3 自带 wslc.exe（wsl --version 查看；wsl --update 升级）
    * 首次会拉取镜像：.NET 基础镜像走 mcr.microsoft.com（可直连），
      postgres 走 Docker Hub —— 国内直连 hub 会超时，默认经 docker.m.daocloud.io 镜像拉取
      （docker/.env.wslc 里的 BAIHUA_WSL_MIRROR，留空则用官方 registry）
    * 本机 OVMS（Windows :8000 服务）从容器里经宿主 IP 访问：启动时自动探测可用地址

  用法：bh wslc <command> [args]
    build [server|webui]   构建镜像（首次/源码变更后；首次会拉 .NET SDK 镜像，较慢）
    pull                   拉取 postgres 镜像（镜像源见 docker/.env.wslc）
    start [svc...]         启动（默认全部：postgres → server → webui）
    stop                   停止并删除容器（命名卷保留数据）
    restart                停止 + 启动
    status                 容器列表 + 访问地址 + OVMS 探测
    logs <svc> [n]         日志（server|webui|postgres，默认 server 50 行）
    exec <svc> <cmd...>    在运行中的容器里执行命令（如 exec postgres psql -U baihua）
    ps                     等价于 wslc container list --all
    update                 git pull + build + start
    dashboard / open       打开 WebUI
    env                    查看/生成 docker/.env.wslc
    help                   本帮助
#>
[CmdletBinding()]
param(
    # 与 tools/bh/bh.ps1 同一套路：ValueFromRemainingArguments 会把所有裸参数收进来，
    # 再手动切分（exec 需要把后续参数原样透传给容器）
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$All = @()
)

$ErrorActionPreference = 'Stop'

$Command = if ($All.Count -gt 0) { $All[0] } else { 'help' }
$Arg1 = if ($All.Count -gt 1) { $All[1] } else { '' }
$Arg2 = if ($All.Count -gt 2) { $All[2] } else { '' }

# tools/bh/win/wslc -> 仓库根（wslc → win → bh → tools → 仓库根）
$Root = Split-Path -Parent $PSCommandPath
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root

$DockerDir = Join-Path $Root 'docker'
$EnvFile = Join-Path $DockerDir '.env.wslc'
$Network = 'bh-net'
$Containers = [ordered]@{ postgres = 'bh-postgres'; server = 'bh-server'; webui = 'bh-webui' }
$Volumes = @('bh-pgdata', 'bh-data', 'bh-logs')
$PgImageName = 'postgres:18-alpine'

function Show-Help {
    Write-Host 'bh wslc — 百花 Windows 容器形态（WSL 自带 wslc.exe，无需 Docker Desktop）'
    Write-Host ''
    Write-Host '用法: bh wslc <command> [args]'
    Write-Host '  build [server|webui]   构建镜像（首次较慢）'
    Write-Host '  pull                   拉取 postgres 镜像'
    Write-Host '  start [svc...] / stop / restart'
    Write-Host '  status / logs <svc> [n] / exec <svc> <cmd...> / ps'
    Write-Host '  update / dashboard / open / env'
    Write-Host ''
    Write-Host '与 native 互斥（同占 8788/5177）：native = bh <cmd>，容器 = bh wslc <cmd>'
    Write-Host '前置：WSL >= 2.9.3（wsl --update）；首次会生成 docker/.env.wslc（随机 PG_PASSWORD）'
}

function Invoke-Wslc {
    param([string[]]$WslcArgs, [switch]$AllowFail)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & wslc @WslcArgs 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -ne 0 -and -not $AllowFail) {
        throw ('wslc ' + ($WslcArgs -join ' ') + ' 失败（exit ' + $code + '）：' + [Environment]::NewLine + $out.Trim())
    }
    return [pscustomobject]@{ ExitCode = $code; Output = $out }
}

function Ensure-Wslc {
    if (-not (Get-Command wslc -ErrorAction SilentlyContinue)) {
        Write-Host '[deps] 未找到 wslc.exe。容器形态需要 WSL 3（wslc 随 WSL 提供）：' -ForegroundColor Yellow
        Write-Host '        wsl --update'
        Write-Host '        wsl --version    # 需要 WSL 2.9.3+（wslc.exe 在 C:\Program Files\WSL\wslc.exe）'
        exit 1
    }
    $v = & wslc version 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host '[deps] wslc 无法启动，请先巡检 WSL：wsl --status / wsl --update' -ForegroundColor Yellow
        Write-Host $v
        exit 1
    }
}

# ---------- 环境文件（首次生成；已有值保留） ----------
function Get-EnvMap {
    $map = @{}
    if (Test-Path $EnvFile) {
        foreach ($line in Get-Content $EnvFile) {
            if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') { $map[$matches[1]] = $matches[2].Trim() }
        }
    }
    $homeUnix = ($HOME -replace '\\', '/')
    $defaults = [ordered]@{
        BAIHUA_WSL_MIRROR = 'docker.m.daocloud.io'
        PG_USER = 'baihua'
        PG_DATABASE = 'baihua'
        PG_HOST_PORT = '5433'
        BAIHUA_SERVER_PORT = '8788'
        BAIHUA_WEBUI_PORT = '5177'
        VAULTS_DIR = $homeUnix + '/Vaults'
        OVMS_HOST_IP = ''
    }
    $changed = $false
    foreach ($k in $defaults.Keys) {
        if (-not $map.ContainsKey($k)) { $map[$k] = $defaults[$k]; $changed = $true }
    }
    if (-not $map.ContainsKey('PG_PASSWORD') -or [string]::IsNullOrWhiteSpace($map['PG_PASSWORD'])) {
        $chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
        $map['PG_PASSWORD'] = -join (1..24 | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
        $changed = $true
        Write-Host '[env] 已生成 PG_PASSWORD（写入 docker/.env.wslc；容器首次初始化后不要再改）'
    }
    if ($changed) {
        $lines = @('# 由 bh wslc 生成/维护（Windows WSL 容器形态）。改端口/路径后 bh wslc restart 生效。')
        foreach ($k in ($map.Keys | Sort-Object)) {
            if ([string]::IsNullOrWhiteSpace($map[$k])) { continue }
            $lines += ($k + '=' + $map[$k])
        }
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [IO.File]::WriteAllLines($EnvFile, $lines, $utf8)
    }
    return $map
}

# ---------- 镜像 ----------
function Get-PgImage {
    param($EnvMap)
    $mirror = $EnvMap['BAIHUA_WSL_MIRROR']
    if (-not [string]::IsNullOrWhiteSpace($mirror)) {
        return ($mirror.TrimEnd('/') + '/library/' + $PgImageName)
    }
    return $PgImageName
}

function Test-Image([string]$Name) {
    return ((Invoke-Wslc -WslcArgs @('image', 'inspect', $Name) -AllowFail).ExitCode -eq 0)
}

function Ensure-Image([string]$Name) {
    if (Test-Image $Name) { return }
    Write-Host ('[pull] ' + $Name + ' ...')
    & wslc pull $Name
    if ($LASTEXITCODE -ne 0) { throw ('拉取镜像失败：' + $Name) }
}

function Ensure-Network {
    if ((Invoke-Wslc -WslcArgs @('network', 'inspect', $Network) -AllowFail).ExitCode -eq 0) { return }
    Write-Host ('[net] 创建网络 ' + $Network)
    Invoke-Wslc -WslcArgs @('network', 'create', $Network) | Out-Null
}

function Ensure-Volumes {
    foreach ($v in $Volumes) {
        if ((Invoke-Wslc -WslcArgs @('volume', 'inspect', $v) -AllowFail).ExitCode -eq 0) { continue }
        Write-Host ('[vol] 创建卷 ' + $v)
        Invoke-Wslc -WslcArgs @('volume', 'create', $v) | Out-Null
    }
}

# ---------- 容器 ----------
function Test-Container([string]$Name) {
    return ((Invoke-Wslc -WslcArgs @('container', 'inspect', $Name) -AllowFail).ExitCode -eq 0)
}

function Remove-Container([string]$Name) {
    if (-not (Test-Container $Name)) { return }
    Invoke-Wslc -WslcArgs @('container', 'stop', $Name) -AllowFail | Out-Null
    Invoke-Wslc -WslcArgs @('container', 'remove', '-f', $Name) -AllowFail | Out-Null
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

function Wait-PgReady([string]$Name, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $r = Invoke-Wslc -WslcArgs @('exec', $Name, 'pg_isready', '-U', $script:EnvMap['PG_USER'], '-d', $script:EnvMap['PG_DATABASE']) -AllowFail
        if ($r.ExitCode -eq 0) { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}

# 容器里访问 Windows 宿主（OVMS）的地址：默认网关 + 各宿主 IP 逐个探测
function Resolve-OvmsUrl {
    param($EnvMap)
    if (-not [string]::IsNullOrWhiteSpace($EnvMap['OVMS_HOST_IP'])) {
        return ('http://' + $EnvMap['OVMS_HOST_IP'] + ':8000')
    }
    if (-not (Test-Image 'bh-server:latest')) { return '' }
    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } |
        Sort-Object @{ Expression = { if ($_.InterfaceAlias -match 'vEthernet') { 2 } else { 1 } } } |
        Select-Object -ExpandProperty IPAddress -Unique
    foreach ($ip in $ips) {
        $url = 'http://' + $ip + ':8000'
        $r = Invoke-Wslc -WslcArgs @('run', '--rm', '--entrypoint', 'curl', 'bh-server:latest', '-fsS', '--max-time', '3', ($url + '/v1/models')) -AllowFail
        if ($r.ExitCode -eq 0) {
            Write-Host ('[ovms] 容器可达宿主 OVMS：' + $url)
            return $url
        }
    }
    Write-Warning '容器访问不到宿主 OVMS（:8000）：本地 AI 相关功能会不可用（可用 docker/.env.wslc 的 OVMS_HOST_IP 指定宿主地址）'
    return ''
}

function Build-Images {
    param([string]$Only)
    $targets = @()
    if ([string]::IsNullOrWhiteSpace($Only) -or $Only -eq 'server') { $targets += 'server' }
    if ([string]::IsNullOrWhiteSpace($Only) -or $Only -eq 'webui') { $targets += 'webui' }
    Push-Location $Root
    try {
        foreach ($t in $targets) {
            $df = 'docker/Dockerfile.' + $t
            Write-Host ('[build] ' + $t + ' ...')
            & wslc build -t ('bh-' + $t + ':latest') -f $df .
            if ($LASTEXITCODE -ne 0) { throw ('构建失败：' + $t) }
        }
    } finally {
        Pop-Location
    }
    Write-Host '[build] done'
}

# ---------- 生命周期 ----------
function Start-Stack {
    param([string]$Only)
    Ensure-Wslc
    $script:EnvMap = Get-EnvMap
    $envMap = $script:EnvMap
    Ensure-Network
    Ensure-Volumes

    if ([string]::IsNullOrWhiteSpace($Only) -or $Only -eq 'postgres') {
        $pgImg = Get-PgImage $envMap
        Ensure-Image $pgImg
        if (-not (Test-Image 'bh-server:latest') -or -not (Test-Image 'bh-webui:latest')) {
            Write-Host '[build] 镜像缺失，先构建 server/webui ...'
            Build-Images ''
        }
        Remove-Container $Containers['postgres']
        Write-Host '[start] postgres ...'
        $args = @('run', '-d', '--name', $Containers['postgres'], '--network', $Network,
            '-e', ('POSTGRES_DB=' + $envMap['PG_DATABASE']),
            '-e', ('POSTGRES_USER=' + $envMap['PG_USER']),
            '-e', ('POSTGRES_PASSWORD=' + $envMap['PG_PASSWORD']),
            '-p', ('127.0.0.1:' + $envMap['PG_HOST_PORT'] + ':5432'),
            '-v', 'bh-pgdata:/var/lib/postgresql',
            $pgImg)
        Invoke-Wslc -WslcArgs $args | Out-Null
        Write-Host '[start] 等待 PostgreSQL 就绪 ...'
        if (-not (Wait-PgReady $Containers['postgres'] 90)) { throw 'PostgreSQL 90s 内未就绪（bh wslc logs postgres）' }
    }

    $ovms = Resolve-OvmsUrl $envMap
    $ovmsEnv = @()
    if ($ovms) {
        $ovmsEnv = @('-e', ('OpenVinoOms__BaseUrl=' + $ovms), '-e', ('OPENVINO_LLM_URL=' + $ovms))
    }

    if ([string]::IsNullOrWhiteSpace($Only) -or $Only -eq 'server') {
        if (-not (Test-Image 'bh-server:latest')) { Build-Images 'server' }
        Remove-Container $Containers['server']
        Write-Host '[start] server ...'
        $args = @('run', '-d', '--name', $Containers['server'], '--network', $Network,
            '-p', ('127.0.0.1:' + $envMap['BAIHUA_SERVER_PORT'] + ':8788'),
            '-e', 'BAIHUA_HOME=/app/data',
            '-e', 'BAIHUA_SKIP_MUTEX=true',
            '-e', 'ASPNETCORE_URLS=http://0.0.0.0:8788',
            '-e', ('PG_HOST=' + $Containers['postgres']),
            '-e', ('PG_USER=' + $envMap['PG_USER']),
            '-e', ('PG_PASSWORD=' + $envMap['PG_PASSWORD']),
            '-e', ('PG_DATABASE=' + $envMap['PG_DATABASE']),
            '-e', 'BAIHUA_VAULT_ROOT=/vaults',
            '-e', 'BAIHUA_ADMIN_ALLOWED_NETS=172.16.0.0/12',
            '-e', 'OpenObserve__Enabled=false',
            '-v', 'bh-data:/app/data',
            '-v', 'bh-logs:/app/logs',
            '-v', ($envMap['VAULTS_DIR'] + ':/vaults')) + $ovmsEnv + @('bh-server:latest')
        Invoke-Wslc -WslcArgs $args | Out-Null
        Write-Host '[start] 等待后端 8788 就绪 ...'
        if (-not (Wait-Port $envMap['BAIHUA_SERVER_PORT'] 180)) { throw '后端 8788 未在 180s 内就绪（bh wslc logs server）' }
    }

    if ([string]::IsNullOrWhiteSpace($Only) -or $Only -eq 'webui') {
        if (-not (Test-Image 'bh-webui:latest')) { Build-Images 'webui' }
        Remove-Container $Containers['webui']
        Write-Host '[start] webui ...'
        $args = @('run', '-d', '--name', $Containers['webui'], '--network', $Network,
            '-p', ('127.0.0.1:' + $envMap['BAIHUA_WEBUI_PORT'] + ':5177'),
            '-e', 'WEBUI_CONFIG_DIR=/app/data',
            '-e', 'ASPNETCORE_URLS=http://0.0.0.0:5177',
            '-e', ('BaihuaServer__BaseUrl=http://' + $Containers['server'] + ':8788/'),
            '-e', 'ADMIN_IP_WHITELIST=172.16.0.0/12',
            '-e', 'OpenObserve__Enabled=false',
            '-v', 'bh-data:/app/data',
            '-v', 'bh-logs:/app/logs',
            '-v', ($envMap['VAULTS_DIR'] + ':/vaults'),
            'bh-webui:latest')
        Invoke-Wslc -WslcArgs $args | Out-Null
        Write-Host '[start] 等待 WebUI 5177 就绪 ...'
        if (-not (Wait-Port $envMap['BAIHUA_WEBUI_PORT'] 120)) { throw 'WebUI 5177 未在 120s 内就绪（bh wslc logs webui）' }
    }

    Write-Host ('[start] 完成：后端 http://127.0.0.1:' + $envMap['BAIHUA_SERVER_PORT'] + '  WebUI http://127.0.0.1:' + $envMap['BAIHUA_WEBUI_PORT'])
    Write-Host '        局域网 :80 入口仍用：bh lan on（宿主 portproxy -> 127.0.0.1:8788）'
}

function Stop-Stack {
    Ensure-Wslc
    foreach ($name in @($Containers['webui'], $Containers['server'], $Containers['postgres'])) {
        if (Test-Container $name) {
            Invoke-Wslc -WslcArgs @('container', 'stop', $name) -AllowFail | Out-Null
            Invoke-Wslc -WslcArgs @('container', 'remove', '-f', $name) -AllowFail | Out-Null
            Write-Host ('[stop] ' + $name)
        }
    }
    Write-Host '[stop] 已停止（命名卷 bh-pgdata / bh-data / bh-logs 保留）'
}

function Show-Status {
    Ensure-Wslc
    $envMap = Get-EnvMap
    Write-Host (Invoke-Wslc -WslcArgs @('container', 'list', '--all') -AllowFail).Output.Trim()
    Write-Host ''
    Write-Host ('后端:   http://127.0.0.1:' + $envMap['BAIHUA_SERVER_PORT'])
    Write-Host ('WebUI:  http://127.0.0.1:' + $envMap['BAIHUA_WEBUI_PORT'])
    $ov = if (Test-PortOpen 8000) { 'RUNNING (port 8000)' } else { 'stopped' }
    Write-Host ('OVMS:   ' + $ov + '（容器内经宿主 IP:8000 访问）')
    $mirror = $envMap['BAIHUA_WSL_MIRROR']
    if ([string]::IsNullOrWhiteSpace($mirror)) { $mirror = '（官方 registry）' }
    Write-Host ('镜像源: ' + $mirror)
}

function Show-Logs {
    param([string]$Svc, [int]$Count)
    if ([string]::IsNullOrWhiteSpace($Svc)) { $Svc = 'server' }
    if (-not $Containers.Contains($Svc)) {
        Write-Host ('未知服务：' + $Svc + '（可选：server|webui|postgres）')
        return
    }
    Invoke-Wslc -WslcArgs @('logs', '--tail', ($Count.ToString()), $Containers[$Svc]) | ForEach-Object { $_.Output.TrimEnd() } | Out-Host
}

function Open-WebUI {
    $envMap = Get-EnvMap
    $port = $envMap['BAIHUA_WEBUI_PORT']
    try {
        $resp = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $port + '/api/auth/cli-token') -Method POST -UseBasicParsing -TimeoutSec 10
        $token = ($resp.Content | ConvertFrom-Json).token
        Start-Process ('http://127.0.0.1:' + $port + '/?cli-token=' + $token)
        Write-Host '[dashboard] opened with cli-token'
    } catch {
        Write-Host ('[dashboard] cli-token 获取失败（' + $_.Exception.Message + '），打开普通地址')
        Start-Process ('http://127.0.0.1:' + $port)
    }
}

function Update-Stack {
    Write-Host '[update] git pull ...'
    & git -C $Root pull origin main
    if ($LASTEXITCODE -ne 0) { throw 'git pull 失败，请检查网络/代理' }
    Build-Images ''
    Start-Stack ''
}

$script:EnvMap = @{}

switch ($Command.ToLower()) {
    'help'      { Show-Help }
    'build'     { Ensure-Wslc; Build-Images $Arg1 }
    'pull'      { Ensure-Wslc; $m = Get-EnvMap; Ensure-Image (Get-PgImage $m) }
    'start'     { Start-Stack $Arg1 }
    'up'        { Start-Stack $Arg1 }
    'stop'      { Stop-Stack }
    'down'      { Stop-Stack }
    'restart'   { Stop-Stack; Start-Stack '' }
    'status'    { Show-Status }
    'ps'        { Ensure-Wslc; Write-Host (Invoke-Wslc -WslcArgs @('container', 'list', '--all') -AllowFail).Output.TrimEnd() }
    'logs'      { $count = 50; if ($Arg2) { $count = [int]$Arg2 }; Show-Logs $Arg1 $count }
    'exec'      {
        Ensure-Wslc
        if ($All.Count -lt 3 -or -not $Containers.Contains($All[1])) {
            Write-Host '用法：bh wslc exec <server|webui|postgres> <命令...>'
            Write-Host '例：  bh wslc exec postgres psql -U baihua -d baihua'
            exit 1
        }
        $svc = $All[1]
        $cmdArgs = @($All | Select-Object -Skip 2)
        & wslc exec $Containers[$svc] @cmdArgs
        exit $LASTEXITCODE
    }
    'update'    { Update-Stack }
    'dashboard' { Open-WebUI }
    'open'      { $m = Get-EnvMap; Start-Process ('http://127.0.0.1:' + $m['BAIHUA_WEBUI_PORT']) }
    'env'       {
        $m = Get-EnvMap
        Write-Host ('[env] ' + $EnvFile)
        foreach ($k in ($m.Keys | Sort-Object)) {
            $v = $m[$k]
            if ($k -eq 'PG_PASSWORD') { $v = $v.Substring(0, [Math]::Min(4, $v.Length)) + '****' }
            Write-Host ('  ' + $k + '=' + $v)
        }
    }
    default {
        Write-Warning ('[bh wslc] 未知命令：' + $Command)
        Show-Help
        exit 1
    }
}
