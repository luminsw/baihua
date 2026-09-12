<#
  expose-k3s-lan.ps1 —— 把 WSL 里 k3s 的 :80 入口转发到 Windows 宿主机，让手机/局域网设备能访问百花。

  背景：Windows 上百花跑在 WSL 的 k3s 里，Traefik 绑的是 WSL 的 :80（WSL IP 形如 172.30.x.x）。
        Windows 本机可直接访问该 IP，但局域网里的手机访问不到 —— 需要宿主把 :80 转发进 WSL。

  用法（**必须以管理员身份运行**）：
    pwsh -File scripts\expose-k3s-lan.ps1              # 添加/更新 :80 转发 + 防火墙放行
    pwsh -File scripts\expose-k3s-lan.ps1 -Remove      # 移除转发规则
    pwsh -File scripts\expose-k3s-lan.ps1 -Ports 80,5177

  注意：WSL 的 IP 在 WSL 重启后会变，届时重跑本脚本即可（脚本是幂等的，会覆盖旧规则）。
#>
[CmdletBinding()]
param(
    [int[]]$Ports = @(80),
    [switch]$Remove,
    # 静默模式：供 bh 自动调用时使用（只输出结果，不打印大段说明）
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host '[X] 需要管理员权限：请右键 PowerShell → 以管理员身份运行，再执行本脚本。' -ForegroundColor Yellow
    Write-Host '    作用：netsh portproxy 把宿主 :80 转发进 WSL（手机等局域网设备访问百花用）。'
    exit 1
}

# WSL 的 IP（k3s 所在网络命名空间对外就是这个地址）
$wslIp = (wsl -e bash -lc "hostname -I | awk '{print `$1}'" 2>$null | Out-String).Trim()
if (-not $wslIp -or $wslIp -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    Write-Host "[X] 取不到 WSL IP（wsl 是否可用？）：'$wslIp'" -ForegroundColor Red
    exit 1
}

# 宿主局域网 IP（手机要访问的地址）
$lanIp = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -match '^(192\.168|10\.|172\.(1[6-9]|2\d|3[01]))\.' -and $_.PrefixOrigin -ne 'WellKnown' } |
    Select-Object -First 1 -ExpandProperty IPAddress)

$fwPrefix = 'baihua-k3s'

foreach ($port in $Ports) {
    if ($Remove) {
        netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$port 2>$null | Out-Null
        Get-NetFirewallRule -DisplayName "$fwPrefix-$port" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        Write-Host "[ok] 已移除 :$port 转发与防火墙规则"
        continue
    }

    # 幂等：先删后加（WSL IP 变化后重跑即生效）
    netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$port 2>$null | Out-Null
    netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=$port connectaddress=$wslIp connectport=$port | Out-Null
    if (-not (Get-NetFirewallRule -DisplayName "$fwPrefix-$port" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName "$fwPrefix-$port" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port | Out-Null
    }
    Write-Host "[ok] 宿主 :$port -> WSL $wslIp`:$port（已放行防火墙）"
}

Write-Host ''
netsh interface portproxy show v4tov4
Write-Host ''
if ($lanIp) {
    Write-Host "手机/局域网访问入口： http://$lanIp/"
    if (-not $Quiet) {
        Write-Host "（配对二维码里的地址由 k8s/01-configmap.yaml 的 Baihua__PublicBaseUrl 决定，"
        Write-Host "  建议改成同一个宿主地址并重跑: bh deploy）"
    }
} else {
    Write-Host '[!] 未识别到宿主局域网 IP，请手动确认手机访问地址。'
}
if (-not $Quiet) {
    Write-Host ''
    Write-Host "提示：WSL 重启后 IP 可能变化，重跑本脚本即可（幂等）；让 bh dashboard 也输出宿主地址："
    Write-Host "      `$env:BAIHUA_PUBLIC_HOST='$lanIp'; bh dashboard"
}
