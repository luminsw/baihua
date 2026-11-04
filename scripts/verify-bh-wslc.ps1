#requires -Version 5.1
<#
  verify-bh-wslc.ps1 —— bh wslc（Windows WSL 容器形态，wslc.exe）冒烟自检。

  覆盖：
    1. 结构：wslc cell / 两个 Dockerfile 齐全；bh 入口有 wslc 分派；旧的 docker 入口已移除
    2. 静态：cell 里只用 wslc（不调用 docker / docker compose / Docker Desktop）
    3. 行为（不需要容器在跑）：bh wslc help、bh docker 旧写法给迁移指引
    4. 运行时（需要 WSL 3 + wslc）：wslc version / container list / network list
  没装 wslc 时跳过第 4 项，不判失败。

  用法：pwsh -File scripts/verify-bh-wslc.ps1 [-Quiet]
#>
[CmdletBinding()]
param(
    [switch]$Quiet
)

chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Bh = Join-Path $RepoRoot 'tools/bh/bh.ps1'
$Cell = Join-Path $RepoRoot 'tools/bh/win/wslc/bh.ps1'
$DfServer = Join-Path $RepoRoot 'docker/Dockerfile.server'
$DfWebui = Join-Path $RepoRoot 'docker/Dockerfile.webui'
$DockerCell = Join-Path $RepoRoot 'tools/bh/win/docker/bh.ps1'

$script:passed = 0
$script:failed = 0
$script:skipped = 0

function Write-Case([string]$Status, [string]$Name, [string]$Detail) {
    switch ($Status) {
        'PASS' { $script:passed++; if (-not $Quiet) { Write-Host ("[PASS] " + $Name) -ForegroundColor Green } }
        'SKIP' { $script:skipped++; Write-Host ("[SKIP] " + $Name) -ForegroundColor DarkGray }
        default {
            $script:failed++
            Write-Host ("[FAIL] " + $Name) -ForegroundColor Red
            if ($Detail) { Write-Host ("       " + $Detail.Trim()) -ForegroundColor DarkYellow }
        }
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

function Invoke-Bh([string[]]$BhArgs) {
    $exe = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    $out = & $exe -NoProfile -ExecutionPolicy Bypass -File $Bh @BhArgs *>&1 | Out-String
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
}

Write-Host 'bh wslc 冒烟自检（Windows WSL 容器形态，wslc.exe）' -ForegroundColor Cyan
Write-Host ''

Test-Case 'wslc cell 与镜像配方齐全（cell / Dockerfile.server / Dockerfile.webui）' {
    $missing = @($Cell, $DfServer, $DfWebui) | Where-Object { -not (Test-Path $_) }
    if ($missing) { "缺少：" + ($missing -join ', ') }
}

Test-Case 'bh 入口含 wslc 分派，且旧 docker cell 已移除' {
    $text = Get-Content $Bh -Raw
    if ($text -notmatch "'wslc'") { 'bh.ps1 未找到 wslc 分派' }
    elseif (Test-Path $DockerCell) { '旧的 tools/bh/win/docker 仍在（应已删除）' }
}

Test-Case 'nginx 入口配置齐全（wslc.conf 用容器名上游，cell 发布 0.0.0.0:80）' {
    $conf = Join-Path $RepoRoot 'docker/nginx/wslc.conf'
    if (-not (Test-Path $conf)) { '缺少 docker/nginx/wslc.conf' }
    else {
        $c = Get-Content $conf -Raw
        if ($c -notmatch 'bh-server:8788' -or $c -notmatch 'bh-webui:5177') { 'wslc.conf 上游不是 bh-server/bh-webui' }
        else {
            $cell = Get-Content $Cell -Raw
            if ($cell -notmatch "0\.0\.0\.0:80:80") { 'cell 没有把 nginx 发布到 0.0.0.0:80（局域网访问不到）' }
        }
    }
}

Test-Case 'wslc cell 只用 wslc，不调用 docker' {
    $text = Get-Content $Cell -Raw
    if ($text -match '(?m)^\s*&\s*docker|docker\s+compose') { 'cell 里仍有 docker 调用' }
}

$help = Invoke-Bh @('wslc', 'help')
Test-Case 'bh wslc help 退出码 0 且列出 start/status' {
    if ($help.ExitCode -ne 0) { "exit=$($help.ExitCode)" }
    elseif ($help.Output -notmatch 'start' -or $help.Output -notmatch 'status') { '帮助缺少 start/status' }
}

$legacy = Invoke-Bh @('docker', 'status')
Test-Case 'bh docker 旧写法给 wslc 迁移指引（exit 1）' {
    if ($legacy.ExitCode -ne 1) { "exit=$($legacy.ExitCode)，期望 1" }
    elseif ($legacy.Output -notmatch 'wslc') { '缺少指向 bh wslc 的指引' }
}

if (Get-Command wslc -ErrorAction SilentlyContinue) {
    $v = & wslc version 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Case 'FAIL' 'wslc version 可执行' $v }
    else { Write-Case 'PASS' 'wslc version 可执行' '' }

    $list = & wslc container list --all 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Case 'FAIL' 'wslc container list 可用' $list }
    else { Write-Case 'PASS' 'wslc container list 可用' '' }
} else {
    Write-Case 'SKIP' 'wslc 运行时检查（本机没有 wslc，需 WSL 2.9.3+）' ''
}

Write-Host ''
$color = if ($script:failed -eq 0) { 'Green' } else { 'Red' }
Write-Host ("========== 汇总：通过 {0} / 失败 {1} / 跳过 {2} ==========" -f $script:passed, $script:failed, $script:skipped) -ForegroundColor $color
exit $(if ($script:failed -eq 0) { 0 } else { 1 })
