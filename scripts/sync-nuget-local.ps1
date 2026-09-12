# 同步 nuget-local 离线包源（Docker 离线构建用）
# 从本机 NuGet 缓存把项目依赖的 nupkg 复制到 nuget-local/，
# 使 Docker 构建（restore --source ./nuget-local）完全离线、不受外网波动影响。
#
# 用法：pwsh scripts/sync-nuget-local.ps1 [-Project services/Baihua.Server/Baihua.Server.csproj] [-Verbose]
param(
    [string]$Project = "services/Baihua.Server/Baihua.Server.csproj",
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root "nuget-local"
$cache = Join-Path $env:USERPROFILE ".nuget\packages"
New-Item -ItemType Directory -Path $target -Force | Out-Null

# 1. restore 生成 project.assets.json（本机网络通常可达 nuget.org/azure.cn）
Write-Host "Restoring $Project ..."
Push-Location $root
try {
    dotnet restore $Project | Out-Null
} finally {
    Pop-Location
}

$assetsFile = Join-Path $root (Join-Path (Split-Path $Project -Parent) "obj\project.assets.json")
if (!(Test-Path $assetsFile)) {
    Write-Error "project.assets.json not found: $assetsFile"
}

$assets = Get-Content $assetsFile -Raw | ConvertFrom-Json
$libs = @($assets.libraries.PSObject.Properties.Name)
Write-Host "Total libs: $($libs.Count)"

$copied = 0; $missing = 0
foreach ($lib in $libs) {
    if ($lib -notmatch '^(.+?)/([^/]+)$') { continue }
    $id = $Matches[1]; $ver = $Matches[2]
    # 跳过框架引用包（SDK 自带，不下载）
    if ($id -match '^(Microsoft\.NETCore\.App|Microsoft\.AspNetCore\.App)') {
        $idLower = $id.ToLowerInvariant()
        $nupkg = Join-Path $cache "$idLower\$ver\$idLower.$ver.nupkg"
        if (Test-Path $nupkg) {
            Copy-Item $nupkg $target -Force
            $copied++
            if ($Verbose) { Write-Host "  + $idLower.$ver.nupkg" }
        }
        continue
    }
    # 跳过项目自身引用
    if ($id -in @("Baihua.AI.Provider","Baihua.Contracts","Baihua.Core","Baihua.Data")) { continue }

    $idLower = $id.ToLowerInvariant()
    $nupkg = Join-Path $cache "$idLower\$ver\$idLower.$ver.nupkg"
    if (Test-Path $nupkg) {
        Copy-Item $nupkg $target -Force
        $copied++
        if ($Verbose) { Write-Host "  + $idLower.$ver.nupkg" }
    } else {
        $missing++
        Write-Warning "Not in local cache: $id $ver"
    }
}

Write-Host "Done. Copied/verified: $copied, missing: $missing"
Write-Host "nuget-local total: $([math]::Round((Get-ChildItem $target -Filter '*.nupkg' | Measure-Object Length -Sum).Sum/1MB, 1)) MB"
