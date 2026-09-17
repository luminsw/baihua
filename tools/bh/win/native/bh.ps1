#Requires -Version 5.1
<#
  baihua - Windows + dotnet native CLI
  Manages 2 .NET services (server/webui) as local processes.

  Usage: .\tools\bh\win\native\bh.ps1 <command> [args]
    build [svc...]      dotnet publish to out/native/（可指定服务，默认全部 2 个）
    build-restart [svc...]  build + restart（编译后立即重启，可指定服务，默认全部）
    start [svc...]      start services（可指定服务，默认全部，按依赖顺序 server→webui）
    stop [svc...]       stop services（可指定服务，默认全部，逆依赖顺序）
    restart [svc...]    stop + start 指定服务（默认全部）
    update              git pull 最新代码 + 重建 + 重启（局域网机器一键升级）
    status              show port/process state per service
    status --json       machine-readable JSON（供 DSH 桥插件）
    logs <svc> [n]      tail service log (default 50 lines)
    dashboard           open browser with cli-token auto-login
    open                open browser to http://localhost:5177
    open-webui start|stop|status   manage Open WebUI (Python venv, port 8080)
    help                this help
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = 'help',
    [Parameter(Position = 1)]
    [string]$Arg1 = '',
    [Parameter(Position = 2)]
    [string]$Arg2 = '',
    [Parameter(Position = 3, ValueFromRemainingArguments = $true)]
    [string[]]$MoreArgs = @()
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSCommandPath
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$OutDir = Join-Path $Root 'out\native'
$PidDir = Join-Path $OutDir 'pids'
$LogDir = Join-Path $OutDir 'logs'
$DataHome = if ($env:BAIHUA_HOME) { $env:BAIHUA_HOME } else { Join-Path $HOME '.baihua' }

# 合并后单一服务架构：server(8788) → webui(5177)
# server 承载家庭/AI/知识库三模块（进程内直调，无模块间 HTTP）
# server 绑 0.0.0.0（跨机入口：算力池 /mg/*、服务器互联）；webui 绑 127.0.0.1
$Services = @(
    @{ Name = 'server'; Project = 'services\Baihua.Server'; Exe = 'bh-server.exe'; Port = 8788 },
    @{ Name = 'webui';  Project = 'services\Baihua.Web';    Exe = 'bh-webui.exe';  Port = 5177 }
)

function Help-Text {
    Get-Content $PSCommandPath | Select-Object -First 20 | Where-Object { $_ -match '^\s{4}[a-z]' } | ForEach-Object { $_.Trim() }
}

function Ensure-Dotnet {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { return }
    Write-Host '[deps] dotnet 缺失，自动安装（winget install Microsoft.DotNet.SDK.10）...'
    & winget install --id Microsoft.DotNet.SDK.10 --accept-source-agreements --accept-package-agreements --silent
    if ($LASTEXITCODE -ne 0) {
        Write-Host '[deps] winget 安装失败，请手动安装 .NET SDK 10: https://dotnet.microsoft.com/download'
        exit 1
    }
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    Write-Host '[deps] dotnet 安装完成'
}

function Invoke-Build {
    param([string[]]$Names = @())
    Ensure-Dotnet
    $targets = @(Resolve-ServiceList $Names)
    if ($targets.Count -eq 0) { return }
    if ($targets.Count -eq $Services.Count) {
        Write-Host '[build] all services'
    } else {
        Write-Host "[build] targets: $($targets.Name -join ', ')"
    }
    for ($i = $targets.Count - 1; $i -ge 0; $i--) { Stop-One $targets[$i] }
    foreach ($svc in $targets) {
        if (-not (Wait-PortClosed $svc.Port 15)) {
            Write-Warning "[$($svc.Name)] port $($svc.Port) 15s 内未释放（有残留进程？请检查）"
        }
    }
    foreach ($svc in $targets) {
        Write-Host "[build] $($svc.Name) ..."
        $proj = Join-Path $Root $svc.Project
        $out = & dotnet publish $proj -c Release -r win-x64 --self-contained false -o (Join-Path $OutDir $svc.Name) 2>&1 | ForEach-Object { Write-Host $_; $_ }
        if ($LASTEXITCODE -ne 0) { throw "publish failed: $($svc.Name)`n$($out | Select-Object -Last 6)" }
    }
    Write-Host "[build] done -> $OutDir"
}

