#Requires -Version 5.1
<#
  baihua - Windows + dotnet native CLI（Windows 唯一形态，2026-11-25 起）
  Manages 2 .NET services (server/webui) as local processes + SQLite（单库 db\baihua.db）.

  Usage: bh <command> [args]（不带参数 = bh status）
    build [svc...]      dotnet publish to out/native/（开发仓库；部署包 apps\ 自包含产物不可 build）
    build-restart [svc...]  build + restart（编译后立即重启，可指定服务，默认全部）
    start [svc...]      start services（可指定服务，默认全部，按依赖顺序 server→webui）
    stop [svc...]       stop services（可指定服务，默认全部，逆依赖顺序）
    restart [svc...]    stop + start 指定服务（默认全部）
    update              git pull 最新代码 + 重建 + 重启（局域网机器一键升级）
    status [--json]     状态总览（端口/进程/OVMS/open-webui/访问地址）；--json 供 DSH 桥插件
    doctor [--json] [--fix]  本机体检：.NET / SQLite / 产物 / 端口 / lan / OVMS / open-webui
    backup [目录]       冷备份数据目录（默认 ~\.baihua，排除 bin/logs/models/venv）→ zip + .sha256（需先 bh stop）
    restore <zip> [-y]  恢复数据目录（旧目录改名 .pre-restore-<时间> 保留；需先 bh stop）
    autostart [on|off|status] 登录自启（用户级 Startup，执行 bh start；不需要管理员）
    logs <svc> [n]      tail 日志（默认 50 行；-f 跟随，-e 看 stderr）
    ovms [status|start|stop|restart|logs [n]]
                        本地模型服务 OVMS(:8000)：自动识别 wslc 容器 / Windows 服务 / 外部进程
    open-webui [install|start|stop|restart|status|logs [n]]
                        AI 对话页（wslc 容器 bh-open-webui，:8080；需 WSL >= 2.9.3，缺失仅降级）
    dashboard           open browser with cli-token auto-login
    open                open browser to http://localhost:5177
    help                this help
#>
# 不用 param()/CmdletBinding：子命令选项（-f / -e / --json / -Prebuilt / -U …）要原样透传，
# 而 CmdletBinding 会先把 `-e` 当 PowerShell 参数解析（与 -ErrorAction 二义性 → 直接报错）。
# 统一用自动变量 $args 收齐，再切成 command / arg1 / arg2 / 其余。
$ArgList = @($args)
$Command = if ($ArgList.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$ArgList[0])) { [string]$ArgList[0] } else { 'status' }
$Arg1 = if ($ArgList.Count -gt 1) { [string]$ArgList[1] } else { '' }
$Arg2 = if ($ArgList.Count -gt 2) { [string]$ArgList[2] } else { '' }
$MoreArgs = @($ArgList | Select-Object -Skip 3)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSCommandPath
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$Root = Split-Path -Parent $Root
$AppsDir = Join-Path $Root 'apps'
# 产物目录：部署包布局（<根>\apps\server\bh-server.exe，自包含 publish）优先，否则开发仓库 out\native
$OutDir = if (Test-Path (Join-Path $AppsDir 'server\bh-server.exe')) { $AppsDir } else { Join-Path $Root 'out\native' }
$IsPackageLayout = ($OutDir -eq $AppsDir)
$PidDir = Join-Path $OutDir 'pids'
$LogDir = Join-Path $OutDir 'logs'
# 数据目录：BAIHUA_DATA_HOME 可覆盖（默认 ~\.baihua）。不再读 BAIHUA_HOME——
# 部署包 install 设 BAIHUA_PKG_HOME 做包定位，与数据目录语义解耦（2026-11-25）；
# 服务进程内仍注入 BAIHUA_HOME=$DataHome（那是服务进程的数据目录语义，见 Start-One）。
$DataHome = if ($env:BAIHUA_DATA_HOME) { $env:BAIHUA_DATA_HOME } else { Join-Path $HOME '.baihua' }
$DbPath = Join-Path $DataHome 'db\baihua.db'

# 合并后单一服务架构：server(8788) → webui(5177)
# server 承载家庭/AI/知识库三模块（进程内直调，无模块间 HTTP）
# server 绑 0.0.0.0（跨机入口：算力池 /mg/*、服务器互联）；webui 绑 127.0.0.1
$Services = @(
    @{ Name = 'server'; Project = 'services\Baihua.Server'; Exe = 'bh-server.exe'; Port = 8788 },
    @{ Name = 'webui';  Project = 'services\Baihua.Web';    Exe = 'bh-webui.exe';  Port = 5177 }
)

function Help-Text {
    Get-Content $PSCommandPath | Select-Object -First 48 | Where-Object { $_ -match '^\s{4}[a-z]' } | ForEach-Object { $_.Trim() }
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

function Get-HostLanIp {
    # 取"默认路由所在网卡"的 IPv4 —— 手机/局域网设备要访问的是这个地址。
    # 只按私有地址正则会把 WSL 的 vEthernet（172.30.208.1）误当成宿主 IP，必须排除虚拟网卡。
    try {
        $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop |
            Sort-Object RouteMetric | Select-Object -First 1
        if ($route) {
            $ip = (Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop |
                Select-Object -First 1).IPAddress
            if ($ip -and $ip -notmatch '^127\.') { return $ip }
        }
    } catch { }
    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
        $_.IPAddress -match '^(192\.168|10\.|172\.(1[6-9]|2\d|3[01]))\.' -and
        $_.InterfaceAlias -notmatch 'WSL|Hyper-V|vEthernet|Loopback'
    } | Select-Object -ExpandProperty IPAddress
    if ($ips) { return $ips[0] }
    return ''
}

