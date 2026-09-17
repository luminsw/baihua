#requires -Version 5.1
<#
  bh-wsl.ps1 - WSL 发行版保活 / 内存上限 / k3s 开机自启（Windows 侧辅助脚本）

  背景（实测根因）：WSL 发行版生命周期是"按需启动、空闲回收"。当发行版内最后一个进程退出
  （或宿主内存紧张被回收），整个 k3s 集群随发行版一起消失，所有容器收到 SIGTERM（退出码 143），
  restart 计数持续累积，表现为"服务起不来/反复重启"，且**看起来很像端口冲突**，实际不是。

  修法：
    1) 保活（pin）：常驻一个 `sleep infinity` 进程，发行版不再被回收；
    2) 自启：k3s 随发行版启动（systemd enable），并由登录计划任务拉起保活进程 + 确保 k3s；
    3) 内存上限：写入 .wslconfig 的 memory/swap/processors，避免 WSL 吃掉整机内存被回收。

  用法（install / status / uninstall 需要管理员权限；脚本内部会自行提权）：
    pwsh -File scripts\bh-wsl.ps1 install            # 保活 + k3s 自启 + 写 .wslconfig
    pwsh -File scripts\bh-wsl.ps1 install -Quiet     # 供 bh-k3s autostart 调用（少输出）
    pwsh -File scripts\bh-wsl.ps1 status             # 查看当前状态（只读，无需管理员）
    pwsh -File scripts\bh-wsl.ps1 uninstall          # 移除计划任务与 .wslconfig
    pwsh -File scripts\bh-wsl.ps1 pin                # 仅临时保活（当前会话生效，重启后失效）
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Action = 'status',
    [string]$Distro = '',
    [int]$MemoryGB = 12,
    [int]$SwapGB = 8,
    [int]$Processors = 8,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$TaskName = 'Baihua-WSL-KeepAlive'
$WslConfig = Join-Path $HOME '.wslconfig'
$Marker = '# --- baihua k3s keep-alive (managed by scripts/bh-wsl.ps1) ---'

function Write-Step([string]$msg) { if (-not $Quiet) { Write-Host $msg } }
function Write-Warn2([string]$msg) { Write-Warning $msg }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-WslListRaw {
    # wsl.exe 输出是 UTF-16LE，读入后字符间夹着 \0，必须先剔除再解析。
    # 注意：必须"先赋值、再 Replace"两步 —— 把 -replace 直接写在管道表达式里时，
    # PowerShell 对 `0 转义的解析会出偏差，只留下第一个字符（实测踩过：得到 "U"）。
    $raw = (& wsl.exe -l -v 2>&1 | Out-String)
    return ($raw -replace ([string][char]0), '')
}

