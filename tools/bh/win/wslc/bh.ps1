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
    * 本机 OVMS（:8000；2026-11-15 起由 wslc 容器 ovms-gpu 承载）从容器里经宿主 IP 访问：启动时自动探测可用地址

  用法：bh wslc <command> [args]
    build [server|webui]   构建镜像（首次/源码变更后；首次会拉 .NET SDK 镜像，较慢）
    pull                   拉取 postgres 镜像（镜像源见 docker/.env.wslc）
    start [svc...]         启动（默认全部：postgres → server → webui）
    stop                   停止并删除容器（命名卷保留数据）
    restart                停止 + 启动
    status                 容器列表 + 访问地址 + OVMS 探测（bh ovms status 看形态/模型）
    logs <svc> [n]         日志（server|webui|postgres，默认 server 50 行）
    exec <svc> <cmd...>    在运行中的容器里执行命令（如 exec postgres psql -U baihua）
    ps                     等价于 wslc container list --all
    update                 git pull + build + start
    dashboard / open       打开 WebUI
    env                    查看/生成 docker/.env.wslc
    help                   本帮助
#>
# 不用 param()/CmdletBinding：`exec` 要把后续参数（含 -U/-d/--format）原样透传给容器，
# 而 CmdletBinding 会先把 `-e` 之类当 PowerShell 参数解析 → 报「参数二义性」。用 $args 收齐。
$All = @($args)

$ErrorActionPreference = 'Stop'
# wslc.exe 输出 UTF-8 中文；Windows PowerShell 5.1 默认按 OEM 代码页（简中 936）解码 → 表头/日志乱码。
# 钉死控制台输出编码，`bh wslc status/ps/logs` 才读得懂。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

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
$Containers = [ordered]@{ postgres = 'bh-postgres'; server = 'bh-server'; webui = 'bh-webui'; nginx = 'bh-nginx' }
$Volumes = @('bh-pgdata', 'bh-data', 'bh-logs')
$PgImageName = 'postgres:18-alpine'

function Show-Help {
    Write-Host 'bh — 百花 CLI（wslc 形态，Windows 默认；WSL 自带 wslc.exe，无需 Docker Desktop）'
    Write-Host ''
    Write-Host '用法: bh <command> [args]        （bh wslc <command> 是等价的显式写法）'
    Write-Host '  build [server|webui] [ -Prebuilt ]   构建镜像（首次较慢；拉不到 sdk 镜像时用 -Prebuilt）'
    Write-Host '  pull                   拉取 postgres 镜像'
    Write-Host '  start [svc...]         启动（默认全部：postgres → server → webui → nginx）'
    Write-Host '  stop [svc...] / restart   停止（可只停指定服务）/ 重启'
    Write-Host '  status [--json] / logs <svc> [n] / exec <svc> <cmd...> / ps'
    Write-Host '  doctor [--json]        体检：wslc/镜像/卷/容器/端口冲突/OVMS/AI 对话页/.env.wslc'
    Write-Host '  autostart [on|off|status]  登录自启（写 baihua-wslc.cmd，登录时 bh start）'
    Write-Host '  update / dashboard / open / env'
    Write-Host ''
    Write-Host '与 dev(native) 互斥（同占 8788/5177）：dev 形态用 bh dev <cmd>；切换前 bh stop / bh dev stop'
    Write-Host '前置：WSL >= 2.9.3（wsl --update）；首次会生成 docker/.env.wslc（随机 PG_PASSWORD）'
    Write-Host '宿主 :80 被占时：docker/.env.wslc 里设 BAIHUA_NGINX_PORT=8080 再 bh restart'
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
        # 宿主 :80 常被 http.sys/IIS/其它服务占着：可用 BAIHUA_NGINX_PORT 换成 8080 之类
        BAIHUA_NGINX_PORT = '80'
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
        # 空值也写（如 OVMS_HOST_IP=），否则下次读取缺键又被当成"新增"，文件每次都被重写
        foreach ($k in ($map.Keys | Sort-Object)) {
            $lines += ($k + '=' + $map[$k])
        }
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [IO.File]::WriteAllLines($EnvFile, $lines, $utf8)
    }
    return $map
}

