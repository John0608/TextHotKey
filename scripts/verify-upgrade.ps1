<#
.SYNOPSIS
    이번에 발행할 패키지로 "이전 릴리스 → 이번 버전" 업데이트가 실제로 되는지 검증한다.

.DESCRIPTION
    사용자가 갖고 있을 이전 릴리스를 골라 그 zip을 내려받고, 각각에 대해
    TextHotKey.Tests의 ReleaseUpgrade 테스트를 실행한다. 하나라도 실패하면 exit 1.
      - 최신 안정판(Latest)
      - 그보다 새로운 사전 릴리스(베타)가 있으면 가장 새로운 것
    이번 버전보다 새롭거나 같은 릴리스(재실행 시 자기 자신 등)는 제외한다.

.EXAMPLE
    ./scripts/verify-upgrade.ps1 -NewZip publish/TextHotKey-win-x64.zip -NewVersion 1.4.0
.EXAMPLE
    # 특정 이전 버전만 확인
    ./scripts/verify-upgrade.ps1 -NewZip publish/TextHotKey-win-x64.zip -NewVersion 1.4.0 -FromTags v1.3.3
#>
param(
    [Parameter(Mandatory)] [string]$NewZip,
    # 표시 버전(예: 1.4.0, 1.4.0-beta.2)
    [Parameter(Mandatory)] [string]$NewVersion,
    # 비우면 자동 선택
    [string[]]$FromTags = @(),
    [string]$Repo = 'John0608/TextHotKey',
    [string]$WorkDir = ''
)

# 외부 명령(dotnet/gh)의 성공 여부는 $LASTEXITCODE로 판단한다. Windows PowerShell 5.1에서 'Stop'이면
# 외부 명령의 stderr 출력만으로 스크립트가 중단되므로 'Continue'로 두고 cmdlet에는 -ErrorAction Stop을 준다.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
if ($WorkDir -eq '') { $WorkDir = Join-Path $root 'publish-upgrade' }
$NewZip = (Resolve-Path $NewZip -ErrorAction Stop).Path

# UpdateManager.SortKey와 같은 순서: 1.4.0-beta.1 < 1.4.0-beta.2 < 1.4.0 < 1.4.1-beta.1
function Get-SortKey([string]$tag) {
    if ($tag.TrimStart('v', 'V') -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-beta\.(\d+))?$') { return $null }
    $rev = if ($matches[4]) { [int]$matches[4] } else { [int]::MaxValue }
    return [version]::new([int]$matches[1], [int]$matches[2], [int]$matches[3], $rev)
}

$newKey = Get-SortKey $NewVersion
if (-not $newKey) { throw "버전 형식을 해석할 수 없음: $NewVersion" }

if ($FromTags.Count -eq 0) {
    $releases = gh release list --repo $Repo --limit 100 --exclude-drafts --json tagName,isPrerelease,isLatest | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'gh release list 실패' }
    $parsed = $releases | ForEach-Object { [pscustomobject]@{ Tag = $_.tagName; Pre = $_.isPrerelease; Latest = $_.isLatest; Key = Get-SortKey $_.tagName } } |
        Where-Object { $_.Key -and $_.Key -lt $newKey }

    $stable = $parsed | Where-Object Latest | Select-Object -First 1
    if (-not $stable) { $stable = $parsed | Where-Object { -not $_.Pre } | Sort-Object Key -Descending | Select-Object -First 1 }
    $beta = $parsed | Where-Object Pre | Sort-Object Key -Descending | Select-Object -First 1

    $FromTags = @()
    if ($stable) { $FromTags += $stable.Tag }
    if ($beta -and (-not $stable -or $beta.Key -gt $stable.Key)) { $FromTags += $beta.Tag }
}

if ($FromTags.Count -eq 0) {
    Write-Host '이전 릴리스가 없어 업데이트 검증을 건너뜀.' -ForegroundColor Yellow
    exit 0
}
Write-Host "==> 업데이트 검증: $($FromTags -join ', ') -> $NewVersion" -ForegroundColor Cyan

# 1.4.0-beta.1 이하 앱은 X.Y.Z-beta.N을 X.Y.Z 정식판보다 높게 판정한다(1.4.0-beta.2에서 수정).
# 그런 베타 사용자는 같은 X.Y.Z 정식판을 자동 업데이트로 받지 못하므로 경고한다.
# (이 검증은 구버전 앱의 판정 단계를 실행하지 않으므로 테스트로는 잡히지 않는다.)
$lastOldOrdering = [version]'1.4.0.1'
foreach ($tag in $FromTags) {
    $k = Get-SortKey $tag
    if ($k.Revision -ne [int]::MaxValue -and $newKey.Revision -eq [int]::MaxValue -and
        $k.Major -eq $newKey.Major -and $k.Minor -eq $newKey.Minor -and $k.Build -eq $newKey.Build -and
        [version]::new($k.Major, $k.Minor, $k.Build, $k.Revision) -le $lastOldOrdering) {
        $msg = "$tag 사용자는 $NewVersion 을(를) 자동 업데이트로 받지 못함(구버전의 베타/정식 판정 결함). 먼저 다음 베타를 배포하거나 수동 설치 안내 필요."
        Write-Host "::warning::$msg"
    }
}

# 테스트 프로젝트는 한 번만 빌드
dotnet build (Join-Path $root 'TextHotKey.Tests') -c Release
if ($LASTEXITCODE -ne 0) { throw '테스트 프로젝트 빌드 실패' }

$failed = @()
foreach ($tag in $FromTags) {
    $dir = Join-Path $WorkDir $tag
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction Stop }
    New-Item -ItemType Directory -Force $dir -ErrorAction Stop | Out-Null
    gh release download $tag --repo $Repo --pattern 'TextHotKey-win-x64.zip' --dir $dir
    if ($LASTEXITCODE -ne 0) { throw "$tag 패키지 다운로드 실패" }

    Write-Host "==> $tag -> $NewVersion" -ForegroundColor Cyan
    $env:THK_UPGRADE_FROM_ZIP = Join-Path $dir 'TextHotKey-win-x64.zip'
    $env:THK_UPGRADE_FROM_VERSION = $tag.TrimStart('v', 'V')
    $env:THK_UPGRADE_TO_ZIP = $NewZip
    $env:THK_UPGRADE_TO_VERSION = $NewVersion
    dotnet test (Join-Path $root 'TextHotKey.Tests') -c Release --no-build --filter 'Category=ReleaseUpgrade' `
        --logger "trx;LogFileName=upgrade-$tag.trx" --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) { $failed += $tag }
}

Remove-Item Env:THK_UPGRADE_* -ErrorAction SilentlyContinue
if ($failed.Count -gt 0) {
    Write-Host "업데이트 검증 실패: $($failed -join ', ') -> $NewVersion" -ForegroundColor Red
    exit 1
}
Write-Host '업데이트 검증 통과.' -ForegroundColor Green
