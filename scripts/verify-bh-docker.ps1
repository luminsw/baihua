#requires -Version 5.1
<#
  verify-bh-docker.ps1 —— bh docker（Windows Docker 形态）冒烟自检。

  覆盖：
    1. 结构：docker 形态入口、Windows compose、两个 Dockerfile 齐全；bh 入口有 docker 分派
    2. 静态：Windows compose 不写 Linux 专有的 network_mode: host / /opt/baihua 路径；
       必须走 host.docker.internal（宿主 OVMS）与命名卷（postgres 数据）
    3. 行为：bh docker help / 无参数 都能给出帮助（不需要 Docker）
    4. 运行时（仅在装了 Docker 时）：docker compose config 校验 + compose ps
  本机没装 Docker 时跳过第 4 项，不算失败（离开 Docker 也能验证前 3 项）。

  用法：pwsh -File scripts/verify-bh-docker.ps1 [-Quiet]
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
$Cell = Join-Path $RepoRoot 'tools/bh/win/docker/bh.ps1'
$Compose = Join-Path $RepoRoot 'docker/docker-compose.windows.yml'
$DfServer = Join-Path $RepoRoot 'docker/Dockerfile.server'
$DfWebui = Join-Path $RepoRoot 'docker/Dockerfile.webui'

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

Write-Host 'bh docker 冒烟自检（Windows Docker Desktop 形态）' -ForegroundColor Cyan
Write-Host ''

# ---------- 1. 结构 ----------
Test-Case 'docker 形态文件齐全（cell / windows compose / 两个 Dockerfile）' {
    $missing = @($Cell, $Compose, $DfServer, $DfWebui) | Where-Object { -not (Test-Path $_) }
    if ($missing) { "缺少：" + ($missing -join ', ') }
}

Test-Case 'bh 入口含 docker 分派' {
    $text = Get-Content $Bh -Raw
    if ($text -notmatch "'docker'") { 'bh.ps1 未找到 docker 分派' }
}

# ---------- 2. 静态约束 ----------
Test-Case 'Windows compose 不含 Linux 专有 network_mode: host 与 /opt/baihua 路径' {
    # 只看真实配置行（注释里解释“Windows 没有 /opt 目录”是允许的）
    $body = ((Get-Content $Compose) | Where-Object { $_ -notmatch '^\s*#' }) -join ([char]10)
    if ($body -match 'network_mode:\s*host') { 'Windows compose 出现 network_mode: host（Windows 不支持）' }
    elseif ($body -match '/opt/baihua') { 'Windows compose 出现 Linux 路径 /opt/baihua' }
}

Test-Case 'Windows compose 用 host.docker.internal 访问宿主 OVMS' {
    $text = Get-Content $Compose -Raw
    if ($text -notmatch 'host\.docker\.internal:8000') { '未找到 host.docker.internal:8000（容器内 127.0.0.1 是容器自己）' }
}

Test-Case 'PostgreSQL 数据用命名卷（Windows 绑定挂载会破坏 postgres 权限）' {
    $text = Get-Content $Compose -Raw
    if ($text -notmatch 'bh-pgdata:/var/lib/postgresql/data') { 'postgres 数据目录不是命名卷 bh-pgdata' }
}

Test-Case 'docker/.env.windows 已被 gitignore（含随机 PG_PASSWORD，不能入库）' {
    $gi = Get-Content (Join-Path $RepoRoot '.gitignore') -Raw
    if ($gi -notmatch 'docker/\.env\.windows') { '.gitignore 缺少 docker/.env.windows' }
}

# ---------- 3. 行为（不依赖 Docker） ----------
$help = Invoke-Bh @('docker', 'help')
Test-Case 'bh docker help 退出码 0 且列出 start/status' {
    if ($help.ExitCode -ne 0) { "exit=$($help.ExitCode)" }
    elseif ($help.Output -notmatch 'start' -or $help.Output -notmatch 'status') { '帮助缺少 start/status' }
}

$bare = Invoke-Bh @('docker')
Test-Case 'bh docker（无参数）给出帮助且退出码 0' {
    if ($bare.ExitCode -ne 0) { "exit=$($bare.ExitCode)" }
    elseif ($bare.Output -notmatch 'bh docker') { '输出不是 docker 帮助' }
}

# ---------- 4. 运行时（需要 Docker） ----------
if (Get-Command docker -ErrorAction SilentlyContinue) {
    $tmpEnv = Join-Path ([IO.Path]::GetTempPath()) ('bh-verify-docker-' + [guid]::NewGuid().ToString('N') + '.env')
    @(
        'BAIHUA_DATA_DIR=C:/bh-verify/data'
        'BAIHUA_LOG_DIR=C:/bh-verify/logs'
        'VAULTS_DIR=C:/bh-verify/vaults'
        'PG_USER=baihua'
        'PG_PASSWORD=verify-only'
        'PG_DATABASE=baihua'
    ) | Set-Content -Path $tmpEnv -Encoding utf8
    try {
        $cfg = & docker compose --env-file $tmpEnv -f $Compose config -q 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Case 'FAIL' 'docker compose config 校验通过' $cfg } else { Write-Case 'PASS' 'docker compose config 校验通过' '' }
    } finally {
        Remove-Item $tmpEnv -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Case 'SKIP' 'docker compose config 校验（本机未安装 docker）' ''
}

Write-Host ''
$color = if ($script:failed -eq 0) { 'Green' } else { 'Red' }
Write-Host ("========== 汇总：通过 {0} / 失败 {1} / 跳过 {2} ==========" -f $script:passed, $script:failed, $script:skipped) -ForegroundColor $color
exit $(if ($script:failed -eq 0) { 0 } else { 1 })