function Update-Services {
    Write-Host '[update] git pull origin main ...'
    git -C $Root pull origin main
    if ($LASTEXITCODE -ne 0) { throw 'git pull 失败，请检查网络/代理' }
    Invoke-Build
    Start-Services
    $rule = netsh advfirewall firewall show rule name='Baihua Server 8788' 2>$null
    if ($LASTEXITCODE -ne 0) {
        netsh advfirewall firewall add rule name='Baihua Server 8788' dir=in action=allow protocol=TCP localport=8788 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host '[update] 已放行防火墙 TCP 8788（局域网算力池/互联入口）'
        } else {
            Write-Warning '[update] 放行防火墙 TCP 8788 失败（需要管理员权限），局域网算力池/互联可能不可达'
        }
    }
    # open-webui（Python venv，:8080）不在 $Services 里，一键更新不会自动带上它。
    # 已安装才自动拉起（避免更新时意外触发几分钟的 pip install）；未安装只提示。
    $owuiExe = Join-Path $DataHome 'open-webui-venv\Scripts\open-webui.exe'
    if (Test-Path $owuiExe) {
        if (Test-PortOpen 8080) {
            Write-Host '[update] open-webui 已在运行（:8080）'
        } else {
            Write-Host '[update] 启动 open-webui ...'
            Invoke-OpenWebUI 'start'
        }
    } else {
        Write-Warning '[update] 未安装 open-webui，跳过（需要时执行: bh open-webui install）'
    }
    $LASTEXITCODE = 0
    Write-Host '[update] done'
}

function Get-PidFile($name) { Join-Path $PidDir "$name.pid" }

function Test-PortOpen($port) {
    $c = New-Object Net.Sockets.TcpClient
    try { $c.Connect('127.0.0.1', $port); return $true } catch { return $false } finally { $c.Dispose() }
}

