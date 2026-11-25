#requires -Version 5.1
<#
  bh - baihua 统一 CLI 自包含定位器（Windows，安装时复制到 %USERPROFILE%\.local\bin）
  与仓库/部署包路径解耦：目录改名/移动后无需重装，自动定位根并转发到根内 tools\bh\bh.ps1。

  定位优先级：
    1. $env:BAIHUA_PKG_HOME（部署包根，install 时设置）
    2. $env:BAIHUA_HOME（历史兼容：旧版包定位/用户显式指定，须含 tools\bh\bh.ps1）
    3. 常见候选路径（新旧目录名都覆盖）
    4. 从当前目录向上查找 tools\bh\bh.ps1（在仓库内或其子目录执行时必然命中）
#>
$ErrorActionPreference = 'Stop'

$candidates = @()
# 1) 部署包根（install 设置 BAIHUA_PKG_HOME）
if ($env:BAIHUA_PKG_HOME -and (Test-Path "$env:BAIHUA_PKG_HOME\tools\bh\bh.ps1")) {
    $candidates += $env:BAIHUA_PKG_HOME
}
# 2) 历史兼容：BAIHUA_HOME 指向含 tools\bh 的根（数据目录形态的 BAIHUA_HOME=~\.baihua
#    不含 tools\bh，自动跳过，不影响）
if (-not $candidates) {
    if ($env:BAIHUA_HOME -and (Test-Path "$env:BAIHUA_HOME\tools\bh\bh.ps1")) {
        $candidates += $env:BAIHUA_HOME
    }
}
# 3) 常见候选路径
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
# 4) 从当前目录向上查找
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
    Write-Error '[bh] 未找到 baihua 安装（缺少 tools\bh\bh.ps1）。请设置 BAIHUA_PKG_HOME 指向部署包根，或重新 bh install，或在安装目录内执行 bh。'
    exit 1
}

& (Join-Path $candidates[0] 'tools\bh\bh.ps1') @args
exit $LASTEXITCODE
