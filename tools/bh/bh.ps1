#requires -Version 5.1
<#
  bh - baihua 统一 CLI 入口（Windows）
  在 Windows 上经 WSL 调用 Linux k3s cell（tools/bh/linux/k8s/bh.sh）。

  部署形态只有一种：Linux k3s（含 PostgreSQL / 后端 / WebUI / OVMS 全部容器化）。
  合并为单进程 + 单库后不再需要 native / docker 两套 cell 脚本。

  用法:
    bh <command> [args]          执行命令（可选写 bh k8s <command> 兼容旧习惯）
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
    'k8s' = @{ Script = 'linux\k8s\bh.sh'; Desc = 'Linux k3s（经 WSL，root）——唯一部署形态' }
}

function Show-Help {
    Write-Host 'bh - baihua 统一 CLI（Windows，经 WSL 调 Linux k3s）'
    Write-Host ''
    Write-Host '用法:'
    Write-Host '  bh <command> [args]           执行命令'
    Write-Host '  bh k8s <command> [args]       同上（显式写 cell，兼容旧习惯）'
    Write-Host '  bh lan [on|off|status]        局域网入口（宿主 :80 -> WSL k3s），status 为默认'
    Write-Host '  bh install / uninstall        加入 / 移出用户 PATH'
    Write-Host ''
    Write-Host '说明: start/deploy/up/update/restart/dashboard 会自动确保局域网入口（首次弹一次 UAC；已就绪则静默）'
    Write-Host '      start/deploy/up/update/restart 还会把配对地址（Baihua__PublicBaseUrl）校正为当前宿主 LAN IP'
    Write-Host ''
    Write-Host '部署形态: Linux k3s（PostgreSQL + 后端 + WebUI + OVMS 全部容器化）'
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

$cell = $Arg1.ToLower()
if ($cell -in @('install', 'uninstall')) {
    if ($cell -eq 'install') { Invoke-Install } else { Invoke-Uninstall }
    exit 0
}

if ($cell -in @('help', '-h', '--help')) {
    Show-Help
    exit 0
}

# 统一入口：Windows 上一律经 WSL 调用 Linux k3s cell
# （可选显式写 bh k8s <command>，与旧用法兼容）
$Rest = if ($Cells.ContainsKey($cell)) { @($All | Select-Object -Skip 1) } else { @($All) }
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
# 背景：k3s 跑在 WSL 里，Traefik 绑的是 WSL 的 :80（172.30.x.x）。Windows 本机能访问，
# 但手机/局域网设备访问不到，需要在宿主做一次 netsh portproxy 转发（管理员）。
# 为了不让用户记脚本，这里把它变成 bh 的自动行为：
#   - 用**连通性**判断（不解析 netsh 输出，避免中文系统/格式差异）：宿主 LAN IP 的 /health 通 = 已就绪
#   - 不通且后端在跑 → 自动以管理员身份执行 scripts\expose-k3s-lan.ps1（弹一次 UAC），做完复检
#   - WSL 重启导致 WSL IP 变化时，同一个检测会发现"过期"并自动重做（幂等）
#   - 若 WSL 已是 mirrored 网络模式，宿主 IP 直接就有 :80，检测直接通过，永远不会弹 UAC

function Get-WslIp {
    $ip = (wsl -e bash -lc "hostname -I | awk '{print `$1}'" 2>$null | Out-String).Trim()
    if ($ip -match '^\d+\.\d+\.\d+\.\d+$') { return $ip }
    return ''
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

function Test-Http([string]$Url) {
    try {
        $r = Invoke-WebRequest -Uri $Url -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop
        return ($r.StatusCode -ge 200 -and $r.StatusCode -lt 400)
    } catch {
        # 302 之类会抛异常，但响应码本身说明服务在
        $code = $_.Exception.Response.StatusCode.value__
        return ($code -ge 200 -and $code -lt 400)
    }
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

    # 需要（重新）转发：自动提权执行
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
# 背景：移动端配对二维码 / 服务器互联广播里的地址取自 ConfigMap 的 Baihua__PublicBaseUrl。
# 容器里探测到的是 Pod IP，没法自动得出宿主地址；而宿主 LAN IP 是 DHCP 的，会变。
# 因此由 Windows 侧（唯一知道真实宿主 IP 的地方）在 start/deploy/up/restart 后校正一次：
#   值已一致 → 静默；不一致 → patch ConfigMap + 滚动重启 bh-server（env 是启动时读入的）
# k8s/01-configmap.yaml 里保留该键（值仅作占位），以免 kubectl apply 时把键删掉导致回退到 Pod IP。

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

# dashboard 特殊处理：CLI 在 WSL 里跑，打不开 Windows 的浏览器 —— 由本包装层代开。
if ($cell -eq 'dashboard' -or ($Rest.Count -gt 0 -and $Rest[0] -eq 'dashboard')) {
    # 公开地址：默认用 WSL 的 IP；做过宿主转发（或 mirrored）后用宿主 IP，手机也能打开
    $st0 = Get-LanExposureState
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

# 局域网入口是 Windows 宿主侧的概念，只有这几个命令需要顺带确保
if ($cell -eq 'lan' -or ($Rest.Count -gt 0 -and $Rest[0] -eq 'lan')) {
    $sub = if ($cell -eq 'lan') { if ($Rest.Count -gt 0) { $Rest[0] } else { 'status' } } else { if ($Rest.Count -gt 1) { $Rest[1] } else { 'status' } }
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

$code = Invoke-Cell $Rest

# 后端类命令执行完顺带做两件事（都已就绪则静默）：
#   1) 校正配对地址（宿主 LAN IP 可能变过）
#   2) 确保局域网入口（未就绪才弹一次 UAC）
if ($Rest.Count -gt 0 -and $Rest[0] -in @('start', 'deploy', 'up', 'update', 'restart') -and $code -eq 0) {
    Sync-PublicBaseUrl
    Ensure-LanExposure
}

exit $code