function Wait-Port($port, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-PortOpen $port) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Wait-PortClosed($port, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-PortOpen $port)) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Wait-ProcessExit($pid2, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $pid2 -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

# ---- OpenVINO Model Server（Windows SCM 服务 ovms，REST :8000）----
$OpenVinoServiceName = 'ovms'
$OpenVinoPort = 8000

function Get-OpenVinoHostService {
    Get-Service -Name $OpenVinoServiceName -ErrorAction SilentlyContinue
}

function Start-One($svc) {
    $exe = Join-Path $OutDir "$($svc.Name)\$($svc.Exe)"
    if (-not (Test-Path $exe)) { throw "not built: $exe (run 'build' first)" }
    if (Test-PortOpen $svc.Port) {
        $conn = Get-NetTCPConnection -LocalPort $svc.Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        $owner = if ($conn) { Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue } else { $null }
        if ($owner -and $owner.ProcessName -like 'bh-*') {
            Write-Warning "[$($svc.Name)] port $($svc.Port) 被残留进程 $($owner.ProcessName)($($owner.Id)) 占用，补杀后重试"
            Stop-Process -Id $owner.Id -Force -ErrorAction SilentlyContinue
            if (-not (Wait-PortClosed $svc.Port 10)) { Write-Warning "[$($svc.Name)] port $($svc.Port) 仍被占用，跳过"; return }
        } else {
            Write-Warning "[$($svc.Name)] port $($svc.Port) already in use, skip"
            return
        }
    }
    # server 是跨机入口（算力池 /mg/*、服务器互联），绑 0.0.0.0；webui 绑回环
    $bind = if ($svc.Name -eq 'server') { '0.0.0.0' } else { '127.0.0.1' }
    $envBlock = @{
        BAIHUA_HOME = $DataHome
        BAIHUA_SKIP_MUTEX = 'true'
        ASPNETCORE_URLS = "http://$bind`:$($svc.Port)"
        OpenObserve__Enabled = 'true'
    }
    $ooPassFile = Join-Path $DataHome 'openobserve-password.txt'
    if (Test-Path $ooPassFile) {
        $envBlock['OpenObserve__Password'] = (Get-Content $ooPassFile -Raw).Trim()
    }
    if ($svc.Name -eq 'server') {
        # PostgreSQL 单库 baihua（用户自行安装 PG，native cell 不管理）
        $envBlock['PG_HOST'] = if ($env:PG_HOST) { $env:PG_HOST } else { '127.0.0.1' }
        $envBlock['PG_USER'] = if ($env:PG_USER) { $env:PG_USER } else { 'postgres' }
        $envBlock['PG_PASSWORD'] = $env:PG_PASSWORD
        $envBlock['PG_DATABASE'] = if ($env:PG_DATABASE) { $env:PG_DATABASE } else { 'baihua' }
        # OpenVINO OVMS（Windows 系统服务，REST :8000）
        $envBlock['OpenVinoOms__BaseUrl'] = if ($env:OPENVINO_OMS_URL) { $env:OPENVINO_OMS_URL } else { 'http://127.0.0.1:8000' }
    }
    if ($svc.Name -eq 'webui') {
        $envBlock['WEBUI_CONFIG_DIR'] = $DataHome
        $envBlock['BaihuaServer__BaseUrl'] = 'http://127.0.0.1:8788/'
    }
    foreach ($k in $envBlock.Keys) { Set-Item -Path ('Env:' + $k) -Value $envBlock[$k] }
    New-Item -ItemType Directory -Force -Path $PidDir, $LogDir | Out-Null
    $logFile = Join-Path $LogDir "$($svc.Name).log"
    $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err" -PassThru -WindowStyle Hidden
    Set-Content -Path (Get-PidFile $svc.Name) -Value $p.Id
    Write-Host "[$($svc.Name)] started pid=$($p.Id) port=$($svc.Port) log=$logFile"
}

function Start-Services {
    New-Item -ItemType Directory -Force -Path $DataHome | Out-Null
    foreach ($svc in $Services) { Start-One $svc }
    Write-Host "[start] waiting for health ..."
    $ok = $true
    foreach ($svc in $Services) {
        if (-not (Wait-Port $svc.Port 60)) { Write-Warning "[$($svc.Name)] port $($svc.Port) not ready in 60s"; $ok = $false }
        else { Write-Host "[$($svc.Name)] ready on $($svc.Port)" }
    }
    if ($ok) { Write-Host "[start] all services up. WebUI: http://localhost:5177" }
}

function Stop-One($svc) {
    $pf = Get-PidFile $svc.Name
    $stopped = $false
    if (Test-Path $pf) {
        $pid2 = [int](Get-Content $pf)
        $proc = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
        if ($proc) {
            Stop-Process -Id $pid2 -Force -ErrorAction SilentlyContinue
            if (-not (Wait-ProcessExit $pid2 10)) { Write-Warning "[$($svc.Name)] pid $pid2 10s 内未退出" }
            $stopped = $true
        }
        Remove-Item $pf -Force -ErrorAction SilentlyContinue
    }
    if (-not $stopped -and (Test-PortOpen $svc.Port)) {
        $conn = Get-NetTCPConnection -LocalPort $svc.Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($conn) { Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue; Write-Host "[$($svc.Name)] stopped by port pid=$($conn.OwningProcess)" }
    }
}

function Stop-Services {
    for ($i = $Services.Count - 1; $i -ge 0; $i--) { Stop-One $Services[$i] }
    foreach ($svc in $Services) {
        if (-not (Wait-PortClosed $svc.Port 15)) {
            Write-Warning "[$($svc.Name)] port $($svc.Port) 15s 内未释放（有残留进程？请检查）"
        }
    }
    Write-Host '[stop] done'
}

function Resolve-SingleService($name, [ref]$svcRef) {
    if (-not $name) { return $false }
    $svc = $Services | Where-Object { $_.Name -eq $name.ToLower() }
    if (-not $svc) { Write-Host "unknown service: $name (server|webui)" -ForegroundColor Yellow; return $true }
    $svcRef.Value = $svc
    return $false
}

function Resolve-ServiceList {
    param([string[]]$Names = @())
    if ($Names.Count -eq 0) { return $Services }
    $found = @()
    $unknown = @()
    foreach ($n in $Names) {
        if ([string]::IsNullOrWhiteSpace($n)) { continue }
        $svc = $Services | Where-Object { $_.Name -eq $n.ToLower() }
        if ($svc) { $found += $svc } else { $unknown += $n }
    }
    if ($unknown.Count -gt 0) {
        Write-Host "unknown service: $($unknown -join ', ') (server|webui)" -ForegroundColor Yellow
    }
    if ($found.Count -eq 0) { return @() }
    return @($Services | Where-Object { $_.Name -in ($found.Name) })
}

function Get-ServiceArgs {
    return @($Arg1) + @($Arg2) + $MoreArgs | Where-Object { $_ }
}

# ---- 特殊服务名 ----------------------------------------------------------------
# status 会展示、卡片也会操作，但**不在 $Services 里**（它们不是 bh-server/bh-webui 那种
# out/native 下的 dotnet 进程）：open-webui = Python venv（:8080，scripts/start-open-webui.ps1），
# openvino = OVMS（:8000，装了 Windows 服务 ovms 才有启停手段，否则只是普通进程）。
# 以前 `bh start|stop|restart open-webui` 会走 Resolve-ServiceList → 只打印
# “unknown service: open-webui (server|webui)” 然后静默什么都不做，
# 于是设置页卡片上这两个服务的按钮全是空操作（2026-09-17 实测）。
$SpecialServices = @('open-webui', 'openvino')

function Get-SpecialList {
    param([string[]]$Names = @())
    $found = @()
    foreach ($n in $Names) {
        if ([string]::IsNullOrWhiteSpace($n)) { continue }
        $k = $n.ToLower()
        if (($SpecialServices -contains $k) -and ($found -notcontains $k)) { $found += $k }
    }
    return @($found)
}

function Start-Special($name) {
    switch ($name) {
        'open-webui' { Invoke-OpenWebUI 'start' }
        'openvino' {
            $svc = Get-OpenVinoHostService
            if ($svc) {
                try { Start-Service -Name $OpenVinoServiceName -ErrorAction Stop; Write-Host "[openvino] 已启动系统服务 $OpenVinoServiceName" }
                catch { Write-Warning "[openvino] 启动系统服务失败：$($_.Exception.Message)" }
            } else {
                Write-Warning "[openvino] 未安装 Windows 服务 $OpenVinoServiceName（当前 ovms 是普通进程，bh 无法启动）；可用 scripts/install-openvino-ovms-service.ps1 装成服务"
            }
        }
    }
}

function Stop-Special($name) {
    switch ($name) {
        'open-webui' { Invoke-OpenWebUI 'stop' }
        'openvino' {
            $svc = Get-OpenVinoHostService
            if ($svc) {
                try { Stop-Service -Name $OpenVinoServiceName -Force -ErrorAction Stop; Write-Host "[openvino] 已停止系统服务 $OpenVinoServiceName" }
                catch { Write-Warning "[openvino] 停止系统服务失败：$($_.Exception.Message)" }
            } else {
                Write-Warning "[openvino] 未安装 Windows 服务 $OpenVinoServiceName（当前 ovms 是普通进程），bh 不代为 kill；请手动结束该进程"
            }
        }
    }
}

function Get-PortOwnerProcess($port) {
    $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $conn) { return $null }
    return Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
}

