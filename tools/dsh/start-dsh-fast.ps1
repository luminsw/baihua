# start-dsh-fast.ps1 — 快速启动 DSH Web（绕过 npx），可选逐行时间戳
#
# 仓库位置：tools/dsh/start-dsh-fast.ps1（本机安装：Copy-Item tools\dsh\start-dsh-fast.ps1 ~\.dsh\ -Force）
#
# 为什么不用 npx：
#   `npx @deepseek-ai/dsh web` 每次都要走 npm 的包解析/registry 往返（实测本机 0.7s，
#   网络/proxy 不巧时要 10s+），而且会多一层 cmd → npx → node 进程。
#   直接用 node 跑已安装的 lib/bin.js：实测 0.06s 起进程。
#
# 用法：
#   pwsh -File ~\.dsh\start-dsh-fast.ps1                 # 正常启动（浏览器会自动打开）
#   pwsh -File ~\.dsh\start-dsh-fast.ps1 -NoOpen         # 不自动开浏览器
#   pwsh -File ~\.dsh\start-dsh-fast.ps1 -Timed          # 每行输出带「已用秒数」，并在 ready 行汇总
#   pwsh -File ~\.dsh\start-dsh-fast.ps1 -Port 3099 -NoOpen -Timed   # 另起一个实例做对比测试
#
# 说明：优先用全局安装的 dsh（npm i -g @deepseek-ai/dsh）；否则自动挑最新的一份
#       npx 缓存里的 @deepseek-ai/dsh（不需要重装、不依赖网络）。

[CmdletBinding()]
param(
    [switch]$NoOpen,
    [int]$Port = 0,
    [switch]$Timed
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Resolve-DshEntry {
    # 1) 全局安装（PATH 上的 dsh）
    $cmd = Get-Command dsh -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and $cmd.Source -notmatch '\.ps1$') {
        $candidate = Join-Path (Split-Path -Parent $cmd.Source) 'node_modules\@deepseek-ai\dsh\lib\bin.js'
        if (Test-Path $candidate) { return $candidate }
    }
    # 2) npx 缓存里最新的一份
    $npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
    if (Test-Path $npxRoot) {
        $found = Get-ChildItem $npxRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'node_modules\@deepseek-ai\dsh\lib\bin.js' } |
            Where-Object { Test-Path $_ } |
            Sort-Object { (Get-Item $_).LastWriteTime } -Descending
        if ($found) { return @($found)[0] }
    }
    return $null
}

$entry = Resolve-DshEntry
if (-not $entry) {
    Write-Error '[start-dsh-fast] 没找到 dsh：先执行一次 `npx @deepseek-ai/dsh --version` 填充缓存，或 `npm i -g @deepseek-ai/dsh`。'
    exit 1
}

$nodeArgs = @($entry, 'web')
if ($NoOpen) { $nodeArgs += '--no-open' }
if ($Port -gt 0) { $nodeArgs += @('--port', "$Port") }

Write-Host "[start-dsh-fast] $entry" -ForegroundColor DarkGray
Write-Host "[start-dsh-fast] node $($nodeArgs -join ' ')" -ForegroundColor DarkGray

if (-not $Timed) {
    & node @nodeArgs
    exit $LASTEXITCODE
}

# -Timed：逐行加时间戳（从本进程启动算起），ready 行额外高亮
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$readyReported = $false
& node @nodeArgs 2>&1 | ForEach-Object {
    $line = [string]$_
    $t = $sw.Elapsed.TotalSeconds
    if (-not $readyReported -and $line -match 'dsh web:\s*https?://') {
        $readyReported = $true
        Write-Host ("{0,8:N2}s | {1}" -f $t, $line) -ForegroundColor Green
        Write-Host ("{0,8:N2}s | >>> 就绪（可用浏览器打开）" -f $t) -ForegroundColor Green
    }
    else {
        Write-Host ("{0,8:N2}s | {1}" -f $t, $line)
    }
}
