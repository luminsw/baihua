#Requires -Version 5.1
<#
  baihua - Windows + dotnet native CLI
  Manages 2 .NET services (server/webui) as local processes.

  Usage: bh <command> [args]（Windows native 默认形态；不带参数 = bh status）
    build [svc...]      dotnet publish to out/native/（可指定服务，默认全部 2 个）
    build-restart [svc...]  build + restart（编译后立即重启，可指定服务，默认全部）
    start [svc...]      start services（可指定服务，默认全部，按依赖顺序 server→webui）
    stop [svc...]       stop services（可指定服务，默认全部，逆依赖顺序）
    restart [svc...]    stop + start 指定服务（默认全部）
    update              git pull 最新代码 + 重建 + 重启（局域网机器一键升级）
    status [--json]     状态总览（端口/进程/OVMS 形态/访问地址）；--json 供 DSH 桥插件
    doctor [--json] [--fix]  本机体检：.NET / PG 登录 / 产物 / 端口 / OVMS（--json 给工具，--fix 自动修可修的）
    autostart [on|off|status] 登录自启（用户级 Startup，执行 bh start；不需要管理员）
    logs <svc> [n]      tail 日志（默认 50 行；-f 跟随，-e 看 stderr）
    ovms [status|start|stop|restart|logs [n]]
                        本地模型服务 OVMS(:8000)：自动识别 wslc 容器 / Windows 服务 / 外部进程
    dashboard           open browser with cli-token auto-login
    open                open browser to http://localhost:5177
    open-webui start|stop|status   manage Open WebUI (Python venv, port 8080)
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
    Get-Content $PSCommandPath | Select-Object -First 30 | Where-Object { $_ -match '^\s{4}[a-z]' } | ForEach-Object { $_.Trim() }
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

