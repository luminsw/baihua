#requires -Version 5.1
<#
  bh - 百花 CLI（Windows）

  环境边界（2026-11-22 约定）：只支持纯 Windows / 纯 Linux，不支持在 WSL 里开发。
  Windows 只有一种运行形态（2026-11-25 起）：
    bh <command>       → native：dotnet 进程 + SQLite（bh-server 8788 / bh-webui 5177）
    bh dev <command>   → 等价别名（历史保留，转发同一实现）
  wslc 容器形态已退役（2026-11）：bh wslc / bh docker 会明确报错。
  可选容器组件（仍走 WSL3 自带 wslc.exe，不需要 Docker Desktop）：
    ovms-gpu（本地推理 :8000）、bh-open-webui（AI 对话页 :8080），由 bh ovms / bh open-webui 管理。
  Linux 只有原生 systemd + SQLite 形态（tools/bh/linux/native/bh.sh）。

  用法:
    bh <command> [args]          native 形态（默认）
    bh lan [on|off|status]       局域网入口（宿主 :80 -> :8788 portproxy）
    bh install                   复制定位器到 %USERPROFILE%\.local\bin（加入用户 PATH）
    bh uninstall                 移除定位器与 PATH 项
#>
# 不用 param()/CmdletBinding：`-e` / `-f` / `--json` / `-Prebuilt` 这类子命令选项必须**原样**透传，
# 而 CmdletBinding 会先把 `-e` 当 PowerShell 参数解析（与 -ErrorAction 二义性 → 直接报错）。
# 用自动变量 $args 全量收齐，再手动切分。
$All = @($args)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSCommandPath          # tools/bh
$Repo = Split-Path -Parent (Split-Path -Parent $Root)   # 仓库根 / 部署包根
# 提权子进程首选 PowerShell 7（pwsh），没有就退回 Windows PowerShell
$ShellExe = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

$Arg1 = if ($All.Count -gt 0) { $All[0] } else { '' }
# @(...) 强制数组：强类型数组的单元素范围索引（$All[1..1]）会返回裸 string，
# 后续 @splat 会把字符串拆成字符数组，必须包成 object[]
$CmdArgs = @($All)

# 判断当前根是否为开发仓库（部署包不含 services/ 源码）
$IsDevRepo = Test-Path (Join-Path $Repo 'services\Baihua.Server\Baihua.Server.csproj')

function Show-Help {
    Write-Host 'bh - 百花 CLI（Windows，native 形态：dotnet 进程 + SQLite）'
    Write-Host ''
    Write-Host '用法:'
    Write-Host '  bh                           状态总览'
    Write-Host '  bh <command> [args]          native 形态命令（默认）'
    Write-Host '  bh dev <command> [args]      等价别名（历史保留，同一实现）'
    Write-Host '  bh doctor [--json] [--fix]   体检（SQLite / 产物 / lan / 容器组件）'
    Write-Host '  bh autostart [on|off|status] 登录自启（用户级 Startup，不需要管理员）'
    Write-Host '  bh lan [on|off|status]       局域网入口（宿主 :80 -> :8788 portproxy）'
    Write-Host '  bh ovms [status|start|stop|restart|logs [n]]   本地模型服务 OVMS(:8000，wslc 容器 ovms-gpu)'
    Write-Host '  bh open-webui <start|stop|status|install>      AI 对话页（wslc 容器 bh-open-webui）'
    Write-Host '  bh backup [目录] / restore <zip> [-y]          数据冷备份 / 恢复（~/.baihua）'
    Write-Host '  bh install / uninstall       安装 / 移除 定位器与用户 PATH'
    Write-Host ''
    Write-Host '常用命令: build / build-restart / start / stop [svc] / restart / update / logs / status [--json]'
    Write-Host '          dashboard / open / env / doctor / autostart / backup / restore'
    Write-Host ''
    Write-Host '服务: bh-server 8788（唯一后端，绑 0.0.0.0）、bh-webui 5177（回环）'
    Write-Host '数据: %USERPROFILE%\.baihua（BAIHUA_DATA_HOME 可覆盖；SQLite 单库 db\baihua.db）'
    Write-Host '依赖: .NET SDK 10（开发仓库 bh build 需要；部署包 apps\ 为自包含产物，无需 .NET）'
    Write-Host '可选: ovms-gpu / bh-open-webui 容器需 WSL >= 2.9.3（wslc.exe），缺失仅本地推理/AI 对话页降级'
    Write-Host ''
    Write-Host '已退役: wslc 容器栈（2026-11）、Docker Desktop 形态、k3s/k8s、PostgreSQL（旧库用 bh db-migrate 迁 SQLite）'
    Write-Host ''
    Write-Host '完整说明见 tools/bh/README.md'
}

