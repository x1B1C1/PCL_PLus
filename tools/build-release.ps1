<#
.SYNOPSIS
    把当前源码发布成单文件 exe，放到 release\ 供本地测试。

.DESCRIPTION
    产物（框架依赖单文件，约 27 MB，需要 .NET 10 Desktop Runtime）：
      release\PCL_Plus_x64.exe              <- 主仓库（直链名，OTA 清单固定引用）
      release\PCL_Plus_Release_x64.exe      <- 主仓库，正式线资产名（suffix 为空 / R / hotfix-N）
      release\PCL_Plus_Beta_x64.exe         <- 主仓库，测试线资产名（beta / snapshot / nightly）
      release\PCL_Plus_v3.0.1-Beta1_x64.exe    <- 版本留档副本
      release\PCL_Plus_v1.5.0-beta_x64.exe    <- SRC-Backups\Beta-V1.2-Aero（-All 时）

    历史版本一律保留：release\ 会累积每个版本的 exe（正式线与测试线都算），
    脚本只覆盖同名文件，不删除任何旧版本。想清理由人工确认后手动删。

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
    [switch]$BetaOnly,
    [switch]$NoHash,
    [switch]$Portable
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------- 编译期密钥
# PCL.Core.SourceGenerators 只在 PCL_WRITE_SECRET 存在时，才把 PCL_* 环境变量
# 编进 SecretDictionary（见 docs/BUILD.md 第三节）。不设这两个变量的话，产物里的
# Secrets.MSOAuthClientId 就是空的，微软登录会直接报「未配置 Client ID」。
# 想换一个 Azure 应用，调用前先设 $env:PCL_MS_CLIENT_ID 即可覆盖下面的默认值。
$defaultClientId = 'c14b0370-8d75-42f8-b329-5b60d39e319f'
if ([string]::IsNullOrWhiteSpace($env:PCL_MS_CLIENT_ID)) { $env:PCL_MS_CLIENT_ID = $defaultClientId }
$env:PCL_WRITE_SECRET = '1'
Write-Host "[key] MS_CLIENT_ID = $($env:PCL_MS_CLIENT_ID)（编译期注入）" -ForegroundColor DarkGray

# 生成器不会在增量编译里重跑：必须先把编译服务器关掉，否则新密钥进不去。
dotnet build-server shutdown 2>&1 | Out-Null

$release = Join-Path $root 'release'
$assets = Join-Path $root 'release-assets'
$stage = Join-Path $root '.build-out'
$csprojMain = Join-Path $root 'Plain Craft Launcher 2\Plain Craft Launcher 2.csproj'
$csprojBeta = Join-Path $root 'SRC-Backups\Beta-V1.2-Aero\Plain Craft Launcher 2\Plain Craft Launcher 2.csproj'

