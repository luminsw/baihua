#Requires -Version 5.1
<#
  生成「最终用户」部署包（Windows，native 形态）：dist\baihua-deploy-<version>.zip

  包内容（镜像仓库结构，native cell 的 5 级向上 = 包根，apps\ 检测生效）：
    bh.cmd（根入口）/ README.txt / VERSION.txt
    tools\bh\（bh CLI：bh.ps1 / bh.cmd / locator.ps1 / README / win\native cell）
    apps\server\ + apps\webui\（self-contained win-x64 publish，最终用户无需 .NET）
  2026-11-25 起 Windows 统一 native + SQLite，不再打包 wslc 容器栈 / docker env 模板。

  用法：
    pwsh -File scripts/make-deploy-package.ps1 [-Tag v0.1.0] [-Version 0.1.0] [-SkipPublish]
    -SkipPublish：复用 stage 里已有的 apps\（本地反复调 README/VERSION 时用）
#>
param(
    [string]$Tag = '',
    [string]$Version = '',
    [string]$OutDir = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($Version)) {
    try { $Version = (& git -C $repo rev-parse --short HEAD 2>$null) } catch { }
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = (Get-Date -Format 'yyyyMMdd') }
}
if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = $Version }
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repo 'dist' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$stage = Join-Path $OutDir ('stage-' + $Version)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# ---- 1) bh CLI（文本文件）----
$files = @(
    'tools/bh/bh.ps1',
    'tools/bh/bh.cmd',
    'tools/bh/locator.ps1',
    'tools/bh/README.md',
    'tools/bh/win/native/bh.ps1'
)
foreach ($rel in $files) {
    $src = Join-Path $repo $rel
    if (-not (Test-Path $src)) { throw ('缺少部署文件：' + $rel) }
    $dst = Join-Path $stage $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    Copy-Item $src $dst -Force
}

# ---- 2) 自包含 publish：apps\{server,webui}（win-x64；部署包形态的 $OutDir 即 apps\）----
$apps = @(
    @{ Name = 'server'; Project = 'services/Baihua.Server/Baihua.Server.csproj' },
    @{ Name = 'webui';  Project = 'services/Baihua.Web/Baihua.Web.csproj' }
)
if (-not $SkipPublish) {
    foreach ($app in $apps) {
        $proj = Join-Path $repo $app.Project
        if (-not (Test-Path $proj)) { throw ('缺少项目文件：' + $app.Project) }
        $dest = Join-Path (Join-Path $stage 'apps') $app.Name
        Write-Host ('[deploy] dotnet publish ' + $app.Project + ' -> apps\' + $app.Name + '（self-contained win-x64）...')
        & dotnet publish $proj -c Release -r win-x64 --self-contained true -o $dest
        if ($LASTEXITCODE -ne 0) { throw ('dotnet publish 失败：' + $app.Project) }
        # .pdb 只在排障时有用，部署包不带（省 ~30% 体积）
        Get-ChildItem $dest -Recurse -Filter '*.pdb' | Remove-Item -Force
        Get-ChildItem $dest -Recurse -Include '*.xml' | Where-Object { $_.Name -match '^Baihua\.' } | Remove-Item -Force
    }
} else {
    Write-Host '[deploy] -SkipPublish：跳过 dotnet publish'
}

# ---- 3) 根入口 / VERSION / README ----
$bhCmd = @'
@echo off
rem 百花部署包入口：转发到包内 bh CLI
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\bh\bh.ps1" %*
exit /b %errorlevel%
'@
Set-Content -Path (Join-Path $stage 'bh.cmd') -Value $bhCmd -Encoding ASCII

$commit = ''
try { $commit = (& git -C $repo rev-parse --short HEAD 2>$null) } catch { }
$versionLines = @(
    ('version=' + $Version),
    ('tag=' + $Tag),
    ('commit=' + $commit),
    ('built=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')),
    'form=native-win-x64'
)
Set-Content -Path (Join-Path $stage 'VERSION.txt') -Value $versionLines -Encoding UTF8