function Get-Distro {
    if ($Distro) { return $Distro }
    # 默认发行版 = `wsl -l -q` 的首项（-q 只输出名字，无表头、无 * 标记）
    $raw = (& wsl.exe -l -q 2>&1 | Out-String)
    $raw = $raw -replace ([string][char]0), ''
    $names = @($raw -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($names.Count -gt 0) { return $names[0] }
    # 回退：从 -l -v 表格里取带 * 的那行
    foreach ($l in ((Get-WslListRaw) -split "`r?`n")) {
        if ($l -match '^\s*\*\s+(\S+)') { return $Matches[1] }
    }
    return 'Ubuntu-24.04'
}

# ---- 保活进程（关键）------------------------------------------------------------
# 必须让 **Windows 侧的 wsl.exe 进程** 常驻：WSL 发行版在"最后一个会话结束"时会被回收。
# 因此不能用 `wsl ... -c "nohup sleep infinity &"`（命令返回即回收，后台进程一起死，
# 实测发行版立刻变 Stopped）——只能 Start-Process 一个长跑会话并保住它的 PID。
# 保活进程的 PID 记录在 BAIHUA_HOME（默认 ~/.baihua）下，供 status/uninstall 复用
$StateDir = if ($env:BAIHUA_HOME) { $env:BAIHUA_HOME } else { Join-Path $HOME '.baihua' }
$StateFile = Join-Path $StateDir 'wsl-pin.json'
$SessionStartScript = Join-Path $PSScriptRoot 'k3s-session-start.ps1'

function Get-PinState {
    if (-not (Test-Path $StateFile)) { return $null }
    try { return (Get-Content $StateFile -Raw | ConvertFrom-Json) } catch { return $null }
}

function Get-LivePinPid {
    $s = Get-PinState
    if ($s -and $s.pid) {
        if (Get-Process -Id $s.pid -ErrorAction SilentlyContinue) { return [int]$s.pid }
    }
    return 0
}

function Start-Pin([string]$d, [switch]$Force) {
    $live = Get-LivePinPid
    if ($live -and -not $Force) { Write-Step "[pin] 保活进程已在运行（pid=$live），跳过"; return $true }
    if ($live -and $Force) { Stop-Process -Id $live -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1 }

    New-Item -ItemType Directory -Force -Path (Split-Path $StateFile) | Out-Null
    $wslExe = (Get-Command wsl.exe).Source
    $p = Start-Process -FilePath $wslExe `
        -ArgumentList @('-d', $d, '-u', 'root', '-e', 'sleep', 'infinity') `
        -PassThru -WindowStyle Hidden
    [pscustomobject]@{
        pid = $p.Id; distro = $d
        startedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    } | ConvertTo-Json | Set-Content -Path $StateFile -Encoding UTF8

    Start-Sleep -Seconds 3
    if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) {
        Write-Step "[pin] 保活进程已启动 pid=$($p.Id)"
        return $true
    }
    Write-Warn2 "[pin] 保活进程启动后立即退出（pid=$($p.Id)）"
    return $false
}

function Stop-Pin {
    $live = Get-LivePinPid
    if ($live) {
        Stop-Process -Id $live -Force -ErrorAction SilentlyContinue
        Write-Step "[pin] 已结束保活进程 pid=$live"
    }
    Remove-Item $StateFile -Force -ErrorAction SilentlyContinue
}

function Ensure-K3s([string]$d) {
    $st = (& wsl.exe -d $d -u root -e bash -c 'systemctl is-active k3s 2>/dev/null || echo inactive' 2>$null | Out-String).Trim()
    if ($st -eq 'active') { return $true }
    Write-Step "[k3s] 服务未运行（$st），尝试启动 ..."
    $null = & wsl.exe -d $d -u root -e bash -c 'systemctl start k3s 2>/dev/null || true' 2>$null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $st = (& wsl.exe -d $d -u root -e bash -c 'systemctl is-active k3s 2>/dev/null || echo inactive' 2>$null | Out-String).Trim()
        if ($st -eq 'active') { return $true }
    }
    return $false
}

function Get-Status {
    $d = Get-Distro
    $list = Get-WslListRaw
    $state = 'Unknown'
    foreach ($l in ($list -split "`r?`n")) {
        if ($l -match "^\s*\*?\s*$([regex]::Escape($d))\s+(\S+)") { $state = $Matches[1]; break }
    }
    $up = ''
    $k3s = ''
    if ($state -eq 'Running') {
        $up = (& wsl.exe -d $d -u root -e bash -c 'cut -d. -f1 /proc/uptime' 2>$null | Out-String).Trim()
        $k3s = (& wsl.exe -d $d -u root -e bash -c 'systemctl is-active k3s 2>/dev/null || echo inactive' 2>$null | Out-String).Trim()
    }
    $pinPid = Get-LivePinPid
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    $cfgHasMarker = (Test-Path $WslConfig) -and ((Get-Content $WslConfig -Raw) -match [regex]::Escape($Marker))
    [pscustomobject]@{
        Distro = $d; State = $state; UptimeSec = $up
        K3sActive = $k3s; PinPid = $pinPid
        ScheduledTask = if ($task) { $task.State.ToString() } else { 'not-installed' }
        WslConfigManaged = $cfgHasMarker; WslConfigPath = $WslConfig
    }
}

