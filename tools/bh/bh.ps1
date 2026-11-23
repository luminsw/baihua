#requires -Version 5.1
<#
  bh - 百花 CLI（Windows）

  环境边界（2026-11-22 约定）：只支持纯 Windows / 纯 Linux，不支持在 WSL 里开发。
  Windows 有两种互斥运行方式（同占 8788/5177）：
    bh <command>       → wslc 容器（默认，面向最终用户：postgres/server/webui/nginx）
    bh dev <command>   → native dotnet 进程（开发/调试：VS F5、快速 publish）
  wslc 依赖 WSL3 自带的 wslc.exe 作为运行时，但这不等于 WSL 开发环境。
  Linux 只有 k3s 形态（tools/bh/linux/k8s/bh.sh）。

  用法:
    bh <command> [args]          wslc 形态（默认）
    bh dev <command> [args]      native 开发形态
    bh lan [on|off|status]       局域网入口（仅 native/dev：宿主 :80 -> :8788 portproxy）
    bh install                   复制定位器到 %USERPROFILE%\.local\bin（加入用户 PATH）
    bh uninstall                 移除定位器与 PATH 项
#>
# 不用 param()/CmdletBinding：`-e` / `-f` / `--json` / `-Prebuilt` 这类子命令选项必须**原样**透传，
# 而 CmdletBinding 会先把 `-e` 当 PowerShell 参数解析（与 -ErrorAction 二义性 → 直接报错）。
# 用自动变量 $args 全量收齐，再手动切分。
$All = @($args)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSCommandPath          # tools/bh
$Repo = Split-Path -Parent (Split-Path -Parent $Root)   # 仓库根
# 提权子进程首选 PowerShell 7（pwsh），没有就退回 Windows PowerShell
$ShellExe = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

$Arg1 = if ($All.Count -gt 0) { $All[0] } else { '' }
# @(...) 强制数组：强类型数组的单元素范围索引（$All[1..1]）会返回裸 string，
# 后续 @splat 会把字符串拆成字符数组，必须包成 object[]
$CmdArgs = @($All)

function Show-Help {
    Write-Host 'bh - 百花 CLI（Windows）'
    Write-Host ''
    Write-Host '用法:'
    Write-Host '  bh                           状态总览（wslc 形态）'
    Write-Host '  bh <command> [args]          wslc 形态命令（Windows 用户默认）'
    Write-Host '  bh dev <command> [args]      native 形态命令（开发/调试用：VS F5、快速 publish）'
    Write-Host '  bh doctor [--json]           体检当前形态（wslc / dev）'
    Write-Host '  bh autostart [on|off|status] 登录自启（用户级 Startup，不需要管理员）'
    Write-Host '  bh lan [on|off|status]       局域网入口（仅 native/dev：宿主 :80 -> :8788 portproxy）'
    Write-Host '  bh ovms [status|start|stop|restart|logs [n]]   本地模型服务 OVMS(:8000)，两种形态通用'
    Write-Host '  bh open-webui <start|stop|status|install>      AI 对话页后端（Windows venv）'
    Write-Host '  bh install / uninstall       安装 / 移除 定位器与用户 PATH'
    Write-Host ''
    Write-Host 'wslc 命令: build / pull / start / stop [svc] / restart / status [--json] / logs / exec / ps'
    Write-Host '           update / dashboard / open / env / doctor [--json] / autostart'
    Write-Host '           backup [目录] / restore <备份目录>（数据卷冷备份与恢复）'
    Write-Host '           全容器（postgres/server/webui/nginx，WSL3 自带 wslc.exe，不需要 Docker Desktop）'
    Write-Host '           依赖: WSL >= 2.9.3（wsl --update）'
    Write-Host ''
    Write-Host 'dev 命令（native）: build / build-restart / start / stop / restart / update / logs / status [--json]'
    Write-Host '           doctor [--json] [--fix] / autostart / dashboard / open'
    Write-Host '           依赖: .NET SDK 10、本机 PostgreSQL（Windows 服务）；不依赖容器'
    Write-Host ''
    Write-Host '两种形态互斥（同占 8788/5177）：切换前先 bh stop（wslc）或 bh dev stop（native）'
    Write-Host '环境边界：Windows = wslc / dev(native)；Linux = k3s；不支持在 WSL 里开发'
    Write-Host ''
    Write-Host '完整说明见 tools/bh/README.md'
}

