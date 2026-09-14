#requires -Version 5.1
<#
  bh - baihua 统一 CLI 入口（Windows）
  默认用 native cell（tools/bh/win/native/bh.ps1，dotnet 进程，不依赖 WSL/k3s）。
  可选 k8s cell（tools/bh/linux/k8s/bh.sh，经 WSL 调用 Linux k3s）。

  用法:
    bh <command> [args]          执行命令（默认 native cell）
    bh k8s <command> [args]      显式用 k8s cell（经 WSL）
    bh install                   复制自包含定位器到 %USERPROFILE%\.local\bin（含 bh.cmd shim，加入用户 PATH）
    bh uninstall                 移除定位器与 PATH 项
#>
[CmdletBinding()]
param(
    # 注意：PowerShell 5.1 中 ValueFromRemainingArguments 会抢占位置参数绑定，
    # 因此不定义 Position 参数，全部裸参数收进 $All 后手动切分（$All[0] = 命令/cell）
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$All = @()
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSCommandPath          # tools/bh
$Repo = Split-Path -Parent (Split-Path -Parent $Root)   # 仓库根（分派器在 tools/bh，比 cell 脚本浅 2 级）

$Arg1 = if ($All.Count -gt 0) { $All[0] } else { '' }
# @(...) 强制数组：强类型数组的单元素范围索引（$All[1..1]）会返回裸 string，
# 后续 @splat 会把字符串拆成字符数组，必须包成 object[]
$Rest = @($All | Select-Object -Skip 1)

$Cells = @{
    'native' = @{ Script = 'win\native\bh.ps1'; Desc = 'Windows native（dotnet 进程，不依赖 WSL/k3s）' }
    'k8s'    = @{ Script = 'linux\k8s\bh.sh';    Desc = 'Linux k3s（经 WSL，root）' }
}
$DefaultCell = 'native'

function Show-Help {
    Write-Host 'bh - baihua 统一 CLI（Windows）'
    Write-Host ''
    Write-Host '用法:'
    Write-Host '  bh <command> [args]           执行命令（默认 native cell）'
    Write-Host '  bh native <command> [args]    显式用 native cell（dotnet 进程）'
    Write-Host '  bh k8s <command> [args]       显式用 k8s cell（经 WSL）'
    Write-Host '  bh lan [on|off|status]        局域网入口（仅 k8s cell，宿主 :80 -> WSL k3s）'
    Write-Host '  bh install / uninstall        加入 / 移出用户 PATH'
    Write-Host ''
    Write-Host '部署形态:'
    Write-Host '  native（默认）— Windows dotnet 进程，不依赖 WSL/k3s（需本机 PostgreSQL）'
    Write-Host '  k8s           — Linux k3s（PostgreSQL + 后端 + WebUI + OVMS 全部容器化）'
    Write-Host ''
    Write-Host '可用命令: bh help（详情见 tools/bh/README.md）'
}

function Invoke-Install {
    # 复制自包含定位器（locator.ps1 + bh.cmd shim）到 %USERPROFILE%\.local\bin，
    # 并把该目录加入用户 PATH。定位器每次调用时自动定位仓库根（BAIHUA_HOME > 常见路径 > 向上查找），
    # 仓库改名/移动后无需重装。
    $bin = Join-Path $HOME '.local\bin'
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    Copy-Item (Join-Path $Root 'locator.ps1') (Join-Path $bin 'bh.ps1') -Force
    Copy-Item (Join-Path $Root 'bh.cmd') (Join-Path $bin 'bh.cmd') -Force

    # 幂等：把 $bin 追加到用户 PATH（HKCU\Environment）
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @($userPath -split ';' | Where-Object { $_ -ne '' })
    $already = $parts | Where-Object { $_.TrimEnd('\') -ieq $bin }
    if ($already) {
        Write-Host "[install] 已在用户 PATH: $bin"
    } else {
        $newPath = (@($parts) + $bin) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Write-Host "[install] 已加入用户 PATH: $bin"
    }
    # 广播环境变量变更（explorer 与后续进程可见）
    Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@ -ErrorAction SilentlyContinue
    $null = [Win32.Native]::SendMessageTimeout([IntPtr]::Zero, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]([UIntPtr]::Zero))
    Write-Host '[install] 完成。新开终端后可直接使用: bh <command>'
    Write-Host '         当前会话请用: .\tools\bh\bh.ps1 <command>'
    Write-Host '[install] 定位器自动查找: $env:BAIHUA_HOME > 常见路径 > 当前目录向上；仓库改名/移动后无需重新安装'
}

function Invoke-Uninstall {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @($userPath -split ';' | Where-Object { $_ -ne '' })
    $bin = Join-Path $HOME '.local\bin'
    $kept = @($parts | Where-Object { $_.TrimEnd('\') -ine $bin })
    if ($kept.Count -eq $parts.Count) {
        Write-Host "[uninstall] 用户 PATH 中未找到: $bin"
    } else {
        [Environment]::SetEnvironmentVariable('Path', $kept -join ';', 'User')
        Write-Host "[uninstall] 已从用户 PATH 移除: $bin"
    }
    Remove-Item (Join-Path $bin 'bh.ps1') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $bin 'bh.cmd') -Force -ErrorAction SilentlyContinue
    Write-Host '[uninstall] 已移除 %USERPROFILE%\.local\bin\bh.ps1 / bh.cmd'
}

# ---- 共享工具函数（native / k8s cell 都用）----

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

function Test-Http([string]$Url) {
    try {
        $r = Invoke-WebRequest -Uri $Url -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop
        return ($r.StatusCode -ge 200 -and $r.StatusCode -lt 400)
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        return ($code -ge 200 -and $code -lt 400)
    }
}

# ---- 确定 cell 与命令 ----
$cell = $Arg1.ToLower()
if ($cell -in @('install', 'uninstall')) {
    if ($cell -eq 'install') { Invoke-Install } else { Invoke-Uninstall }
    exit 0
}
if ($cell -in @('help', '-h', '--help')) { Show-Help; exit 0 }

if ($Cells.ContainsKey($cell)) {
    $activeCell = $cell
    $cmdArgs = @($All | Select-Object -Skip 1)
} else {
    $activeCell = $DefaultCell
    $cmdArgs = @($All)
}

# ==================== native cell ====================
# Windows dotnet 进程，不经 WSL/k3s
if ($activeCell -eq 'native') {
    $nativeScript = Join-Path $Root 'win\native\bh.ps1'
    if (-not (Test-Path $nativeScript)) { Write-Error "[native] 缺少 $nativeScript"; exit 1 }

    # lan 命令：native cell 下做宿主 :80 -> :8788 portproxy（移动端默认无端口访问）
    $cmd0 = if ($cmdArgs.Count -gt 0) { $cmdArgs[0] } else { '' }
    if ($cmd0 -eq 'lan') {
        $sub = if ($cmdArgs.Count -gt 1) { $cmdArgs[1] } else { 'status' }
        switch ($sub) {
            'on' {
                $lanIp = Get-HostLanIp
                if (-not $lanIp) { Write-Host '[lan] 未识别到宿主局域网 IP'; exit 0 }
                Write-Host '[lan] 设置宿主 :80 -> :8788 转发（需要管理员授权）...'
                try {
                    $p = Start-Process -FilePath 'pwsh' -Verb RunAs -PassThru -ArgumentList @(
                        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
                        'netsh interface portproxy add v4tov4 listenport=80 connectport=8788 connectaddress=127.0.0.1; netsh advfirewall firewall add rule name="Baihua LAN 80" dir=in action=allow protocol=TCP localport=80'
                    ) -ErrorAction Stop
                    $null = Wait-Process -Id $p.Id -Timeout 30 -ErrorAction SilentlyContinue
                    Write-Host "[lan] 局域网入口就绪：http://$lanIp/  （-> 127.0.0.1:8788）"
                } catch { Write-Warning '[lan] 提权失败或被取消' }
            }
            'off' {
                Write-Host '[lan] 撤销宿主 :80 转发（需要管理员授权）...'
                try {
                    $p = Start-Process -FilePath 'pwsh' -Verb RunAs -PassThru -ArgumentList @(
                        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
                        'netsh interface portproxy delete v4tov4 listenport=80; netsh advfirewall firewall delete rule name="Baihua LAN 80"'
                    ) -ErrorAction Stop
                    $null = Wait-Process -Id $p.Id -Timeout 30 -ErrorAction SilentlyContinue
                    Write-Host '[lan] 已撤销'
                } catch { Write-Warning '[lan] 提权失败或被取消' }
            }
            default {
                $lanIp = Get-HostLanIp
                $proxy = (netsh interface portproxy show v4tov4 2>$null | Out-String)
                $has80 = $proxy -match '\b80\b'
                Write-Host "[lan] 宿主 IP: $lanIp"
                Write-Host "[lan] 后端端口: 8788（server 绑 0.0.0.0）"
                if ($has80) {
                    Write-Host "[lan] 局域网入口: 已就绪 http://$lanIp/  （:80 -> :8788）"
                } else {
                    Write-Host "[lan] 局域网入口: 未就绪（bh lan on 配置 :80 转发）"
                    Write-Host "[lan] 备选: 直接用 http://$lanIp`:8788/ （需放行防火墙 TCP 8788）"
                }
            }
        }
        exit 0
    }

    # 其余命令直接委托给 native 脚本
    & $nativeScript @cmdArgs
    exit $LASTEXITCODE
}

# ==================== k8s cell ====================
# 经 WSL 调用 Linux k3s（PostgreSQL + 后端 + WebUI + OVMS 全部容器化）
$wslRepo = (wsl wslpath -u ($Repo -replace '\\', '/') 2>$null | Out-String).Trim()
if (-not $wslRepo) { Write-Error '[k8s] wslpath 不可用，请确认已安装 WSL 且可执行 wsl 命令'; exit 1 }

function Invoke-Cell([string[]]$CellArgs, [string]$EnvPrefix = '') {
    $inner = ($CellArgs | ForEach-Object { "'" + ($_ -replace "'", "'\''") + "'" }) -join ' '
    # 必须经管道逐行转发：wsl.exe 是原生子进程，直接把输出写到控制台句柄，
    # 当本脚本的 stdout 不是控制台（CI / agent harness / 被其它程序捕获）时，
    # 紧跟着的 `exit` 会在这些输出被刷出之前终止进程 —— 表现为"命令明明成功却没有任何输出"。
    wsl -u root -e bash -lc "cd '$wslRepo' && $EnvPrefix tools/bh/linux/k8s/bh.sh $inner" 2>&1 |
        ForEach-Object { Write-Host $_ }
    return $LASTEXITCODE
}

# ---------------- 局域网入口（宿主 -> WSL k3s :80）----------------
function Get-WslIp {
    $ip = (wsl -e bash -lc "hostname -I | awk '{print `$1}'" 2>$null | Out-String).Trim()
    if ($ip -match '^\d+\.\d+\.\d+\.\d+$') { return $ip }
    return ''
}

function Get-LanExposureState {
    $wslIp = Get-WslIp
    $lanIp = Get-HostLanIp
    $state = [ordered]@{
        WslIp = $wslIp; LanIp = $lanIp
        BackendUp = ($wslIp -and (Test-Http "http://$wslIp/health"))
        LanUp = ($lanIp -and (Test-Http "http://$lanIp/health"))
        Mirrored = $false
    }
    if ($state.LanUp -and $state.WslIp -eq $state.LanIp) { $state.Mirrored = $true }
    return [pscustomobject]$state
}

function Ensure-LanExposure {
    param([switch]$Quiet)

    $st = Get-LanExposureState
    if (-not $st.LanIp) { if (-not $Quiet) { Write-Host '[lan] 未识别到宿主局域网 IP，跳过局域网入口配置' }; return }
    if ($st.LanUp) {
        if (-not $Quiet) {
            $how = if ($st.Mirrored) { 'WSL mirrored 网络（无需转发）' } else { '宿主 :80 已转发进 WSL' }
            Write-Host "[lan] 局域网入口就绪：http://$($st.LanIp)/  （$how）"
        }
        return
    }
    if (-not $st.BackendUp) {
        if (-not $Quiet) { Write-Host "[lan] 后端未在 WSL 内就绪（http://$($st.WslIp)/health 不通），先 bh start/deploy" }
        return
    }

    $script = Join-Path $Repo 'scripts\expose-k3s-lan.ps1'
    if (-not (Test-Path $script)) { Write-Warning "[lan] 缺少 $script"; return }
    if (-not $Quiet) { Write-Host "[lan] 局域网入口未就绪 → 需要一次管理员授权（UAC）来做宿主 :80 转发 ..." }
    try {
        $p = Start-Process -FilePath 'pwsh' -Verb RunAs -PassThru -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$script`"", '-Quiet'
        ) -ErrorAction Stop
        $null = Wait-Process -Id $p.Id -Timeout 120 -ErrorAction SilentlyContinue
    } catch {
        Write-Warning "[lan] 自动提权失败或被取消：请以管理员运行 pwsh -File `"$script`""
        return
    }

    $again = Get-LanExposureState
    if ($again.LanUp) {
        Write-Host "[lan] 局域网入口已就绪：http://$($again.LanIp)/  （手机/局域网设备可用）"
    } else {
        Write-Warning "[lan] 转发后仍不通，请手动执行：pwsh -File `"$script`""
    }
}

function Show-LanStatus {
    $st = Get-LanExposureState
    Write-Host "[lan] WSL IP : $($st.WslIp)"
    Write-Host "[lan] 宿主 IP: $($st.LanIp)"
    Write-Host "[lan] 后端可用: $(if ($st.BackendUp) { '是' } else { '否（先 bh start/deploy）' })"
    Write-Host "[lan] 局域网入口: $(if ($st.LanUp) { "已就绪 http://$($st.LanIp)/" } else { '未就绪（bh lan on 配置，或 WSL 重启后重跑）' })"
    if ($st.Mirrored) { Write-Host '[lan] 模式: WSL mirrored（宿主直接持有 :80，无需 portproxy）' }
    elseif ($st.LanUp) { Write-Host '[lan] 模式: netsh portproxy 宿主:80 -> WSL:80' }
}

# ---------------- 配对地址（Baihua__PublicBaseUrl）自动校正 ----------------
function Get-ClusterPublicBaseUrl {
    $v = (wsl -u root -e bash -lc "k3s kubectl -n baihua get configmap baihua-config -o jsonpath='{.data.Baihua__PublicBaseUrl}'" 2>$null | Out-String).Trim()
    return $v
}

function Sync-PublicBaseUrl {
    param([switch]$Quiet)

    $lanIp = Get-HostLanIp
    if (-not $lanIp) { if (-not $Quiet) { Write-Host '[lan] 未识别到宿主局域网 IP，跳过配对地址校正' }; return }

    $cur = Get-ClusterPublicBaseUrl
    if (-not $cur) {
        if (-not $Quiet) { Write-Host '[lan] 集群内暂无 Baihua__PublicBaseUrl（ConfigMap 未就绪），跳过' }
        return
    }

    $want = "http://$lanIp"
    if ($cur.TrimEnd('/') -eq $want) {
        if (-not $Quiet) { Write-Host "[lan] 配对地址已一致：$cur" }
        return
    }

    if (-not $Quiet) { Write-Host "[lan] 配对地址需校正：$cur -> $want（滚动重启后端生效）" }
    $json = '{"data":{"Baihua__PublicBaseUrl":"' + $want + '"}}'
    wsl -u root -e bash -lc "k3s kubectl -n baihua patch configmap baihua-config --type merge -p '$json'" 2>&1 |
        ForEach-Object { if ($_ -notmatch '^\s*$') { Write-Host "  $_" } }
    wsl -u root -e bash -lc "k3s kubectl -n baihua rollout restart deployment/bh-server" 2>&1 |
        ForEach-Object { if ($_ -notmatch '^\s*$') { Write-Host "  $_" } }
}

# dashboard：CLI 在 WSL 里跑，打不开 Windows 的浏览器 —— 由本包装层代开。
if ($cmdArgs.Count -gt 0 -and $cmdArgs[0] -eq 'dashboard') {
    Ensure-LanExposure -Quiet
    $st0 = Get-LanExposureState
    $publicHost = $env:BAIHUA_PUBLIC_HOST
    if (-not $publicHost -and $st0.LanUp) { $publicHost = $st0.LanIp }

    $envPrefix = 'BAIHUA_DASHBOARD_PRINT_ONLY=1 '
    if ($publicHost) { $envPrefix += "BAIHUA_PUBLIC_HOST=$publicHost " }

    $out = wsl -u root -e bash -lc "cd '$wslRepo' && $envPrefix tools/bh/linux/k8s/bh.sh dashboard" 2>&1
    $out | Where-Object { $_ -notmatch '^URL=' } | ForEach-Object { Write-Host $_ }
    $urlLine = $out | Where-Object { $_ -match '^URL=' } | Select-Object -Last 1
    if (-not $urlLine) { Write-Warning '[dashboard] 未取到 URL（服务可能未就绪：bh status）'; exit 1 }

    $url = $urlLine.Substring(4).Trim()
    Write-Host ''
    Write-Host "[dashboard] 正在用 Windows 默认浏览器打开：$url"
    try { Start-Process $url } catch { Write-Warning "[dashboard] 自动打开失败，请手动复制：$url" }
    exit 0
}

# 局域网入口
if ($cmdArgs.Count -gt 0 -and $cmdArgs[0] -eq 'lan') {
    $sub = if ($cmdArgs.Count -gt 1) { $cmdArgs[1] } else { 'status' }
    switch ($sub) {
        'on'     { Ensure-LanExposure }
        'off'    {
            $script = Join-Path $Repo 'scripts\expose-k3s-lan.ps1'
            Write-Host '[lan] 撤销宿主 :80 转发（需要一次管理员授权）...'
            try {
                $p = Start-Process -FilePath 'pwsh' -Verb RunAs -PassThru -ArgumentList @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$script`"", '-Remove', '-Quiet'
                ) -ErrorAction Stop
                $null = Wait-Process -Id $p.Id -Timeout 120 -ErrorAction SilentlyContinue
            } catch { Write-Warning "[lan] 提权失败或被取消：请以管理员运行 pwsh -File `"$script`" -Remove" }
        }
        default  { Show-LanStatus }
    }
    exit 0
}

$code = Invoke-Cell $cmdArgs

# 后端类命令执行完顺带做两件事（都已就绪则静默）：
#   1) 校正配对地址（宿主 LAN IP 可能变过）
#   2) 确保局域网入口（未就绪才弹一次 UAC）
if ($cmdArgs.Count -gt 0 -and $cmdArgs[0] -in @('start', 'deploy', 'up', 'update', 'restart') -and $code -eq 0) {
    Sync-PublicBaseUrl
    Ensure-LanExposure
}

exit $code
