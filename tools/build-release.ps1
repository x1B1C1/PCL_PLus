<#
.SYNOPSIS
    把当前源码发布成单文件 exe，放到 release\ 供本地测试。

.DESCRIPTION
    产物（框架依赖单文件，约 27 MB，需要 .NET 10 Desktop Runtime）：
      release\PCL_Plus_x64.exe              <- 主仓库
      release\PCL_Plus_v1.5.0-beta_x64.exe    <- SRC-Backups\Beta-V1.2-Aero（-All 时）

    release\ 里**只放 exe**；校验文件、说明、清单模板与文件夹版都进 release-assets\：
      release-assets\*.sha256
      release-assets\PCL_Plus-portable\             （-Portable 时）
      release-assets\PCL_Plus_v1.5.0-beta-portable\   （-Portable 时）

.PARAMETER All
    连 Beta（Aero）一起发。

.PARAMETER NoHash
    不写 .sha256 文件。

.PARAMETER Portable
    额外产出一份「文件夹版」（不解包、不需要写 %TEMP%）：
      release-assets\PCL_Plus-portable\
      release-assets\PCL_Plus_v1.5.0-beta-portable\
    单文件版启动时，.NET 宿主必须把原生库解压到 %TEMP%\.net\<exe 名>\；
    如果那台机器的 %TEMP% 不可写，单文件版会在应用代码跑起来之前就失败
    （"Failure processing application bundle ... Error code: 5"）。
    文件夹版没有这一步，适合 %TEMP% 受限的机器。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-release.ps1
    powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -All
    powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -All -Portable
#>
[CmdletBinding()]
param(
    [switch]$All,
    [switch]$NoHash,
    [switch]$Portable
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root 'release'
$assets = Join-Path $root 'release-assets'
$stage = Join-Path $root '.build-out'
$csprojMain = Join-Path $root 'Plain Craft Launcher 2\Plain Craft Launcher 2.csproj'
$csprojBeta = Join-Path $root 'SRC-Backups\Beta-V1.2-Aero\Plain Craft Launcher 2\Plain Craft Launcher 2.csproj'

function Publish-One {
    param([string]$Project, [string]$OutDir, [string]$TargetName, [string]$PortableDir)

    Write-Host "> 发布 $(Split-Path $Project -Parent | Split-Path -Leaf) ..." -ForegroundColor Cyan
    dotnet publish $Project -c Release -p:Platform=x64 -r win-x64 --no-self-contained -o $OutDir -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）：$Project" }

    $exe = Join-Path $OutDir 'Plain Craft Launcher 2.exe'
    if (-not (Test-Path $exe)) { throw "没找到发布产物：$exe" }

    $dest = Join-Path $release $TargetName
    Copy-Item $exe $dest -Force

    $size = [math]::Round((Get-Item $dest).Length / 1MB, 2)
    $ver = (Get-Item $dest).VersionInfo.ProductVersion
    Write-Host "  -> $TargetName  ${size} MB  $ver" -ForegroundColor Green

    if (-not $NoHash) {
        $hash = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $TargetName" | Set-Content (Join-Path $assets "$TargetName.sha256") -Encoding ascii -NoNewline
    }

    if ($Portable) {
        # 文件夹版必须单独发一次：单文件发布出来的目录里只有那个 exe，直接复制过去等于没区别。
        $portableOut = Join-Path $OutDir 'portable'
        Write-Host "> 发布文件夹版 ..." -ForegroundColor Cyan
        $publishArgs = @(
            'publish', $Project,
            '-c', 'Release',
            '-p:Platform=x64',
            '-r', 'win-x64',
            '--no-self-contained',
            '-p:PublishSingleFile=false',
            '-o', $portableOut,
            '-v', 'q'
        )
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish（文件夹版）失败（exit $LASTEXITCODE）：$Project" }

        $portableDest = Join-Path $assets $PortableDir
        Remove-Item $portableDest -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $portableDest -Force | Out-Null
        Copy-Item (Join-Path $portableOut '*') $portableDest -Recurse -Force
        $count = (Get-ChildItem $portableDest -Recurse -File).Count
        $total = [math]::Round(((Get-ChildItem $portableDest -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
        Write-Host "  -> $PortableDir  $count 个文件  ${total} MB（文件夹版）" -ForegroundColor Green
    }
}

New-Item -ItemType Directory -Path $stage, $release, $assets -Force | Out-Null

# Beta 的文件名跟着 metadata.json 走，改版本号不用改脚本。
$betaMetaPath = Join-Path $root 'SRC-Backups\Beta-V1.2-Aero\Plain Craft Launcher 2\metadata.json'
$betaTag = 'v1.3-beta'
if (Test-Path $betaMetaPath) {
    $meta = Get-Content $betaMetaPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $betaBase = $meta.version.base
    $betaSuffix = $meta.version.suffix
    if ([string]::IsNullOrWhiteSpace($betaSuffix)) { $betaTag = "v$betaBase" } else { $betaTag = "v$betaBase-$betaSuffix" }
}

Publish-One -Project $csprojMain -OutDir (Join-Path $stage 'v1.0') -TargetName 'PCL_Plus_x64.exe' -PortableDir 'PCL_Plus-portable'

if ($All) {
    if (-not (Test-Path $csprojBeta)) {
        Write-Warning "跳过 Beta：找不到 $csprojBeta"
    }
    else {
        Publish-One -Project $csprojBeta -OutDir (Join-Path $stage $betaTag) -TargetName "PCL_Plus_${betaTag}_x64.exe" -PortableDir "PCL_Plus_${betaTag}-portable"
    }
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "完成：exe 在 $release，校验与文件夹版在 $assets" -ForegroundColor Cyan
