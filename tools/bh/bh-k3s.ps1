#requires -Version 5.1
<#
  bh-k3s - 百花 k3s（全容器化）部署 CLI —— Windows 包装层
  本脚本不含任何 native 逻辑：它把命令送进 WSL 执行 tools/bh/linux/k8s/bh.sh（root），
  并把 WSL 里做不到的事（打开 Windows 浏览器、宿主 :80 portproxy、配对地址校正）在 Windows 侧代做。

  前提：Windows 需要 WSL2 + 一个 Linux 发行版（k3s 是 Linux 运行时，Windows 无原生 k3s）。
        首次安装请在 WSL 内跑一键脚本（装 k3s + clone + bh-k3s install + up）：
          wsl -d Ubuntu            # 进入 WSL
          curl -fsSL https://raw.githubusercontent.com/luminsw/baihua/main/scripts/install-baihua.sh | bash
        然后在 Windows 侧装本包装层：bh-k3s install
        详见 tools/bh/README.md「首次运行」节。

  用法:
    bh-k3s <command> [args]    执行 k3s 命令（无参数 = status）
    bh-k3s install             把 %USERPROFILE%\.local\bin 加入用户 PATH（同时装 bh 与 bh-k3s）
    bh-k3s uninstall           移除定位器与 PATH 项
    bh-k3s help                帮助
#>
[CmdletBinding()]
param(
    # 注意：PowerShell 5.1 中 ValueFromRemainingArguments 会抢占位置参数绑定，
    # 因此不定义 Position 参数，全部裸参数收进 $All 后手动切分
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$All = @()
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSCommandPath          # tools/bh
$Repo = Split-Path -Parent (Split-Path -Parent $Root)   # 仓库根

$Arg1 = if ($All.Count -gt 0) { $All[0] } else { '' }
# @(...) 强制数组：强类型数组的单元素范围索引（$All[1..1]）会返回裸 string，
# 后续 @splat 会把字符串拆成字符数组，必须包成 object[]
$CmdArgs = @($All)

function Show-Help {
    Write-Host 'bh-k3s - 百花 k3s 部署 CLI（Windows 包装层，经 WSL 执行）'
    Write-Host ''
    Write-Host '用法:'
    Write-Host '  bh-k3s <command> [args]    执行 k3s 命令（无参数 = status）'
    Write-Host '  bh-k3s status              全局状态（pods / svc / pvc；DSH 插件用 --json）'
    Write-Host '  bh-k3s build [img...]      构建镜像（默认全部；如 build server webui）'
    Write-Host '  bh-k3s up                  全量重建 .NET 应用镜像 + deploy'
    Write-Host '  bh-k3s update              git pull + up'
    Write-Host '  bh-k3s deploy              仅 apply k8s/ 清单 + 滚动重启'
    Write-Host '  bh-k3s start|stop|restart <svc>   单服务伸缩/滚动重启'
    Write-Host '  bh-k3s logs <svc> [n]      tail pod 日志'
    Write-Host '  bh-k3s openvino <on|off|status>   Intel GPU 推理按需启停'
    Write-Host '  bh-k3s lan [on|off|status] 局域网入口（宿主 :80 -> WSL k3s Traefik）'
    Write-Host '  bh-k3s autostart [on|off|status]  WSL 保活 + 登录自启（防止集群被空闲回收）'
    Write-Host '  bh-k3s dashboard           打开 WebUI（cli-token 自动登录，调用 Windows 默认浏览器）'
    Write-Host '  bh-k3s prune / destroy     清空构建缓存 / 删除 baihua 命名空间'
    Write-Host '  bh-k3s install / uninstall 安装（bh + bh-k3s）/ 移除 定位器'
    Write-Host ''
    Write-Host 'native（Windows 本地 dotnet 进程）请用: bh <command>'
    Write-Host '完整说明见 tools/bh/README.md'
}

# install/uninstall：与 native 入口共用同一套定位器（安装内容一致，装哪个都行）
function Invoke-Install {
    & (Join-Path $Root 'bh.ps1') install
}
function Invoke-Uninstall {
    & (Join-Path $Root 'bh.ps1') uninstall
}

# 保活/自启：委托 scripts/bh-wsl.ps1（它负责 .wslconfig + 计划任务 + 常驻 wsl.exe 进程）
function Invoke-Wsl([string]$sub) {
    $script = Join-Path $Repo 'scripts\bh-wsl.ps1'
    if (-not (Test-Path $script)) { Write-Error "[autostart] 缺少 $script"; exit 1 }
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $script $sub
    exit $LASTEXITCODE
}

# 状态旁白：k3s 形态下最容易被误判成"服务起不来"的原因是 WSL 发行版被回收 —— 未保活时明确提示
function Show-KeepAliveHint {
    $script = Join-Path $Repo 'scripts\bh-wsl.ps1'
    if (-not (Test-Path $script)) { return }
    try {
        $out = & pwsh -NoProfile -ExecutionPolicy Bypass -File $script status 2>&1 | Out-String
    } catch { return }
    if ($out -match '未检测到保活进程') {
        Write-Host ''
        Write-Warning '[bh-k3s] 未检测到 WSL 保活进程：发行版空闲后被回收会导致整个集群中断（容器 restart 计数暴涨）。'
        Write-Warning '         修复: bh-k3s autostart on'
    } elseif ($out -match '登录自启未安装') {
        Write-Host ''
        Write-Warning '[bh-k3s] 保活进程在运行，但未安装登录自启：重启/注销后需手动拉起。修复: bh-k3s autostart on'
    }
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
        $code = $_.Exception.Response.StatusCode.value__
        return ($code -ge 200 -and $code -lt 400)
    }
}