function Show-Status {
    foreach ($svc in $Services) {
        $portOpen = Test-PortOpen $svc.Port
        $pf = Get-PidFile $svc.Name
        $pidAlive = $false
        if (Test-Path $pf) {
            $pid2 = [int](Get-Content $pf)
            $pidAlive = [bool](Get-Process -Id $pid2 -ErrorAction SilentlyContinue)
        }
        $owner = if ($portOpen) { Get-PortOwnerProcess $svc.Port } else { $null }
        $ownerName = if ($owner) { $owner.ProcessName } else { '' }
        $isReleaseExe = $ownerName -like 'bh-*'
        $isDotnetRun = $false
        if ($owner) {
            try {
                $ownerProc = Get-CimInstance Win32_Process -Filter "ProcessId=$($owner.Id)" -ErrorAction Stop
                $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($ownerProc.ParentProcessId)" -ErrorAction Stop
                $isDotnetRun = $parent.Name -eq 'dotnet.exe'
            } catch { $isDotnetRun = $false }
        }

        $state = if ($portOpen -and $pidAlive) { 'RUNNING (release)' }
                 elseif ($portOpen -and $isDotnetRun) { 'RUNNING (dotnet run)' }
                 elseif ($portOpen -and $isReleaseExe) { 'RUNNING (release, 外部启动)' }
                 elseif ($portOpen) { "PORT-OPEN (foreign:$ownerName)" }
                 elseif ($pidAlive) { 'PROC-ALIVE' }
                 else { 'stopped' }
        Write-Host ("{0,-8} port={1,-5} {2}" -f $svc.Name, $svc.Port, $state)
    }
    $ovSvc = Get-OpenVinoHostService
    $ovState = if ($ovSvc) { $ovSvc.Status.ToString() } else { 'not installed' }
    if (Test-PortOpen $OpenVinoPort) { $ovState = 'RUNNING (port 8000)' }
    Write-Host ("{0,-8} port={1,-5} {2}" -f 'openvino', $OpenVinoPort, $ovState)
    $owui = Get-OpenWebUIStatus
    $owuiState = if ($owui.portOpen) { 'RUNNING' } else { 'stopped' }
    Write-Host ("{0,-8} port={1,-5} {2}" -f 'open-webui', $owui.port, $owuiState)
}

