#requires -Version 5.1
<#
  bh - baihua 统一 CLI 自包含定位器（Windows，安装时复制到 %USERPROFILE%\.local\bin）
  与仓库路径解耦：目录改名/移动后无需重装，自动定位仓库根并转发到仓库内 bh.ps1。

  定位优先级：
    1. $env:BAIHUA_HOME（显式指定）
    2. 常见候选路径（新旧目录名都覆盖）
    3. 从当前目录向上查找 tools\bh\bh.ps1（在仓库内或其子目录执行时必然命中）
#>
$ErrorActionPreference = 'Stop'

$candidates = @()
# 1) 环境变量显式指定
if ($env:BAIHUA_HOME -and (Test-Path "$env:BAIHUA_HOME\tools\bh\bh.ps1")) {
    $candidates += $env:BAIHUA_HOME
}
# 2) 常见候选路径
foreach ($cand in @(
    "$HOME\src\mdyj\baihua",
    "$HOME\src\mdyj\baihuagu",
    "$HOME\src\baihua",
    "$HOME\src\baihuagu",
    "$HOME\baihua",
    "$HOME\baihuagu"
)) {
    if (-not $candidates -and (Test-Path "$cand\tools\bh\bh.ps1")) {
        $candidates += $cand
    }
}
# 3) 从当前目录向上查找
if (-not $candidates) {
    $dir = (Get-Location).Path
    while ($dir) {
        if (Test-Path (Join-Path $dir 'tools\bh\bh.ps1')) {
            $candidates += $dir
            break
        }
        $parent = Split-Path -Parent $dir
        if ($parent -eq $dir) { break }   # 到盘符根
        $dir = $parent
    }
}

if (-not $candidates) {
    Write-Error '[bh] 未找到 baihua 仓库（缺少 tools\bh\bh.ps1）。请设置 BAIHUA_HOME 指向仓库根，或在仓库目录内执行 bh。'
    exit 1
}

& (Join-Path $candidates[0] 'tools\bh\bh.ps1') @args
exit $LASTEXITCODE
