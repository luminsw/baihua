<#
  migrate-to-single-db.ps1 —— 把旧的 family / vault / ai 三库合并为单一 baihua 库。

  背景：合并前每个服务独占一个 PostgreSQL 库；合并后整个百花只有一个库（public schema），
  表名在三库之间几乎不重叠（唯一重叠是 BenchmarkSessions，历史上 family 与 ai 各建了一份）。

  安全性：
    - **只读源库**：脚本不改动 family / vault / ai，失败可随时回退（旧部署仍可用旧库）。
    - 仅新建 baihua 库并导入；目标库已存在时默认拒绝执行（-Force 才先删除重建）。

  用法：
    pwsh scripts/migrate-to-single-db.ps1                       # 按当前 PG_* 环境变量执行
    pwsh scripts/migrate-to-single-db.ps1 -TargetDb baihua_test # 导到别的库名做演练
#>
[CmdletBinding()]
param(
    [string]$DbHost = $(if ($env:PG_HOST) { $env:PG_HOST } else { '127.0.0.1' }),
    [int]$Port = 5432,
    [string]$User = $(if ($env:PG_USER) { $env:PG_USER } else { 'baihua' }),
    [string]$Password = $env:PG_PASSWORD,
    [string]$TargetDb = 'baihua',
    [string[]]$Sources = @('family', 'vault', 'ai'),
    [switch]$Force,
    [string]$PgBin = ''
)

$ErrorActionPreference = 'Stop'

function Resolve-PgTool([string]$name) {
    if ($PgBin) {
        $p = Join-Path $PgBin "$name.exe"
        if (Test-Path $p) { return $p }
    }
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $found = Get-ChildItem 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "bin\$name.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if ($found) { return $found }
    throw "找不到 $name（请用 -PgBin 指定 PostgreSQL bin 目录）"
}

$psql = Resolve-PgTool 'psql'
$pgDump = Resolve-PgTool 'pg_dump'
if ($Password) { $env:PGPASSWORD = $Password }

function Invoke-Psql([string]$database, [string[]]$Arguments) {
    & $psql -h $DbHost -p $Port -U $User -d $database -v ON_ERROR_STOP=1 --no-psqlrc @Arguments
    if ($LASTEXITCODE -ne 0) { throw "psql 执行失败（库=$database）：$($Arguments -join ' ')" }
}

Write-Host "=== 百花单库迁移 ===" -ForegroundColor Cyan
Write-Host "源库：$($Sources -join ', ')  →  目标库：$TargetDb（$DbHost`:$Port，用户 $User）"

# 1) 目标库存在性检查
$exists = (@(& $psql -h $DbHost -p $Port -U $User -d postgres -t -A -c "SELECT 1 FROM pg_database WHERE datname='$TargetDb';") -join '').Trim()
if ($exists -eq '1') {
    if (-not $Force) {
        Write-Host "[中止] 目标库 $TargetDb 已存在。确认要重建请加 -Force（会先 DROP）。" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "[警告] -Force：删除已存在的 $TargetDb" -ForegroundColor Yellow
    Invoke-Psql 'postgres' @('-c', "DROP DATABASE IF EXISTS `"$TargetDb`" WITH (FORCE);")
}

# 2) 建库
Invoke-Psql 'postgres' @('-c', "CREATE DATABASE `"$TargetDb`";")
Write-Host "[1/3] 已创建 $TargetDb"

# 3) 逐库导入（BenchmarkSessions 只保留一份：源库顺序中先出现的那个保留）
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("baihua-migrate-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$claimed = @{}
try {
    $i = 0
    foreach ($src in $Sources) {
        $i++
        $srcExists = (@(& $psql -h $DbHost -p $Port -U $User -d postgres -t -A -c "SELECT 1 FROM pg_database WHERE datname='$src';") -join '').Trim()
        if ($srcExists -ne '1') { Write-Host "[$i/$($Sources.Count)] 跳过 $src（库不存在）" -ForegroundColor DarkGray; continue }

        $tables = @(& $psql -h $DbHost -p $Port -U $User -d $src -t -A -c "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY 1;") |
            Where-Object { $_ -and $_.Trim() -ne '' } | ForEach-Object { $_.Trim() }

        $dumpArgs = @('-h', $DbHost, '-p', $Port, '-U', $User, '-d', $src, '--no-owner', '--no-acl', '--no-tablespaces')
        $excluded = @()
        foreach ($t in $tables) {
            if ($claimed.ContainsKey($t)) {
                # 表名含大写（EF 用带引号标识符建表）：pg_dump 的模式匹配必须带双引号，否则被折成小写匹配不到
                $dumpArgs += @('--exclude-table', "`"$t`"")
                $excluded += $t
            }
            else { $claimed[$t] = $src }
        }

        $file = Join-Path $tmp "$src.sql"
        & $pgDump @dumpArgs -f $file
        if ($LASTEXITCODE -ne 0) { throw "pg_dump $src 失败" }

        Invoke-Psql $TargetDb @('-q', '-f', $file)
        $msg = "[$i/$($Sources.Count)] $src → $TargetDb：$($tables.Count) 张表"
        if ($excluded.Count) { $msg += "（跳过重复表：$($excluded -join ', ')）" }
        Write-Host $msg
    }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

# 4) 校验：目标库表数量与源库并集一致
$targetTables = @(& $psql -h $DbHost -p $Port -U $User -d $TargetDb -t -A -c "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE';") |
    Where-Object { $_ -and $_.Trim() -ne '' }
Write-Host ""
Write-Host "=== 完成 ===" -ForegroundColor Green
Write-Host "目标库 $TargetDb 现有 $($targetTables.Count) 张表（源库并集应为 $($claimed.Count) 张）"
if ($targetTables.Count -ne $claimed.Count) {
    Write-Host "[警告] 表数量不一致，请人工核对" -ForegroundColor Yellow
}
Write-Host "旧库 family / vault / ai 未被修改，可随时回退。"
Write-Host "下一步：部署合并后的服务（连接串只认 PG_DATABASE=$TargetDb，默认即 baihua）。"
