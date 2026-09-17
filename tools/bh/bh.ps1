#requires -Version 5.1
<#
  bh - 百花 CLI（Windows，native 形态：dotnet 进程直接跑在 Windows 上）
  不依赖 WSL/k3s；委托 tools/bh/win/native/bh.ps1 执行（PostgreSQL 由用户自行安装）。

  k3s（全容器化，经 WSL）已独立为另一个命令：bh-k3s <command>。

  用法:
    bh <command> [args]          执行 native 命令（委托 win/native/bh.ps1）
    bh lan [on|off|status]       局域网入口（宿主 :80 -> :8788 portproxy）
    bh install                   复制定位器到 %USERPROFILE%\.local\bin（装 bh 与 bh-k3s，加入用户 PATH）
    bh uninstall                 移除定位器与 PATH 项
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
    Write-Host 'bh - 百花 CLI（Windows native：dotnet 进程，不依赖 WSL/k3s）'
    Write-Host ''
    Write-Host '用法:'
    Write-Host '  bh                           打开管理面板（= bh dashboard）'
    Write-Host '  bh <command> [args]           执行 native 命令'
    Write-Host '  bh lan [on|off|status]        局域网入口（宿主 :80 -> :8788 portproxy）'
    Write-Host '  bh install / uninstall        安装（bh + bh-k3s）/ 移除 定位器与用户 PATH'
    Write-Host ''
    Write-Host 'native 命令: build / build-restart / start / stop / restart / update'
    Write-Host '             status [--json] / logs <svc> [n] / dashboard / open / open-webui'
    Write-Host 'native 依赖: .NET SDK 10、本机 PostgreSQL（Windows 服务）、可选 OVMS(:8000)'
    Write-Host ''
    Write-Host 'k3s（全容器化，经 WSL）已独立为另一个命令: bh-k3s <command>'
    Write-Host '  例: bh-k3s status / bh-k3s build server webui / bh-k3s up / bh-k3s dashboard'
    Write-Host ''
    Write-Host '完整说明见 tools/bh/README.md'
}

function Invoke-Install {
    # 复制自包含定位器到 %USERPROFILE%\.local\bin，并把该目录加入用户 PATH。
    # bh 与 bh-k3s 各自一个定位器（同一套定位逻辑，只是转发的入口脚本不同），
    # 装哪个入口都一样——这里一次把两个都装上，避免"装了 bh 却没有 bh-k3s"。
    # 定位器每次调用时自动定位仓库根（BAIHUA_HOME > 常见路径 > 向上查找），
    # 仓库改名/移动后无需重装。
    $bin = Join-Path $HOME '.local\bin'
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    $files = @(
        @{ Src = 'locator.ps1';     Dst = 'bh.ps1' },
        @{ Src = 'bh.cmd';          Dst = 'bh.cmd' },
        @{ Src = 'locator-k3s.ps1'; Dst = 'bh-k3s.ps1' },
        @{ Src = 'bh-k3s.cmd';      Dst = 'bh-k3s.cmd' }
    )
    foreach ($f in $files) {
        $src = Join-Path $Root $f.Src
        if (-not (Test-Path $src)) { Write-Warning "[install] 缺少 $src（跳过 $($f.Dst)）"; continue }
        Copy-Item $src (Join-Path $bin $f.Dst) -Force
    }
    Write-Host "[install] 已安装定位器: bh / bh-k3s -> $bin"

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
    Write-Host '[install] 完成。新开终端后可直接使用: bh <command> / bh-k3s <command>'
    Write-Host '         当前会话请用: .\tools\bh\bh.ps1 <command>'
    Write-Host '[install] 定位器自动查找: $env:BAIHUA_HOME > 常见路径 > 当前目录向上；仓库改名/移动后无需重新安装'
    Write-Host '[install] 注意: bh / bh-k3s 只装 Windows 侧定位器；k3s 需另行在 WSL 内完成首次安装（见 tools/bh/README.md）'
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
    foreach ($n in @('bh.ps1', 'bh.cmd', 'bh-k3s.ps1', 'bh-k3s.cmd')) {
        Remove-Item (Join-Path $bin $n) -Force -ErrorAction SilentlyContinue
    }
    Write-Host '[uninstall] 已移除 %USERPROFILE%\.local\bin\bh.ps1 / bh.cmd / bh-k3s.ps1 / bh-k3s.cmd'
}

# ---- 共享工具函数 ----

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

