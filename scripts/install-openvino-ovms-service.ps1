# ============================================================
# 安装/更新 百花 OpenVINO Model Server（Intel OVMS）Windows 服务
# 用法（管理员 PowerShell）:
#   powershell -ExecutionPolicy Bypass -File install-openvino-ovms-service.ps1
#   powershell -ExecutionPolicy Bypass -File install-openvino-ovms-service.ps1 -Remove   # 卸载
# 说明:
#   - 服务名 ovms（官方），REST 端口 8000 —— 新代码（Baihua.AI.Provider.OpenVino）
#     默认 OpenVinoOms:BaseUrl=http://127.0.0.1:8000，无需改配置即可对接 /v3 推理
#   - OVMS 二进制: GitHub release ovms_windows_2026.3.0_python_on.zip -> $BAIHUA_HOME\ovms
#   - 模型仓库:   $BAIHUA_HOME\models（config.json 注册 4 个 servable；
#                 ovms --configure 为已下载模型幂等生成 graph.pbtxt）
#   - 旧自研 host（openvino_host.py / BaihuaOpenVinoHost :8866）已弃用
# ============================================================
param(
    [switch]$Remove,
    [string]$OmsHome = ''   # 覆盖 OVMS 安装目录（默认 $BAIHUA_HOME\ovms）
)
$ErrorActionPreference = 'Stop'

# --- 0. 管理员检查 ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '[X] 需要管理员权限！请右键 PowerShell -> 以管理员身份运行' -ForegroundColor Red
    exit 1
}

$DataHome   = if ($env:BAIHUA_HOME) { $env:BAIHUA_HOME } else { Join-Path $HOME '.baihua' }
$ModelsDir  = Join-Path $DataHome 'models'
if (-not $OmsHome) { $OmsHome = Join-Path $DataHome 'ovms' }
$OvmsExe    = Join-Path $OmsHome 'ovms.exe'
$ServiceName = 'ovms'
$RestPort   = 8000
$OmsTag     = 'v2026.3'
$OmsUrl     = "https://github.com/openvinotoolkit/model_server/releases/download/$OmsTag/ovms_windows_2026.3.0_python_on.zip"

# OVMS 注册的 3 个 servable（id 与 services/Baihua.AI.Provider.OpenVino/OmsModelMap.cs 一致）
$Models = @(
    @{ name = 'qwen2.5';       dir = 'Qwen2.5-7B-Instruct-int4-ov' },
    @{ name = 'qwen2.5-vl-7b'; dir = 'Qwen2.5-VL-7B-Instruct-int4-ov' },
    @{ name = 'bge-small-zh';  dir = 'bge-small-zh-v1.5' }
)

# --- 卸载 ---
if ($Remove) {
    Write-Host '[0/1] 停止并删除服务 ovms ...'
    & sc.exe stop $ServiceName 2>$null | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $ServiceName | Out-Null
    Write-Host '[OK] 已删除服务 ovms（OVMS 目录与模型仓库保留，可随时重装）'
    exit 0
}

# --- 1. 下载/解压 OVMS（ovms.exe 存在则跳过） ---
if (-not (Test-Path $OvmsExe)) {
    New-Item -ItemType Directory -Force -Path $OmsHome | Out-Null
    $zip = Join-Path $OmsHome 'ovms.zip'
    $ok = $false
    foreach ($url in @($OmsUrl,
                       "https://mirror.ghproxy.com/$OmsUrl",
                       "https://ghfast.top/$OmsUrl",
                       "https://ghproxy.net/$OmsUrl")) {
        try {
            Write-Host "[1/6] 下载 OVMS（$url）..."
            Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 900
            $ok = $true; break
        } catch {
            Write-Host "      下载失败: $($_.Exception.Message)"
        }
    }
    if (-not $ok) {
        Write-Host '[X] 下载失败（直连+镜像均不可达），请手动下载 ovms_windows_2026.3.0_python_on.zip 并解压到:'
        Write-Host "    $OmsHome"
        exit 1
    }
    Write-Host '[1/6] 解压 ...'
    $tmp = Join-Path $OmsHome 'extract'
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    # zip 内可能有一层 ovms 目录
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    $src = if ($inner) { $inner.FullName } else { $tmp }
    Copy-Item -Path (Join-Path $src '*') -Destination $OmsHome -Recurse -Force
    Remove-Item $zip, $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $OvmsExe)) {
        Write-Host "[X] 解压后未找到 ovms.exe（$OmsHome）"
        exit 1
    }
}
Write-Host "[1/6] OVMS: $OvmsExe"

# --- 2. 写模型仓库 config.json（4 个 servable） ---
New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
$configPath = Join-Path $ModelsDir 'config.json'
$cfgList = $Models | ForEach-Object {
    # base_path 用正斜杠（Windows 下 OVMS 兼容，且避免 JSON 反斜杠转义）
    @{ config = @{ name = $_.name; base_path = ($ModelsDir + '/' + $_.dir) } }
}
$cfg = @{ model_config_list = $cfgList } | ConvertTo-Json -Depth 5
# 无 BOM UTF-8（PS5.1 的 -Encoding UTF8 会带 BOM，rapidjson 不剥离 BOM 可能解析失败）
[System.IO.File]::WriteAllText($configPath, $cfg, [System.Text.UTF8Encoding]::new($false))
Write-Host "[2/6] 模型仓库配置已写入: $configPath"