# ---------- 镜像 ----------
# Docker Hub 镜像在国内常超时：按 BAIHUA_WSL_MIRROR 改写（library/x 与 org/x 都支持）
function Get-MirroredImage {
    param([string]$Name, $EnvMap)
    $mirror = $EnvMap['BAIHUA_WSL_MIRROR']
    if ([string]::IsNullOrWhiteSpace($mirror)) { return $Name }
    $m = $mirror.TrimEnd('/')
    if ($Name -match '^([^/:]+)/([^/:]+):(.+)$') {
        return ($m + '/' + $matches[1] + '/' + $matches[2] + ':' + $matches[3])
    }
    return ($m + '/library/' + $Name)
}

function Get-PgImage {
    param($EnvMap)
    return (Get-MirroredImage $PgImageName $EnvMap)
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
    # ⚠️ 别改回 TcpClient.Connect：机器上有防火墙/WFP 过滤时，对**未监听**的回环端口
    # 不返回 RST 而是一直等到超时（实测 ~2s/次）；本脚本要探 8788/5177/80/8000，
    # status 会卡 8s 以上。端口枚举一次 ~30ms，4 个端口都走同一实现。
    try {
        $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
        foreach ($ep in $listeners) {
            if ([int]$ep.Port -ne [int]$port) { continue }
            $addr = $ep.Address
            if ($addr.Equals([System.Net.IPAddress]::Any) -or $addr.Equals([System.Net.IPAddress]::IPv6Any) -or [System.Net.IPAddress]::IsLoopback($addr)) { return $true }
        }
    } catch { }
    return $false
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
    # 优先用已在跑的 postgres 容器探测（alpine 自带 wget；prebuilt 的 server 镜像没有 curl）
    $pgName = $Containers['postgres']
    $useExec = Test-Container $pgName
    if (-not $useExec -and -not (Test-Image 'bh-server:latest')) { return '' }
    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } |
        Sort-Object @{ Expression = { if ($_.InterfaceAlias -match 'vEthernet') { 2 } else { 1 } } } |
        Select-Object -ExpandProperty IPAddress -Unique
    foreach ($ip in $ips) {
        $url = 'http://' + $ip + ':8000'
        if ($useExec) {
            $r = Invoke-Wslc -WslcArgs @('exec', $pgName, 'wget', '-qO-', '--timeout=3', ($url + '/v1/models')) -AllowFail
        } else {
            $r = Invoke-Wslc -WslcArgs @('run', '--rm', '--entrypoint', 'curl', 'bh-server:latest', '-fsS', '--max-time', '3', ($url + '/v1/models')) -AllowFail
        }
        if ($r.ExitCode -eq 0) {
            Write-Host ('[ovms] 容器可达宿主 OVMS：' + $url)
            return $url
        }
    }
    Write-Warning '容器访问不到宿主 OVMS（:8000）：本地 AI 相关功能会不可用（可用 docker/.env.wslc 的 OVMS_HOST_IP 指定宿主地址）'
    return ''
}