# ---- 命令分派 ----
if ($Arg1 -in @('install', 'uninstall')) {
    if ($Arg1 -eq 'install') { Invoke-Install } else { Invoke-Uninstall }
    exit 0
}
if ($Arg1 -in @('help', '-h', '--help')) { Show-Help; exit 0 }
# 旧写法是 bh k8s <cmd>；k3s 已独立成命令，这里给出可执行的迁移指引而不是静默当成 native 命令
if ($Arg1 -eq 'k8s') {
    $rest = (@($All | Select-Object -Skip 1) -join ' ')
    Write-Warning "[bh] 'k8s' 不再是子命令：k3s 部署已独立为 bh-k3s。请改用: bh-k3s $rest"
    exit 1
}
# 旧写法是 bh native <cmd>；native 就是本命令的默认形态，前缀已无意义
if ($Arg1 -eq 'native') {
    $rest = (@($All | Select-Object -Skip 1) -join ' ')
    Write-Warning "[bh] 'native' 前缀已移除（bh 本身就是 native 形态）。请直接: bh $rest"
    $All = @($All | Select-Object -Skip 1)
    $Arg1 = if ($All.Count -gt 0) { $All[0] } else { '' }
    $CmdArgs = @($All)
    if ($CmdArgs.Count -eq 0) { Show-Help; exit 0 }
}

# ==================== native ====================
$nativeScript = Join-Path $Root 'win\native\bh.ps1'
if (-not (Test-Path $nativeScript)) { Write-Error "[native] 缺少 $nativeScript"; exit 1 }

# lan 命令：native 下做宿主 :80 -> :8788 portproxy（移动端默认无端口访问）
if ($Arg1 -eq 'lan') {
    $sub = if ($CmdArgs.Count -gt 1) { $CmdArgs[1] } else { 'status' }
    switch ($sub) {
        'on' {
            $lanIp = Get-HostLanIp
            if (-not $lanIp) { Write-Host '[lan] 未识别到宿主局域网 IP'; exit 0 }
            Write-Host '[lan] 设置宿主 :80 -> :8788 转发（需要管理员授权）...'
            try {
                # 先删掉 :80 上的旧规则（含 k3s 形态遗留的 :80 -> <WSL IP>:80——它指向
                # 已消失的 WSL IP，只 add 不 delete 会与之并存）；再重建 :80 -> 127.0.0.1:8788。
                $p = Start-Process -FilePath 'pwsh' -Verb RunAs -PassThru -ArgumentList @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
                    'netsh interface portproxy delete v4tov4 listenport=80 listenaddress=0.0.0.0 | Out-Null; netsh interface portproxy delete v4tov4 listenport=80 listenaddress=* | Out-Null; netsh interface portproxy add v4tov4 listenport=80 connectport=8788 connectaddress=127.0.0.1; netsh advfirewall firewall add rule name="Baihua LAN 80" dir=in action=allow protocol=TCP localport=80'
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
            # 只认 "listenport=80 → connectaddress=127.0.0.1 connectport=8788" 的规则：
            # k3s 形态会留下 :80 -> <WSL IP>:80 的规则，仅凭端口号 80 判断会误报"已就绪"，
            # 而那条规则指向已消失的 WSL IP —— 连接被接受但永不响应（表现为 HTTP 挂死超时）。
            $rows = @($proxy -split "`r?`n" | Where-Object { $_ -match '^\s*\S+\s+80\s+\S+\s+\d+' })
            $good = @($rows | Where-Object { $_ -match '127\.0\.0\.1\s+8788' })
            $bad = @($rows | Where-Object { $_ -notmatch '127\.0\.0\.1\s+8788' })
            Write-Host "[lan] 宿主 IP: $lanIp"
            Write-Host "[lan] 后端端口: 8788（server 绑 0.0.0.0）"
            if ($good.Count -gt 0) {
                Write-Host "[lan] 局域网入口: 已就绪 http://$lanIp/  （:80 -> 127.0.0.1:8788）"
            } elseif ($rows.Count -gt 0) {
                Write-Host "[lan] 局域网入口: 规则指向错误 → $($rows[0].Trim())"
                Write-Host "[lan] 该转发会挂死（目标不可达）：执行 bh lan on 重建为 :80 -> 127.0.0.1:8788"
            } else {
                Write-Host "[lan] 局域网入口: 未就绪（bh lan on 配置 :80 转发）"
                Write-Host "[lan] 备选: 直接用 http://$lanIp`:8788/ （需放行防火墙 TCP 8788）"
            }
            if ($bad.Count -gt 0) {
                Write-Host "[lan] 残留 :80 规则（会挂死/可能抢占）："
                $bad | ForEach-Object { Write-Host "[lan]   $($_.Trim())" }
                Write-Host "[lan] 建议执行 bh lan on 清理并重建"
            }
        }
    }
    exit 0
}

# 其余命令直接委托给 native 脚本
& $nativeScript @CmdArgs
exit $LASTEXITCODE
