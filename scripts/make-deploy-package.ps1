#Requires -Version 5.1
<#
  生成「最终用户」瘦部署包（不含 C# 源码）：dist\baihua-deploy-<version>.zip

  包内容只有运行所需：
    bh CLI（tools/bh，含 locator/bh.cmd/wslc cell）
    nginx 配置（docker/nginx/wslc.conf + server-proxy-headers.conf）
    docker/.env.wslc.example
    bh.cmd（根入口）/ README.txt / VERSION.txt
  镜像默认从华为云 SWR 拉取（docker/.env.wslc 的 BAIHUA_IMAGE_SOURCE=auto 在无源码时即 registry；
  国内匿名拉取已验证，Docker Hub 直连国内超时已避开）。

  用法：
    pwsh -File scripts/make-deploy-package.ps1 [-Tag latest] [-Version 0.1.0]
#>
param(
    [string]$Tag = 'latest',
    [string]$Version = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($Version)) {
    try { $Version = (& git -C $repo rev-parse --short HEAD 2>$null) } catch { }
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = (Get-Date -Format 'yyyyMMdd') }
}
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repo 'dist' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$stage = Join-Path $OutDir ('stage-' + $Version)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$files = @(
    'tools/bh/bh.ps1',
    'tools/bh/bh.cmd',
    'tools/bh/locator.ps1',
    'tools/bh/README.md',
    'tools/bh/win/wslc/bh.ps1',
    'tools/bh/win/native/bh.ps1',
    'docker/nginx/wslc.conf',
    'docker/nginx/server-proxy-headers.conf',
    'docker/.env.wslc.example'
)
foreach ($rel in $files) {
    $src = Join-Path $repo $rel
    if (-not (Test-Path $src)) { throw ('缺少部署文件：' + $rel) }
    $dst = Join-Path $stage $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    Copy-Item $src $dst -Force
}

# 预置 docker/.env.wslc（不止 .example）：镜像 tag 固定为本次发布版本。
# wslc cell 的 Get-EnvMap 只补缺失键，BAIHUA_IMAGE_TAG=<Tag> 会被保留 →
# 旧部署包不会被未来发布的新 latest 带跑（v0.1.0 之前 -Tag 只写 VERSION.txt，无人消费，实际拉的是 latest）。
# VAULTS_DIR 不预置：首启按最终用户 $HOME 生成（example 里的"你的用户名"占位符不能落进真实配置）。
$envExample = Join-Path $stage 'docker\.env.wslc.example'
$seedLines = @(
    '# 部署包预置配置（make-deploy-package.ps1 生成；BAIHUA_IMAGE_TAG 已固定为本包版本）',
    '# 首次 bh start 自动补全缺失项（随机 PG_PASSWORD 等）；改端口/路径后 bh restart 生效。'
)
foreach ($line in (Get-Content $envExample)) {
    if ($line -match '^\s*VAULTS_DIR=') { continue }
    if ($line -match '^\s*BAIHUA_IMAGE_TAG=') { $seedLines += ('BAIHUA_IMAGE_TAG=' + $Tag); continue }
    $seedLines += $line
}
[IO.File]::WriteAllLines((Join-Path $stage 'docker\.env.wslc'), $seedLines,
    (New-Object System.Text.UTF8Encoding($false)))

$bhCmd = @'
@echo off
rem 百花部署包入口：转发到包内 bh CLI
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\bh\bh.ps1" %*
exit /b %errorlevel%
'@
Set-Content -Path (Join-Path $stage 'bh.cmd') -Value $bhCmd -Encoding ASCII

$versionLines = @(
    ('version=' + $Version),
    'image_namespace=luminsw',
    ('image_tag=' + $Tag),
    ('built=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
)
Set-Content -Path (Join-Path $stage 'VERSION.txt') -Value $versionLines -Encoding UTF8

$readme = @"
百花 · 部署包（Windows，wslc 容器形态）

1. 解压到任意目录（例如 C:\baihua-deploy），不要放在 C:\Program Files 下。
2. 在解压目录打开终端，执行：bh.cmd install
3. 启动：bh.cmd start
   首次会拉取镜像：bh-server / bh-webui 来自华为云 SWR（国内直连快；可在 docker\.env.wslc 改 BAIHUA_IMAGE_REGISTRY 换源），PostgreSQL / Nginx / Open WebUI 来自公共镜像。
4. 浏览器打开：http://<本机局域网 IP>/    （服务器名默认随机生成，可在 WebUI 里修改）
5. 手机花记：在 WebUI 的配对页扫码（二维码自动使用本机局域网 IP）

常用命令：
  bh.cmd status / bh.cmd doctor / bh.cmd logs server 50
  bh.cmd stop
  bh.cmd update         拉取新镜像并重建容器（命名卷数据保留）
  bh.cmd autostart on   登录自启
  bh.cmd backup         冷备份全部数据卷+配置 到 backup\baihua-backup-<时间>\（自动恢复演练）
  bh.cmd restore <备份目录>   从备份恢复（覆盖现有数据，需输 yes 确认）

数据与配置：
  命名卷 bh-pgdata / bh-data / bh-logs / bh-openwebui-data
  知识库目录：docker\.env.wslc 的 VAULTS_DIR（默认 %USERPROFILE%\Vaults）
  配置：docker\.env.wslc（部署包已预置镜像版本（BAIHUA_IMAGE_TAG）；首次启动补全随机 PG 密码等，勿外传）

卸载：
  bh.cmd stop，然后 bh.cmd uninstall（移除定位器并清除 BAIHUA_HOME），再删除部署目录。
  数据卷不会被 bh 删除，需要时请手动清理。
"@
Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -Encoding UTF8

# 统一 CRLF：部署包仅在 Windows 使用。Linux CI 检出时，未在 .gitattributes 声明 eol 的文件
# （.cmd / .example / .conf 及本脚本生成的入口文件）是 LF，而 cmd.exe 解析 .cmd 要求 CRLF。
# ⚠ .ps1 必须写回 UTF-8 with BOM：bh.cmd 调的是 Windows PowerShell 5.1，
#   无 BOM 会按 GBK 读中文注释（吞换行→解析全崩；v0.1.0 首版部署包即栽在这里，
#   且 pwsh 7 下自测不出——7 默认 UTF-8）。其余文件（.cmd 纯 ASCII/.txt/.conf/.example）不带 BOM。
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
foreach ($f in (Get-ChildItem $stage -Recurse -File)) {
    $enc = if ($f.Extension -ieq '.ps1') { $utf8Bom } else { $utf8NoBom }
    $text = [IO.File]::ReadAllText($f.FullName, $utf8NoBom) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText($f.FullName, $text, $enc)
}

$zip = Join-Path $OutDir ('baihua-deploy-' + $Version + '.zip')
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

Write-Host ('[deploy] 已生成：' + $zip)
Write-Host ('[deploy] 镜像：luminsw/baihua-server:' + $Tag + '（默认经华为云 SWR；国内 Docker Hub 直连超时，已避开）')