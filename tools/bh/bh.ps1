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
    Write-Host '  bh install / uninstall        加入 / 移出用户 PATH'
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
    wsl -u root -e bash -lc "cd '$wslRepo' && $EnvPrefix tools/bh/linux/k8s/bh.sh $inner"
    return $LASTEXITCODE
}

# dashboard 特殊处理：CLI 在 WSL 里跑，打不开 Windows 的浏览器 —— 由本包装层代开。
if ($cell -eq 'dashboard' -or ($Rest.Count -gt 0 -and $Rest[0] -eq 'dashboard')) {
    # 公开地址：默认用 WSL 的 IP（Windows 侧实测可访问）；若已做 :80 转发到宿主机，
    # 可设 BAIHUA_PUBLIC_HOST=192.168.3.9 让 URL 也能被手机/局域网设备打开。
    $publicHost = $env:BAIHUA_PUBLIC_HOST
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

    if (-not $publicHost) {
        $wslIp = ($url -replace '^https?://([^/:]+).*$', '$1')
        Write-Host "[dashboard] 说明：URL 用的是 WSL 的 IP（$wslIp），Windows 本机可访问；"
        Write-Host "            手机等局域网设备需先把宿主 :80 转发进 WSL：以管理员运行"
        Write-Host "              pwsh -File scripts\expose-k3s-lan.ps1"
    }
    exit 0
}

exit (Invoke-Cell $Rest)