# 容器状态：running / exited / created / paused / dead；'' = 没有这个容器
# 不用 `wslc list --format json`：它把 Command 字段截断成非法 JSON（含孤立代理字符），
# ConvertFrom-Json 会直接抛错；inspect 的输出是逐行 JSON 文本，用正则取状态最稳（且不受控制台代码页影响）。
function Get-OvmsContainerState {
    if (-not (Get-WslcExe)) { return '' }
    $out = (& wslc container inspect $OvmsContainerName 2>$null | Out-String)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($out)) { return '' }
    if ($out -match '"Status"\s*:\s*"([a-zA-Z]+)"') { return $matches[1].ToLower() }
    if ($out -match '"Running"\s*:\s*true') { return 'running' }
    if ($out -match '"Running"\s*:\s*false') { return 'exited' }
    return ''
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

    # 静默外壳：任务动作改成 `powershell -WindowStyle Hidden -File start-<svc>.ps1`。
    #  · 为什么还留 cmd 这一层：env / chcp 65001 / icacls / `>>` 追加日志都在 .cmd 里，原样不动；
    #  · 为什么要这层外壳：计划任务在用户会话里直接起控制台程序会分配一个**可见**控制台窗口，
    #    而 cmd 同步等 exe，窗口就挂到服务生命周期结束（两个服务 = 两个黑框，2026-11-15 用户反馈）；
    #  · 为什么外壳必须**同步等** cmd 结束：任务实例要继续持有服务进程，否则任务结束会把它连坐杀掉
    #    （2026-11-04 实测）。隐藏窗口不影响这条语义；
    #  · 为什么用 PowerShell 而不是 wscript+VBS：不引入 VBScript 依赖（微软已把 VBScript 列入弃用路线）。
    #    代价是控制台先创建再隐藏，可能有 0.1~0.3s 的闪烁（比挂一整张窗口好得多）。
    $startPs1 = Join-Path $LogDir "start-$($svc.Name).ps1"
    $psLines = @(
        '# generated by bh start - silent launcher (hidden window; waits for the .cmd so the task keeps owning the service)',
        '$ErrorActionPreference = ''Continue''',
        ('$cmd = Join-Path $PSScriptRoot ''' + "start-$($svc.Name).cmd" + ''''),
        '& cmd.exe /c $cmd'
    )
    Set-Content -Path $startPs1 -Value $psLines -Encoding ASCII
    # 清理早期版本生成的 VBS 启动器（升级后不留垃圾）
    Remove-Item (Join-Path $LogDir "start-$($svc.Name).vbs") -Force -ErrorAction SilentlyContinue
    $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $taskAction = if (Test-Path $powershellExe) {
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $startPs1 + '"'
    } else {
        $startCmd
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
    # 轻量前置检查：PostgreSQL 不可达 / 口令没设时先给结论，别让用户干等 60s 只看到「端口没起来」
    $pgHost = if ($env:PG_HOST) { $env:PG_HOST } else { '127.0.0.1' }
    if ($pgHost -in @('127.0.0.1', 'localhost')) {
        if (-not (Test-PortOpen 5432)) {
            Write-Warning "[start] PostgreSQL 未监听 :5432 —— server 大概率起不来（bh doctor 体检 / bh logs server 看原因）"
        } elseif (-not $env:PG_PASSWORD) {
            Write-Warning "[start] 环境变量 PG_PASSWORD 未设置，口令认证会失败（setx PG_PASSWORD `"<口令>`" 后重开终端）"
        }
    }
    foreach ($svc in $Services) { Start-One $svc }
    Write-Host "[start] waiting for health ..."
    $ok = $true
    foreach ($svc in $Services) {
        if (-not (Wait-Port $svc.Port 60)) { Write-Warning "[$($svc.Name)] port $($svc.Port) not ready in 60s"; $ok = $false }
        else { Update-RealPid $svc; Write-Host "[$($svc.Name)] ready on $($svc.Port)" }
    }
    if ($ok) { Write-Host "[start] all services up. WebUI: http://localhost:5177" }
    # AI 对话页：Baihua.Web 的 /open-webui 是个 iframe，后端指向 :8080（open-webui）
    # —— 没装就不管（只提示），装了没跑就后台拉起（首启要 1-2 分钟，不阻塞 bh start）
    $owuiExe = Join-Path $DataHome 'open-webui-venv\Scripts\open-webui.exe'
    $owuiLauncher = Join-Path $Root 'scripts\start-open-webui.ps1'
    if (-not (Test-Path $owuiExe)) {
        Write-Host '[start] open-webui 未安装：AI 对话页（/open-webui）会空白（需要时 bh open-webui install）'
    } elseif (Test-PortOpen 8080) {
        Write-Host '[start] open-webui 已在运行（:8080）'
    } elseif (Test-OpenWebUIStarting) {
        # 注意顺序：-Detached 实例会一直 hold 着服务，tasks 状态常态就是 Running ——
        # 所以必须先看端口，再看「是否启动中」，否则在跑的服务会被误判成启动中
        Write-Host '[start] open-webui 正在启动中（后台），稍后用 bh open-webui status 确认'
    } elseif (Test-Path $owuiLauncher) {
        Write-Host '[start] open-webui（AI 对话页）未运行 → 后台启动（首启 1-2 分钟，bh open-webui status 查看）'
        & $owuiLauncher -Async
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
# out/native 下的 dotnet 进程）：open-webui = Python venv（:8080，scripts/start-open-webui.ps1），
# openvino = OVMS（:8000，装了 Windows 服务 ovms 才有启停手段，否则只是普通进程）。
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
    $owuiState = if ($owui.portOpen) { 'RUNNING' } else { 'stopped' }
    $owuiColor = if ($owui.portOpen) { 'Green' } else { 'DarkGray' }
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

function Test-OpenWebUIStarting {
    # 已经在启动途中就别再触发一次：计划任务是以「-Detached 实例 wait 服务进程」的方式持活的，
    # `Register-ScheduledTask -Force` 会把正在启动的实例打断，等于永远启动不完（实测会卡住调用方）。
    $task = Get-ScheduledTask -TaskName 'baihua-open-webui-start' -ErrorAction SilentlyContinue
    if ($task -and $task.State.ToString() -eq 'Running') { return $true }
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*start-open-webui.ps1*' })
    return ($procs.Count -gt 0)
}

# ---- doctor（本机体检）--------------------------------------------------------
# native 用户第一次跑、或 bh start 失败时，一条命令说清「哪一环断了 + 下一步敲什么」。
# 只读：不改服务、不装东西、不写环境变量（唯一写入是 $DataHome 下的一次性探针文件）。
function Add-DoctorCheck($list, [string]$name, [string]$status, [string]$detail, [string]$fix) {
    [void]$list.Add([pscustomobject]@{ name = $name; status = $status; detail = $detail; fix = $fix })
}

function Invoke-DoctorChecks {
    $checks = New-Object System.Collections.ArrayList
    $pgHost = if ($env:PG_HOST) { $env:PG_HOST } else { '127.0.0.1' }
    $pgUser = if ($env:PG_USER) { $env:PG_USER } else { 'postgres' }
    $pgDb = if ($env:PG_DATABASE) { $env:PG_DATABASE } else { 'baihua' }
    $isLocalPg = $pgHost -in @('127.0.0.1', 'localhost')

    # 1) .NET SDK 10 —— publish/构建的前提
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
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
    if ($missing.Count -gt 0) {
        Add-DoctorCheck $checks '构建产物 out\native' 'fail' ('缺少：' + ($missing -join ', ')) 'bh build（首次 publish 需要几分钟）'
    } else {
        Add-DoctorCheck $checks '构建产物 out\native' 'ok' 'server / webui 都在' ''
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

    # 4) PostgreSQL 服务
    $pgSvcs = @(Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue)
    $pgUp = @($pgSvcs | Where-Object { $_.Status -eq 'Running' })
    if ($pgUp.Count -gt 0) {
        Add-DoctorCheck $checks 'PostgreSQL 服务' 'ok' (($pgUp | ForEach-Object { "$($_.Name) $($_.Status)" }) -join ', ') ''
    } elseif ($pgSvcs.Count -gt 0) {
        Add-DoctorCheck $checks 'PostgreSQL 服务' 'fail' (($pgSvcs | ForEach-Object { "$($_.Name) $($_.Status)" }) -join ', ') ('Start-Service ' + $pgSvcs[0].Name + '（或 services.msc 手动启动）')
    } else {
        Add-DoctorCheck $checks 'PostgreSQL 服务' 'warn' '未检测到 postgresql* Windows 服务' 'PG 在别的机器就忽略；否则装 PostgreSQL 18 并建库 baihua'
    }

    # 5) PostgreSQL 端口
    if ($isLocalPg) {
        if (Test-PortOpen 5432) {
            Add-DoctorCheck $checks ('PostgreSQL 端口 ' + $pgHost + ':5432') 'ok' '可连接' ''
        } else {
            Add-DoctorCheck $checks ('PostgreSQL 端口 ' + $pgHost + ':5432') 'fail' '端口未监听' '先启动 PostgreSQL 服务；或把 PG_HOST 指到正确地址'
        }
    } else {
        Add-DoctorCheck $checks 'PostgreSQL 端口' 'warn' ('PG_HOST=' + $pgHost + ' 是远端，未探测') ''
    }

    # 6) PostgreSQL 登录（用 psql -w：绝不弹口令提示，避免体检卡住）
    $psql = ''
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($base -and (Test-Path $base) -and -not $psql) {
            $hit = Get-ChildItem (Join-Path $base 'PostgreSQL\*\bin\psql.exe') -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
            if ($hit) { $psql = $hit.FullName }
        }
    }
    if (-not $psql) {
        $c = Get-Command psql -ErrorAction SilentlyContinue
        if ($c) { $psql = $c.Source }
    }
    if (-not $isLocalPg -or -not (Test-PortOpen 5432)) {
        Add-DoctorCheck $checks 'PostgreSQL 登录' 'warn' '跳过（PG 不在本机或端口未监听）' ''
    } elseif (-not $psql) {
        Add-DoctorCheck $checks 'PostgreSQL 登录' 'warn' '没找到 psql.exe，跳过口令校验' '确认 PG_PASSWORD 与 PostgreSQL 用户口令一致（setx PG_PASSWORD "<口令>"）'
    } else {
        $prevPw = $env:PGPASSWORD
        $env:PGPASSWORD = $env:PG_PASSWORD
        try {
            $out = & $psql -h $pgHost -p 5432 -U $pgUser -d $pgDb -w -tAc 'select 1' 2>&1 | Out-String
            $code = $LASTEXITCODE
        } finally { $env:PGPASSWORD = $prevPw }
        if ($code -eq 0 -and $out.Trim() -match '^1$') {
            Add-DoctorCheck $checks 'PostgreSQL 登录' 'ok' ($pgUser + '@' + $pgHost + '/' + $pgDb + ' 可登录') ''
        } elseif ($out -match 'does not exist') {
            Add-DoctorCheck $checks 'PostgreSQL 登录' 'fail' ('库 ' + $pgDb + ' 不存在') ('pwsh scripts/init-pg.ps1（或 psql -U ' + $pgUser + ' -c "CREATE DATABASE ' + $pgDb + '"）')
        } else {
            $line = ($out -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
            Add-DoctorCheck $checks 'PostgreSQL 登录' 'fail' ($pgUser + '@' + $pgHost + '/' + $pgDb + ' 登录失败：' + $line) '核对 PG_PASSWORD / PG_USER；设好口令后重开终端（setx PG_PASSWORD "<口令>"）'
        }
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

    # AI 对话页依赖：Baihua.Web 的 /open-webui 是 iframe，后端是 open-webui(:8080)
    $owuiExe = Join-Path $DataHome 'open-webui-venv\Scripts\open-webui.exe'
    if (-not (Test-Path $owuiExe)) {
        Add-DoctorCheck $checks 'AI 对话页 open-webui' 'warn' '未安装（/open-webui 页会空白）' 'bh open-webui install（Python venv，几分钟）'
    } elseif (Test-PortOpen 8080) {
        Add-DoctorCheck $checks 'AI 对话页 open-webui' 'ok' 'RUNNING :8080' ''
    } elseif (Test-OpenWebUIStarting) {
        Add-DoctorCheck $checks 'AI 对话页 open-webui' 'warn' '正在启动中（首启 1-2 分钟）' '稍后 bh open-webui status 确认'
    } else {
        Add-DoctorCheck $checks 'AI 对话页 open-webui' 'warn' '已安装但没在运行（/open-webui 页会空白）' 'bh open-webui start（首启 1-2 分钟）'
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

function Get-PsqlExe {
    $psql = ''
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($base -and (Test-Path $base) -and -not $psql) {
            $hit = Get-ChildItem (Join-Path $base 'PostgreSQL\*\bin\psql.exe') -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
            if ($hit) { $psql = $hit.FullName }
        }
    }
    if (-not $psql) {
        $c = Get-Command psql -ErrorAction SilentlyContinue
        if ($c) { $psql = $c.Source }
    }
    return $psql
}

# ---- doctor --fix -------------------------------------------------------------
# 只修「确定性、可回滚、不需要人判断」的项：完整性标签 / PG 服务 / 缺库 / 防火墙 /
# 定位器 / OVMS 容器 / 构建产物。口令错误、数据目录权限这类只报不修。
function Invoke-DoctorFix {
    $actions = New-Object System.Collections.ArrayList
    $pgHost = if ($env:PG_HOST) { $env:PG_HOST } else { '127.0.0.1' }
    $pgUser = if ($env:PG_USER) { $env:PG_USER } else { 'postgres' }
    $pgDb = if ($env:PG_DATABASE) { $env:PG_DATABASE } else { 'baihua' }
    $isLocalPg = $pgHost -in @('127.0.0.1', 'localhost')

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

    # 2) PostgreSQL 服务没运行 → 启动（必要时提权）
    $stoppedPg = @(Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue | Where-Object { $_.Status -ne 'Running' })
    foreach ($pgSvc in $stoppedPg) {
        Write-Host ('[fix] PostgreSQL 服务未运行 → 启动 ' + $pgSvc.Name)
        $ok = $false
        try { Start-Service -Name $pgSvc.Name -ErrorAction Stop; $ok = $true } catch { $ok = $false }
        if (-not $ok) { $ok = Invoke-ElevatedCmdFile @('@echo off', 'net start ' + $pgSvc.Name) }
        if (-not $ok) {
            $s = Get-Service -Name $pgSvc.Name -ErrorAction SilentlyContinue
            if ($s -and $s.Status -eq 'Running') { $ok = $true }
        }
        $detail = ''
        if (-not $ok) { $detail = '需要管理员：以管理员运行 net start ' + $pgSvc.Name }
        [void]$actions.Add([pscustomobject]@{ action = '启动 PostgreSQL 服务 ' + $pgSvc.Name; ok = $ok; detail = $detail })
        if ($ok) { Write-Host '[fix]   ok' -ForegroundColor Green }
    }

    # 3) 库不存在 → 建库（口令错就不动，交给 doctor 报）
    $psql = Get-PsqlExe
    if ($psql -and $isLocalPg -and (Test-PortOpen 5432)) {
        $prevPw = $env:PGPASSWORD
        $env:PGPASSWORD = $env:PG_PASSWORD
        try {
            $probe = & $psql -h $pgHost -p 5432 -U $pgUser -d $pgDb -w -tAc 'select 1' 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0 -and $probe -match 'does not exist') {
                Write-Host ('[fix] 库 ' + $pgDb + ' 不存在 → 创建')
                $null = & $psql -h $pgHost -p 5432 -U $pgUser -d postgres -w -tAc ('CREATE DATABASE "' + $pgDb + '"') 2>&1
                $ok = ($LASTEXITCODE -eq 0)
                $detail = ''
                if (-not $ok) { $detail = '建库失败：确认 PG_PASSWORD 有建库权限，或手动 psql -U ' + $pgUser + ' -d postgres 建库' }
                [void]$actions.Add([pscustomobject]@{ action = '创建数据库 ' + $pgDb; ok = $ok; detail = $detail })
                if ($ok) { Write-Host '[fix]   ok' -ForegroundColor Green }
            }
        } finally { $env:PGPASSWORD = $prevPw }
    }

    # 4) 防火墙 TCP 8788（局域网/手机入口；需要管理员）
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

    # 5) bh 定位器（不在 PATH 时新终端里没有 bh 命令）
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

    # 6) OVMS 容器停着 → 拉起（只在容器已存在的前提下；没装交给 doctor 的 WARN）
    $ov = Get-OvmsState -SkipModels
    if ($ov.Engine -eq 'wslc' -and $ov.ContainerState -ne 'running') {
        Write-Host ('[fix] OVMS 容器 ' + $ov.Container + ' 停着 → 启动')
        Start-Ovms | Out-Null
        $ov2 = Get-OvmsState -SkipModels
        [void]$actions.Add([pscustomobject]@{ action = '启动 OVMS 容器 ' + $ov.Container; ok = $ov2.PortOpen; detail = '' })
    }

    # 7) AI 对话页（open-webui）装了没跑 → 后台拉起（不阻塞，首启慢）
    $owuiExe = Join-Path $DataHome 'open-webui-venv\Scripts\open-webui.exe'
    $owuiLauncher = Join-Path $Root 'scripts\start-open-webui.ps1'
    if ((Test-Path $owuiExe) -and -not (Test-PortOpen 8080) -and -not (Test-OpenWebUIStarting) -and (Test-Path $owuiLauncher)) {
        Write-Host '[fix] open-webui（AI 对话页）没在运行 → 后台启动'
        & $owuiLauncher -Async
        [void]$actions.Add([pscustomobject]@{ action = '启动 open-webui（AI 对话页）'; ok = $true; detail = '后台启动中，1-2 分钟后 bh open-webui status 确认' })
    }

    # 8) 清理遗留的一次性启动任务
    $leftover = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'bh-start-*' })
    foreach ($t in $leftover) {
        Write-Host ('[fix] 删除遗留计划任务 ' + $t.TaskName)
        $ok = $true
        try { Unregister-ScheduledTask -TaskName $t.TaskName -Confirm:$false -ErrorAction Stop } catch { $ok = $false }
        $detail = ''
        if (-not $ok) { $detail = '需要管理员：Unregister-ScheduledTask -TaskName ' + $t.TaskName }
        [void]$actions.Add([pscustomobject]@{ action = '删除遗留启动任务 ' + $t.TaskName; ok = $ok; detail = $detail })
    }

    # 9) 构建产物缺失 → bh build（最慢，放最后）
    $missing = @()
    foreach ($svc in $Services) {
        $exe = Join-Path $OutDir ($svc.Name + '\' + $svc.Exe)
        if (-not (Test-Path $exe)) { $missing += $svc.Name }
    }
    if ($missing.Count -gt 0) {
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
    Write-Host '[autostart] 百花 native 登录自启（用户级 Startup 目录，不需要管理员）'
    if (Test-Path $AutostartFile) {
        Write-Host '[autostart] 状态: 已启用' -ForegroundColor Green
        Write-Host ('[autostart] 文件: ' + $AutostartFile)
        Write-Host '[autostart] 动作: 登录时执行 bh start（幂等：已在跑的服务不会重复起）'
    } else {
        Write-Host '[autostart] 状态: 未启用' -ForegroundColor DarkGray
        Write-Host '[autostart] 开启: bh autostart on'
    }
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
    Write-Host '[autostart] 说明: 只启动 out\native 下已构建的产物；改了代码要先 bh build；日志见 bh logs server|webui'
}

function Enable-Autostart {
    if (-not (Test-Path $StartupDir)) { New-Item -ItemType Directory -Force -Path $StartupDir | Out-Null }
    $shim = Get-BhShimPath
    # .cmd 必须纯 ASCII（cmd 按 ANSI 读）：注释也用英文
    $lines = @(
        '@echo off',
        'rem baihua native autostart - generated by: bh autostart on',
        'rem action: bh start (idempotent). disable with: bh autostart off',
        ('start "baihua" /min "' + $shim + '" start')
    )
    Set-Content -Path $AutostartFile -Value $lines -Encoding ASCII
    Write-Host ('[autostart] 已启用: ' + $AutostartFile) -ForegroundColor Green
    if (-not (Test-Path (Join-Path $HOME '.local\bin\bh.cmd'))) {
        Write-Warning '[autostart] 用的是仓库内入口；建议先 bh install（装定位器），仓库搬家后才不用重开自启'
    }
    Write-Host '[autostart] 下次登录生效（现在想看效果：bh start）'
}

function Disable-Autostart {
    if (Test-Path $AutostartFile) {
        Remove-Item $AutostartFile -Force
        Write-Host '[autostart] 已关闭（删除 Startup 里的 baihua-native.cmd）' -ForegroundColor Green
    } else {
        Write-Host '[autostart] 本来就是关闭状态' -ForegroundColor DarkGray
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