function Build-Images {
    param([string]$Only, [switch]$Prebuilt)
    $targets = @()
    if ([string]::IsNullOrWhiteSpace($Only) -or $Only -eq 'server') { $targets += 'server' }
    if ([string]::IsNullOrWhiteSpace($Only) -or $Only -eq 'webui') { $targets += 'webui' }

    if ($Prebuilt) {
        # 预编译路径：宿主机 publish（linux-x64）→ 产物打进 aspnet 运行时镜像。
        # 适用：拉不到 mcr 的 sdk 构建镜像（本机实测 sdk:* 会卡住，aspnet:* 正常）。
        $publishDir = Join-Path $Root 'out/wslc'
        foreach ($t in $targets) {
            if ($t -eq 'server') { $proj = 'services/Baihua.Server/Baihua.Server.csproj' } else { $proj = 'services/Baihua.Web/Baihua.Web.csproj' }
            $dest = Join-Path $publishDir $t
            Write-Host ('[build] dotnet publish ' + $t + ' (linux-x64) ...')
            & dotnet publish (Join-Path $Root $proj) -c Release -r linux-x64 --self-contained false -o $dest
            if ($LASTEXITCODE -ne 0) { throw ('dotnet publish 失败：' + $t) }
            $df = Join-Path $Root ('docker/Dockerfile.' + $t + '.prebuilt')
            Write-Host ('[build] ' + $t + ' image (prebuilt) ...')
            & wslc build -t ('bh-' + $t + ':latest') -f $df $dest
            if ($LASTEXITCODE -ne 0) { throw ('构建失败：' + $t) }
        }
        Write-Host '[build] done（prebuilt）'
        return
    }

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
    Assert-NoDevConflict
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
            # 172.16/12：webui 容器（bridge）；169.254/16：wslc 的 127.0.0.1 端口代理在宿主看来是这个源地址
            # （不加它，宿主机访问 /api/* 会被 AccessControl 判成非本机 403）
            '-e', 'BAIHUA_ADMIN_ALLOWED_NETS=127.0.0.1/32,172.16.0.0/12,169.254.0.0/16',
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

    # ---------- nginx（局域网统一入口 :80：/mg、/api、/pair → server；其余 → webui） ----------
    if ([string]::IsNullOrWhiteSpace($Only) -or $Only -eq 'nginx') {
        $nginxPort = [int]$envMap['BAIHUA_NGINX_PORT']
        $proxy = (netsh interface portproxy show v4tov4 2>$null | Out-String)
        if ($proxy -match ('(?m)^\s*\S+\s+' + $nginxPort + '\s+')) {
            Write-Warning ('端口 ' + $nginxPort + ' 上有 netsh portproxy（bh lan on 的遗留）：请先执行 bh lan off')
        } else {
            try {
                $nginxImg = Get-MirroredImage 'nginx:1.27-alpine' $envMap
                Ensure-Image $nginxImg
                Remove-Container $Containers['nginx']
                Write-Host ('[start] nginx (:' + $nginxPort + ') ...')
                $conf = Join-Path $Root 'docker/nginx/wslc.conf'
                $headers = Join-Path $Root 'docker/nginx/server-proxy-headers.conf'
                # 显式 0.0.0.0：wslc 默认把发布端口绑在 127.0.0.1，局域网/手机访问不到
                $args = @('run', '-d', '--name', $Containers['nginx'], '--network', $Network,
                    '-p', ('0.0.0.0:' + $nginxPort + ':80'),
                    '-v', ($conf + ':/etc/nginx/conf.d/default.conf:ro'),
                    '-v', ($headers + ':/etc/nginx/server-proxy-headers.conf:ro'),
                    $nginxImg)
                Invoke-Wslc -WslcArgs $args | Out-Null
                if (Wait-Port $nginxPort 60) { if ($nginxPort -eq 80) { Write-Host '  nginx ready: http://127.0.0.1/ 或 http://<宿主IP>/' } else { Write-Host ('  nginx ready: http://127.0.0.1:' + $nginxPort + '/ 或 http://<宿主IP>:' + $nginxPort + '/') } }
                else { Write-Warning 'nginx 60s 内未就绪（bh wslc logs nginx）' }
            } catch {
                Write-Warning ('nginx 启动失败（:' + $nginxPort + ' 可能被占用）：' + $_.Exception.Message)
                Write-Warning '  仍可用 8788（后端）/ 5177（WebUI）直连；换端口：docker/.env.wslc 里 BAIHUA_NGINX_PORT=8080 后 bh restart'
            }
        }
    }

    if (-not (Test-PortOpen 8000)) {
        Write-Host '  OVMS 未运行（:8000）：本地 AI 会降级，可用 bh ovms start 拉起（容器 ovms-gpu）' -ForegroundColor Yellow
    }
    Write-Host ('[start] 完成：后端 http://127.0.0.1:' + $envMap['BAIHUA_SERVER_PORT'] + '  WebUI http://127.0.0.1:' + $envMap['BAIHUA_WEBUI_PORT'] + '  入口 http://127.0.0.1/')
    Write-Host '        局域网：http://<宿主IP>/ （nginx :80，浏览器与花记移动端共用）'
}