# ---- 完整性标签守卫 ----
# 背景：Windows 按 .exe 文件的完整性标签决定进程令牌的 IL。若构建产物落在带 Low 标签的目录里
# （沙箱类工具，例如 DSH 的 Windows 沙箱，会给工作区打 Low 标签好让受限令牌能写），
# out\native 下的 bh-server.exe 一启动就是 Low IL：读文件正常，写 %TEMP% / ~\.baihua 全部
# "Access denied"（知识库写笔记、索引快照、自动备份全灭）。详见 docs/PLAN_NATIVE_CELL.md。
function Get-IntegrityLabel([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    $line = (& icacls $path 2>$null | Where-Object { $_ -match 'Mandatory Level' } | Select-Object -First 1)
    if (-not $line) { return '' }
    if ($line -match 'Low Mandatory') { return 'Low' }
    if ($line -match 'Medium Mandatory') { return 'Medium' }
    if ($line -match 'High Mandatory') { return 'High' }
    return 'Other'
}

function Repair-LowIntegrityLabel([string]$path, [switch]$Quiet) {
    if (-not (Test-Path $path)) { return }
    if ((Get-IntegrityLabel $path) -ne 'Low') { return }

    if (-not $Quiet) {
        Write-Host "[integrity] $path 带 Low 完整性标签：从这里启动的 exe 会以 Low IL 运行" -ForegroundColor Yellow
        Write-Host "[integrity] 后果是「能读不能写」：写 $env:TEMP、$DataHome 全部 Access denied" -ForegroundColor Yellow
        Write-Host "[integrity] 正在改为 Medium …" -ForegroundColor Yellow
    }
    & icacls $path /setintegritylevel '(OI)(CI)Medium' 2>$null | Out-Null
    if ((Get-IntegrityLabel $path) -eq 'Low') {
        Write-Warning "[integrity] 自动修复失败（本进程可能就运行在低完整性沙箱里）。请在普通终端执行："
        Write-Warning "            icacls `"$path`" /setintegritylevel (OI)(CI)Medium"
    } elseif (-not $Quiet) {
        Write-Host "[integrity] 已改为 Medium（不影响其余目录）"
    }
}

function Invoke-Build {
    param([string[]]$Names = @())
    if ($IsPackageLayout) {
        throw '[build] 部署包形态（apps\ 自包含产物）无需编译；更新请重新下载部署包并 bh install'
    }
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
        # ⚠ PowerShell 5.1 在 $ErrorActionPreference='Stop' 下会把「2>&1 重定向进来的原生 stderr」
        # 当成终止性错误（NativeCommandError）—— dotnet 一条警告就能让整次 build 半路炸掉。
        # 所以这类调用必须局部放宽 EAP，只按退出码判成败。
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $out = & dotnet publish $proj -c Release -r win-x64 --self-contained false -o (Join-Path $OutDir $svc.Name) 2>&1 | ForEach-Object { Write-Host $_; $_ }
            $publishCode = $LASTEXITCODE
        } finally { $ErrorActionPreference = $prevEap }
        if ($publishCode -ne 0) { throw "publish failed: $($svc.Name)`n$($out | Select-Object -Last 6)" }
        # 发布后立刻校正完整性标签：产物若继承到 Low 标签，exe 会以 Low IL 启动（能读不能写）
        Repair-LowIntegrityLabel (Join-Path $OutDir $svc.Name)
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
    # open-webui（wslc 容器 bh-open-webui，:8080）：容器已创建就保持运行；未安装只提示。
    $owState = Get-OpenWebUiContainerState
    if ($owState -eq 'running') {
        Write-Host '[update] open-webui 已在运行（:8080）'
    } elseif ($owState) {
        Write-Host '[update] open-webui 容器未运行 → 启动'
        Start-OpenWebUi
    } else {
        Write-Warning '[update] open-webui 容器未安装，跳过（需要时执行: bh open-webui install）'
    }
    $LASTEXITCODE = 0
    Write-Host '[update] done'
}

function Get-PidFile($name) { Join-Path $PidDir "$name.pid" }

<#
    本机监听端口集合（一次 .NET 枚举，毫秒级；不做缓存：Wait-Port 需要每次拿最新状态）。

    只收「回环可达」的绑定：通配地址（0.0.0.0 / ::）或明确的回环地址 —— 与旧实现
    「连 127.0.0.1:port」语义一致（server 绑 0.0.0.0、webui 绑 127.0.0.1 都命中）。
#>
function Get-ListeningPortSet {
    $set = @{}
    try {
        $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
        foreach ($ep in $listeners) {
            $addr = $ep.Address
            if ($addr.Equals([System.Net.IPAddress]::Any) -or
                $addr.Equals([System.Net.IPAddress]::IPv6Any) -or
                [System.Net.IPAddress]::IsLoopback($addr)) {
                $set[[int]$ep.Port] = $true
            }
        }
    } catch { }
    return $set
}

function Test-PortOpen($port) {
    # ⚠️ 不要改回 TcpClient.Connect：部分机器（有防火墙/WFP 过滤时）对**未监听**的回环端口
    # 不返回 RST 而是一直等到超时，实测 ~2.05s/次；`bh status` 要探 4 个端口 → 6s+，
    # 而 DSH 插件是同步拿这个结果（历史版本 spawnSync）→ 整个 harness 卡 6s。
    # 端口枚举一次只要 ~30ms，且 4 个端口复用同一实现总耗时仍是 ~30ms。
    return (Get-ListeningPortSet).ContainsKey([int]$port)
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

# ---- OpenVINO Model Server（OVMS，REST :8000）----
# OVMS 的宿主形态会变，bh 不再假设只有「Windows 服务」一种：
#   wslc     WSL3 容器 ovms-gpu（现行形态，2026-11-15 起；wslc.exe 随 WSL3 提供）
#   service  Windows SCM 服务 ovms（历史形态，已卸载，仍兼容）
#   process  只是某个进程占着 :8000（bh 不代为 kill，只报 pid）
# 运行手册：%BAIHUA_HOME%\ovms\README.md
$OpenVinoServiceName = 'ovms'
$OpenVinoPort = 8000
$OvmsContainerName = 'ovms-gpu'
$OvmsUrl = 'http://127.0.0.1:' + $OpenVinoPort

function Get-OpenVinoHostService {
    Get-Service -Name $OpenVinoServiceName -ErrorAction SilentlyContinue
}

# wslc 只探一次（status / doctor / ovms 子命令复用，避免反复 Get-Command）
$script:WslcExe = $null
function Get-WslcExe {
    if ($null -eq $script:WslcExe) {
        $c = Get-Command wslc -ErrorAction SilentlyContinue
        $script:WslcExe = if ($c) { $c.Source } else { '' }
    }
    return $script:WslcExe
}

# wslc 调用外壳：EAP 局部放宽（PS 5.1 会把 stderr 当终止错误），只按退出码判成败
function Invoke-WslcAllowFail {
    param([string[]]$WslcArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & wslc @WslcArgs 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    return [pscustomobject]@{ ExitCode = $code; Output = $out }
}

# 从 `wslc container inspect` 的 JSON 文本取容器状态：running / exited / created / ...
# 注意：有 healthcheck 的容器（open-webui）输出里**第一个** `"Status"` 是 Health.Status（healthy/…），
# 真正的状态在 State 块里（Running 布尔 + Status 字符串），必须先锚定 State 块再取。
function Get-WslcContainerStateFromInspect {
    param([string]$InspectText)
    $stateBlock = if ($InspectText -match '(?s)"State"\s*:\s*\{(.*)') { $matches[1] } else { $InspectText }
    if ($stateBlock -match '"Running"\s*:\s*true') { return 'running' }
    if ($stateBlock -match '"Running"\s*:\s*false') {
        if ($stateBlock -match '"Status"\s*:\s*"([a-zA-Z]+)"') { return $matches[1].ToLower() }
        return 'exited'
    }
    return ''
}

# 容器状态：running / exited / created / paused / dead；'' = 没有这个容器
# 不用 `wslc list --format json`：它把 Command 字段截断成非法 JSON（含孤立代理字符），
# ConvertFrom-Json 会直接抛错；inspect 的输出是逐行 JSON 文本，用正则取状态最稳（且不受控制台代码页影响）。
function Get-OvmsContainerState {
    if (-not (Get-WslcExe)) { return '' }
    $r = Invoke-WslcAllowFail @('container', 'inspect', $OvmsContainerName)
    if ($r.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($r.Output)) { return '' }
    return Get-WslcContainerStateFromInspect $r.Output
}

function Get-OvmsModels {
    try {
        $r = Invoke-RestMethod -Uri ($OvmsUrl + '/v1/models') -TimeoutSec 3
        return @($r.data | ForEach-Object { $_.id })
    } catch { return @() }
}

# OVMS 现况：形态（wslc / service / process / none）+ 容器状态 + 模型列表
function Get-OvmsState {
    param([switch]$SkipModels)
    $container = Get-OvmsContainerState
    $svc = Get-OpenVinoHostService
    $portOpen = Test-PortOpen $OpenVinoPort
    $engine = 'none'
    if ($container) { $engine = 'wslc' }
    elseif ($svc) { $engine = 'service' }
    elseif ($portOpen) { $engine = 'process' }
    $models = @()
    if ($portOpen -and -not $SkipModels) { $models = Get-OvmsModels }
    $ownerPid = 0
    if ($portOpen -and $engine -eq 'process') {
        $p = Get-PortOwnerProcess $OpenVinoPort
        if ($p) { $ownerPid = $p.Id }
    }
    return [pscustomobject]@{
        Engine = $engine
        Container = $OvmsContainerName
        ContainerState = $container
        ServiceStatus = if ($svc) { $svc.Status.ToString() } else { '' }
        Models = $models
        OwnerPid = $ownerPid
        PortOpen = $portOpen
        Url = $OvmsUrl
    }
}

function Show-OvmsStatus {
    $st = Get-OvmsState
    Write-Host '[ovms] 本地模型服务（OVMS，REST :8000）'
    $engineText = switch ($st.Engine) {
        'wslc'    { 'wslc 容器 ' + $st.Container + '（' + $st.ContainerState + '）' }
        'service' { 'Windows 服务 ' + $OpenVinoServiceName + '（' + $st.ServiceStatus + '）' }
        'process' { '外部进程 pid ' + $st.OwnerPid + '（不是 bh 管的）' }
        default   { '未安装' }
    }
    Write-Host ('[ovms] 形态: ' + $engineText)
    if ($st.PortOpen) {
        $models = if ($st.Models.Count -gt 0) { $st.Models -join ', ' } else { '（/v1/models 没返回模型）' }
        Write-Host ('[ovms] 状态: RUNNING  ' + $st.Url + '  模型: ' + $models)
    } else {
        Write-Host '[ovms] 状态: STOPPED'
    }
    if ($st.Engine -eq 'none') {
        Write-Host '[ovms] 提示: 本机还没装 OVMS；参考 %BAIHUA_HOME%\ovms\README.md 用 wslc 起 GPU 容器'
    } elseif ($st.Engine -eq 'wslc' -and $st.ContainerState -ne 'running') {
        Write-Host '[ovms] 提示: bh ovms start 拉起容器'
    }
}

function Start-Ovms {
    $st = Get-OvmsState -SkipModels
    switch ($st.Engine) {
        'wslc' {
            if ($st.ContainerState -eq 'running') { Write-Host ('[ovms] 容器 ' + $st.Container + ' 已在运行：' + $st.Url); return }
            $boot = Join-Path $DataHome 'ovms\start-ovms-wslc.ps1'
            if (Test-Path $boot) {
                # 优先用机器自带的拉起脚本：内含端口占用检查 + 等所有模型就绪
                Write-Host ('[ovms] 启动容器 ' + $st.Container + '（' + $boot + '）...')
                $shellExe = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
                & $shellExe -NoProfile -ExecutionPolicy Bypass -File $boot
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning ('[ovms] 拉起脚本退出码 ' + $LASTEXITCODE + '（详见 %BAIHUA_HOME%\ovms\README.md）')
                    return
                }
            } else {
                Write-Host ('[ovms] 启动容器 ' + $st.Container + ' ...')
                & wslc start $st.Container | Out-Null
                if ($LASTEXITCODE -ne 0) { Write-Warning '[ovms] wslc start 失败'; return }
            }
            # 固定接入 bh-net：server/open-webui 容器用容器名 http://ovms-gpu:8000 访问，
            # 不依赖每次启动都会变化的 WSL 宿主 IP（已连接时该命令会报错，忽略即可）
            $null = Invoke-WslcAllowFail @('network', 'connect', 'bh-net', $st.Container)
            if (Wait-Port $OpenVinoPort 120) {
                Write-Host ('[ovms] 就绪：' + $st.Url + '（模型：' + ((Get-OvmsModels) -join ', ') + '）')
            } else {
                Write-Warning ('[ovms] 120s 内 :' + $OpenVinoPort + ' 未就绪（bh ovms logs 看日志）')
            }
        }
        'service' {
            try {
                Start-Service -Name $OpenVinoServiceName -ErrorAction Stop
                Write-Host ('[ovms] 已启动 Windows 服务 ' + $OpenVinoServiceName)
            } catch {
                Write-Warning ('[ovms] 启动 Windows 服务失败：' + $_.Exception.Message + '（需要管理员）')
            }
        }
        'process' {
            Write-Warning ('[ovms] :' + $OpenVinoPort + ' 已被外部进程 pid ' + $st.OwnerPid + ' 占用：bh 不接管，请自行管理')
        }
        default {
            Write-Warning ('[ovms] 本机没有 OVMS（既无 ' + $OvmsContainerName + ' 容器，也无 ' + $OpenVinoServiceName + ' 服务）')
            Write-Warning '[ovms] 安装/重建参考 %BAIHUA_HOME%\ovms\README.md；不装也能启动百花（AI 功能降级）'
        }
    }
}

function Stop-Ovms {
    $st = Get-OvmsState -SkipModels
    switch ($st.Engine) {
        'wslc' {
            if ($st.ContainerState -ne 'running') { Write-Host ('[ovms] 容器 ' + $st.Container + ' 未在运行（' + $st.ContainerState + '）'); return }
            Write-Host ('[ovms] 停止容器 ' + $st.Container + ' ...')
            & wslc stop $st.Container | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warning '[ovms] wslc stop 失败'; return }
            if (Wait-PortClosed $OpenVinoPort 30) { Write-Host '[ovms] 已停止（容器与模型文件还在，bh ovms start 可恢复）' }
            else { Write-Warning ('[ovms] :' + $OpenVinoPort + ' 30s 内仍在监听') }
        }
        'service' {
            try {
                Stop-Service -Name $OpenVinoServiceName -Force -ErrorAction Stop
                Write-Host ('[ovms] 已停止 Windows 服务 ' + $OpenVinoServiceName)
            } catch {
                Write-Warning ('[ovms] 停止 Windows 服务失败：' + $_.Exception.Message + '（需要管理员）')
            }
        }
        'process' {
            Write-Warning ('[ovms] :' + $OpenVinoPort + ' 是外部进程 pid ' + $st.OwnerPid + '，bh 不代为 kill；确需停止：taskkill /PID ' + $st.OwnerPid + ' /F')
        }
        default { Write-Host '[ovms] 本机没有 OVMS，无需停止' }
    }
}

function Invoke-OvmsLogs([int]$n = 50) {
    $st = Get-OvmsState -SkipModels
    switch ($st.Engine) {
        'wslc' { & wslc logs --tail $n $st.Container }
        default {
            $f = Get-ChildItem (Join-Path $DataHome 'ovms\*.log') -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($f) { Get-Content $f.FullName -Tail $n; return }
            Write-Host '[ovms] 没有可读日志（当前不是 wslc 容器形态）'
        }
    }
}

function Invoke-OvmsCommand {
    $sub = if ($Arg1) { $Arg1.ToLower() } else { 'status' }
    switch ($sub) {
        'start'   { Start-Ovms }
        'stop'    { Stop-Ovms }
        'restart' { Stop-Ovms; Start-Ovms }
        'logs'    { $n = 50; if ($Arg2) { $n = [int]$Arg2 }; Invoke-OvmsLogs $n }
        default   { Show-OvmsStatus }
    }
}

# ---- 静默启动器（GUI 子系统的小 exe）-----------------------------------------
# 计划任务 / Explorer 在用户会话里起控制台程序 → 一定会分配控制台窗口；`powershell -WindowStyle Hidden`
# 只是"先创建再隐藏"，实测仍会露出窗口（2026-11-15、11-17 两次用户反馈黑框）。
# 换成一个 WindowsApplication（GUI 子系统）外壳：它自身没有控制台，再用 CreateNoWindow
# 起子进程并 WaitForExit —— 全程没有任何窗口可闪，也不需要 VBScript。
#
# 两种调用方式（都保持"父进程一直等到子进程退出"的语义，任务/快捷方式因此继续持有服务）：
#   bh-launch.exe <script.cmd> [workdir]            → cmd /c <script.cmd>（服务启动用）
#   bh-launch.exe --ps1 <script.ps1> [args...]      → powershell -File <script> <args>（自启/open-webui 用）
# 放在 %BAIHUA_HOME%\bin（稳定路径，与 out/ 是否重编无关）；版本号变了会重新编译。
$script:BhLauncherVersion = '2'
function Get-BhLauncherExe {
    $binDir = Join-Path $DataHome 'bin'
    $exe = Join-Path $binDir 'bh-launch.exe'
    $verFile = Join-Path $binDir 'bh-launch.version'
    $ver = if (Test-Path $verFile) { (Get-Content $verFile -Raw).Trim() } else { '' }
    if ((Test-Path $exe) -and $ver -eq $script:BhLauncherVersion) { return $exe }
    $src = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

public static class BhLaunch
{
    public static int Main(string[] args)
    {
        if (args == null || args.Length < 1 || string.IsNullOrEmpty(args[0])) return 2;
        ProcessStartInfo psi;
        if (string.Equals(args[0], "--ps1", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2) return 2;
            var script = args[1];
            var sb = new StringBuilder("-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"");
            for (int i = 2; i < args.Length; i++)
            {
                sb.Append(' ');
                sb.Append(args[i].IndexOf(' ') >= 0 ? "\"" + args[i] + "\"" : args[i]);
            }
            psi = new ProcessStartInfo(PowerShellPath(), sb.ToString());
            psi.WorkingDirectory = Path.GetDirectoryName(script);
        }
        else
        {
            var cmd = args[0];
            var workDir = (args.Length > 1 && !string.IsNullOrEmpty(args[1])) ? args[1] : Path.GetDirectoryName(cmd);
            psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"");
            psi.WorkingDirectory = workDir;
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using (var p = Process.Start(psi))
        {
            if (p == null) return 3;
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    private static string PowerShellPath()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var p = Path.Combine(sys, @"WindowsPowerShell\v1.0\powershell.exe");
        return File.Exists(p) ? p : "powershell.exe";
    }
}
'@
    try {
        New-Item -ItemType Directory -Force -Path $binDir | Out-Null
        Remove-Item $exe -Force -ErrorAction SilentlyContinue
        Add-Type -TypeDefinition $src -OutputAssembly $exe -OutputType WindowsApplication -ErrorAction Stop
        Set-Content -Path $verFile -Value $script:BhLauncherVersion -Encoding ASCII
    } catch {
        Write-Warning ('[launcher] 生成静默启动器失败：' + $_.Exception.Message + '（退回 PowerShell 隐藏外壳）')
        return ''
    }
    if (Test-Path $exe) { return $exe }
    return ''
}

function Start-One($svc) {
    $exe = Join-Path $OutDir "$($svc.Name)\$($svc.Exe)"
    if (-not (Test-Path $exe)) { throw "not built: $exe (run 'build' first)" }
    if (Test-PortOpen $svc.Port) {
        $conn = Get-NetTCPConnection -LocalPort $svc.Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        $owner = if ($conn) { Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue } else { $null }
        if ($owner -and $owner.ProcessName -like 'bh-*') {
            # 幂等：端口上是**我们自己记录的那个 pid** ⇒ 服务健康在跑，直接跳过。
            # （以前一律「补杀后重试」：`bh start` 会把正在服务的进程杀掉重起，登录自启也会踩到——
            #   正在处理的请求、内存里的会话全丢。）
            $pf = Get-PidFile $svc.Name
            $knownPid = 0
            if (Test-Path $pf) { $knownPid = [int](Get-Content $pf) }
            if ($knownPid -eq $owner.Id) {
                Write-Host "[$($svc.Name)] 已在运行（pid=$($owner.Id), port=$($svc.Port)），跳过"
                return
            }
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
        # 移动端扫码即授权（与容器时代一致）；用户可用环境变量显式覆盖
        $envBlock['Baihua__AutoAuthorizeDevices'] = if ($env:Baihua__AutoAuthorizeDevices) { $env:Baihua__AutoAuthorizeDevices } else { 'true' }
        # 二维码/局域网发现 baseUrl：BAIHUA_PUBLIC_BASE_URL 显式指定，否则按默认路由网卡 IP 推导
        # （无端口、指向宿主 IP；server 侧留空也会自行探测，这里注入保证与容器时代行为一致）
        $publicBase = $env:BAIHUA_PUBLIC_BASE_URL
        if (-not $publicBase) {
            $lanIp = Get-HostLanIp
            if ($lanIp) { $publicBase = 'http://' + $lanIp }
        }
        if ($publicBase) { $envBlock['Baihua__PublicBaseUrl'] = $publicBase }
        # OpenVINO OVMS（REST :8000；无本地推理则服务端自行降级）
        $envBlock['OpenVinoOms__BaseUrl'] = if ($env:OPENVINO_OMS_URL) { $env:OPENVINO_OMS_URL } else { 'http://127.0.0.1:8000' }
    }
    if ($svc.Name -eq 'webui') {
        $envBlock['WEBUI_CONFIG_DIR'] = $DataHome
        $envBlock['BaihuaServer__BaseUrl'] = 'http://127.0.0.1:8788/'
    }
    New-Item -ItemType Directory -Force -Path $PidDir, $LogDir | Out-Null
    $logFile = Join-Path $LogDir "$($svc.Name).log"
    # 启动前自愈：产物目录若带 Low 标签，从这里启动的进程会是 Low IL（能读不能写）
    Repair-LowIntegrityLabel (Split-Path $exe)
    # 用一次性计划任务启动：脱离调用方（终端/工具）的 Job Object（Start-Process 会随命令结束被杀）。
    # 注意：进程的完整性级别由 .exe 文件自带的标签决定，切换启动方式（WMI/Start-Process/schtasks）
    # 不会改变它 —— 曾因此误判为「WMI 令牌降级」。
    $startCmd = Join-Path $LogDir "start-$($svc.Name).cmd"
    $lines = @('@echo off')
    # 控制台代码页钉死 UTF-8：计划任务启动的进程默认拿到系统 OEM 代码页（简中 936/GBK），
    # 会把中文日志写成 GBK 字节，与其它运行方式写入的 UTF-8 字节混进同一个 .log（乱码一半）。
    # 服务进程内部也设了 Console.OutputEncoding；这里再保证子进程/原生输出同样是 UTF-8。
    $lines += 'chcp 65001 >nul'
    foreach ($k in $envBlock.Keys) { $lines += "set `"$k=$($envBlock[$k])`"" }
    $lines += "cd /d `"$(Split-Path $exe)`""
    # 计划任务以用户 Medium 令牌运行，能修好调用方（可能处于低完整性沙箱）修不了的标签
    $lines += "icacls `"$(Split-Path $exe)`" /setintegritylevel `"(OI)(CI)Medium`" >nul 2>&1"
    $lines += "`"$exe`" >> `"$logFile`" 2>> `"$logFile.err`""
    Set-Content -Path $startCmd -Value $lines -Encoding ASCII

    # ---- 静默启动外壳 ------------------------------------------------------------
    # 目标：任务在用户会话里拉起控制台程序时**一个窗口都不出现**（2026-11-15 用户两次反馈黑框）。
    # 优先级：
    #   1) bh-launch.exe —— GUI 子系统的小启动器（自己不是控制台程序，天生没有窗口可闪），
    #      内部 CreateProcess(cmd.exe /c start-<svc>.cmd, CREATE_NO_WINDOW) 并 WaitForExit；
    #   2) 编译不出来 → powershell -WindowStyle Hidden（控制台先创建再隐藏，可能闪 0.1~0.3s）；
    #   3) 再不行 → 直接 cmd（可见窗口，功能优先）。
    # 无论哪条路，外壳都必须**同步等** cmd 结束：任务实例要继续持有服务进程，
    # 否则任务结束会把它连坐杀掉（2026-11-04 实测）。
    $launcherExe = Get-BhLauncherExe
    $psLauncher = ''
    if (-not $launcherExe) {
        $psLauncher = Join-Path $LogDir "start-$($svc.Name).ps1"
        $psLines = @(
            '# generated by bh start - fallback silent launcher (hidden window; waits for the .cmd so the task keeps owning the service)',
            '$ErrorActionPreference = ''Continue''',
            ('$cmd = Join-Path $PSScriptRoot ''' + "start-$($svc.Name).cmd" + ''''),
            '& cmd.exe /c $cmd'
        )
        Set-Content -Path $psLauncher -Value $psLines -Encoding ASCII
    }
    # 清理早期版本留下的 VBS / PS1 启动器（升级后不留垃圾）
    Remove-Item (Join-Path $LogDir "start-$($svc.Name).vbs") -Force -ErrorAction SilentlyContinue
    if ($launcherExe) { Remove-Item (Join-Path $LogDir "start-$($svc.Name).ps1") -Force -ErrorAction SilentlyContinue }
    if ($launcherExe) {
        $taskAction = '"' + $launcherExe + '" "' + $startCmd + '" "' + (Split-Path -Parent $startCmd) + '"'
    } elseif ($psLauncher) {
        $taskAction = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $psLauncher + '"'
    } else {
        $taskAction = $startCmd
    }

    $taskName = "bh-start-$($svc.Name)-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    # ⚠ schtasks 会把「/ST 早于当前时间」的**警告**写到 stderr；PS 5.1 在 EAP=Stop 下 2>&1 会把它
    # 当成终止性错误（NativeCommandError），服务直接起不来（实测 bh start 报 schtasks 警告后退出）。
    # 必须局部放宽 EAP，只按退出码判成败。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $createOut = (& schtasks /create /tn $taskName /tr $taskAction /sc once /st 00:00 /f /rl limited 2>&1 | Out-String)
        $createCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prevEap }
    if ($createCode -ne 0) { throw "[$($svc.Name)] schtasks create failed: $($createOut.Trim())" }
    try {
        $ErrorActionPreference = 'Continue'
        $null = & schtasks /run /tn $taskName 2>&1
        Start-Sleep -Milliseconds 800
    }
    finally {
        $null = & schtasks /delete /tn $taskName /f 2>&1
        $ErrorActionPreference = 'Stop'
    }
    Write-Host "[$($svc.Name)] started (silent, scheduled task) port=$($svc.Port) log=$logFile"
}

function Update-RealPid($svc) {
    $conn = Get-NetTCPConnection -LocalPort $svc.Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) { Set-Content -Path (Get-PidFile $svc.Name) -Value $conn.OwningProcess }
}

function Start-Services {
    New-Item -ItemType Directory -Force -Path $DataHome | Out-Null
    foreach ($svc in $Services) { Start-One $svc }
    Write-Host "[start] waiting for health ..."
    $ok = $true
    foreach ($svc in $Services) {
        if (-not (Wait-Port $svc.Port 60)) { Write-Warning "[$($svc.Name)] port $($svc.Port) not ready in 60s"; $ok = $false }
        else { Update-RealPid $svc; Write-Host "[$($svc.Name)] ready on $($svc.Port)" }
    }
    if ($ok) { Write-Host "[start] all services up. WebUI: http://localhost:5177" }
    # AI 对话页：wslc 容器 bh-open-webui（:8080）。容器已创建未运行 → 拉起；
    # 未创建只提示（镜像拉取耗时，交给显式 bh open-webui install）。
    $owState = Get-OpenWebUiContainerState
    if ($owState -eq 'running') {
        Write-Host '[start] open-webui 已在运行（:8080）'
    } elseif ($owState) {
        Write-Host '[start] open-webui 容器未运行 → 启动'
        Start-OpenWebUi
    } else {
        Write-Host '[start] open-webui 容器未创建：AI 对话页（/open-webui）会空白（需要时 bh open-webui install）'
    }
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
    # 端口兜底：bootstrap pid 可能是 cmd，真实服务 pid 不同，必须再按端口确认一次
    if (Test-PortOpen $svc.Port) {
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
# 产物目录下的 dotnet 进程）：open-webui = wslc 容器 bh-open-webui（:8080），
# openvino/ovms = OVMS（:8000，wslc 容器 ovms-gpu / Windows 服务 / 外部进程）。
# 以前 `bh start|stop|restart open-webui` 会走 Resolve-ServiceList → 只打印
# “unknown service: open-webui (server|webui)” 然后静默什么都不做，
# 于是设置页卡片上这两个服务的按钮全是空操作（2026-09-17 实测）。
$SpecialServices = @('open-webui', 'openvino', 'ovms')

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
        'openvino' { Start-Ovms }
        'ovms'     { Start-Ovms }
    }
}

function Stop-Special($name) {
    switch ($name) {
        'open-webui' { Invoke-OpenWebUI 'stop' }
        'openvino' { Stop-Ovms }
        'ovms'     { Stop-Ovms }
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
        if ($owner -and -not $isReleaseExe) {
            # 只在「不是自己产物」时才多查两层父进程（CIM 查询慢，别拖慢 bh status）
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
        $color = if ($state -like 'RUNNING*') { 'Green' } elseif ($state -like 'PORT-OPEN*') { 'Yellow' } elseif ($state -eq 'stopped') { 'DarkGray' } else { 'Yellow' }
        Write-Host ("{0,-8} port={1,-5} {2}" -f $svc.Name, $svc.Port, $state) -ForegroundColor $color
    }
    # OVMS 形态现在可能是 wslc 容器（2026-11-15 起）/ Windows 服务（历史）/ 外部进程
    $ov = Get-OvmsState -SkipModels
    $ovState = switch ($ov.Engine) {
        'wslc'    { if ($ov.PortOpen) { 'RUNNING (wslc:' + $ov.Container + ')' } else { 'STOPPED (wslc:' + $ov.Container + ' ' + $ov.ContainerState + ')' } }
        'service' { if ($ov.PortOpen) { 'RUNNING (windows service ' + $OpenVinoServiceName + ')' } else { 'STOPPED (windows service ' + $ov.ServiceStatus + ')' } }
        'process' { 'RUNNING (external pid ' + $ov.OwnerPid + ')' }
        default   { 'not installed' }
    }
    $ovColor = if ($ov.PortOpen) { 'Green' } elseif ($ov.Engine -eq 'none') { 'DarkGray' } else { 'Yellow' }
    Write-Host ("{0,-8} port={1,-5} {2}" -f 'ovms', $OpenVinoPort, $ovState) -ForegroundColor $ovColor
    $owui = Get-OpenWebUIStatus
    # 容器 running 且端口开才是真 RUNNING；端口开但容器没在跑 = 旧 venv 等容器外进程占着 :8080
    $owuiState = if ($owui.portOpen -and $owui.containerState -eq 'running') { 'RUNNING (wslc:' + $OpenWebUiContainerName + ')' }
                 elseif ($owui.portOpen) { 'PORT-OPEN (容器未运行，:8080 被容器外进程占用？)' }
                 elseif ($owui.containerState) { 'stopped (wslc:' + $OpenWebUiContainerName + ' ' + $owui.containerState + ')' }
                 else { 'not installed' }
    $owuiColor = if ($owui.portOpen -and $owui.containerState -eq 'running') { 'Green' } elseif ($owui.containerState) { 'Yellow' } elseif ($owui.portOpen) { 'Yellow' } else { 'DarkGray' }
    Write-Host ("{0,-8} port={1,-5} {2}" -f 'open-webui', $owui.port, $owuiState) -ForegroundColor $owuiColor
    Write-Host ''
    Write-Host 'WebUI  http://localhost:5177      后端  http://127.0.0.1:8788（server 绑 0.0.0.0）'
    Write-Host '下一步  bh dashboard 打开面板 · bh doctor 体检 · bh help 全部命令'
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
    $ov = Get-OvmsState -SkipModels
    $ovPhase = if ($ov.PortOpen) { 'Running' }
               elseif ($ov.Engine -eq 'wslc') { 'Stopped' }
               elseif ($ov.Engine -eq 'service') { $ov.ServiceStatus }
               else { 'not installed' }
    $ovImage = if ($ov.Engine -eq 'wslc') { 'wslc:' + $ov.Container } elseif ($ov.Engine -eq 'service') { 'ovms' } elseif ($ov.Engine -eq 'process') { 'external' } else { 'none' }
    $entries += [pscustomobject]@{
        name = 'openvino'
        ready = if ($ov.PortOpen) { 1 } else { 0 }
        replicas = 1
        image = $ovImage
        age = ''
        restarts = 0
        phase = $ovPhase
        imageCommit = $gitHead
        upToDate = $true
        engine = $ov.Engine
        container = $ov.Container
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

function Show-Logs($svcName, $n, [switch]$Follow, [switch]$ErrLog) {
    $svc = $Services | Where-Object { $_.Name -eq $svcName }
    if (-not $svc) { Write-Host "unknown service: $svcName (server|webui)" -ForegroundColor Yellow; return }
    $log = if ($ErrLog) { Join-Path $LogDir "$svcName.log.err" } else { Join-Path $LogDir "$svcName.log" }
    if (-not (Test-Path $log)) { Write-Host "no log yet: $log"; return }
    if ($Follow) {
        # -f：跟随输出（Ctrl+C 退出）；日志本身由服务以 UTF-8 写入
        Get-Content -Path $log -Tail $n -Wait -Encoding UTF8
        return
    }
    $fs = [System.IO.File]::Open($log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $bytes = New-Object byte[] ([int]$fs.Length)
        [void]$fs.Read($bytes, 0, $bytes.Length)
    } finally { $fs.Dispose() }
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    try { $text = $utf8.GetString($bytes) }
    catch { $text = [System.Text.Encoding]::GetEncoding([System.Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage).GetString($bytes) }
    # 新日志带 UTF-8 BOM（方便记事本/PowerShell 5.1 识别）：显示前去掉行首 BOM
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
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

# ---- Open WebUI（AI 对话页，wslc 容器 bh-open-webui，:8080）--------------------
# 2026-11-25 起由 wslc 容器承载（替代旧 Python venv 形态）：镜像 ghcr.io/open-webui/open-webui
# 固定 v0.13.3（与旧 venv 版本一致，避免自动升级破坏 webui.db 数据格式），数据卷 bh-openwebui-data。
# wslc.exe 缺失（无 WSL3）时降级提示：AI 对话页不可用，不影响核心功能。
$OpenWebUiContainerName = 'bh-open-webui'
$OpenWebUiPort = 8080
$OpenWebUiNetwork = 'bh-net'
$OpenWebUiVolume = 'bh-openwebui-data'
$OpenWebUiImage = 'ghcr.io/open-webui/open-webui:v0.13.3'

function Get-OpenWebUiContainerState {
    # running / exited / created / ...；'' = 没有这个容器或没有 wslc（语义同 Get-OvmsContainerState）
    if (-not (Get-WslcExe)) { return '' }
    $r = Invoke-WslcAllowFail @('container', 'inspect', $OpenWebUiContainerName)
    if ($r.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($r.Output)) { return '' }
    return Get-WslcContainerStateFromInspect $r.Output
}

function Ensure-OpenWebUIImage {
    # 返回镜像名；wslc 缺失 / 拉取失败返回 ''（调用方降级提示）
    if (-not (Get-WslcExe)) {
        Write-Warning '[open-webui] 未找到 wslc.exe（需要 WSL >= 2.9.3，wsl --update）；AI 对话页可选，不影响核心功能'
        return ''
    }
    if ((Invoke-WslcAllowFail @('image', 'inspect', $OpenWebUiImage)).ExitCode -eq 0) { return $OpenWebUiImage }
    Write-Host ('[open-webui] 拉取镜像 ' + $OpenWebUiImage + ' ...')
    if ((Invoke-WslcAllowFail @('pull', $OpenWebUiImage)).ExitCode -eq 0) { return $OpenWebUiImage }
    # 国内直连 ghcr.io 常超时：南大镜像站拉取后 retag 成官方名（镜像内容一致）
    $mirror = $OpenWebUiImage -replace '^ghcr\.io/', 'ghcr.nju.edu.cn/'
    Write-Host ('[open-webui] 直连失败，尝试镜像站 ' + $mirror + ' ...')
    if ((Invoke-WslcAllowFail @('pull', $mirror)).ExitCode -ne 0) {
        Write-Warning ('[open-webui] 镜像拉取失败：' + $OpenWebUiImage + '（网络/代理问题）；稍后重试 bh open-webui install')
        return ''
    }
    $null = Invoke-WslcAllowFail @('tag', $mirror, $OpenWebUiImage)
    return $OpenWebUiImage
}

function New-OpenWebUiContainer {
    param([string]$Image)
    # 网络 / 数据卷（幂等）
    if ((Invoke-WslcAllowFail @('network', 'inspect', $OpenWebUiNetwork)).ExitCode -ne 0) {
        Write-Host ('[open-webui] 创建网络 ' + $OpenWebUiNetwork)
        $null = Invoke-WslcAllowFail @('network', 'create', $OpenWebUiNetwork)
    }
    if ((Invoke-WslcAllowFail @('volume', 'inspect', $OpenWebUiVolume)).ExitCode -ne 0) {
        Write-Host ('[open-webui] 创建数据卷 ' + $OpenWebUiVolume)
        $null = Invoke-WslcAllowFail @('volume', 'create', $OpenWebUiVolume)
    }
    # 本地推理端点：ovms-gpu 容器存在时经容器名互访（bh-net），否则不注入（open-webui 独立跑）
    $ovEnv = @()
    if (Get-OvmsContainerState) {
        $ovmsApi = 'http://ovms-gpu:8000/v1'
        $ovEnv = @('-e', 'ENABLE_OPENAI_API=true',
            '-e', ('OPENAI_API_BASE_URL=' + $ovmsApi),
            '-e', ('RAG_OPENAI_API_BASE_URL=' + $ovmsApi))
    }
    $owEnv = @('-e', 'ENABLE_OLLAMA_API=false', '-e', 'OLLAMA_BASE_URL=http://localhost:1',
        '-e', 'OPENAI_API_KEY=dummy', '-e', 'RAG_OPENAI_API_KEY=dummy',
        '-e', 'RAG_EMBEDDING_ENGINE=openai', '-e', 'RAG_EMBEDDING_MODEL=qwen3-embedding-0.6b',
        '-e', 'HF_HUB_OFFLINE=1', '-e', 'TRANSFORMERS_OFFLINE=1', '-e', 'WEBUI_AUTH=false') + $ovEnv
    $runArgs = @('run', '-d', '--name', $OpenWebUiContainerName, '--network', $OpenWebUiNetwork,
        '--restart', 'always',
        '-p', '127.0.0.1:8080:8080',
        '-v', ($OpenWebUiVolume + ':/app/backend/data')) + $owEnv + @($Image)
    Write-Host ('[open-webui] 创建容器 ' + $OpenWebUiContainerName + ' ...')
    $r = Invoke-WslcAllowFail $runArgs
    if ($r.ExitCode -ne 0) {
        Write-Warning ('[open-webui] 创建容器失败：' + $r.Output.Trim())
        return $false
    }
    # ovms-gpu 接入 bh-net：open-webui 容器经容器名访问本地推理（已连接时该命令报错，忽略）
    if (Get-OvmsContainerState) {
        $null = Invoke-WslcAllowFail @('network', 'connect', $OpenWebUiNetwork, 'ovms-gpu')
    }
    return $true
}

function Start-OpenWebUi {
    if (-not (Get-WslcExe)) {
        Write-Warning '[open-webui] 未找到 wslc.exe（需要 WSL >= 2.9.3，wsl --update）；AI 对话页可选，不影响核心功能'
        return
    }
    $state = Get-OpenWebUiContainerState
    if ($state -eq 'running' -and (Test-PortOpen $OpenWebUiPort)) {
        Write-Host ('[open-webui] 已在运行 http://127.0.0.1:' + $OpenWebUiPort)
        return
    }
    if ($state -eq 'running' -and -not (Test-PortOpen $OpenWebUiPort)) {
        Write-Warning ('[open-webui] 容器在跑但 :' + $OpenWebUiPort + ' 未监听（启动中？稍候 bh open-webui status 确认）')
        return
    }
    if (-not $state) {
        Write-Host '[open-webui] 容器未创建，首次安装（拉镜像 + 建容器）...'
        $img = Ensure-OpenWebUIImage
        if (-not $img) { return }
        if (-not (New-OpenWebUiContainer $img)) { return }
    } else {
        Write-Host ('[open-webui] 启动容器 ' + $OpenWebUiContainerName + ' ...')
        $r = Invoke-WslcAllowFail @('container', 'start', $OpenWebUiContainerName)
        if ($r.ExitCode -ne 0) {
            Write-Warning ('[open-webui] 启动失败：' + $r.Output.Trim() + '（bh open-webui logs 看日志）')
            return
        }
    }
    if (Wait-Port $OpenWebUiPort 180) {
        Write-Host ('[open-webui] 就绪 http://127.0.0.1:' + $OpenWebUiPort)
    } else {
        Write-Warning ('[open-webui] 180s 内 :' + $OpenWebUiPort + ' 未就绪（bh open-webui logs 看日志）')
    }
}

function Stop-OpenWebUi {
    if (-not (Get-OpenWebUiContainerState)) { Write-Host '[open-webui] 容器不存在'; return }
    $null = Invoke-WslcAllowFail @('container', 'stop', $OpenWebUiContainerName)
    $null = Invoke-WslcAllowFail @('container', 'remove', '-f', $OpenWebUiContainerName)
    Write-Host ('[open-webui] 已停止并移除容器（数据卷 ' + $OpenWebUiVolume + ' 保留，重装不丢数据）')
}

function Show-OpenWebUiStatus {
    if (-not (Get-WslcExe)) {
        Write-Host '[open-webui] 未找到 wslc.exe：AI 对话页不可用（需要 WSL >= 2.9.3，wsl --update）'
        return
    }
    $state = Get-OpenWebUiContainerState
    $portOpen = Test-PortOpen $OpenWebUiPort
    if ($state -eq 'running' -and $portOpen) {
        Write-Host ('[open-webui] RUNNING http://127.0.0.1:' + $OpenWebUiPort + '（容器 ' + $OpenWebUiContainerName + '，数据卷 ' + $OpenWebUiVolume + '）')
    } elseif ($state -eq 'running' -and -not $portOpen) {
        Write-Host ('[open-webui] 容器 ' + $OpenWebUiContainerName + ' 运行中，:' + $OpenWebUiPort + ' 尚未就绪（启动中）')
    } elseif ($state) {
        Write-Host ('[open-webui] 容器 ' + $OpenWebUiContainerName + ' 状态 ' + $state + '（bh open-webui start 启动）')
    } elseif ($portOpen) {
        Write-Host ('[open-webui] :' + $OpenWebUiPort + ' 被容器外进程占用（旧 venv open-webui？scripts\start-open-webui.ps1 -Stop 停掉后再 bh open-webui start）')
    } else {
        Write-Host '[open-webui] 容器未创建（bh open-webui install 安装）'
    }
}

function Invoke-OpenWebUI {
    param([string]$SubCmd = 'status')
    switch (($SubCmd + '').ToLower()) {
        'install' {
            if (Get-OpenWebUiContainerState) { Write-Host ('[open-webui] 容器已存在（' + $OpenWebUiContainerName + '），直接 start'); Start-OpenWebUi; return }
            $img = Ensure-OpenWebUIImage
            if (-not $img) { return }
            if (New-OpenWebUiContainer $img) { Start-OpenWebUi }
        }
        'start'   { Start-OpenWebUi }
        'stop'    { Stop-OpenWebUi }
        'restart' { Stop-OpenWebUi; Start-OpenWebUi }
        'logs'    {
            $n = 50
            if ($Arg2) { $n = [int]$Arg2 }
            $r = Invoke-WslcAllowFail @('logs', '--tail', ($n.ToString()), $OpenWebUiContainerName)
            Write-Host $r.Output
        }
        default   { Show-OpenWebUiStatus }
    }
}

function Get-OpenWebUIStatus {
    # status --json / Show-Status 复用；与旧 venv 版返回同一属性形状（DSH 卡片兼容）
    $portOpen = Test-PortOpen $OpenWebUiPort
    $state = Get-OpenWebUiContainerState
    $phase = if ($portOpen) { 'Running' }
             elseif ($state) { 'Stopped' }
             else { 'NotInstalled' }
    return [pscustomobject]@{
        name = 'open-webui'
        ready = if ($portOpen) { 1 } else { 0 }
        replicas = 1
        image = 'wslc:' + $OpenWebUiContainerName
        age = ''
        restarts = 0
        phase = $phase
        imageCommit = ''
        upToDate = $true
        port = $OpenWebUiPort
        portOpen = $portOpen
        containerState = $state
    }
}

# ---- backup / restore（数据目录冷备份/恢复）--------------------------------------
# 语义对齐 Linux 原生形态（tools/bh/linux/native/bh.sh 的 backup_data/restore_data）：
# 冷备份 = 服务停止后打包数据目录 → zip + .sha256；恢复 = 解包 staging →
# 旧目录改名 .pre-restore-<时间> 保留 → 搬入新数据（不删旧数据，可手动回滚）。
# 两者都要求先 bh stop：SQLite 运行中打包 WAL 会得到损坏的快照。
# 排除 bin/logs（运行产物）、models（OVMS 模型，~11GB 可重新下载）、
# open-webui-venv（已退役的 Python venv，~2.3GB 安装产物）——只备「数据」。

function Assert-ServicesStoppedForDataOp {
    $running = @()
    foreach ($svc in $Services) {
        if (Test-PortOpen $svc.Port) { $running += $svc.Name + '(:' + [string]$svc.Port + ')' }
    }
    if ($running.Count -gt 0) {
        Write-Warning ('[backup/restore] 服务仍在运行: ' + ($running -join ', ') + ' —— SQLite 运行中打包会损坏数据，请先 bh stop')
        return $false
    }
    return $true
}

function Invoke-Backup {
    param([string[]]$CmdArgs = @())
    if (-not (Assert-ServicesStoppedForDataOp)) { exit 1 }
    if (-not (Test-Path $DataHome)) {
        Write-Warning ('[backup] 数据目录不存在: ' + $DataHome)
        exit 1
    }
    # 目标目录：第一个参数，默认 $Root\backup（仓库/部署包根下，仓库形态已 gitignore）
    $destDir = if ($CmdArgs.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($CmdArgs[0])) { [string]$CmdArgs[0] } else { Join-Path $Root 'backup' }
    $destFull = [System.IO.Path]::GetFullPath($destDir)
    if ($destFull.StartsWith($DataHome.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning '[backup] 目标目录不能在数据目录内（会把备份卷进备份）'
        exit 1
    }
    if (-not (Test-Path $destFull)) { New-Item -ItemType Directory -Force -Path $destFull | Out-Null }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $zipName = 'baihua-backup-' + $stamp + '.zip'
    $zipPath = Join-Path $destFull $zipName
    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ('bh-backup-stage-' + $stamp)
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Write-Host ('[backup] 数据目录: ' + $DataHome)
    Write-Host ('[backup] 暂存（排除 bin/logs/models/open-webui-venv）: ' + $stage)
    # robocopy：/XD 目录名任意层级匹配；exit code 0-7 = 成功（1=有文件复制），>=8 才是失败
    & robocopy $DataHome $stage /E /XD bin logs models open-webui-venv /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) {
        Write-Warning ('[backup] robocopy 失败（exit ' + $LASTEXITCODE + '），中止')
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        exit 1
    }
    $fileCount = @(Get-ChildItem $stage -Recurse -File -Force).Count
    if ($fileCount -eq 0) {
        Write-Warning '[backup] 数据目录为空（没有可备份的文件），中止'
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        exit 1
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Write-Host ('[backup] 压缩 -> ' + $zipPath)
    # 不用 ZipFile::CreateFromDirectory：它在 Windows 下用 '\' 做条目分隔符，
    # Linux 解包工具会把 'db\baihua.db' 当成一个文件名。手动建条目、统一 '/'。
    $zipFs = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
    $archive = New-Object System.IO.Compression.ZipArchive($zipFs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem $stage -Recurse -File -Force | ForEach-Object {
            $rel = $_.FullName.Substring($stage.Length).TrimStart('\') -replace '\\', '/'
            # CreateEntryFromFile 是扩展方法，PowerShell 里不能当实例方法调，走静态类
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
        $zipFs.Dispose()
    }
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

    $hash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash
    $shaPath = $zipPath + '.sha256'
    Set-Content -Path $shaPath -Value ($hash.ToLower() + '  ' + $zipName) -Encoding ASCII

    $sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host ('[backup] 完成: ' + $zipPath + '（' + $fileCount + ' 个文件，' + $sizeMb + ' MB）') -ForegroundColor Green
    Write-Host ('[backup] 校验和: ' + $shaPath)
    Write-Host '[backup] 恢复: bh restore <上面的 zip>'
}

function Invoke-Restore {
    param([string]$ZipPath, [switch]$AssumeYes)
    if ([string]::IsNullOrWhiteSpace($ZipPath)) {
        Write-Warning '[restore] 用法: bh restore <backup.zip> [-y]'
        exit 1
    }
    if (-not (Test-Path $ZipPath)) {
        Write-Warning ('[restore] 备份文件不存在: ' + $ZipPath)
        exit 1
    }
    if (-not (Assert-ServicesStoppedForDataOp)) { exit 1 }
    $ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path
    if ($ZipPath.StartsWith($DataHome.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        # zip 在数据目录里也能恢复（先解包到 staging 再改名，无递归风险），只提示一句
        Write-Host '[restore] 注意: 备份文件位于数据目录内，恢复完成后它会留在 .pre-restore-* 旧目录里'
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $stage = Join-Path $HOME ('.baihua-restore-' + $stamp)
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Write-Host ('[restore] 解包 ' + $ZipPath + ' -> ' + $stage)
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $stage)
    } catch {
        Write-Warning ('[restore] 解包失败: ' + $_.Exception.Message)
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        exit 1
    }
    # 容错：zip 里带一层根目录（手工打包常见）→ 下钻一层
    $src = $stage
    $top = @(Get-ChildItem $stage -Force)
    if ($top.Count -eq 1 -and $top[0].PSIsContainer) {
        $inner = Join-Path $stage $top[0].Name
        if (Test-Path (Join-Path $inner 'db')) { $src = $inner }
    }
    if (-not (Test-Path (Join-Path $src 'db\baihua.db'))) {
        Write-Warning ('[restore] 备份内容不像百花数据目录（缺 db\baihua.db）: ' + $src + ' —— 中止，未改动任何现有数据')
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        exit 1
    }

    if (-not $AssumeYes) {
        Write-Host ''
        Write-Host ('[restore] 目标数据目录: ' + $DataHome)
        Write-Host ('[restore] 备份来源:     ' + $ZipPath)
        if (Test-Path $DataHome) {
            Write-Host ('[restore] 当前数据将改名保留为: ' + $DataHome + '.pre-restore-' + $stamp + '（不会删除）')
        }
        $ans = Read-Host '[restore] 确认继续? (yes/no)'
        if ($ans -notin @('yes', 'y')) {
            Write-Host '[restore] 已取消'
            Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
            exit 1
        }
    }

    if (Test-Path $DataHome) {
        $old = $DataHome + '.pre-restore-' + $stamp
        Move-Item -LiteralPath $DataHome -Destination $old
        Write-Host ('[restore] 旧数据已保留: ' + $old)
    } else {
        New-Item -ItemType Directory -Force -Path $DataHome | Out-Null
    }
    Get-ChildItem $src -Force | Move-Item -Destination $DataHome
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ('[restore] 完成: ' + $DataHome) -ForegroundColor Green
    Write-Host '[restore] 下一步: bh start（若数据不对可回滚：把 .pre-restore-* 目录改名回原数据目录）'
}

# ---- doctor（本机体检）--------------------------------------------------------
# native 用户第一次跑、或 bh start 失败时，一条命令说清「哪一环断了 + 下一步敲什么」。
# 只读：不改服务、不装东西、不写环境变量（唯一写入是 $DataHome 下的一次性探针文件）。
function Add-DoctorCheck($list, [string]$name, [string]$status, [string]$detail, [string]$fix) {
    [void]$list.Add([pscustomobject]@{ name = $name; status = $status; detail = $detail; fix = $fix })
}

function Invoke-DoctorChecks {
    $checks = New-Object System.Collections.ArrayList

    # 1) .NET SDK 10 —— 开发仓库 publish 的前提（部署包 apps\ 为自包含产物，不需要 .NET）
    if ($IsPackageLayout) {
        Add-DoctorCheck $checks '.NET SDK' 'ok' '部署包形态（apps\ 自包含产物），无需 .NET' ''
    } elseif (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Add-DoctorCheck $checks '.NET SDK' 'fail' 'PATH 里没有 dotnet' 'winget install --id Microsoft.DotNet.SDK.10（bh build 也会自动装）'
    } else {
        $sdks = @(& dotnet --list-sdks 2>$null)
        $v10 = @($sdks | Where-Object { $_.Trim() -match '^10\.' })
        if ($v10.Count -eq 0) {
            $have = @($sdks | ForEach-Object { ($_.Trim() -split ' ')[0] }) -join ', '
            Add-DoctorCheck $checks '.NET SDK' 'fail' ('没有 10.x SDK（现有：' + $have + '）') 'winget install --id Microsoft.DotNet.SDK.10'
        } else {
            Add-DoctorCheck $checks '.NET SDK' 'ok' ($v10[-1].Trim()) ''
        }
    }

    # 2) 构建产物
    $missing = @()
    foreach ($svc in $Services) {
        $exe = Join-Path $OutDir "$($svc.Name)\$($svc.Exe)"
        if (-not (Test-Path $exe)) { $missing += $svc.Name }
    }
    $prodLabel = if ($IsPackageLayout) { '构建产物 apps\（部署包）' } else { '构建产物 out\native' }
    if ($missing.Count -gt 0) {
        Add-DoctorCheck $checks $prodLabel 'fail' ('缺少：' + ($missing -join ', ')) 'bh build（首次 publish 需要几分钟）'
    } else {
        Add-DoctorCheck $checks $prodLabel 'ok' 'server / webui 都在' ''
    }

    # 3) 产物完整性标签（Low 标签 ⇒ 进程能读不能写，写 %TEMP%/~/.baihua 全 Access denied）
    $low = @()
    foreach ($svc in $Services) {
        $dir = Join-Path $OutDir $svc.Name
        if ((Test-Path $dir) -and ((Get-IntegrityLabel $dir) -eq 'Low')) { $low += $svc.Name }
    }
    if ($low.Count -gt 0) {
        Add-DoctorCheck $checks '产物完整性标签' 'fail' ('带 Low 标签：' + ($low -join ', ') + '（能读不能写）') ('icacls "' + $OutDir + '" /setintegritylevel (OI)(CI)Medium')
    } else {
        Add-DoctorCheck $checks '产物完整性标签' 'ok' 'Medium' ''
    }

    # 4) SQLite 数据库（单库 baihua.db；首次启动自动建库，缺失不算致命）
    if (Test-Path $DbPath) {
        $dbSize = '{0:N1} MB' -f ((Get-Item $DbPath).Length / 1MB)
        Add-DoctorCheck $checks 'SQLite 数据库' 'ok' ($DbPath + '（' + $dbSize + '）') ''
    } else {
        Add-DoctorCheck $checks 'SQLite 数据库' 'warn' ('未找到 ' + $DbPath) '首次 bh start 会自动建库；从旧 PostgreSQL 迁移用 bh db-migrate（tools/db-migrate）'
    }

    # 5) 知识库根目录（vaults；服务按需创建，缺失只提示）
    $vaultsDir = Join-Path $DataHome 'vaults'
    if (Test-Path $vaultsDir) {
        $vaultCount = @(Get-ChildItem $vaultsDir -Directory -ErrorAction SilentlyContinue).Count
        Add-DoctorCheck $checks '知识库目录 vaults' 'ok' ($vaultsDir + '（' + $vaultCount + ' 个子目录）') ''
    } else {
        Add-DoctorCheck $checks '知识库目录 vaults' 'warn' ('未找到 ' + $vaultsDir) '首次同步知识库时会自动创建'
    }

    # 7) 8788 / 5177 端口占用
    foreach ($svc in $Services) {
        $label = '端口 ' + $svc.Port + '（' + $svc.Name + '）'
        if (-not (Test-PortOpen $svc.Port)) {
            Add-DoctorCheck $checks $label 'ok' '空闲' ''
            continue
        }
        $owner = Get-PortOwnerProcess $svc.Port
        $ownerName = if ($owner) { $owner.ProcessName } else { '?' }
        if ($ownerName -like 'bh-*') {
            Add-DoctorCheck $checks $label 'ok' ('已由 ' + $ownerName + ' 占用（服务在跑）') ''
        } else {
            $pidText = if ($owner) { 'pid ' + $owner.Id } else { 'pid 未知' }
            Add-DoctorCheck $checks $label 'warn' ('被外来进程 ' + $ownerName + '(' + $pidText + ') 占用') '先 bh stop 再重试；占位进程不是百花就停掉它（taskkill /PID <pid> /F）'
        }
    }

    # 8) OVMS（可选组件：没有它百花照样跑，只是本地 AI 功能降级）
    $ov = Get-OvmsState
    switch ($ov.Engine) {
        'wslc' {
            if ($ov.PortOpen) {
                Add-DoctorCheck $checks 'OVMS 本地模型' 'ok' ('RUNNING wslc:' + $ov.Container + ' 模型 ' + ($ov.Models -join ', ')) ''
            } else {
                Add-DoctorCheck $checks 'OVMS 本地模型' 'warn' ('容器 ' + $ov.Container + ' ' + $ov.ContainerState + '（:8000 未监听）') 'bh ovms start（不影响其它模块）'
            }
        }
        'service' {
            if ($ov.PortOpen) { Add-DoctorCheck $checks 'OVMS 本地模型' 'ok' ('RUNNING windows service ' + $OpenVinoServiceName) '' }
            else { Add-DoctorCheck $checks 'OVMS 本地模型' 'warn' ('Windows 服务 ' + $ov.ServiceStatus) 'Start-Service ' + $OpenVinoServiceName }
        }
        'process' {
            Add-DoctorCheck $checks 'OVMS 本地模型' 'warn' ('外部进程 pid ' + $ov.OwnerPid + ' 占着 :8000') 'bh 不管这个进程；模型可用与否看 /v1/models'
        }
        default {
            Add-DoctorCheck $checks 'OVMS 本地模型' 'warn' '未安装（:8000 无监听）' '参考 %BAIHUA_HOME%\ovms\README.md 用 wslc 起 GPU 容器；不装也能启动百花（AI 降级）'
        }
    }

    # AI 对话页：wslc 容器 bh-open-webui（:8080）
    if (-not (Get-WslcExe)) {
        Add-DoctorCheck $checks 'AI 对话页 open-webui' 'warn' '未找到 wslc.exe（需要 WSL >= 2.9.3）' 'wsl --update；AI 对话页可选，不影响核心功能'
    } else {
        $ow = Get-OpenWebUIStatus
        if ($ow.containerState -eq 'running' -and $ow.portOpen) {
            Add-DoctorCheck $checks 'AI 对话页 open-webui' 'ok' ('RUNNING :8080（容器 ' + $OpenWebUiContainerName + '）') ''
        } elseif ($ow.portOpen) {
            Add-DoctorCheck $checks 'AI 对话页 open-webui' 'warn' ':8080 被容器外的进程占用（旧 venv open-webui？）' 'scripts\start-open-webui.ps1 -Stop 停掉旧实例后 bh open-webui start'
        } elseif ($ow.containerState) {
            Add-DoctorCheck $checks 'AI 对话页 open-webui' 'warn' ('容器 ' + $OpenWebUiContainerName + ' ' + $ow.containerState + '（:8080 未监听）') 'bh open-webui start'
        } else {
            Add-DoctorCheck $checks 'AI 对话页 open-webui' 'warn' '容器未安装（/open-webui 页会空白）' 'bh open-webui install（需 WSL3，可选组件）'
        }
    }

    # 9) 数据目录可写（密钥、备份、日志都在这里）
    $probe = Join-Path $DataHome ('.doctor-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.tmp')
    try {
        New-Item -ItemType Directory -Force -Path $DataHome | Out-Null
        Set-Content -Path $probe -Value 'ok' -Encoding ASCII
        Remove-Item $probe -Force -ErrorAction SilentlyContinue
        Add-DoctorCheck $checks '数据目录可写' 'ok' $DataHome ''
    } catch {
        Add-DoctorCheck $checks '数据目录可写' 'fail' ($DataHome + ' 写入失败：' + $_.Exception.Message) '检查目录权限 / 磁盘空间'
    }

    # 10) bh 定位器（新终端里 bh 能不能直接用）
    $binDir = Join-Path $HOME '.local\bin'
    $locator = Join-Path $binDir 'bh.cmd'
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $onPath = @(($userPath -split ';') | Where-Object { $_ -and ($_.TrimEnd('\') -ieq $binDir) })
    if (-not (Test-Path $locator)) {
        Add-DoctorCheck $checks 'bh 定位器' 'warn' '未安装（新终端里没有 bh 命令）' '.\tools\bh\bh.ps1 install'
    } elseif ($onPath.Count -eq 0) {
        Add-DoctorCheck $checks 'bh 定位器' 'warn' ($binDir + ' 不在用户 PATH') '.\tools\bh\bh.ps1 install（幂等，会补 PATH）'
    } else {
        Add-DoctorCheck $checks 'bh 定位器' 'ok' ($binDir + ' 已在用户 PATH') ''
    }

    # 11) 防火墙（局域网/手机访问 8788 需要放行）
    $null = netsh advfirewall firewall show rule name='Baihua Server 8788' 2>$null
    if ($LASTEXITCODE -eq 0) {
        Add-DoctorCheck $checks '防火墙 TCP 8788' 'ok' '已放行（局域网入口）' ''
    } else {
        Add-DoctorCheck $checks '防火墙 TCP 8788' 'warn' '未放行（本机访问不受影响）' 'bh update 会自动放行；或 netsh advfirewall firewall add rule name="Baihua Server 8788" dir=in action=allow protocol=TCP localport=8788'
    }

    # 11.5) 局域网统一入口（宿主 :80 -> :8788 portproxy；浏览器/移动端默认 80 无显式端号）
    #   只认 "listenport=80 → connectaddress=127.0.0.1 connectport=8788" 的规则：
    #   k3s 时代会留下 :80 -> <WSL IP>:80 的规则，指向已消失的 IP —— 连接被接受但永不响应。
    $proxyOut = (netsh interface portproxy show v4tov4 2>$null | Out-String)
    $proxyRows = @($proxyOut -split "`r?`n" | Where-Object { $_ -match '^\s*\S+\s+80\s+\S+\s+\d+' })
    $goodProxy = @($proxyRows | Where-Object { $_ -match '127\.0\.0\.1\s+8788' })
    if ($goodProxy.Count -gt 0) {
        Add-DoctorCheck $checks '局域网入口 :80' 'ok' 'portproxy :80 -> 127.0.0.1:8788' ''
    } else {
        Add-DoctorCheck $checks '局域网入口 :80' 'warn' '未配置（本机访问不受影响；手机/局域网设备需要）' 'bh lan on（需要管理员授权）'
    }

    # 12) 一次性启动任务残留（bh start 用 schtasks 拉起服务；异常中断会留下 bh-start-*）
    $leftover = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'bh-start-*' })
    if ($leftover.Count -gt 0) {
        Add-DoctorCheck $checks '遗留启动任务' 'warn' (($leftover | ForEach-Object { $_.TaskName }) -join ', ') 'bh doctor --fix 删除（否则在任务计划里手点"运行"会起第二个 server）'
    } else {
        Add-DoctorCheck $checks '遗留启动任务' 'ok' '无' ''
    }

    # 13) 仓库状态（bh update 会被脏工作区拦住）
    if (Test-Path (Join-Path $Root '.git')) {
        $branch = (git -C $Root rev-parse --abbrev-ref HEAD 2>$null | Select-Object -First 1)
        $dirty = @(git -C $Root status --porcelain 2>$null)
        if ($dirty.Count -gt 0) {
            Add-DoctorCheck $checks '仓库状态' 'warn' ($branch + ' 有 ' + $dirty.Count + ' 个未提交改动') 'bh update（git pull）前先提交或 stash'
        } else {
            Add-DoctorCheck $checks '仓库状态' 'ok' ($branch + ' 干净') ''
        }
    }

    return ,$checks
}

function Show-Doctor {
    $checks = Invoke-DoctorChecks
    Write-Host 'bh doctor —— 本机体检（Windows native 形态，只读）' -ForegroundColor Cyan
    Write-Host ''
    foreach ($c in $checks) {
        $suffix = if ($c.detail) { ' — ' + $c.detail } else { '' }
        switch ($c.status) {
            'ok'   { Write-Host ('[ OK ] ' + $c.name + $suffix) -ForegroundColor Green }
            'warn' {
                Write-Host ('[WARN] ' + $c.name + $suffix) -ForegroundColor Yellow
                if ($c.fix) { Write-Host ('       → ' + $c.fix) -ForegroundColor DarkYellow }
            }
            default {
                Write-Host ('[FAIL] ' + $c.name + $suffix) -ForegroundColor Red
                if ($c.fix) { Write-Host ('       → ' + $c.fix) -ForegroundColor DarkYellow }
            }
        }
    }
    $ok = @($checks | Where-Object { $_.status -eq 'ok' }).Count
    $warn = @($checks | Where-Object { $_.status -eq 'warn' }).Count
    $fail = @($checks | Where-Object { $_.status -eq 'fail' }).Count
    Write-Host ''
    $color = if ($fail -gt 0) { 'Red' } elseif ($warn -gt 0) { 'Yellow' } else { 'Green' }
    Write-Host ('体检完成：' + $ok + ' 项通过 / ' + $warn + ' 项警告 / ' + $fail + ' 项失败') -ForegroundColor $color
    if ($fail -eq 0) { Write-Host '下一步：bh start → bh dashboard（或 bh doctor 再看一遍）' -ForegroundColor Green }
    return $fail
}

function Show-DoctorJson {
    param($Fixes = @())
    $checks = Invoke-DoctorChecks
    $ok = @($checks | Where-Object { $_.status -eq 'ok' }).Count
    $warn = @($checks | Where-Object { $_.status -eq 'warn' }).Count
    $fail = @($checks | Where-Object { $_.status -eq 'fail' }).Count
    $doc = [pscustomobject]@{
        cell = 'native'
        updatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        ok = ($fail -eq 0)
        summary = [pscustomobject]@{ passed = $ok; warned = $warn; failed = $fail }
        checks = @($checks)
    }
    if (@($Fixes).Count -gt 0) { $doc | Add-Member -NotePropertyName fixes -NotePropertyValue @($Fixes) }
    $doc | ConvertTo-Json -Depth 6
}

# ---- doctor --fix / 需要管理员的动作 -------------------------------------------
function Test-IsElevated {
    # 当前进程是否已是管理员（完整性标签/Startup 不需要，防火墙/服务需要）
    try {
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

# --fix 默认会弹 UAC 修需要管理员的项；脚本/无人值守场景用 --no-elevate 明确跳过（不弹窗）
$script:NoElevate = $false

function Invoke-ElevatedCmdFile([string[]]$Lines) {
    # 需要管理员的命令：先写成临时 .cmd 再 RunAs —— 避免 -Command 里的引号被 Start-Process 拆坏。
    # 会弹 UAC；用户取消返回 $false（预期分支，不是异常）。
    if ($script:NoElevate) {
        Write-Host ('[fix] --no-elevate：跳过需要管理员的操作 → ' + ($Lines | Select-Object -Last 1))
        return $false
    }
    $tmp = Join-Path $env:TEMP ('bh-elev-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.cmd')
    Set-Content -Path $tmp -Value $Lines -Encoding ASCII
    try {
        $proc = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $tmp) -Verb RunAs -PassThru -ErrorAction Stop
        $null = $proc.WaitForExit(120000)
        try { return ($proc.ExitCode -eq 0) } catch { return $false }
    } catch {
        Write-Warning '[fix] 提权被取消（需要管理员权限的操作已跳过）'
        return $false
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

# ---- doctor --fix -------------------------------------------------------------
# 只修「确定性、可回滚、不需要人判断」的项：完整性标签 / 防火墙 / lan 入口 /
# 定位器 / OVMS 容器 / open-webui 容器 / 构建产物。口令错误、数据目录权限这类只报不修。
function Invoke-DoctorFix {
    $actions = New-Object System.Collections.ArrayList

    # 1) 产物完整性标签（Low ⇒ 进程能读不能写；普通用户即可修）
    foreach ($svc in $Services) {
        $dir = Join-Path $OutDir $svc.Name
        if (-not (Test-Path $dir)) { continue }
        if ((Get-IntegrityLabel $dir) -ne 'Low') { continue }
        Write-Host ('[fix] 产物带 Low 完整性标签 → 改 Medium：' + $dir)
        & icacls $dir /setintegritylevel '(OI)(CI)Medium' 2>$null | Out-Null
        $ok = ((Get-IntegrityLabel $dir) -ne 'Low')
        $detail = ''
        if (-not $ok) { $detail = '仍失败：请在普通（非沙箱）终端执行 icacls' }
        [void]$actions.Add([pscustomobject]@{ action = '完整性标签 → Medium（' + $svc.Name + '）'; ok = $ok; detail = $detail })
        if ($ok) { Write-Host '[fix]   ok' -ForegroundColor Green } else { Write-Warning '[fix]   失败' }
    }

    # 2) 防火墙 TCP 8788（局域网/手机入口；需要管理员）
    $null = netsh advfirewall firewall show rule name='Baihua Server 8788' 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host '[fix] 防火墙未放行 TCP 8788 → 添加规则（可能弹 UAC）'
        $ok = Invoke-ElevatedCmdFile @('@echo off', 'netsh advfirewall firewall add rule name="Baihua Server 8788" dir=in action=allow protocol=TCP localport=8788')
        if ($ok) {
            $null = netsh advfirewall firewall show rule name='Baihua Server 8788' 2>$null
            $ok = ($LASTEXITCODE -eq 0)
        }
        $detail = ''
        if (-not $ok) { $detail = '需要管理员：netsh advfirewall firewall add rule name="Baihua Server 8788" dir=in action=allow protocol=TCP localport=8788' }
        [void]$actions.Add([pscustomobject]@{ action = '放行防火墙 TCP 8788'; ok = $ok; detail = $detail })
        if ($ok) { Write-Host '[fix]   ok' -ForegroundColor Green }
    }

    # 3) 局域网统一入口 :80 portproxy 未配置 → 重建（先清可能挂死的旧规则；需要管理员）
    $proxyOut = (netsh interface portproxy show v4tov4 2>$null | Out-String)
    $proxyRows = @($proxyOut -split "`r?`n" | Where-Object { $_ -match '^\s*\S+\s+80\s+\S+\s+\d+' })
    if (@($proxyRows | Where-Object { $_ -match '127\.0\.0\.1\s+8788' }).Count -eq 0) {
        Write-Host '[fix] 局域网入口 :80 未配置 → 添加 portproxy + 防火墙规则（可能弹 UAC）'
        $ok = Invoke-ElevatedCmdFile @(
            '@echo off',
            'netsh interface portproxy delete v4tov4 listenport=80 listenaddress=0.0.0.0 >nul 2>&1',
            'netsh interface portproxy add v4tov4 listenport=80 connectport=8788 connectaddress=127.0.0.1',
            'netsh advfirewall firewall add rule name="Baihua LAN 80" dir=in action=allow protocol=TCP localport=80'
        )
        if ($ok) {
            $proxy2 = (netsh interface portproxy show v4tov4 2>$null | Out-String)
            $ok = (@(($proxy2 -split "`r?`n") | Where-Object { $_ -match '127\.0\.0\.1\s+8788' }).Count -gt 0)
        }
        $detail = ''
        if (-not $ok) { $detail = '需要管理员：bh lan on（或手动 netsh interface portproxy add v4tov4 listenport=80 connectport=8788 connectaddress=127.0.0.1）' }
        [void]$actions.Add([pscustomobject]@{ action = '配置局域网入口 :80 -> 127.0.0.1:8788'; ok = $ok; detail = $detail })
        if ($ok) { Write-Host '[fix]   ok' -ForegroundColor Green }
    }

    # 4) bh 定位器（不在 PATH 时新终端里没有 bh 命令）
    $binDir = Join-Path $HOME '.local\bin'
    $locator = Join-Path $binDir 'bh.cmd'
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $onPath = @(($userPath -split ';') | Where-Object { $_ -and ($_.TrimEnd('\') -ieq $binDir) })
    if (-not (Test-Path $locator) -or $onPath.Count -eq 0) {
        $installer = Join-Path $Root 'tools\bh\bh.ps1'
        if (Test-Path $installer) {
            Write-Host '[fix] 定位器缺失或不在 PATH → 安装（bh install）'
            & $installer install | Out-Null
            $ok = (Test-Path $locator)
            [void]$actions.Add([pscustomobject]@{ action = '安装 bh 定位器并写入用户 PATH'; ok = $ok; detail = '' })
            if ($ok) { Write-Host '[fix]   ok（新开的终端才生效）' -ForegroundColor Green }
        }
    }

    # 5) OVMS 容器停着 → 拉起（只在容器已存在的前提下；没装交给 doctor 的 WARN）
    $ov = Get-OvmsState -SkipModels
    if ($ov.Engine -eq 'wslc' -and $ov.ContainerState -ne 'running') {
        Write-Host ('[fix] OVMS 容器 ' + $ov.Container + ' 停着 → 启动')
        Start-Ovms | Out-Null
        $ov2 = Get-OvmsState -SkipModels
        [void]$actions.Add([pscustomobject]@{ action = '启动 OVMS 容器 ' + $ov.Container; ok = $ov2.PortOpen; detail = '' })
    }

    # 6) AI 对话页容器已创建未运行 → 拉起（容器不存在交给 doctor 的 WARN，不自动拉镜像）
    if ((Get-WslcExe) -and -not (Test-PortOpen $OpenWebUiPort)) {
        $owState = Get-OpenWebUiContainerState
        if ($owState -and $owState -ne 'running') {
            Write-Host '[fix] open-webui 容器未运行 → 启动'
            Start-OpenWebUi
            [void]$actions.Add([pscustomobject]@{ action = '启动 open-webui 容器'; ok = (Test-PortOpen $OpenWebUiPort); detail = '' })
        }
    }

    # 7) 清理遗留的一次性启动任务
    $leftover = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'bh-start-*' })
    foreach ($t in $leftover) {
        Write-Host ('[fix] 删除遗留计划任务 ' + $t.TaskName)
        $ok = $true
        try { Unregister-ScheduledTask -TaskName $t.TaskName -Confirm:$false -ErrorAction Stop } catch { $ok = $false }
        $detail = ''
        if (-not $ok) { $detail = '需要管理员：Unregister-ScheduledTask -TaskName ' + $t.TaskName }
        [void]$actions.Add([pscustomobject]@{ action = '删除遗留启动任务 ' + $t.TaskName; ok = $ok; detail = $detail })
    }

    # 8) 构建产物缺失 → bh build（最慢，放最后；部署包形态不能 build，只提示）
    $missing = @()
    foreach ($svc in $Services) {
        $exe = Join-Path $OutDir ($svc.Name + '\' + $svc.Exe)
        if (-not (Test-Path $exe)) { $missing += $svc.Name }
    }
    if ($missing.Count -gt 0) {
        if ($IsPackageLayout) {
            Write-Warning ('[fix] 部署包产物缺失（apps\：' + ($missing -join ', ') + '）：请重新下载部署包并解压')
            [void]$actions.Add([pscustomobject]@{ action = '构建产物'; ok = $false; detail = '部署包形态不能 bh build；请重新下载部署包' })
        } else {
            Write-Host ('[fix] 缺少构建产物（' + ($missing -join ', ') + '）→ 执行 bh build（首次要几分钟）')
            # build 会先停掉在跑的服务（否则 exe 被占用）；记下来，构建成功后原样拉回去
            $wereUp = @()
            foreach ($svc in $Services) { if (Test-PortOpen $svc.Port) { $wereUp += $svc.Name } }
            try {
                Invoke-Build
                $ok = $true
                foreach ($svc in $Services) {
                    if (-not (Test-Path (Join-Path $OutDir ($svc.Name + '\' + $svc.Exe)))) { $ok = $false }
                }
                [void]$actions.Add([pscustomobject]@{ action = '构建产物（dotnet publish）'; ok = $ok; detail = '' })
                if ($ok -and $wereUp.Count -gt 0) {
                    Write-Host ('[fix] 构建前在跑的服务（' + ($wereUp -join ', ') + '）→ 重新拉起')
                    Start-Services
                }
            } catch {
                [void]$actions.Add([pscustomobject]@{ action = '构建产物（dotnet publish）'; ok = $false; detail = $_.Exception.Message })
            }
        }
    }

    return ,$actions
}

# ---- 登录自启（用户级 Startup 目录：不需要管理员，也不碰系统服务）----------------
$StartupDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
$AutostartFile = Join-Path $StartupDir 'baihua-native.cmd'
$OvmsStartupFile = Join-Path $StartupDir 'ovms-wslc.cmd'

function Get-BhShimPath {
    # 优先已安装的定位器（仓库搬家/改名都不失效），否则用仓库内入口
    $loc = Join-Path $HOME '.local\bin\bh.cmd'
    if (Test-Path $loc) { return $loc }
    return (Join-Path $Root 'tools\bh\bh.cmd')
}

function Show-Autostart {
    $lnk = Join-Path $StartupDir 'baihua-native.lnk'
    $cmdFile = Join-Path $StartupDir 'baihua-native.cmd'
    Write-Host '[autostart] 百花（native）登录自启（用户级 Startup 目录，不需要管理员）'
    if (Test-Path $lnk) {
        Write-Host '[autostart] 状态: 已启用（无窗口：.lnk → bh-launch.exe）' -ForegroundColor Green
        Write-Host ('[autostart] 文件: ' + $lnk)
    } elseif (Test-Path $cmdFile) {
        Write-Host '[autostart] 状态: 已启用（旧式 .cmd，登录时会闪一个最小化控制台）' -ForegroundColor Yellow
        Write-Host ('[autostart] 文件: ' + $cmdFile + '  ← 重新执行 bh autostart on 可换成无窗口的 .lnk')
    } else {
        Write-Host '[autostart] 状态: 未启用' -ForegroundColor DarkGray
        Write-Host '[autostart] 开启: bh autostart on'
    }
    Write-Host '[autostart] 动作: 登录时执行 bh start（幂等：已在跑的服务不会重复起）'
    $ps1Shim = if (Test-Path (Join-Path $HOME '.local\bin\bh.ps1')) { Join-Path $HOME '.local\bin\bh.ps1' } else { Join-Path $Root 'tools\bh\bh.ps1' }
    Write-Host ('[autostart] 入口: ' + $ps1Shim)
    if (Test-Path (Join-Path $HOME '.local\bin\bh.cmd')) {
        Write-Host ('[autostart] 入口: ' + (Join-Path $HOME '.local\bin\bh.cmd') + '（已安装定位器，仓库搬家不影响）')
    } else {
        Write-Host ('[autostart] 入口: ' + (Join-Path $Root 'tools\bh\bh.cmd') + '（仓库内入口；仓库搬家后需重新 bh autostart on）')
    }
    if (Test-Path $OvmsStartupFile) {
        Write-Host '[autostart] OVMS: 有自己的自启项 ovms-wslc.cmd（模型服务独立于百花本体）'
    } else {
        Write-Host '[autostart] OVMS: 没有开机自启项（需要时参考 %BAIHUA_HOME%\ovms\README.md）'
    }
    Write-Host '[autostart] 说明: 只启动已构建的产物（仓库 out\native / 部署包 apps\）；仓库形态改了代码要先 bh build；日志见 bh logs server|webui'
}

function Get-BhPs1ShimPath {
    # 自启走 powershell -File <ps1>：优先已安装的定位器（仓库搬家不失效），否则仓库入口
    $loc = Join-Path $HOME '.local\bin\bh.ps1'
    if (Test-Path $loc) { return $loc }
    return (Join-Path $Root 'tools\bh\bh.ps1')
}

function Enable-Autostart {
    if (-not (Test-Path $StartupDir)) { New-Item -ItemType Directory -Force -Path $StartupDir | Out-Null }
    $launcher = Get-BhLauncherExe
    $ps1Shim = Get-BhPs1ShimPath
    if ($launcher) {
        # .lnk → bh-launch.exe（GUI 子系统，无控制台）→ powershell -File bh.ps1 start
        # 必须用 .lnk 而不是 .cmd：Explorer 跑 .cmd 一定会分配一个控制台窗口（start /min 只是最小化）
        $lnk = Join-Path $StartupDir 'baihua-native.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($lnk)
        $sc.TargetPath = $launcher
        $sc.Arguments = '--ps1 "' + $ps1Shim + '" start'
        $sc.WorkingDirectory = Split-Path -Parent $ps1Shim
        $sc.WindowStyle = 7
        $sc.Description = 'baihua dev autostart (silent)'
        $sc.Save()
        Remove-Item (Join-Path $StartupDir 'baihua-native.cmd') -Force -ErrorAction SilentlyContinue
        Write-Host ('[autostart] 已启用: ' + $lnk) -ForegroundColor Green
        Write-Host '[autostart] 走 bh-launch.exe（GUI 子系统，无控制台）：登录时不会弹黑框'
    } else {
        $shim = Get-BhShimPath
        $lines = @(
            '@echo off',
            'rem baihua native autostart - generated by: bh autostart on',
            'rem action: bh start (idempotent). disable with: bh autostart off',
            ('start "baihua" /min "' + $shim + '" start')
        )
        Set-Content -Path (Join-Path $StartupDir 'baihua-native.cmd') -Value $lines -Encoding ASCII
        Write-Warning '[autostart] 没能生成 bh-launch.exe，退回 .cmd（登录时会有个最小化控制台窗口）'
    }
    if (-not (Test-Path (Join-Path $HOME '.local\bin\bh.ps1'))) {
        Write-Warning '[autostart] 用的是仓库内入口；建议先 bh install（装定位器），仓库搬家后才不用重开自启'
    }
    Write-Host '[autostart] 下次登录生效（现在想看效果：bh start）'
}

function Disable-Autostart {
    $removed = @()
    foreach ($f in @('baihua-native.lnk', 'baihua-native.cmd')) {
        $path = Join-Path $StartupDir $f
        if (Test-Path $path) { Remove-Item $path -Force -ErrorAction SilentlyContinue; $removed += $f }
    }
    if ($removed.Count -gt 0) {
        Write-Host ('[autostart] 已关闭（删除 ' + ($removed -join ', ') + '）') -ForegroundColor Green
    } else {
        Write-Host '[autostart] 本来就是关闭状态' -ForegroundColor DarkGray
    }
}

switch ($Command.ToLower()) {
    'build'     { Invoke-Build (Get-ServiceArgs) }
    'build-restart' {
        $names = Get-ServiceArgs
        # open-webui / openvino 是本仓库之外的东西（wslc 容器 / 外部 OVMS），没有编译目标，
        # 明确提示而不是走 Resolve-ServiceList 打一句 unknown 就静默退出。
        foreach ($s in Get-SpecialList $names) {
            $why = if ($s -eq 'open-webui') { 'wslc 容器，非本仓库构建' } else { '外部 OVMS，非本仓库构建' }
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
                    else { Update-RealPid $svc; Write-Host "[$($svc.Name)] ready on $($svc.Port)" }
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
    'backup'    { Invoke-Backup (Get-ServiceArgs) }
    'restore'   {
        # 用法: bh restore <backup.zip> [-y|--yes]；-y 跳过确认（脚本/CI 用）
        $rArgs = @(Get-ServiceArgs)
        $zip = ''
        $assumeYes = $false
        foreach ($a in $rArgs) {
            if ([string]::IsNullOrWhiteSpace($a)) { continue }
            if ($a -in @('-y', '--yes')) { $assumeYes = $true }
            elseif (-not $zip) { $zip = $a }
            else { Write-Warning ("[restore] 忽略多余参数: $a") }
        }
        Invoke-Restore $zip -AssumeYes:$assumeYes
    }
    'doctor'    {
        # --fix 先自动修「可确定修的项」，再照常体检（体检失败 => 退出码 1，便于脚本/CI 判断）
        $docArgs = @(Get-ServiceArgs)
        $asJson = ($docArgs -contains '--json')
        $doFix = ($docArgs -contains '--fix')
        $script:NoElevate = ($docArgs -contains '--no-elevate') -or ($docArgs -contains '--no-uac')
        $fixes = @()
        if ($doFix) {
            if ($asJson) {
                # --json 时 stdout 必须是纯 JSON：Write-Host/Write-Warning 走 info/warning 流（6/3），
                # 调用方 `*>&1` 会把它们混进来，直接丢弃（修复结果在 JSON 的 fixes 字段里）
                $fixes = Invoke-DoctorFix 3>$null 6>$null
            } else {
                $fixes = Invoke-DoctorFix
                Write-Host ''
            }
        }
        if ($asJson) { Show-DoctorJson -Fixes $fixes }
        else {
            $failCount = Show-Doctor
            if ($failCount -gt 0) { exit 1 }
        }
    }
    'launcher-path' {
        # 内部用：确保 bh-launch.exe 存在并打印路径（wslc 的 autostart 也复用它）
        $lp = Get-BhLauncherExe
        if ($lp) { Write-Output $lp; exit 0 }
        exit 1
    }
    'autostart' {
        # 用户级 Startup 目录（不需要管理员）；on=写 baihua-native.cmd，登录时跑 bh start
        switch ($Arg1.ToLower()) {
            'on'      { Enable-Autostart }
            'enable'  { Enable-Autostart }
            'off'     { Disable-Autostart }
            'disable' { Disable-Autostart }
            default   { Show-Autostart }
        }
    }
    'logs'      {
        # 支持: bh logs server 100 / bh logs server -f（跟随）/ bh logs server -e（stderr 日志）
        $svcName = ''; $count = 50; $follow = $false; $errLog = $false
        foreach ($a in @(Get-ServiceArgs)) {
            if ([string]::IsNullOrWhiteSpace($a)) { continue }
            switch -Regex ($a) {
                '^(-f|--follow)$'       { $follow = $true }
                '^(-e|--err|--stderr)$' { $errLog = $true }
                '^\d+$'                 { $count = [int]$a }
                default {
                    if (-not $svcName) { $svcName = $a.ToLower() }
                    else { Write-Warning ("[logs] 忽略多余参数: $a") }
                }
            }
        }
        if (-not $svcName) { $svcName = 'server' }
        Show-Logs $svcName $count -Follow:$follow -ErrLog:$errLog
    }
    'ovms'      { Invoke-OvmsCommand }
    'openvino'  { Invoke-OvmsCommand }
    'dashboard' { Open-Dashboard }
    'open'      { Start-Process 'http://127.0.0.1:5177' }
    'open-webui' { Invoke-OpenWebUI $Arg1 }
    'help'      { Help-Text }
    default     { Help-Text }
}

exit 0