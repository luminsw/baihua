# ============================================================
# 百花 PostgreSQL 初始化：建三库（family/vault/ai）+ 设 PG 环境变量
# 用法（普通 PowerShell 即可，无需管理员）:
#   pwsh scripts/init-pg.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/init-pg.ps1
# 作用:
#   1. 用 PG 超级用户连接，幂等创建 family / vault / ai 三个库（一服务一库）
#   2. 设置【用户级】环境变量 PG_USER / PG_PASSWORD（setx，新进程生效）
#   3. 提示 bh restart（已运行的 bh 进程不会继承新变量）
# 说明: 代码 DbConnections.For(dbName) 读 PG_HOST/PG_USER/PG_PASSWORD，
#       PG_HOST 默认 localhost（本机无需设置）。
# ============================================================
$ErrorActionPreference = "Stop"

$PG_HOME = "C:\Program Files\PostgreSQL\18\bin"
$PSQL = Join-Path $PG_HOME "psql.exe"
if (-not (Test-Path $PSQL)) {
    # 自动探测版本目录
    $dir = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $dir) { Write-Host "[X] 未找到 PostgreSQL（C:\Program Files\PostgreSQL\<ver>\bin\psql.exe）"; exit 1 }
    $PSQL = Join-Path $dir.FullName "bin\psql.exe"
}

$AdminUser = "postgres"   # PG 超级用户（安装时创建）
$AdminPassword = ""
if ($env:PG_ADMIN_PASSWORD) {
    $AdminPassword = $env:PG_ADMIN_PASSWORD   # 非交互：先设 PG_ADMIN_PASSWORD 环境变量
} else {
    $sec = Read-Host "输入 PostgreSQL 超级用户($AdminUser)的密码" -AsSecureString
    $AdminPassword = [Runtime.InteropServices.Marshal]::PtrToStringUni(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}

# ---- 1. 验证连接 ----
$env:PGPASSWORD = $AdminPassword
$test = & $PSQL -h localhost -U $AdminUser -d postgres -t -A -c "SELECT 1;" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "[X] 无法连接 PostgreSQL（用户名 $AdminUser）：$($test | Select-Object -First 1)"
    exit 1
}
Write-Host "[ok] 已连接 PostgreSQL（$AdminUser）"

# ---- 2. 幂等建库（单一数据库：整个百花只有一个库）----
$db = if ($env:PG_DATABASE) { $env:PG_DATABASE } else { "baihua" }
$exists = & $PSQL -h localhost -U $AdminUser -d postgres -t -A -c "SELECT 1 FROM pg_database WHERE datname='$db';" 2>&1
if ($LASTEXITCODE -ne 0 -or "$exists".Trim() -ne "1") {
    & $PSQL -h localhost -U $AdminUser -d postgres -c "CREATE DATABASE $db;" 2>&1 | Out-Null
    Write-Host "[ok] 已创建库 $db"
} else {
    Write-Host "[ok] 库 $db 已存在"
}
Write-Host "     注意：旧部署的 family / vault / ai 三库如需保留数据，先跑 scripts/migrate-to-single-db.ps1"

# ---- 3. 设置用户级环境变量（新进程生效）----
setx PG_USER $AdminUser | Out-Null
setx PG_PASSWORD $AdminPassword | Out-Null
setx PG_DATABASE $db | Out-Null
Write-Host "[ok] 已设置用户级环境变量 PG_USER=$AdminUser / PG_PASSWORD=*** / PG_DATABASE=$db（PG_HOST 本机可省略）"

# ---- 4. 提示 ----
Write-Host ""
Write-Host "完成。下一步："
Write-Host "  1) 新开一个终端（或注销重登）让环境变量生效"
Write-Host "  2) bh deploy（k3s）或 bh restart（容器栈）—— 后端会自动连 PG 并建表"
Write-Host "  3) 验证: bh status / 打开 http://localhost:5177"