function Stop-Stack {
    Ensure-Wslc
    foreach ($name in @($Containers['nginx'], $Containers['webui'], $Containers['server'], $Containers['postgres'])) {
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
    $nginxPort = [int]$envMap['BAIHUA_NGINX_PORT']
    $nginxState = if (Test-Container $Containers['nginx']) { 'RUNNING (:' + $nginxPort + ')' } else { 'stopped' }
    $entryUrl = if ($nginxPort -eq 80) { 'http://127.0.0.1/' } else { 'http://127.0.0.1:' + $nginxPort + '/' }
    Write-Host ('入口:   ' + $entryUrl + '  nginx=' + $nginxState)
    # 端口占用者：dev/native 在跑时 8788/5177 被 bh-* 占着，别让上面的 URL 误导成 wslc 已就绪
    foreach ($pair in @(@([int]$envMap['BAIHUA_SERVER_PORT'], 'server'), @([int]$envMap['BAIHUA_WEBUI_PORT'], 'webui'), @($nginxPort, 'nginx'))) {
        $owner = Get-PortOwnerName $pair[0]
        if ($owner) {
            $tag = ''
            if ($owner -match '^bh-') { $tag = '  <- native(dev) 占用，不是 wslc 容器' }
            Write-Host ('        端口 ' + $pair[0] + ' 占用者: ' + $owner + $tag) -ForegroundColor DarkGray
        }
    }
    $ov = if (Test-PortOpen 8000) { 'RUNNING (port 8000)' } else { 'stopped' }
    Write-Host ('OVMS:   ' + $ov + '（wslc 容器 ovms-gpu；容器内经宿主 IP:8000 访问，bh ovms status 看详情）')
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

# ---- 端口占用 / 形态冲突 ------------------------------------------------------
function Get-PortOwnerName([int]$port) {
    $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $conn) { return '' }
    $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
    if ($proc) { return ($proc.ProcessName + '(' + $proc.Id + ')') }
    return ('pid ' + $conn.OwningProcess)
}

function Assert-NoDevConflict {
    # wslc 与 dev/native 都占 8788/5177/80：先看端口是否被 native 的 bh-* 进程占着
    $conflicts = @()
    foreach ($port in @(8788, 5177, 80)) {
        $owner = Get-PortOwnerName $port
        if ($owner -match '^bh-') { $conflicts += ($port.ToString() + ' <- ' + $owner) }
    }
    if ($conflicts.Count -gt 0) {
        Write-Host '[wslc] 检测到 native(dev) 形态在跑，端口冲突：' -ForegroundColor Yellow
        $conflicts | ForEach-Object { Write-Host ('       ' + $_) -ForegroundColor Yellow }
        Write-Host '[wslc] 先停 native：bh dev stop    然后重试：bh start' -ForegroundColor Yellow
        exit 1
    }
}

function Stop-Stacks {
    param([string[]]$Names = @())
    Ensure-Wslc
    $order = @($Containers['nginx'], $Containers['webui'], $Containers['server'], $Containers['postgres'])
    $targets = @()
    if ($Names.Count -eq 0) {
        $targets = $order
    } else {
        foreach ($n in $Names) {
            $key = ($n.ToLower() -replace '^bh-', '')
            if ($Containers.Contains($key)) { if ($targets -notcontains $Containers[$key]) { $targets += $Containers[$key] } }
            else { Write-Host ('[stop] 未知服务：' + $n + '（可选：postgres|server|webui|nginx）') -ForegroundColor Yellow }
        }
    }
    foreach ($name in $targets) {
        if (Test-Container $name) {
            Invoke-Wslc -WslcArgs @('container', 'stop', $name) -AllowFail | Out-Null
            Invoke-Wslc -WslcArgs @('container', 'remove', '-f', $name) -AllowFail | Out-Null
            Write-Host ('[stop] ' + $name)
        }
    }
    if ($targets.Count -eq $order.Count) {
        Write-Host '[stop] 已停止（命名卷 bh-pgdata / bh-data / bh-logs 保留）'
    }
}

# ---- status --json（与 native 同构：DSH 插件消费同一份契约，cell 标 wslc）--------
function Show-StatusJson {
    Ensure-Wslc
    $envMap = Get-EnvMap
    $head = 'unknown'; $branch = 'unknown'; $dirty = $false
    if (Test-Path (Join-Path $Root '.git')) {
        try {
            $h = git -C $Root rev-parse --short HEAD 2>$null
            if ($h) { $head = ($h | Select-Object -First 1) }
            $b = git -C $Root rev-parse --abbrev-ref HEAD 2>$null
            if ($b) { $branch = ($b | Select-Object -First 1) }
            $d = git -C $Root status --porcelain 2>$null
            if ($d) { $dirty = $true }
        } catch { }
    }
    $ports = @{ postgres = [int]$envMap['PG_HOST_PORT']; server = [int]$envMap['BAIHUA_SERVER_PORT']; webui = [int]$envMap['BAIHUA_WEBUI_PORT']; nginx = [int]$envMap['BAIHUA_NGINX_PORT'] }
    $entries = @()
    foreach ($name in @('postgres', 'server', 'webui', 'nginx')) {
        $cn = $Containers[$name]
        $exists = Test-Container $cn
        $running = $false
        $image = ''
        if ($exists) {
            $ins = (& wslc container inspect $cn 2>$null | Out-String)
            if ($ins -match '"Running"\s*:\s*true') { $running = $true }
            if ($ins -match '"Image"\s*:\s*"([^"]+)"') { $image = $matches[1] }
        }
        $portOpen = Test-PortOpen $ports[$name]
        $phase = 'not installed'
        if ($running) { $phase = 'Running' } elseif ($exists) { $phase = 'Stopped' }
        $entries += [pscustomobject]@{
            name = $name
            ready = if ($running -and $portOpen) { 1 } else { 0 }
            replicas = 1
            image = if ($image) { $image } else { 'wslc' }
            age = ''
            restarts = 0
            phase = $phase
            imageCommit = $head
            upToDate = $true
        }
    }
    $ovReady = Test-PortOpen 8000
    $ovPhase = 'not installed'
    if ($ovReady) { $ovPhase = 'Running' }
    $entries += [pscustomobject]@{
        name = 'openvino'
        ready = if ($ovReady) { 1 } else { 0 }
        replicas = 1
        image = 'wslc:ovms-gpu'
        age = ''
        restarts = 0
        phase = $ovPhase
        imageCommit = $head
        upToDate = $true
        engine = 'wslc'
        container = 'ovms-gpu'
    }
    $readyTotal = @($entries | Where-Object { $_.ready -eq 1 }).Count
    [pscustomobject]@{
        cell = 'wslc'
        namespace = 'wslc'
        updatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        git = [pscustomobject]@{ head = $head; branch = $branch; dirty = $dirty }
        services = $entries
        summary = [pscustomobject]@{ ready = $readyTotal; total = $entries.Count }
    } | ConvertTo-Json -Depth 5
}

# ---- doctor（wslc 形态体检） --------------------------------------------------
function Add-WslcCheck($list, [string]$name, [string]$status, [string]$detail, [string]$fix) {
    [void]$list.Add([pscustomobject]@{ name = $name; status = $status; detail = $detail; fix = $fix })
}

function Invoke-WslcDoctorChecks {
    $checks = New-Object System.Collections.ArrayList
    $envMap = Get-EnvMap

    # 1) wslc / WSL 版本
    $wslcCmd = Get-Command wslc -ErrorAction SilentlyContinue
    if (-not $wslcCmd) {
        Add-WslcCheck $checks 'wslc.exe' 'fail' 'PATH 里没有 wslc（Windows 默认形态依赖 WSL3）' 'wsl --update（需要 WSL 3+，wslc 随 WSL 提供）'
    } else {
        $ver = (& wslc version 2>&1 | Out-String).Trim()
        Add-WslcCheck $checks 'wslc.exe' 'ok' $ver ''
    }

    # 2) 镜像（bh-server / bh-webui 是本仓库构建的）
    $missingImg = @()
    foreach ($img in @('bh-server:latest', 'bh-webui:latest')) {
        $okImg = ((& wslc image inspect $img 2>$null) -ne $null -and $LASTEXITCODE -eq 0)
        if (-not $okImg) { $missingImg += $img }
    }
    if ($missingImg.Count -gt 0) {
        Add-WslcCheck $checks '镜像' 'fail' ('缺少：' + ($missingImg -join ', ')) 'bh build（首次会拉 .NET 基础镜像，较慢；拉不到 SDK 用 bh build -Prebuilt）'
    } else {
        Add-WslcCheck $checks '镜像' 'ok' 'bh-server / bh-webui 都在' ''
    }

    # 3) 命名卷
    $missingVol = @()
    foreach ($v in @('bh-pgdata', 'bh-data', 'bh-logs')) {
        $vo = (& wslc volume inspect $v 2>$null)
        if ($LASTEXITCODE -ne 0) { $missingVol += $v }
    }
    if ($missingVol.Count -gt 0) {
        Add-WslcCheck $checks '命名卷' 'warn' ('未创建：' + ($missingVol -join ', ')) 'bh start 会自动创建（数据卷删了就是丢数据，别手删）'
    } else {
        Add-WslcCheck $checks '命名卷' 'ok' 'bh-pgdata / bh-data / bh-logs' ''
    }

    # 4) 容器状态
    $running = @()
    $stopped = @()
    foreach ($name in @('postgres', 'server', 'webui', 'nginx')) {
        $cn = $Containers[$name]
        if (-not (Test-Container $cn)) { $stopped += ($name + '(未创建)'); continue }
        $ins = (& wslc container inspect $cn 2>$null | Out-String)
        if ($ins -match '"Running"\s*:\s*true') { $running += $name } else { $stopped += $name }
    }
    if ($stopped.Count -eq 0) {
        Add-WslcCheck $checks '容器' 'ok' ('全部运行：' + ($running -join ', ')) ''
    } elseif ($running.Count -eq 0) {
        Add-WslcCheck $checks '容器' 'warn' '全部未运行' 'bh start（首次约 1-3 分钟）'
    } else {
        Add-WslcCheck $checks '容器' 'warn' ('运行：' + ($running -join ', ') + '；未运行：' + ($stopped -join ', ')) 'bh start（幂等，只会补起缺的）'
    }

    # 5) 端口：8788 / 5177 / 80 是否可用（native 占着就会冲突）
    foreach ($pair in @(@([int]$envMap['BAIHUA_SERVER_PORT'], 'server'), @([int]$envMap['BAIHUA_WEBUI_PORT'], 'webui'), @([int]$envMap['BAIHUA_NGINX_PORT'], 'nginx'))) {
        $port = $pair[0]; $label = $pair[1]
        $owner = Get-PortOwnerName $port
        if (-not $owner) { Add-WslcCheck $checks ('端口 ' + $port + '（' + $label + '）') 'ok' '空闲' '' ; continue }
        if ($owner -match '^bh-') {
            Add-WslcCheck $checks ('端口 ' + $port + '（' + $label + '）') 'fail' ('被 native(dev) 占用：' + $owner) 'bh dev stop（然后 bh start）'
        } else {
            Add-WslcCheck $checks ('端口 ' + $port + '（' + $label + '）') 'warn' ('被外部进程占用：' + $owner) '停掉占位进程，或改 docker/.env.wslc 里的端口'
        }
    }

    # 6) OVMS（可选组件）
    if (Test-PortOpen 8000) {
        $models = @()
        try { $r = Invoke-RestMethod 'http://127.0.0.1:8000/v1/models' -TimeoutSec 3; $models = @($r.data | ForEach-Object { $_.id }) } catch { }
        Add-WslcCheck $checks 'OVMS 本地模型' 'ok' ('RUNNING :8000  模型 ' + ($models -join ', ')) ''
    } else {
        Add-WslcCheck $checks 'OVMS 本地模型' 'warn' '未运行（:8000 无监听）' 'bh ovms start（不影响其它模块，AI 降级）'
    }

    # 7) AI 对话页后端（Windows venv，两种形态共用）
    $owuiExe = Join-Path (Join-Path $HOME '.baihua') 'open-webui-venv\Scripts\open-webui.exe'
    if (-not (Test-Path $owuiExe)) {
        Add-WslcCheck $checks 'AI 对话页 open-webui' 'warn' '未安装' 'bh open-webui install'
    } elseif (Test-PortOpen 8080) {
        Add-WslcCheck $checks 'AI 对话页 open-webui' 'ok' 'RUNNING :8080' ''
    } else {
        Add-WslcCheck $checks 'AI 对话页 open-webui' 'warn' '已安装但没在运行' 'bh open-webui start（首启 1-2 分钟）'
    }

    # 8) .env.wslc
    if (Test-Path $EnvFile) {
        Add-WslcCheck $checks 'docker/.env.wslc' 'ok' $EnvFile ''
    } else {
        Add-WslcCheck $checks 'docker/.env.wslc' 'warn' '还没生成（bh start 首次会生成，含随机 PG_PASSWORD）' 'bh env'
    }
    return ,$checks
}

function Show-Doctor {
    Ensure-Wslc
    $checks = Invoke-WslcDoctorChecks
    Write-Host 'bh doctor —— wslc 形态体检（Windows 默认形态）' -ForegroundColor Cyan
    Write-Host ''
    foreach ($c in $checks) {
        $suffix = if ($c.detail) { ' — ' + $c.detail } else { '' }
        switch ($c.status) {
            'ok'   { Write-Host ('[ OK ] ' + $c.name + $suffix) -ForegroundColor Green }
            'warn' {
                Write-Host ('[WARN] ' + $c.name + $suffix) -ForegroundColor Yellow
                if ($c.fix) { Write-Host ('       -> ' + $c.fix) -ForegroundColor DarkYellow }
            }
            default {
                Write-Host ('[FAIL] ' + $c.name + $suffix) -ForegroundColor Red
                if ($c.fix) { Write-Host ('       -> ' + $c.fix) -ForegroundColor DarkYellow }
            }
        }
    }
    $ok = @($checks | Where-Object { $_.status -eq 'ok' }).Count
    $warn = @($checks | Where-Object { $_.status -eq 'warn' }).Count
    $fail = @($checks | Where-Object { $_.status -eq 'fail' }).Count
    Write-Host ''
    $color = if ($fail -gt 0) { 'Red' } elseif ($warn -gt 0) { 'Yellow' } else { 'Green' }
    Write-Host ('体检完成：' + $ok + ' 项通过 / ' + $warn + ' 项警告 / ' + $fail + ' 项失败') -ForegroundColor $color
    return $fail
}

function Show-DoctorJson {
    Ensure-Wslc
    $checks = Invoke-WslcDoctorChecks
    $ok = @($checks | Where-Object { $_.status -eq 'ok' }).Count
    $warn = @($checks | Where-Object { $_.status -eq 'warn' }).Count
    $fail = @($checks | Where-Object { $_.status -eq 'fail' }).Count
    [pscustomobject]@{
        cell = 'wslc'
        updatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        ok = ($fail -eq 0)
        summary = [pscustomobject]@{ passed = $ok; warned = $warn; failed = $fail }
        checks = @($checks)
    } | ConvertTo-Json -Depth 6
}

# ---- 登录自启（用户级 Startup；wslc 是默认形态） -------------------------------
$StartupDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
$WslcAutostartFile = Join-Path $StartupDir 'baihua-wslc.cmd'
$WslcAutostartLnk = Join-Path $StartupDir 'baihua-wslc.lnk'
$NativeAutostartFile = Join-Path $StartupDir 'baihua-native.cmd'

function Show-Autostart {
    Write-Host '[autostart] 百花登录自启（用户级 Startup，不需要管理员）'
    if (Test-Path $WslcAutostartLnk) {
        Write-Host '[autostart] 状态: 已启用（无窗口：.lnk → bh-launch.exe）' -ForegroundColor Green
        Write-Host ('[autostart] 文件: ' + $WslcAutostartLnk)
    } elseif (Test-Path $WslcAutostartFile) {
        Write-Host '[autostart] 状态: 已启用（旧式 .cmd，登录时会闪一个最小化控制台）' -ForegroundColor Yellow
        Write-Host ('[autostart] 文件: ' + $WslcAutostartFile + '  ← 重新 bh autostart on 可换成无窗口的 .lnk')
    } else {
        Write-Host '[autostart] 状态: 未启用' -ForegroundColor DarkGray
        Write-Host '[autostart] 开启: bh autostart on'
    }
    if (Test-Path $NativeAutostartFile) {
        Write-Host '[autostart] 注意: 还留着 native 的自启项 baihua-native.cmd（与 wslc 抢 8788/5177）' -ForegroundColor Yellow
        Write-Host '[autostart]       关掉它: bh dev autostart off   （或 bh autostart on 会自动改名停用）'
    }
    Write-Host '[autostart] 动作: 登录时执行 bh start（= wslc start，幂等）'
}

function Enable-Autostart {
    if (-not (Test-Path $StartupDir)) { New-Item -ItemType Directory -Force -Path $StartupDir | Out-Null }
    # native 的自启项会在登录时抢端口：改成 .disabled（可恢复），并明确告知
    if (Test-Path $NativeAutostartFile) {
        Move-Item -Path $NativeAutostartFile -Destination ($NativeAutostartFile + '.disabled') -Force
        Write-Host '[autostart] 已停用旧的 native 自启项（baihua-native.cmd -> .disabled）' -ForegroundColor Yellow
    }
    # 优先 .lnk → bh-launch.exe（GUI 子系统，无控制台）：Explorer 跑 .cmd 必定分配控制台窗口
    $launcher = ''
    $nativeCell = Join-Path $Root 'win\native\bh.ps1'
    if (Test-Path $nativeCell) {
        try { $launcher = (@(& $nativeCell 'launcher-path' 2>$null) | Select-Object -Last 1) } catch { $launcher = '' }
    }
    if ($launcher -and (Test-Path $launcher)) {
        $ps1Shim = Join-Path $HOME '.local\bin\bh.ps1'
        if (-not (Test-Path $ps1Shim)) { $ps1Shim = Join-Path $Root 'tools\bh\bh.ps1' }
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($WslcAutostartLnk)
        $sc.TargetPath = $launcher
        $sc.Arguments = '--ps1 "' + $ps1Shim + '" start'
        $sc.WorkingDirectory = Split-Path -Parent $ps1Shim
        $sc.WindowStyle = 7
        $sc.Description = 'baihua wslc autostart (silent)'
        $sc.Save()
        Remove-Item $WslcAutostartFile -Force -ErrorAction SilentlyContinue
        Write-Host ('[autostart] 已启用: ' + $WslcAutostartLnk) -ForegroundColor Green
        Write-Host '[autostart] 走 bh-launch.exe（GUI 子系统，无控制台）：登录时不会弹黑框'
    } else {
        $lines = @(
            '@echo off',
            'rem baihua wslc autostart - generated by: bh autostart on',
            'rem action: bh start (wslc containers, idempotent). disable with: bh autostart off',
            'start "baihua" /min "%USERPROFILE%\.local\bin\bh.cmd" start'
        )
        Set-Content -Path $WslcAutostartFile -Value $lines -Encoding ASCII
        Write-Host ('[autostart] 已启用: ' + $WslcAutostartFile) -ForegroundColor Green
        Write-Warning '[autostart] 没能拿到 bh-launch.exe，退回 .cmd（登录时会有个最小化控制台窗口）'
    }
    if (-not (Test-Path (Join-Path $HOME '.local\bin\bh.cmd'))) {
        Write-Warning '[autostart] 没找到定位器 ~\.local\bin\bh.cmd；先 bh install（否则登录时找不到 bh）'
    }
    Write-Host '[autostart] 下次登录生效（现在想看效果：bh start）'
}

function Disable-Autostart {
    $removed = @()
    foreach ($f in @($WslcAutostartLnk, $WslcAutostartFile)) {
        if (Test-Path $f) { Remove-Item $f -Force -ErrorAction SilentlyContinue; $removed += (Split-Path -Leaf $f) }
    }
    if ($removed.Count -gt 0) {
        Write-Host ('[autostart] 已关闭（删除 ' + ($removed -join ', ') + '）') -ForegroundColor Green
    } else {
        Write-Host '[autostart] 本来就是关闭状态' -ForegroundColor DarkGray
    }
}

$script:EnvMap = @{}

switch ($Command.ToLower()) {
    'help'      { Show-Help }
    'build'     {
        Ensure-Wslc
        $pre = ($All -contains '-Prebuilt') -or ($All -contains '--prebuilt') -or ($All -contains '-prebuilt')
        $only = ''
        foreach ($a in @($All | Select-Object -Skip 1)) {
            if ($a -eq 'server' -or $a -eq 'webui') { $only = $a }
        }
        Build-Images $only -Prebuilt:$pre
    }
    'pull'      { Ensure-Wslc; $m = Get-EnvMap; Ensure-Image (Get-PgImage $m) }
    'start'     { Start-Stack $Arg1 }
    'up'        { Start-Stack $Arg1 }
    'stop'      { $names = @($All | Select-Object -Skip 1); Stop-Stacks $names }
    'down'      { $names = @($All | Select-Object -Skip 1); Stop-Stacks $names }
    'restart'   { Stop-Stack; Start-Stack '' }
    'status'    { if ($Arg1 -eq '--json') { Show-StatusJson } else { Show-Status } }
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
    'doctor'    { if ($Arg1 -eq '--json') { Show-DoctorJson } else { $failCount = Show-Doctor; if ($failCount -gt 0) { exit 1 } } }
    'autostart' {
        switch ($Arg1.ToLower()) {
            'on'      { Enable-Autostart }
            'enable'  { Enable-Autostart }
            'off'     { Disable-Autostart }
            'disable' { Disable-Autostart }
            default   { Show-Autostart }
        }
    }
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

# 内部探测命令（如 inspect 未命中容器）返回 1 是正常分支，不该让整条命令看起来失败。
# 真正的失败在上面的分派里已 exit 1，这里统一归零。
exit 0