function Show-StatusJson {
    $gitHead = 'unknown'; $gitBranch = 'unknown'; $gitDirty = $false
    if (Test-Path (Join-Path $Root '.git')) {
        try {
            $h = git -C $Root rev-parse --short HEAD 2>$null
            if ($h) { $gitHead = ($h | Select-Object -First 1) }
            $b = git -C $Root rev-parse --abbrev-ref HEAD 2>$null
            if ($b) { $gitBranch = ($b | Select-Object -First 1) }
            $d = git -C $Root status --porcelain 2>$null
            if ($d) { $gitDirty = $true }
        } catch { }
    }
    $entries = @()
    foreach ($svc in $Services) {
        $portOpen = Test-PortOpen $svc.Port
        $pf = Get-PidFile $svc.Name
        $pidAlive = $false
        if (Test-Path $pf) {
            $pid2 = [int](Get-Content $pf)
            $pidAlive = [bool](Get-Process -Id $pid2 -ErrorAction SilentlyContinue)
        }
        $running = $portOpen -and $pidAlive
        $phase = if ($running) { 'Running' } elseif ($portOpen) { 'PortOpen' } elseif ($pidAlive) { 'ProcAlive' } else { 'Stopped' }
        $entries += [pscustomobject]@{
            name = $svc.Name
            ready = if ($running) { 1 } else { 0 }
            replicas = 1
            image = 'native'
            age = ''
            restarts = 0
            phase = $phase
            imageCommit = $gitHead
            upToDate = $true
        }
    }
    $ovSvc = Get-OpenVinoHostService
    $ovRunning = Test-PortOpen $OpenVinoPort
    $ovPhase = if ($ovRunning) { 'Running' } elseif ($ovSvc) { $ovSvc.Status.ToString() } else { 'not installed' }
    $entries += [pscustomobject]@{
        name = 'openvino'
        ready = if ($ovRunning) { 1 } else { 0 }
        replicas = 1
        image = 'ovms'
        age = ''
        restarts = 0
        phase = $ovPhase
        imageCommit = $gitHead
        upToDate = $true
    }
    $owui = Get-OpenWebUIStatus
    $entries += [pscustomobject]@{
        name = $owui.name
        ready = $owui.ready
        replicas = 1
        image = $owui.image
        age = ''
        restarts = 0
        phase = $owui.phase
        imageCommit = $gitHead
        upToDate = $true
    }
    $readyTotal = @($entries | Where-Object { $_.ready -eq 1 }).Count
    [pscustomobject]@{
        cell = 'native'
        namespace = 'windows'
        updatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        git = [pscustomobject]@{ head = $gitHead; branch = $gitBranch; dirty = $gitDirty }
        services = $entries
        summary = [pscustomobject]@{ ready = $readyTotal; total = $entries.Count }
    } | ConvertTo-Json -Depth 5
}

