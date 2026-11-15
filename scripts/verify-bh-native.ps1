#requires -Version 5.1
<#
  verify-bh-native.ps1 —— bh（Windows 原生形态）冒烟自检。

  背景：本机 WSL 已卸载，bh 的 k3s 形态（bh-k3s，经 WSL）随之退役，Windows 上只剩 native
  一个形态（dotnet 进程，不依赖 WSL/Docker/k3s）。这个脚本把「重构后的不变量」变成可回归的检查，
  避免以后又悄悄把 WSL/Docker 依赖或旧的 bh-k3s 入口加回来。

  检查项：
    1. 结构：Windows 侧只剩 bh 一个入口（bh-k3s.* / locator-k3s.ps1 已删除，bh 入口齐全）
    2. 静态：native 实现不依赖 docker / kubectl，也不直接调 WSL 发行版
       （wslc 是唯一例外：OVMS 本地模型服务自 2026-11-15 起由 wslc 容器 ovms-gpu 承载，
         bh ovms 需要能启停/看日志，但百花本体仍不跑在任何容器里）
    3. 行为：bh help / status / status --json / doctor / doctor --json / ovms status 正常；
       bh k8s 旧写法给退役指引并失败；不带参数 = 状态总览
    4. 兼容：bh native <cmd> 旧前缀仍可用（等价于 bh <cmd>）

  用法：
    pwsh -File scripts/verify-bh-native.ps1
    pwsh -File scripts/verify-bh-native.ps1 -Quiet   # 只输出失败与汇总

  注意：本脚本只读（不 start/stop 任何服务），可在任意时刻运行。
#>
[CmdletBinding()]
param(
    [switch]$Quiet
)

chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$BhPs1 = Join-Path $RepoRoot 'tools/bh/bh.ps1'
$BhCmd = Join-Path $RepoRoot 'tools/bh/bh.cmd'
$Locator = Join-Path $RepoRoot 'tools/bh/locator.ps1'
$Native = Join-Path $RepoRoot 'tools/bh/win/native/bh.ps1'

$script:passed = 0
$script:failed = 0

function Write-Case([string]$Status, [string]$Name, [string]$Detail) {
    if ($Status -eq 'PASS') {
        $script:passed++
        if (-not $Quiet) { Write-Host ("[{0}] {1}" -f $Status, $Name) -ForegroundColor Green }
    } else {
        $script:failed++
        Write-Host ("[{0}] {1}" -f $Status, $Name) -ForegroundColor Red
        if ($Detail) { Write-Host ("       " + $Detail.Trim()) -ForegroundColor DarkYellow }
    }
}

function Test-Case([string]$Name, [scriptblock]$Body) {
    try {
        $result = & $Body
        if ($result -is [string] -and $result) { Write-Case 'FAIL' $Name $result }
        else { Write-Case 'PASS' $Name '' }
    } catch {
        Write-Case 'FAIL' $Name $_.Exception.Message
    }
}

# 以独立进程调用 bh（与用户实际调用方式一致；不污染当前会话）
function Invoke-Bh([string[]]$BhArgs) {
    $exe = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    $out = & $exe -NoProfile -ExecutionPolicy Bypass -File $BhPs1 @BhArgs *>&1 | Out-String
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
}

Write-Host 'bh native 冒烟自检（Windows 原生形态，不依赖 WSL/Docker/k3s）' -ForegroundColor Cyan
Write-Host ''

# ---------- 1. 结构：只剩 native 一个 Windows 形态 ----------
Test-Case 'Windows 侧已删除 bh-k3s 入口（bh-k3s.ps1/.cmd、locator-k3s.ps1）' {
    $legacy = @('bh-k3s.ps1', 'bh-k3s.cmd', 'locator-k3s.ps1') |
        ForEach-Object { Join-Path (Join-Path $RepoRoot 'tools/bh') $_ } |
        Where-Object { Test-Path $_ }
    if ($legacy) { "残留：$($legacy -join ', ')" }
}

Test-Case 'native 入口齐全（bh.ps1 / bh.cmd / locator.ps1 / win/native/bh.ps1）' {
    $missing = @($BhPs1, $BhCmd, $Locator, $Native) | Where-Object { -not (Test-Path $_) }
    if ($missing) { "缺少：$($missing -join ', ')" }
}

# ---------- 2. 静态：实现不依赖 WSL / Docker / kubectl ----------
Test-Case 'native 实现不依赖 docker / kubectl / WSL 发行版（wslc 除外）' {
    $text = Get-Content $Native -Raw
    # wslc 放行：OVMS（本地模型服务）跑在 wslc 容器里，bh ovms 必须能管它；
    # 但 wsl.exe / WSL 发行版仍禁止 —— 百花本体不依赖任何虚拟化（这条是 2026-10-27 的底线）。
    $hits = [regex]::Matches($text, '(?i)\b(wsl(?!c)|docker|kubectl)\b') |
        ForEach-Object { $_.Value } | Select-Object -Unique
    if ($hits) { "命中：$($hits -join ', ')" }
}

# ---------- 3. 行为：help / status / status --json / k8s 旧写法 ----------
$help = Invoke-Bh @('help')
Test-Case 'bh help 退出码 0 且说明 native 形态' {
    if ($help.ExitCode -ne 0) { "exit=$($help.ExitCode)" }
    elseif ($help.Output -notmatch 'native') { '输出未提到 native' }
}