function Show-Status {
    $s = Get-Status
    Write-Host '[bh-wsl] WSL 发行版保活状态'
    Write-Host "  发行版        : $($s.Distro)（$($s.State)）"
    if ($s.UptimeSec) { Write-Host "  本次运行时长  : $($s.UptimeSec) 秒" }
    Write-Host "  k3s 服务      : $(if ($s.K3sActive) { $s.K3sActive } else { '（发行版未运行，未知）' })"
    Write-Host "  保活进程      : $(if ($s.PinPid) { "运行中 pid=$($s.PinPid)" } else { '未运行' })"
    Write-Host "  登录自启任务  : $($s.ScheduledTask)（$TaskName）"
    Write-Host "  .wslconfig    : $(if ($s.WslConfigManaged) { "已托管 $($s.WslConfigPath)" } else { '未托管' })"
    Write-Host ''
    if (-not $s.PinPid) {
        Write-Host '  提示: 未检测到保活进程 —— 发行版空闲后会被回收，k3s 会随之中断（表现为容器 restart 暴涨）。'
        Write-Host '        修复（需管理员）: pwsh -File scripts\bh-wsl.ps1 install'
    } elseif ($s.ScheduledTask -eq 'not-installed') {
        Write-Host '  提示: 保活进程当前存在，但登录自启未安装 —— 重启/注销后需手动拉起。'
        Write-Host '        修复（需管理员）: pwsh -File scripts\bh-wsl.ps1 install'
    } else {
        Write-Host '  状态: 保活已就位（发行版不会因空闲被回收）。'
    }
}

# ---- .wslconfig：只在没有该文件时创建；已存在则只提示，绝不覆盖用户配置 ----
function Set-WslConfig {
    if (Test-Path $WslConfig) {
        $cur = Get-Content $WslConfig -Raw
        if ($cur -match [regex]::Escape($Marker)) { Write-Step "[wslconfig] 已托管，跳过"; return }
        Write-Warn2 "[wslconfig] $WslConfig 已存在（非本脚本创建），未改动。"
        Write-Warn2 "           如需限制 WSL 内存，请手动加入：[wsl2] memory=${MemoryGB}GB / swap=${SwapGB}GB / processors=$Processors"
        return
    }
    $content = @"
$Marker
# WSL2 资源上限：不加限制时 WSL 会占用至多宿主一半内存，宿主内存紧张时容易被整体回收，
# 表现为 k3s 集群"莫名其妙整个消失、容器 restart 计数暴涨"。
[wsl2]
memory=${MemoryGB}GB
swap=${SwapGB}GB
processors=$Processors
"@
    Set-Content -Path $WslConfig -Value $content -Encoding UTF8
    Write-Step "[wslconfig] 已写入 $WslConfig（memory=${MemoryGB}GB swap=${SwapGB}GB processors=$Processors）"
    Write-Step '[wslconfig] 生效需 `wsl --shutdown` 重启发行版（会中断 k3s，模型 bind-mount 会自动恢复）'
}

function Remove-WslConfig {
    if (-not (Test-Path $WslConfig)) { return }
    $cur = Get-Content $WslConfig -Raw
    if ($cur -notmatch [regex]::Escape($Marker)) {
        Write-Warn2 "[wslconfig] $WslConfig 非本脚本创建，未删除"
        return
    }
    Remove-Item $WslConfig -Force
    Write-Step "[wslconfig] 已删除托管的 $WslConfig"
}