# --- 3. 设置 OVMS 运行环境（python_on 包需要 PYTHONHOME；仅当前会话） ---
$env:PYTHONHOME = Join-Path $OmsHome 'python'
$env:PATH = "$OmsHome;$env:PYTHONHOME;$env:PYTHONHOME\Scripts;$env:PATH"

# --- 4. 为已下载模型生成 graph.pbtxt（幂等；缺失跳过） ---
Write-Host '[3/6] 为已下载模型生成 graph.pbtxt（ovms --configure，幂等）...'
foreach ($m in $Models) {
    $dir = Join-Path $ModelsDir $m.dir
    if (-not (Test-Path $dir)) {
        Write-Host "    跳过缺失模型: $($m.dir)（请先下载到 $dir）"
        continue
    }
    $task = if ($m.name -eq 'bge-small-zh') { 'embeddings' } else { 'text_generation' }
    $dev  = if ($m.name -eq 'bge-small-zh') { 'CPU' } else { 'GPU' }
    Write-Host "    configure $($m.dir) (task=$task device=$dev)"
    & $OvmsExe --configure --model_path $dir --task $task --target_device $dev
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    [WARN] configure 失败: $($m.dir)（已继续，可稍后手动重试）" -ForegroundColor Yellow
    }
}

# --- 5. 注册 Windows 服务 ---
# 增大 Windows 服务启动超时（默认 30 秒，OVMS 启动时同步加载 2GB+ 模型会超时 1053），
# 需重启系统生效。幂等：已设置则跳过。
$pipeTimeout = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name ServicesPipeTimeout -ErrorAction SilentlyContinue).ServicesPipeTimeout
if ($pipeTimeout -lt 180000) {
    Write-Host '[4/6] 设置服务启动超时 ServicesPipeTimeout=180000（3 分钟，需重启系统生效）...'
    reg.exe add HKLM\SYSTEM\CurrentControlSet\Control /v ServicesPipeTimeout /t REG_DWORD /d 180000 /f | Out-Null
} else {
    Write-Host '[4/6] 服务启动超时已配置: ServicesPipeTimeout=$pipeTimeout'
}

$installBat = Join-Path $OmsHome 'install_ovms_service.bat'
if (Test-Path $installBat) {
    Write-Host '[4/6] 运行官方安装脚本 install_ovms_service.bat ...'
    & $installBat $ModelsDir
    if ($LASTEXITCODE -ne 0) { Write-Host '[X] install_ovms_service.bat 失败'; exit 1 }
    # 官方脚本默认手动启动，改回开机自启（与旧 BaihuaOpenVinoHost 行为一致）
    & sc.exe config $ServiceName start= auto | Out-Null
} else {
    # 兜底：手动注册（与官方 bat 等价：sc create ovms + ovms.exe install）
    Write-Host '[4/6] install_ovms_service.bat 不存在，手动注册服务 ...'
    $binPath = '"' + $OvmsExe + '" --rest_port ' + $RestPort + ' --config_path "' + $configPath + '" --log_level INFO --log_path "' + (Join-Path $OmsHome 'ovms_server.log') + '"'
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        & sc.exe config $ServiceName binPath= $binPath start= auto | Out-Null
    } else {
        & sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= 'OpenVINO Model Server (Baihua)' | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { Write-Host '[X] sc create/config 失败'; exit 1 }
    & $OvmsExe install
    if ($LASTEXITCODE -ne 0) { Write-Host '[X] ovms.exe install 失败'; exit 1 }
}

# --- 6. 启动并验证 ---
Write-Host '[5/6] 启动服务 ovms ...'
& sc.exe start $ServiceName | Out-Null
Write-Host '[6/6] 等待 OVMS 就绪（首次加载 7B 模型编译较慢，最多 5 分钟）...'
$deadline = (Get-Date).AddMinutes(5)
$ready = $false
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-RestMethod -Uri "http://127.0.0.1:$RestPort/v1/models" -TimeoutSec 3
        if ($r.data -and $r.data.Count -gt 0) { $ready = $true; break }
    } catch { Start-Sleep -Seconds 5 }
}
if ($ready) {
    $ids = ($r.data | ForEach-Object { $_.id }) -join ', '
    Write-Host "[OK] OVMS 已就绪: http://127.0.0.1:$RestPort （模型: $ids）" -ForegroundColor Green
} else {
    Write-Host '[!] 服务已启动但 8000 未就绪（模型未下载/首次加载慢？）' -ForegroundColor Yellow
    Write-Host "    日志: $(Join-Path $OmsHome 'ovms_server.log') ；服务管理: services.msc -> ovms"
}

Write-Host ''
Write-Host '完成。新代码（OpenVinoChatInference / OpenVinoVisionService / 嵌入）默认走'
Write-Host 'http://127.0.0.1:8000 的 /v3 端点，无需额外配置。bh status 可查看服务状态。'