$status = Invoke-Bh @('status')
Test-Case 'bh status 退出码 0 且列出 server(8788) / webui(5177)' {
    if ($status.ExitCode -ne 0) { "exit=$($status.ExitCode)" }
    elseif ($status.Output -notmatch 'server' -or $status.Output -notmatch '8788') { '输出缺少 server / 8788' }
    elseif ($status.Output -notmatch 'webui' -or $status.Output -notmatch '5177') { '输出缺少 webui / 5177' }
}

$json = Invoke-Bh @('status', '--json')
Test-Case 'bh status --json 退出码 0 且是 cell=native 的合法 JSON（DSH 插件契约）' {
    if ($json.ExitCode -ne 0) { "exit=$($json.ExitCode)" }
    else {
        try { $doc = $json.Output | ConvertFrom-Json } catch { return "JSON 解析失败：$($_.Exception.Message)" }
        if ($doc.cell -ne 'native') { return "cell=$($doc.cell)，期望 native" }
        $names = @($doc.services | ForEach-Object { $_.name })
        if ($names -notcontains 'server' -or $names -notcontains 'webui') { return "services 缺少 server/webui：$($names -join ',')" }
        if (-not $doc.summary -or $doc.summary.total -lt 2) { return 'summary.total 不合法' }
    }
}

Test-Case 'bh status --json 的 openvino 条目带 engine（wslc / 服务 / 外部进程三态）' {
    if ($json.ExitCode -ne 0) { return "exit=$($json.ExitCode)" }
    try { $docS = $json.Output | ConvertFrom-Json } catch { return "JSON 解析失败：$($_.Exception.Message)" }
    $ov = @($docS.services | Where-Object { $_.name -eq 'openvino' })
    if ($ov.Count -ne 1) { return 'services 里没有 openvino 条目' }
    if (-not $ov[0].engine) { return 'openvino 条目缺少 engine 字段' }
}

$bare = Invoke-Bh @()
Test-Case 'bh 不带参数输出状态总览（端口 + 下一步）' {
    if ($bare.ExitCode -ne 0) { "exit=$($bare.ExitCode)" }
    elseif ($bare.Output -notmatch '8788' -or $bare.Output -notmatch '下一步') { '输出不是状态总览' }
}

$docJson = Invoke-Bh @('doctor', '--json')
Test-Case 'bh doctor --json 是合法 JSON（cell=native / summary / checks 齐全）' {
    if ($docJson.ExitCode -ne 0) { "exit=$($docJson.ExitCode)" }
    else {
        try { $d = $docJson.Output | ConvertFrom-Json } catch { return "JSON 解析失败：$($_.Exception.Message)" }
        if ($d.cell -ne 'native') { return "cell=$($d.cell)，期望 native" }
        if (-not $d.summary -or $d.summary.passed -lt 1) { return 'summary.passed 不合法' }
        if (@($d.checks).Count -lt 8) { return "checks 太少：$(@($d.checks).Count)" }
        $names = @($d.checks | ForEach-Object { $_.name })
        if (-not ($names | Where-Object { $_ -match 'PostgreSQL|\.NET' })) { return 'checks 缺少 .NET / PostgreSQL 项' }
    }
}

$doc = Invoke-Bh @('doctor')
Test-Case 'bh doctor 输出逐项结论与汇总（退出码 0/1 表示有无 FAIL）' {
    if ($doc.ExitCode -notin @(0, 1)) { "exit=$($doc.ExitCode)" }
    elseif ($doc.Output -notmatch '体检完成') { '输出缺少「体检完成」汇总' }
}

$ovms = Invoke-Bh @('ovms', 'status')
Test-Case 'bh ovms status 退出码 0 且报告 OVMS 形态' {
    if ($ovms.ExitCode -ne 0) { "exit=$($ovms.ExitCode)" }
    elseif ($ovms.Output -notmatch '形态') { '输出缺少「形态」' }
}

$auto = Invoke-Bh @('autostart', 'status')
Test-Case 'bh autostart status 退出码 0 且报告启用状态与入口' {
    if ($auto.ExitCode -ne 0) { "exit=$($auto.ExitCode)" }
    elseif ($auto.Output -notmatch '状态') { '输出缺少「状态」' }
    elseif ($auto.Output -notmatch '入口') { '输出缺少「入口」' }
}

$fix = Invoke-Bh @('doctor', '--fix', '--no-elevate', '--json')
Test-Case 'bh doctor --fix --no-elevate --json 合法且带 checks（不弹 UAC、不改口令）' {
    if ($fix.ExitCode -ne 0) { "exit=$($fix.ExitCode)" }
    else {
        try { $f = $fix.Output | ConvertFrom-Json } catch { return "JSON 解析失败：$($_.Exception.Message)" }
        if ($f.cell -ne 'native') { return "cell=$($f.cell)，期望 native" }
        if (@($f.checks).Count -lt 8) { return 'checks 太少' }
    }
}

$k8s = Invoke-Bh @('k8s', 'status')
Test-Case 'bh k8s 旧写法失败（exit 1）并给出退役指引' {
    if ($k8s.ExitCode -ne 1) { "exit=$($k8s.ExitCode)，期望 1" }
    elseif ($k8s.Output -notmatch '退役') { '缺少退役指引文案' }
}

$legacyNative = Invoke-Bh @('native', 'status')
Test-Case 'bh native status 旧前缀仍可用（退出码 0）' {
    if ($legacyNative.ExitCode -ne 0) { "exit=$($legacyNative.ExitCode)" }
}

# ---------- 汇总 ----------
Write-Host ''
$color = if ($script:failed -eq 0) { 'Green' } else { 'Red' }
Write-Host ("========== 汇总：通过 {0} / 失败 {1} ==========" -f $script:passed, $script:failed) -ForegroundColor $color
exit $(if ($script:failed -eq 0) { 0 } else { 1 })