function Show-Logs($svcName, $n) {
    $svc = $Services | Where-Object { $_.Name -eq $svcName }
    if (-not $svc) { Write-Host "unknown service: $svcName (server|webui)"; return }
    $log = Join-Path $LogDir "$svcName.log"
    if (-not (Test-Path $log)) { Write-Host "no log yet: $log"; return }
    $fs = [System.IO.File]::Open($log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $bytes = New-Object byte[] ([int]$fs.Length)
        [void]$fs.Read($bytes, 0, $bytes.Length)
    } finally { $fs.Dispose() }
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    try { $text = $utf8.GetString($bytes) }
    catch { $text = [System.Text.Encoding]::GetEncoding([System.Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage).GetString($bytes) }
    ($text -split "\r?\n") | Where-Object { $_ -ne '' } | Select-Object -Last $n
}

function Open-Dashboard {
    try {
        $resp = Invoke-WebRequest -Uri 'http://127.0.0.1:5177/api/auth/cli-token' -Method POST -UseBasicParsing -TimeoutSec 15
        $token = ($resp.Content | ConvertFrom-Json).token
        Start-Process "http://127.0.0.1:5177/?cli-token=$token"
        Write-Host "[dashboard] opened with cli-token"
    } catch {
        Write-Host "[dashboard] cli-token failed ($($_.Exception.Message)), opening plain URL"
        Start-Process 'http://127.0.0.1:5177'
    }
}

# ---- Open WebUI（Python venv，端口 8080）----
function Invoke-OpenWebUI {
    param([string]$SubCmd = 'start')
    $script = Join-Path $Root 'scripts\start-open-webui.ps1'
    if (-not (Test-Path $script)) { Write-Error "缺少 $script"; return }
    switch ($SubCmd.ToLower()) {
        'stop'    { & $script -Stop }
        'status'  { & $script -Status }
        'install' { & $script -Install }
        default   { & $script }
    }
}

function Get-OpenWebUIStatus {
    $port = 8080
    $portOpen = Test-PortOpen $port
    $dataHome = if ($env:BAIHUA_HOME) { $env:BAIHUA_HOME } else { Join-Path $HOME '.baihua' }
    $pidFile = Join-Path $dataHome 'open-webui.pid'
    $pidAlive = $false
    if (Test-Path $pidFile) {
        $pid2 = [int](Get-Content $pidFile)
        $pidAlive = [bool](Get-Process -Id $pid2 -ErrorAction SilentlyContinue)
    }
    $running = $portOpen -and $pidAlive
    $phase = if ($running) { 'Running' } elseif ($portOpen) { 'PortOpen' } elseif ($pidAlive) { 'ProcAlive' } else { 'Stopped' }
    return [pscustomobject]@{
        name = 'open-webui'
        ready = if ($running) { 1 } else { 0 }
        replicas = 1
        image = 'venv'
        age = ''
        restarts = 0
        phase = $phase
        imageCommit = ''
        upToDate = $true
        port = $port
        portOpen = $portOpen
    }
}

switch ($Command.ToLower()) {
    'build'     { Invoke-Build (Get-ServiceArgs) }
    'build-restart' {
        $names = Get-ServiceArgs
        # open-webui / openvino 是本仓库之外的东西（Python venv / 外部 OVMS），没有编译目标，
        # 明确提示而不是走 Resolve-ServiceList 打一句 unknown 就静默退出。
        foreach ($s in Get-SpecialList $names) {
            $why = if ($s -eq 'open-webui') { 'Python venv，非本仓库构建' } else { '外部 OVMS，非本仓库构建' }
            Write-Warning "[build-restart] $s 没有编译目标（$why）；如需重启请用: bh restart $s"
        }
        $regular = if ($names.Count -eq 0) { @() } else { @($names | Where-Object { $SpecialServices -notcontains $_.ToLower() }) }
        if ($names.Count -gt 0 -and $regular.Count -eq 0) { break }
        $targets = Resolve-ServiceList $regular
        if ($targets.Count -eq 0) { break }
        Invoke-Build $targets.Name
        if ($targets.Count -eq $Services.Count) {
            Start-Services
        } else {
            foreach ($svc in $targets) { Start-One $svc }
            Write-Host "[build-restart] waiting for health ..."
            foreach ($svc in $targets) {
                if (-not (Wait-Port $svc.Port 60)) { Write-Warning "[$($svc.Name)] port $($svc.Port) not ready in 60s" }
                else { Write-Host "[$($svc.Name)] ready on $($svc.Port)" }
            }
            Write-Host "[build-restart] done: $($targets.Name -join ', ')"
        }
    }
    'start'     {
        $names = Get-ServiceArgs
        if ($names.Count -eq 0) { Start-Services; break }
        # 特殊服务（open-webui/openvino）先处理；其余交给 $Services。
        # 注意：过滤后若为空不能再调 Resolve-ServiceList —— 它对空数组会返回**全部**
        # $Services，那样 `bh start open-webui` 会误把 server+webui 一起拉起来。
        foreach ($s in Get-SpecialList $names) { Start-Special $s }
        $regular = @($names | Where-Object { $SpecialServices -notcontains $_.ToLower() })
        if ($regular.Count -gt 0) {
            $targets = Resolve-ServiceList $regular
            if ($targets.Count -gt 0) {
                foreach ($svc in $targets) { Start-One $svc; Write-Host "[start] $($svc.Name) starting..." }
                Write-Host "[start] waiting for health ..."
                foreach ($svc in $targets) {
                    if (-not (Wait-Port $svc.Port 60)) { Write-Warning "[$($svc.Name)] port $($svc.Port) not ready in 60s" }
                    else { Write-Host "[$($svc.Name)] ready on $($svc.Port)" }
                }
            }
        }
    }
    'stop'      {
        $names = Get-ServiceArgs
        if ($names.Count -eq 0) { Stop-Services; break }
        foreach ($s in Get-SpecialList $names) { Stop-Special $s }
        $regular = @($names | Where-Object { $SpecialServices -notcontains $_.ToLower() })
        if ($regular.Count -gt 0) {
            $targets = Resolve-ServiceList $regular
            if ($targets.Count -gt 0) {
                for ($i = $targets.Count - 1; $i -ge 0; $i--) { Stop-One $targets[$i] }
                Write-Host "[stop] done: $($targets.Name -join ', ')"
            }
        }
    }
    'restart'   {
        $names = Get-ServiceArgs
        if ($names.Count -eq 0) { Stop-Services; Start-Services; break }
        $specials = Get-SpecialList $names
        foreach ($s in $specials) { Stop-Special $s }
        foreach ($s in $specials) { Start-Special $s }
        $regular = @($names | Where-Object { $SpecialServices -notcontains $_.ToLower() })
        if ($regular.Count -gt 0) {
            $targets = Resolve-ServiceList $regular
            if ($targets.Count -gt 0) {
                for ($i = $targets.Count - 1; $i -ge 0; $i--) { Stop-One $targets[$i] }
                foreach ($svc in $targets) {
                    if (-not (Wait-PortClosed $svc.Port 15)) { Write-Warning "[$($svc.Name)] port $($svc.Port) 15s 内未释放" }
                }
                foreach ($svc in $targets) { Start-One $svc }
                Write-Host "[restart] restarting: $($targets.Name -join ', ')"
            }
        }
    }
    'update'    { Update-Services }
    'status'    { if ($Arg1 -eq '--json') { Show-StatusJson } else { Show-Status } }
    'logs'      { $count = 50; if ($Arg2) { $count = [int]$Arg2 }; Show-Logs $Arg1 $count }
    'dashboard' { Open-Dashboard }
    'open'      { Start-Process 'http://127.0.0.1:5177' }
    'open-webui' { Invoke-OpenWebUI $Arg1 }
    'help'      { Help-Text }
    default     { Help-Text }
}

exit 0