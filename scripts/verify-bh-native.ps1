#requires -Version 5.1
<#
  verify-bh-native.ps1 —— bh（Windows 原生形态）冒烟自检。

  背景：本机 WSL 已卸载，bh 的 k3s 形态（bh-k3s，经 WSL）随之退役，Windows 上只剩 native
  一个形态（dotnet 进程，不依赖 WSL/Docker/k3s）。这个脚本把「重构后的不变量」变成可回归的检查，
  避免以后又悄悄把 WSL/Docker 依赖或旧的 bh-k3s 入口加回来。

  检查项：
    1. 结构：Windows 侧只剩 bh 一个入口（bh-k3s.* / locator-k3s.ps1 已删除，bh 入口齐全）
    2. 静态：native 实现里不出现 wsl / docker / kubectl（不依赖任何容器/WSL 运行时）
    3. 行为：bh help / bh status / bh status --json 正常；bh k8s 旧写法给退役指引并失败
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
Test-Case 'native 实现不出现 wsl / docker / kubectl' {
    $text = Get-Content $Native -Raw
    $hits = [regex]::Matches($text, '(?i)\b(wsl|docker|kubectl)\b') |
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