function Invoke-Install {
    # 复制自包含定位器到 %USERPROFILE%\.local\bin，并把该目录加入用户 PATH。
    # 定位器每次调用时自动定位仓库根（BAIHUA_HOME > 常见路径 > 向上查找），
    # 仓库改名/移动后无需重装。
    # k3s 已退役：install 不再装 bh-k3s，并主动清理旧版安装的 bh-k3s 定位器
    # （源文件已从仓库删除，旧装机残留只会报"找不到脚本"）。
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
    # 广播环境变量变更（explorer 与后续进程可见）
    # 瘦部署包（无 services/）安装后，定位器依赖 BAIHUA_HOME 才能找到包目录。
    # ⚠ 必须设 $Repo（含 tools\bh 的根），不是 $Root（tools\bh 本身）——
    #   定位器按 "<BAIHUA_HOME>\tools\bh\bh.ps1" 查找，设成 tools\bh 会被静默忽略，
    #   新装机上 bh 会报"未找到 baihua 仓库"；csproj 检查同理要用 $Repo。
    if (-not (Test-Path (Join-Path $Repo 'services\Baihua.Server\Baihua.Server.csproj'))) {
        [Environment]::SetEnvironmentVariable('BAIHUA_HOME', $Repo, 'User')
        Write-Host "[install] 已设置 BAIHUA_HOME=$Repo（部署包定位）"
    }
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
    foreach ($n in @('bh.ps1', 'bh.cmd', 'bh-k3s.ps1', 'bh-k3s.cmd')) {
        Remove-Item (Join-Path $bin $n) -Force -ErrorAction SilentlyContinue
    }
    Write-Host '[uninstall] 已移除 %USERPROFILE%\.local\bin\bh.ps1 / bh.cmd（含 k3s 遗留 bh-k3s.* 如有）'
    # 部署包形态的 install 设过用户级 BAIHUA_HOME=$Repo（见 Invoke-Install），uninstall 是逆操作，应清除。
    # 守卫①：本根不是 dev 仓库（无 services\Baihua.Server）——dev 仓库的 install 从不设 BAIHUA_HOME；
    # 守卫②：当前用户级值确实指向本根才删——用户手动设的其它值（如 native 形态的 ~/.baihua）不误伤。
    if (-not (Test-Path (Join-Path $Repo 'services\Baihua.Server\Baihua.Server.csproj'))) {
        $bah = [Environment]::GetEnvironmentVariable('BAIHUA_HOME', 'User')
        if ($bah -and ($bah.TrimEnd('\') -ieq $Repo.TrimEnd('\'))) {
            [Environment]::SetEnvironmentVariable('BAIHUA_HOME', $null, 'User')
            Write-Host "[uninstall] 已清除用户级 BAIHUA_HOME=$Repo（部署包定位）"
        }
    }
    # 广播环境变量变更（PATH 移除 / BAIHUA_HOME 清除对 explorer 新开终端立即生效）
    Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@ -ErrorAction SilentlyContinue
    $null = [Win32.Native]::SendMessageTimeout([IntPtr]::Zero, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]([UIntPtr]::Zero))
    Write-Host '[uninstall] 完成。容器/数据卷/镜像不受影响（需要时: bh stop 后删部署目录，数据卷另行手动清理）'
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
    Write-Warning "[bh] Windows 只有 bh（wslc）/ bh dev（native）两种运行方式；不支持在 WSL 里开发或跑 k3s。"
    exit 1
}
# ==================== native（开发形态：bh dev <cmd>） ====================
# 2026-11-16 起：Windows 默认形态切到 wslc；native 降级为开发/调试形态，收进 bh dev。
# 理由：容器形态自带生命周期管理（重启/日志/卷），native 的启动链路需要三层拼装才能
# 关终端不被杀、不弹窗口；但 native 的开发内环（VS F5、publish 秒级）依然值钱，保留。
$nativeScript = Join-Path $Root 'win\native\bh.ps1'
if ($Arg1 -in @('dev', 'native')) {
    if (-not (Test-Path $nativeScript)) { Write-Error "[bh dev] 缺少 $nativeScript"; exit 1 }
    if ($Arg1 -eq 'native') {
        Write-Warning "[bh] 'native' 已改名：Windows 默认已是 wslc，native 是开发形态 → bh dev <cmd>"
    }
    $rest = @($CmdArgs | Select-Object -Skip 1)
    if ($rest.Count -eq 0) { $rest = @('status') }
    # --json 时不能往 stdout 混横幅（DSH 插件要解析纯 JSON）
    if (-not ($rest -contains '--json')) {
        Write-Host '[bh dev] native 开发形态（dotnet 进程 + 本机 PostgreSQL；与 wslc 互斥）' -ForegroundColor DarkGray
    }
    & $nativeScript @rest
    exit $LASTEXITCODE
}

# ==================== wslc（Windows 默认形态：WSL3 容器） ====================
# 2026-11-16 起 bh 默认 = wslc：<command> 直落这里；`bh wslc <cmd>` 保留为显式写法（脚本/文档兼容）。
if ($Arg1 -in @('wslc', 'container')) {
    $cell = Join-Path $Root 'win\wslc\bh.ps1'
    if (-not (Test-Path $cell)) { Write-Error "[wslc] 缺少 $cell"; exit 1 }
    $rest = @($CmdArgs | Select-Object -Skip 1)
    if ($rest.Count -eq 0) { $rest = @('help') }
    & $cell @rest
    exit $LASTEXITCODE
}
# 旧写法：Windows 容器形态不再用 Docker Desktop，统一走 wslc
if ($Arg1 -eq 'docker') {
    Write-Warning "[bh] Windows 容器形态已改用 WSL 自带的 wslc.exe（不需要 Docker Desktop）：bh wslc <cmd>"
    Write-Warning "[bh] 例：bh wslc status / bh wslc start"
    exit 1
}

# ==================== 形态无关的命令（两种形态都要能跑） ====================
# lan：dev/native 用宿主 :80 -> :8788 portproxy；wslc 形态由 nginx 容器直接占 :80
#      （wslc 下执行 bh lan on 会提示改用 nginx，见下面分支里的检测）
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
            # wslc 形态自带 nginx 容器占宿主 :80（浏览器 + 花记移动端统一入口），不需要 portproxy
            $wslcNginx = $false
            if (Get-Command wslc -ErrorAction SilentlyContinue) {
                & wslc container inspect bh-nginx 2>$null | Out-Null
                $wslcNginx = ($LASTEXITCODE -eq 0)
            }
            if ($wslcNginx) {
                Write-Warning '[lan] 当前是 wslc 形态：nginx 容器已占宿主 :80（/mg、/api → 后端，其余 → WebUI），无需 bh lan on'
                Write-Warning '[lan] 若之前建过 portproxy，请执行 bh lan off 清理（否则 nginx 无法绑定 :80）'
                exit 0
            }
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

# ==================== 其余命令：两种形态的兼容入口 ====================
# ovms / openvino：OVMS(:8000) 的启停两种形态通用（实现放在 native cell，逻辑与形态无关）
if ($Arg1 -in @('ovms', 'openvino')) {
    if (-not (Test-Path $nativeScript)) { Write-Error "[bh] 缺少 $nativeScript"; exit 1 }
    & $nativeScript @CmdArgs
    exit $LASTEXITCODE
}
# open-webui：wslc 形态是容器 bh-open-webui；native(dev) 形态仍是 Windows venv（dev 分支已优先处理）
if ($Arg1 -eq 'open-webui') {
    $wslcCellOw = Join-Path $Root 'win\wslc\bh.ps1'
    if (-not (Test-Path $wslcCellOw)) { Write-Error "[wslc] 缺少 $wslcCellOw"; exit 1 }
    & $wslcCellOw @CmdArgs
    exit $LASTEXITCODE
}

# ==================== 默认：wslc（Windows 用户形态） ====================
$wslcCell = Join-Path $Root 'win\wslc\bh.ps1'
if (-not (Test-Path $wslcCell)) { Write-Error "[wslc] 缺少 $wslcCell"; exit 1 }
$rest = @($CmdArgs)
if ($rest.Count -eq 0) { $rest = @('status') }
& $wslcCell @rest
exit $LASTEXITCODE
