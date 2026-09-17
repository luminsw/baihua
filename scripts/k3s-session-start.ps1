#requires -Version 5.1
<#
  k3s-session-start.ps1 - 会话启动脚本（由计划任务 Baihua-WSL-KeepAlive 在登录时调用）

  做两件事：
    1) 启动一个常驻的 `wsl.exe -e sleep infinity` 进程 —— 这是保持发行版存活的关键。
       ⚠️ 关键教训：不要用 `wsl ... -c "nohup sleep infinity &"`。命令返回后 WSL 认为
       该会话已结束，发行版（连同里面的 k3s 与所有容器）会被回收，后台进程一起消失。
       必须让 **Windows 侧的 wsl.exe 进程本身**一直活着。
    2) 确认 k3s 已运行（systemd 已 enable，此处只是兜底拉起），等待 API 就绪。

  注意：本脚本必须由计划任务在**交互式登录会话**中运行（否则发行版回收逻辑仍会生效）。
#>
[CmdletBinding()]
param(
    [string]$Distro = '',
    [int]$TimeoutSec = 180,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$StateDir = if ($env:BAIHUA_HOME) { $env:BAIHUA_HOME } else { Join-Path $HOME '.baihua' }
$StateFile = Join-Path $StateDir 'wsl-pin.json'

function Write-Step([string]$m) { if (-not $Quiet) { Write-Host "[k3s-session] $m" } }

if (-not $Distro) {
    $raw = (& wsl.exe -l -q 2>&1 | Out-String)
    $raw = $raw -replace ([string][char]0), ''
    $names = @($raw -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $Distro = if ($names.Count -gt 0) { $names[0] } else { 'Ubuntu-24.04' }
}

New-Item -ItemType Directory -Force -Path $StateDir | Out-Null

# 幂等：已有存活的保活进程就不再启一个
$existing = $null
if (Test-Path $StateFile) {
    try { $existing = (Get-Content $StateFile -Raw | ConvertFrom-Json) } catch { $existing = $null }
}
if ($existing -and $existing.pid -and (Get-Process -Id $existing.pid -ErrorAction SilentlyContinue)) {
    Write-Step "保活进程已存在（pid=$($existing.pid)），跳过"
} else {
    $wslExe = (Get-Command wsl.exe).Source
    $p = Start-Process -FilePath $wslExe `
        -ArgumentList @('-d', $Distro, '-u', 'root', '-e', 'sleep', 'infinity') `
        -PassThru -WindowStyle Hidden
    [pscustomobject]@{
        pid = $p.Id; distro = $Distro
        startedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    } | ConvertTo-Json | Set-Content -Path $StateFile -Encoding UTF8
    Write-Step "已启动保活进程 pid=$($p.Id)（发行版不会被空闲回收）"
}

# k3s 兜底拉起 + 等 API 就绪
for ($i = 1; $i -le [Math]::Max(1, [int]($TimeoutSec / 3)); $i++) {
    $st = (& wsl.exe -d $Distro -u root -e bash -c 'systemctl is-active k3s 2>/dev/null || echo inactive' 2>$null | Out-String).Trim()
    if ($st -eq 'active') {
        Write-Step "k3s 已运行（耗时约 $($i * 3)s 内确认）"
        exit 0
    }
    if ($i -eq 1) {
        Write-Step "\nk3s 未运行，尝试启动 ..."
        $null = & wsl.exe -d $Distro -u root -e bash -c 'systemctl start k3s 2>/dev/null || true' 2>$null
    }
    Start-Sleep -Seconds 3
}
Write-Warning "[k3s-session] k3s 在 ${TimeoutSec}s 内未变为 active，请进 WSL 检查: systemctl status k3s"
exit 1