function Install-All {
    if (-not (Test-Admin)) {
        Write-Step '[bh-wsl] 需要管理员权限（注册计划任务 / 写 .wslconfig），正在提权 ...'
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", 'install',
                     '-MemoryGB', $MemoryGB, '-SwapGB', $SwapGB, '-Processors', $Processors)
        if ($Distro) { $argList += @('-Distro', $Distro) }
        if ($Quiet) { $argList += '-Quiet' }
        $p = Start-Process -FilePath 'pwsh' -Verb RunAs -PassThru -ArgumentList $argList -ErrorAction Stop
        $null = Wait-Process -Id $p.Id -Timeout 300 -ErrorAction SilentlyContinue
        if ($p.ExitCode -ne 0) { Write-Warn2 "[bh-wsl] 提权执行未成功（exit=$($p.ExitCode)）" }
        return
    }

    $d = Get-Distro
    Set-WslConfig

    # 计划任务：登录时跑 k3s-session-start.ps1（它负责启动常驻 wsl.exe 保活进程 + 确认 k3s 就绪）。
    # 计划任务必须跑在**交互式登录会话**里（principal 见下），否则发行版回收逻辑照旧生效。
    if (-not (Test-Path $SessionStartScript)) {
        Write-Warn2 "[autostart] 缺少 $SessionStartScript，无法注册自启任务"
    } else {
        $pwshExe = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
        if (-not $pwshExe) { $pwshExe = (Get-Command powershell).Source }
        $action = New-ScheduledTaskAction -Execute $pwshExe `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$SessionStartScript`" -Distro $d -Quiet"
        $trigger = New-ScheduledTaskTrigger -AtLogOn
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
            -Principal $principal -Force -Description '百花 k3s：保持 WSL 发行版常驻（登录自启），避免空闲回收导致集群整体中断' | Out-Null
        Write-Step "[autostart] 已注册计划任务 $TaskName（登录时执行 $([IO.Path]::GetFileName($SessionStartScript))）"
    }

    if (Start-Pin $d) { Write-Step '[pin] 保活进程已启动' } else { Write-Warn2 '[pin] 保活进程启动失败（发行版可能未就绪，重新登录后计划任务会再试）' }
    if (Ensure-K3s $d) { Write-Step '[k3s] 已运行' } else { Write-Warn2 '[k3s] 未能启动，请进 WSL 检查: systemctl status k3s' }

    Write-Step ''
    Write-Step '[bh-wsl] 完成。此后：Windows 登录即保活 + k3s 常驻；`bh-k3s status` 应稳定返回。'
}

function Uninstall-All {
    if (-not (Test-Admin)) {
        Write-Step '[bh-wsl] 需要管理员权限，正在提权 ...'
        $p = Start-Process -FilePath 'pwsh' -Verb RunAs -PassThru -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", 'uninstall') -ErrorAction Stop
        $null = Wait-Process -Id $p.Id -Timeout 120 -ErrorAction SilentlyContinue
        return
    }
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Step "[autostart] 已移除计划任务 $TaskName"
    } else {
        Write-Step "[autostart] 计划任务 $TaskName 不存在"
    }
    Remove-WslConfig
    Stop-Pin
    Write-Step '[pin] 已结束保活进程（发行版空闲后将被正常回收）'
}

switch ($Action.ToLower()) {
    'install'   { Install-All }
    'uninstall' { Uninstall-All }
    'pin'       { $d = Get-Distro; if (Start-Pin $d) { Write-Host '[pin] ok' } else { Write-Warn2 '[pin] 失败' } }
    'status'    { Show-Status }
    default     {
        Write-Host 'bh-wsl.ps1 - WSL 保活 / 内存上限 / k3s 自启'
        Write-Host '  install    保活 + 登录自启 + 写 .wslconfig（需管理员，自动提权）'
        Write-Host '  uninstall  移除计划任务与 .wslconfig（需管理员，自动提权）'
        Write-Host '  status     查看状态（只读）'
        Write-Host '  pin        仅临时保活当前会话'
    }
}