function Invoke-Install {
    # 复制自包含定位器到 %USERPROFILE%\.local\bin，并把该目录加入用户 PATH。
    # 定位器每次调用时自动定位根（BAIHUA_PKG_HOME > BAIHUA_HOME > 常见路径 > 向上查找），
    # 仓库/部署包改名、移动后无需重装。
    # k3s 已退役：install 不再装 bh-k3s，并主动清理旧版安装的 bh-k3s 定位器。
    $bin = Join-Path $HOME '.local\bin'
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    $files = @(
        @{ Src = 'locator.ps1'; Dst = 'bh.ps1' },
        @{ Src = 'bh.cmd';      Dst = 'bh.cmd' }
    )
    foreach ($f in $files) {
        $src = Join-Path $Root $f.Src
        if (-not (Test-Path $src)) { Write-Warning "[install] 缺少 $src（跳过 $($f.Dst)）"; continue }
        Copy-Item $src (Join-Path $bin $f.Dst) -Force
    }
    Write-Host "[install] 已安装定位器: bh -> $bin"

    # 清理旧版遗留的 bh-k3s 定位器（k3s 形态已退役）
    $legacy = @('bh-k3s.ps1', 'bh-k3s.cmd') | ForEach-Object { Join-Path $bin $_ }
    $removed = @($legacy | Where-Object { Test-Path $_ })
    if ($removed.Count -gt 0) {
        $removed | ForEach-Object { Remove-Item $_ -Force -ErrorAction SilentlyContinue }
        Write-Host '[install] 已清理 k3s 形态遗留定位器: bh-k3s.ps1 / bh-k3s.cmd'
    }

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

    # 部署包（无 services/ 源码）安装后，定位器依赖 BAIHUA_PKG_HOME 才能找到包目录。
    # ⚠ 包定位用 BAIHUA_PKG_HOME（2026-11-25 起）；BAIHUA_HOME 语义是**数据目录**（服务读
    #   %USERPROFILE%\.baihua），两者解耦，不再把包根塞进 BAIHUA_HOME。
    #   旧版包安装遗留的 BAIHUA_HOME=<包根> 同步清除（值精确匹配本根才动，不误伤用户自设值）。
    # 必须设 $Repo（含 tools\bh 的根），不是 $Root（tools\bh 本身）。
    if (-not $IsDevRepo) {
        [Environment]::SetEnvironmentVariable('BAIHUA_PKG_HOME', $Repo, 'User')
        Write-Host "[install] 已设置 BAIHUA_PKG_HOME=$Repo（部署包定位）"
        $legacyHome = [Environment]::GetEnvironmentVariable('BAIHUA_HOME', 'User')
        if ($legacyHome -and ($legacyHome.TrimEnd('\') -ieq $Repo.TrimEnd('\'))) {
            [Environment]::SetEnvironmentVariable('BAIHUA_HOME', $null, 'User')
            Write-Host "[install] 已清除旧版包安装遗留的 BAIHUA_HOME=$Repo（定位改用 BAIHUA_PKG_HOME）"
        }
    }
    Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@ -ErrorAction SilentlyContinue
    $null = [Win32.Native]::SendMessageTimeout([IntPtr]::Zero, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]([UIntPtr]::Zero))
    Write-Host '[install] 完成。新开终端后可直接使用: bh <command>'
    Write-Host '         当前会话请用: .\tools\bh\bh.ps1 <command>'
    Write-Host '[install] 定位器自动查找: $env:BAIHUA_PKG_HOME > $env:BAIHUA_HOME > 常见路径 > 当前目录向上；改名/移动后无需重新安装'
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
    Write-Host '[uninstall] 已移除 %USERPROFILE%\.local\bin\bh.ps1 / bh.cmd（含 k3s 遗留 bh-k3s.* 如有）'
    # 部署包形态的 install 设过用户级 BAIHUA_PKG_HOME=$Repo（见 Invoke-Install），uninstall 是逆操作。
    # 守卫①：本根不是 dev 仓库（无 services\Baihua.Server）——dev 仓库的 install 从不设 BAIHUA_PKG_HOME；
    # 守卫②：当前用户级值确实指向本根才删——用户手动设的其它值不误伤。
    # 兼容：旧版包安装设的是 BAIHUA_HOME=<包根>，同样按值匹配清除。
    if (-not $IsDevRepo) {
        $pkgHome = [Environment]::GetEnvironmentVariable('BAIHUA_PKG_HOME', 'User')
        if ($pkgHome -and ($pkgHome.TrimEnd('\') -ieq $Repo.TrimEnd('\'))) {
            [Environment]::SetEnvironmentVariable('BAIHUA_PKG_HOME', $null, 'User')
            Write-Host "[uninstall] 已清除用户级 BAIHUA_PKG_HOME=$Repo（部署包定位）"
        }
        $legacyHome = [Environment]::GetEnvironmentVariable('BAIHUA_HOME', 'User')
        if ($legacyHome -and ($legacyHome.TrimEnd('\') -ieq $Repo.TrimEnd('\'))) {
            [Environment]::SetEnvironmentVariable('BAIHUA_HOME', $null, 'User')
            Write-Host "[uninstall] 已清除旧版包安装遗留的 BAIHUA_HOME=$Repo"
        }
    }
    # 广播环境变量变更（PATH 移除 / 定位变量清除对 explorer 新开终端立即生效）
    Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@ -ErrorAction SilentlyContinue
    $null = [Win32.Native]::SendMessageTimeout([IntPtr]::Zero, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]([UIntPtr]::Zero))
    Write-Host '[uninstall] 完成。数据目录 %USERPROFILE%\.baihua 不受影响（需要时手动清理）'
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
# 旧写法 bh k8s <cmd> / bh-k3s <cmd>：k3s 只属于 Linux，给出明确指引而非静默失败
if ($Arg1 -eq 'k8s') {
    Write-Warning "[bh] 'k8s' 不是 Windows 子命令：k3s 只在 Linux（tools/bh/linux/k8s/bh.sh）。"
    Write-Warning "[bh] Windows 只有 native 形态（dotnet 进程 + SQLite）；不支持在 WSL 里开发或跑 k3s。"
    exit 1
}

# 'dev' / 'native' 前缀：历史别名，native 现已是默认形态，剥掉前缀继续分派即可。
if ($Arg1 -in @('dev', 'native')) {
    $CmdArgs = @($CmdArgs | Select-Object -Skip 1)
    $Arg1 = if ($CmdArgs.Count -gt 0) { $CmdArgs[0] } else { '' }
    # --json 时不能往 stdout 混横幅（DSH 插件要解析纯 JSON）
    if (-not ($CmdArgs -contains '--json')) {
        Write-Host '[bh] dev/native 已等价于 bh（native 自 2026-11-25 起为 Windows 默认形态）' -ForegroundColor DarkGray
    }
}

# wslc / docker 容器形态已退役（2026-11）：Windows 统一 native + SQLite
if ($Arg1 -in @('wslc', 'container', 'docker')) {
    Write-Warning "[bh] wslc 容器形态已退役（2026-11）：Windows 统一 native + SQLite（dotnet 进程，数据 %USERPROFILE%\.baihua）。"
    Write-Warning "[bh] 历史数据迁移：旧 PostgreSQL 用 bh db-migrate（tools/db-migrate）迁入 SQLite。"
    Write-Warning "[bh] 当前命令请直接用: bh build / bh start / bh status"
    exit 1
}

$nativeScript = Join-Path $Root 'win\native\bh.ps1'

# ==================== 形态无关命令（实现在 native cell） ====================
# ovms / openvino：OVMS(:8000) wslc 容器 ovms-gpu，逻辑与形态无关
if ($Arg1 -in @('ovms', 'openvino')) {
    if (-not (Test-Path $nativeScript)) { Write-Error "[bh] 缺少 $nativeScript"; exit 1 }
    & $nativeScript @CmdArgs
    exit $LASTEXITCODE
}
# open-webui：AI 对话页（wslc 容器 bh-open-webui，2026-11-25 起由 native cell 管理）
if ($Arg1 -eq 'open-webui') {
    if (-not (Test-Path $nativeScript)) { Write-Error "[bh] 缺少 $nativeScript"; exit 1 }
    & $nativeScript @CmdArgs
    exit $LASTEXITCODE
}

# ==================== lan：宿主 :80 -> :8788 portproxy ====================
# native 形态的局域网入口（浏览器 + 花记移动端统一入口；移动端默认 80 端口无显式端号）。
# lan 结果实测：提权子进程被 UAC 取消时也可能走"成功"路径，必须用 netsh 复核
function Test-LanPortProxy {
    $proxy = (netsh interface portproxy show v4tov4 2>$null | Out-String)
    $rows = @($proxy -split "`r?`n" | Where-Object { $_ -match '^\s*\S+\s+80\s+\S+\s+\d+' })
    return (@($rows | Where-Object { $_ -match '127\.0\.0\.1\s+8788' }).Count -gt 0)
}
function Test-LanFirewallRule {
    $null = netsh advfirewall firewall show rule name="Baihua LAN 80" 2>$null
    return ($LASTEXITCODE -eq 0)
}

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
                $p = Start-Process -FilePath $ShellExe -Verb RunAs -PassThru -ArgumentList @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
                    'netsh interface portproxy delete v4tov4 listenport=80 listenaddress=0.0.0.0 | Out-Null; netsh interface portproxy delete v4tov4 listenport=80 listenaddress=* | Out-Null; netsh interface portproxy add v4tov4 listenport=80 connectport=8788 connectaddress=127.0.0.1; netsh advfirewall firewall add rule name="Baihua LAN 80" dir=in action=allow protocol=TCP localport=80'
                ) -ErrorAction Stop
                $null = Wait-Process -Id $p.Id -Timeout 30 -ErrorAction SilentlyContinue
                if ((Test-LanPortProxy) -and (Test-LanFirewallRule)) {
                    Write-Host "[lan] 局域网入口就绪：http://$lanIp/  （-> 127.0.0.1:8788）"
                } else {
                    Write-Warning '[lan] 未生效：UAC 被取消或无管理员权限。请用管理员 PowerShell 执行：'
                    Write-Warning '       netsh interface portproxy add v4tov4 listenport=80 connectport=8788 connectaddress=127.0.0.1'
                    Write-Warning '       netsh advfirewall firewall add rule name="Baihua LAN 80" dir=in action=allow protocol=TCP localport=80'
                    exit 1
                }
            } catch { Write-Warning '[lan] 提权失败或被取消' }
        }
        'off' {
            Write-Host '[lan] 撤销宿主 :80 转发（需要管理员授权）...'
            try {
                $p = Start-Process -FilePath $ShellExe -Verb RunAs -PassThru -ArgumentList @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
                    'netsh interface portproxy delete v4tov4 listenport=80 listenaddress=0.0.0.0; netsh advfirewall firewall delete rule name="Baihua LAN 80"'
                ) -ErrorAction Stop
                $null = Wait-Process -Id $p.Id -Timeout 30 -ErrorAction SilentlyContinue
                if (-not (Test-LanPortProxy)) {
                    Write-Host '[lan] 已撤销'
                } else {
                    Write-Warning '[lan] 未生效：UAC 被取消或无管理员权限。请用管理员 PowerShell 执行：'
                    Write-Warning '       netsh interface portproxy delete v4tov4 listenport=80 listenaddress=0.0.0.0'
                    Write-Warning '       netsh advfirewall firewall delete rule name="Baihua LAN 80"'
                    exit 1
                }
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

# ==================== 默认：native（Windows 唯一形态） ====================
if (-not (Test-Path $nativeScript)) { Write-Error "[bh] 缺少 $nativeScript"; exit 1 }
$rest = @($CmdArgs)
if ($rest.Count -eq 0) { $rest = @('status') }
& $nativeScript @rest
exit $LASTEXITCODE