function Publish-One {
    param([string]$Project, [string]$OutDir, [string]$TargetName, [string]$PortableDir,
          [string]$ChannelCopy = '', [string]$ChannelLine = '')

    Write-Host "> 发布 $(Split-Path $Project -Parent | Split-Path -Leaf) ..." -ForegroundColor Cyan

    # 源生成器把 PCL_* 写进 SecretDictionary 时，MSBuild 并不会把「环境变量」
    # 当成输入，所以增量编译会直接复用上一次的产物 —— 密钥就永远进不去。
    # 想稳妥地把 client id 编进去，只能在发布前清掉这几处的 obj/bin。
    $projDir = Split-Path $Project -Parent
    $treeRoot = Split-Path $projDir -Parent
    foreach ($sub in @('PCL.Core', 'PCL.Core.SourceGenerators', 'Plain Craft Launcher 2')) {
        foreach ($leaf in @('obj', 'bin')) {
            $p = Join-Path (Join-Path $treeRoot $sub) $leaf
            if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    dotnet publish $Project -c Release -p:Platform=x64 -r win-x64 --no-self-contained -o $OutDir -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）：$Project" }

    $exe = Join-Path $OutDir 'Plain Craft Launcher 2.exe'
    if (-not (Test-Path $exe)) { throw "没找到发布产物：$exe" }

    # 直接 Copy-Item 覆盖会被杀软/索引器偶发占用，失败了就改成「改名旧的，再写新的」。
    $dest = Join-Path $release $TargetName
    try {
        Copy-Item $exe $dest -Force
    }
    catch {
        $old = "$dest.old"
        if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
        Rename-Item $dest ([IO.Path]::GetFileName($old)) -ErrorAction Stop
        Copy-Item $exe $dest -Force
        Remove-Item $old -Force -ErrorAction SilentlyContinue
    }

    $size = [math]::Round((Get-Item $dest).Length / 1MB, 2)
    $ver = (Get-Item $dest).VersionInfo.ProductVersion
    Write-Host "  -> $TargetName  ${size} MB  $ver" -ForegroundColor Green

    if (-not $NoHash) {
        $hash = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $TargetName" | Set-Content (Join-Path $assets "$TargetName.sha256") -Encoding ascii -NoNewline
    }

    # 发布线资产名与版本号解耦：正式线永远是 PCL_Plus_Release_x64.exe，
    # 测试线永远是 PCL_Plus_Beta_x64.exe，方便直链下载与 OTA 清单引用。
    if ($ChannelCopy) {
        $channelPath = Join-Path $release $ChannelCopy
        Copy-Item $dest $channelPath -Force
        $channelHash = (Get-FileHash $channelPath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$channelHash  $ChannelCopy" | Set-Content (Join-Path $assets "$ChannelCopy.sha256") -Encoding ascii -NoNewline
        Write-Host "  -> $ChannelCopy（$ChannelLine 线）" -ForegroundColor DarkGray
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

if (-not $BetaOnly) {
    # 先读主树 metadata：发布线与文件名都跟着版本号走。
    $mainMetaPath = Join-Path $root 'Plain Craft Launcher 2\metadata.json'
    $mMeta = $null
    if (Test-Path $mainMetaPath) {
        $mMeta = Get-Content $mainMetaPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }

    # 正式线：无后缀 / R / hotfix-N；其余（beta / Beta1 / snapshot / nightly / pre）都算测试线。
    $mLine = 'Beta'
    if ($mMeta) {
        $mSuffix = [string]$mMeta.version.suffix
        if ([string]::IsNullOrWhiteSpace($mSuffix)) { $mLine = 'Release' }
        if ($mSuffix -eq 'R') { $mLine = 'Release' }
        if ($mSuffix -like 'hotfix*') { $mLine = 'Release' }
    }

    # 更新器与文档都用固定的 PCL_Plus_x64.exe，所以这个直链名保留；
    # 另外留一份发布线资产名与一份带版本号的副本，历史版本才会一直累积。
    $mainArgs = @{
        Project     = $csprojMain
        OutDir      = (Join-Path $stage 'v1.0')
        TargetName  = 'PCL_Plus_x64.exe'
        PortableDir = 'PCL_Plus-portable'
        ChannelCopy = "PCL_Plus_${mLine}_x64.exe"
        ChannelLine = $mLine
    }
    Publish-One @mainArgs

    if ($mMeta) {
        # 发布标记 R 是正式版，副本名跟着界面显示走（PCL_Plus_v1.1.1R_x64.exe）；
        # 真正的 GitHub tag 仍是 SemVer 合法的 v1.1.1-R，由发布脚本单独拼。
        $mTag = "v$($mMeta.version.base)-$($mMeta.version.suffix)"
        if ([string]::IsNullOrWhiteSpace($mMeta.version.suffix)) { $mTag = "v$($mMeta.version.base)" }
        if ($mMeta.version.suffix -eq 'R') { $mTag = "v$($mMeta.version.base)R" }
        $versioned = Join-Path $release "PCL_Plus_${mTag}_x64.exe"
        Copy-Item (Join-Path $release 'PCL_Plus_x64.exe') $versioned -Force
        Write-Host "  + $([IO.Path]::GetFileName($versioned))（版本副本，留档）" -ForegroundColor DarkGray

        # 留档副本也要有校验值，否则过几个版本就分不清手里那个文件是哪次构建
        $versionedName = [IO.Path]::GetFileName($versioned)
        $versionedHash = (Get-FileHash $versioned -Algorithm SHA256).Hash.ToLowerInvariant()
        "$versionedHash  $versionedName" | Set-Content (Join-Path $assets "$versionedName.sha256") -Encoding ascii -NoNewline
    }
}

if ($All -or $BetaOnly) {
    if (-not (Test-Path $csprojBeta)) {
        Write-Warning "跳过 Beta：找不到 $csprojBeta"
    }
    else {
        Publish-One -Project $csprojBeta -OutDir (Join-Path $stage $betaTag) -TargetName "PCL_Plus_${betaTag}_x64.exe" -PortableDir "PCL_Plus_${betaTag}-portable"
    }
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "完成：exe 在 $release，校验与文件夹版在 $assets" -ForegroundColor Cyan
