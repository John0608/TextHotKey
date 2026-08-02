<#
.SYNOPSIS
    TextHotKey 릴리스 패키지(자체 포함 zip)를 만든다.

.DESCRIPTION
    1) 메인 앱을 자체 포함(self-contained)으로 publish
    2) Updater를 자체 포함 단일 파일로 publish
    3) Updater.exe를 앱 폴더에 복사
    4) 앱 폴더 내용을 TextHotKey-win-x64.zip으로 압축

    결과: publish\TextHotKey-win-x64.zip
    이 zip 이름/구조는 UpdateManager.AssetName 및 자동 업데이트 로직과 맞춰져 있다.

.EXAMPLE
    ./build-release.ps1
#>
param(
    [string]$Configuration = 'Release',
    [string]$Rid = 'win-x64',
    # 지정 시 어셈블리 버전을 이 값으로 덮어쓴다(예: 태그 v1.2.0 → "1.2.0").
    # 비우면 csproj의 <Version>을 사용한다.
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$versionArgs = @()
if ($Version -ne '') {
    $versionArgs = @("-p:Version=$Version", "-p:AssemblyVersion=$Version", "-p:FileVersion=$Version")
    Write-Host "==> 버전 지정: $Version" -ForegroundColor Cyan
}

# 설치 파일 이름/표시에 쓸 버전. -Version 없으면 csproj의 <Version>을 사용.
$appVer = $Version
if ($appVer -eq '') {
    [xml]$csproj = Get-Content (Join-Path $root 'TextHotKey\TextHotKey.csproj')
    $appVer = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $appVer) { $appVer = '0.0.0' }
}

$publishRoot = Join-Path $root 'publish'
$outApp      = Join-Path $publishRoot 'app'
$outUpdater  = Join-Path $publishRoot 'updater'
$assetName   = 'TextHotKey-win-x64.zip'
$zipPath     = Join-Path $publishRoot $assetName

Write-Host '==> 이전 publish 정리' -ForegroundColor Cyan
if (Test-Path $publishRoot) { Remove-Item $publishRoot -Recurse -Force }

Write-Host '==> 메인 앱 publish (self-contained)' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'TextHotKey\TextHotKey.csproj') `
    -c $Configuration -r $Rid --self-contained true `
    -p:PublishSingleFile=false `
    @versionArgs `
    -o $outApp
if ($LASTEXITCODE -ne 0) { throw '메인 앱 publish 실패' }

Write-Host '==> Updater publish (self-contained, single file)' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'Updater\Updater.csproj') `
    -c $Configuration -r $Rid --self-contained true `
    -p:PublishSingleFile=true `
    @versionArgs `
    -o $outUpdater
if ($LASTEXITCODE -ne 0) { throw 'Updater publish 실패' }

Write-Host '==> Updater.exe를 앱 폴더로 복사' -ForegroundColor Cyan
Copy-Item (Join-Path $outUpdater 'Updater.exe') (Join-Path $outApp 'Updater.exe') -Force

Write-Host "==> 압축: $assetName" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $outApp '*') -DestinationPath $zipPath -Force

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

# 신규 설치용 설치 마법사(Inno Setup) 빌드. ISCC가 있으면 만들고, 없으면 zip만 두고 넘어간다.
Write-Host '==> 설치 마법사(Inno Setup) 빌드' -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }

$setupExe = $null
if ($iscc) {
    $iss = Join-Path $root 'installer\TextHotKey.iss'
    & $iscc `
        "/DMyAppVersion=$appVer" `
        "/DAppDir=$outApp" `
        "/DOutputDir=$publishRoot" `
        "/DIconFile=$(Join-Path $root 'TextHotKey\favicon.ico')" `
        $iss
    if ($LASTEXITCODE -ne 0) { throw '설치 마법사 빌드 실패' }
    $setupExe = Join-Path $publishRoot "TextHotKey-Setup-$appVer.exe"
} else {
    Write-Host 'ISCC.exe(Inno Setup)를 찾지 못해 설치 마법사는 건너뜁니다. (zip만 생성)' -ForegroundColor Yellow
    Write-Host '설치: winget install JRSoftware.InnoSetup  또는  choco install innosetup' -ForegroundColor Yellow
}

Write-Host ""
Write-Host "완료: $zipPath ($sizeMb MB)" -ForegroundColor Green
Write-Host "  · 기존 사용자용 업데이트 zip (업데이터가 이 파일을 받아 교체)" -ForegroundColor Green
if ($setupExe -and (Test-Path $setupExe)) {
    $setupMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
    Write-Host "완료: $setupExe ($setupMb MB)" -ForegroundColor Green
    Write-Host "  · 신규 사용자용 설치 마법사 (per-user 설치)" -ForegroundColor Green
}