if ($Arg1 -in @('help', '-h', '--help')) { Show-Help; exit 0 }
if ($Arg1 -eq 'install') { Invoke-Install; exit 0 }
if ($Arg1 -eq 'uninstall') { Invoke-Uninstall; exit 0 }
# autostart：Windows 侧保活/自启（不启 WSL 也能看 status）
if ($Arg1 -eq 'autostart') {
    $sub = if ($All.Count -gt 1) { $All[1] } else { 'status' }
    switch ($sub.ToLower()) {
        'on'  { Invoke-Wsl 'install' }
        'off' { Invoke-Wsl 'uninstall' }
        default { Invoke-Wsl 'status' }
    }
}
# 前缀冲突提示：旧写法是 bh k8s <cmd>，现在 k8s 不再是子命令
if ($Arg1 -eq 'k8s') {
    Write-Error "[bh-k3s] 'k8s' 不再是子命令：k3s 部署已独立为 bh-k3s，请直接写: bh-k3s $($All | Select-Object -Skip 1 | ForEach-Object { $_ })"
    exit 1
}

# WSL 前置检查：给出可执行的修复指引，而不是让 wslpath 报一句英文错就结束
$wslOk = $true
try {
    $null = wsl -l -q 2>&1
    if ($LASTEXITCODE -ne 0) { $wslOk = $false }
} catch { $wslOk = $false }
if (-not $wslOk) {
    Write-Error @'
[bh-k3s] 未检测到可用的 WSL。k3s 是 Linux 运行时，Windows 上必须经 WSL2 运行。
  1) 安装 WSL2 与发行版（管理员 PowerShell）: wsl --install -d Ubuntu
  2) 进入 WSL 安装 k3s + 百花: wsl -d Ubuntu
       curl -fsSL https://raw.githubusercontent.com/luminsw/baihua/main/scripts/install-baihua.sh | bash
  3) 回到 Windows 装本包装层: bh-k3s install
  详见 tools/bh/README.md「首次运行」
'@
    exit 1
}

# 无参数默认 status（与 WSL 内 bh-k3s 的默认一致；native 侧无参数是 dashboard，两者前提不同）
if ($CmdArgs.Count -eq 0) { $CmdArgs = @('status') }

$wslRepo = (wsl wslpath -u ($Repo -replace '\\', '/') 2>$null | Out-String).Trim()
if (-not $wslRepo) {
    Write-Error '[bh-k3s] wslpath 不可用（WSL 已安装但无法访问该路径？确认发行版已启动且仓库在 WSL 可见路径下）'
    exit 1
}

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
        if (-not $Quiet) { Write-Host "[lan] 后端未在 WSL 内就绪（http://$($st.WslIp)/health 不通），先 bh-k3s start/deploy" }
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
    Write-Host "[lan] 后端可用: $(if ($st.BackendUp) { '是' } else { '否（先 bh-k3s start/deploy）' })"
    Write-Host "[lan] 局域网入口: $(if ($st.LanUp) { "已就绪 http://$($st.LanIp)/" } else { '未就绪（bh-k3s lan on 配置，或 WSL 重启后重跑）' })"
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
if ($CmdArgs[0] -eq 'dashboard') {
    Ensure-LanExposure -Quiet
    $st0 = Get-LanExposureState
    $publicHost = $env:BAIHUA_PUBLIC_HOST
    if (-not $publicHost -and $st0.LanUp) { $publicHost = $st0.LanIp }

    $envPrefix = 'BAIHUA_DASHBOARD_PRINT_ONLY=1 '
    if ($publicHost) { $envPrefix += "BAIHUA_PUBLIC_HOST=$publicHost " }

    $out = wsl -u root -e bash -lc "cd '$wslRepo' && $envPrefix tools/bh/linux/k8s/bh.sh dashboard" 2>&1
    $out | Where-Object { $_ -notmatch '^URL=' } | ForEach-Object { Write-Host $_ }
    $urlLine = $out | Where-Object { $_ -match '^URL=' } | Select-Object -Last 1
    if (-not $urlLine) { Write-Warning '[dashboard] 未取到 URL（服务可能未就绪：bh-k3s status）'; exit 1 }

    $url = $urlLine.Substring(4).Trim()
    Write-Host ''
    Write-Host "[dashboard] 正在用 Windows 默认浏览器打开：$url"
    try { Start-Process $url } catch { Write-Warning "[dashboard] 自动打开失败，请手动复制：$url" }
    exit 0
}

# 局域网入口
if ($CmdArgs[0] -eq 'lan') {
    $sub = if ($CmdArgs.Count -gt 1) { $CmdArgs[1] } else { 'status' }
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

$code = Invoke-Cell $CmdArgs

# status：顺带诊断"集群是不是被 WSL 回收拖垮的"（--json 时保持纯 JSON，供 DSH 插件消费）
if ($CmdArgs[0] -eq 'status' -and $CmdArgs -notcontains '--json') { Show-KeepAliveHint }

# 后端类命令执行完顺带做两件事（都已就绪则静默）：
#   1) 校正配对地址（宿主 LAN IP 可能变过）
#   2) 确保局域网入口（未就绪才弹一次 UAC）
if ($CmdArgs[0] -in @('start', 'deploy', 'up', 'update', 'restart') -and $code -eq 0) {
    Sync-PublicBaseUrl
    Ensure-LanExposure
}

exit $code