$readme = @"
百花 · 部署包（Windows，native 形态：dotnet 进程 + SQLite，无需 .NET / Docker）

1. 解压到任意目录（例如 C:\baihua），不要放在 C:\Program Files 下（无需管理员）。
2. 在解压目录打开终端，执行：bh.cmd install
   （安装定位器到 %USERPROFILE%\.local\bin 并加入 PATH，之后任意目录可用 bh）
3. 启动：bh.cmd start
   首次自动建库：%USERPROFILE%\.baihua\db\baihua.db（BAIHUA_DATA_HOME 可覆盖数据目录）
4. 浏览器打开：http://localhost:5177
   局域网入口（手机/其它设备）：bh.cmd lan on 配置 :80 转发 → http://<本机局域网 IP>/
5. 手机花记：在 WebUI 的配对页扫码（二维码自动使用本机局域网 IP）

常用命令：
  bh status / bh doctor / bh logs server 50 / bh stop
  bh backup            冷备份数据目录 → zip + .sha256（需先 bh stop）
  bh restore <zip>     恢复（旧数据改名 .pre-restore-<时间> 保留；需先 bh stop）
  bh autostart on      登录自启（用户级 Startup，不需要管理员）

可选组件（需 WSL >= 2.9.3，缺失仅对应功能降级）：
  bh open-webui install    AI 对话页（wslc 容器 bh-open-webui，:8080）
  bh ovms status           本地推理 OVMS（wslc 容器 ovms-gpu，:8000）

升级：下载新部署包解压到新目录 → 执行 bh.cmd install（BAIHUA_PKG_HOME 自动切换到新目录）
      → bh start。数据在 %USERPROFILE%\.baihua，与包目录无关，升级/搬家不受影响。
卸载：bh stop → bh uninstall（移除定位器）→ 删除解压目录；数据目录手动清理。
"@
Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -Encoding UTF8

# ---- 4) CRLF/BOM 归一化（只处理文本文件，apps\ 二进制绝不碰）----
# 部署包仅在 Windows 使用。Linux CI 检出时，未在 .gitattributes 声明 eol 的文件是 LF，
# 而 cmd.exe 解析 .cmd 要求 CRLF。
# ⚠ .ps1 必须写回 UTF-8 with BOM：bh.cmd 调的是 Windows PowerShell 5.1，
#   无 BOM 会按 GBK 读中文注释（吞换行→解析全崩；v0.1.0 首版部署包即栽在这里，
#   且 pwsh 7 下自测不出——7 默认 UTF-8）。其余文本文件（.cmd 纯 ASCII / .md / .txt）不带 BOM。
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$appsRoot = Join-Path $stage 'apps'
foreach ($f in (Get-ChildItem $stage -Recurse -File)) {
    if ($f.FullName.StartsWith($appsRoot, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
    $enc = if ($f.Extension -ieq '.ps1') { $utf8Bom } else { $utf8NoBom }
    $text = [IO.File]::ReadAllText($f.FullName, $utf8NoBom) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText($f.FullName, $text, $enc)
}

# ---- 5) 打 zip（手动建条目统一 '/' 分隔符：PS 5.1 的 ZipFile.CreateFromDirectory 用 '\'，
#         Linux/跨平台解包工具会把 'apps\server\...' 当成一个文件名）----
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $OutDir ('baihua-deploy-' + $Version + '.zip')
if (Test-Path $zip) { Remove-Item $zip -Force }
$zipFs = [System.IO.File]::Open($zip, [System.IO.FileMode]::Create)
$archive = New-Object System.IO.Compression.ZipArchive($zipFs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length).TrimStart('\') -replace '\\', '/'
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally {
    $archive.Dispose()
    $zipFs.Dispose()
}
Remove-Item $stage -Recurse -Force

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ('[deploy] 已生成：' + $zip + '（' + $sizeMb + ' MB）')
Write-Host ('[deploy] 形态：native win-x64（bh CLI + apps\ 自包含产物；数据 %USERPROFILE%\.baihua）')
