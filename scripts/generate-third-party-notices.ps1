#requires -Version 5.1

<#
.SYNOPSIS
    Generates and drift-gates the third-party attribution inventory and the
    per-package license/notice files of the LiveCore distribution (CORE-LIC-003).

.DESCRIPTION
    The single source of truth for attribution is csv/third_party_notices.csv. This
    script renders it into the committed THIRD-PARTY-NOTICES.md at the repository
    root (the inventory that ships in the container images and the package
    tarballs) and keeps the per-package copies in sync:

      - each published package (packages/<name>) carries a copy of the root
        AGPL LICENSE so the license ships in every package tarball, and
      - a copy of THIRD-PARTY-NOTICES.md, listed in the package's files[].

    It also runs the NOTICE-coverage check: every direct, runtime-shipping NuGet
    PackageReference in the API/worker projects must have an attribution row, so a
    newly added attribution-requiring dependency cannot ship without a notice.

    Modes:
      -Write   regenerate THIRD-PARTY-NOTICES.md and refresh the per-package
               LICENSE/THIRD-PARTY-NOTICES.md copies, then verify coverage.
      (default) check-only: fail (exit 1) if the committed inventory, the
               per-package copies or coverage are out of date - the CI drift gate.

    Fail-closed: a missing source CSV, an uncovered shipping dependency, or any
    drift exits non-zero. Compatible with Windows PowerShell 5.1 and pwsh on Linux.

.EXAMPLE
    pwsh -NoProfile -File scripts/generate-third-party-notices.ps1 -Write

.EXAMPLE
    pwsh -NoProfile -File scripts/generate-third-party-notices.ps1   # check-only (CI gate)
#>

[CmdletBinding()]
param(
    # Regenerate the committed files instead of only checking them.
    [switch]$Write,

    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $scriptDir }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

Import-Module (Join-Path $scriptDir 'LiveCoreLicenseCompliance.psm1') -Force

# The four published packages and the runtime host projects whose shipping
# dependencies must be attributed.
$packages = @('contracts', 'sdk-ts', 'design-tokens', 'ui-core')
$csprojRelativePaths = @('apps/api/LiveCore.Api.csproj', 'apps/worker/LiveCore.Worker.csproj')

$attributionCsv = Join-Path $RepoRoot 'csv/third_party_notices.csv'
$rootLicensePath = Join-Path $RepoRoot 'LICENSE'
$rootNoticePath = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md'

function Read-LiveCoreTextLf {
    # Reads a file and normalizes CRLF to LF so a Windows checkout (autocrlf) and a
    # freshly rendered LF string compare equal.
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return ([System.IO.File]::ReadAllText($Path) -replace "`r`n", "`n")
}

function Write-LiveCoreTextLf {
    param([string]$Path, [string]$Content)
    $normalized = $Content -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($Path, $normalized)
}

# --- Render the inventory from the source of truth. ---
$notices = Get-LiveCoreThirdPartyNotices -Path $attributionCsv
$renderedNotice = ConvertTo-LiveCoreThirdPartyNoticeContent -Notices $notices

# --- Coverage: every shipping dependency must be attributed. ---
$shipping = New-Object System.Collections.Generic.List[string]
foreach ($relative in $csprojRelativePaths) {
    $csproj = Join-Path $RepoRoot $relative
    foreach ($name in (Get-LiveCoreShippingPackageReferences -CsprojPath $csproj)) { $shipping.Add($name) }
}
$coverage = Test-LiveCoreNoticeCoverage -ShippingReferenceName @($shipping | Sort-Object -Unique) -Notices $notices

$drift = New-Object System.Collections.Generic.List[string]

if (-not (Test-Path -LiteralPath $rootLicensePath)) {
    throw "Root LICENSE not found at $rootLicensePath; cannot sync per-package license files."
}
$rootLicense = Read-LiveCoreTextLf -Path $rootLicensePath

if ($Write) {
    Write-LiveCoreTextLf -Path $rootNoticePath -Content $renderedNotice
    foreach ($package in $packages) {
        $packageDir = Join-Path $RepoRoot "packages/$package"
        Write-LiveCoreTextLf -Path (Join-Path $packageDir 'THIRD-PARTY-NOTICES.md') -Content $renderedNotice
        Write-LiveCoreTextLf -Path (Join-Path $packageDir 'LICENSE') -Content $rootLicense
    }
    Write-Host "Generated THIRD-PARTY-NOTICES.md and refreshed LICENSE/THIRD-PARTY-NOTICES.md in $($packages.Count) package(s)." -ForegroundColor Green
}
else {
    $committedNotice = Read-LiveCoreTextLf -Path $rootNoticePath
    if ($null -eq $committedNotice) {
        $drift.Add('THIRD-PARTY-NOTICES.md is missing at the repository root.')
    }
    elseif ($committedNotice -ne $renderedNotice) {
        $drift.Add('THIRD-PARTY-NOTICES.md is out of date with csv/third_party_notices.csv. Run scripts/generate-third-party-notices.ps1 -Write and commit.')
    }
    foreach ($package in $packages) {
        $packageDir = Join-Path $RepoRoot "packages/$package"
        $packageNotice = Read-LiveCoreTextLf -Path (Join-Path $packageDir 'THIRD-PARTY-NOTICES.md')
        if ($null -eq $packageNotice) { $drift.Add("packages/$package/THIRD-PARTY-NOTICES.md is missing.") }
        elseif ($packageNotice -ne $renderedNotice) { $drift.Add("packages/$package/THIRD-PARTY-NOTICES.md is out of date with the root inventory.") }

        $packageLicense = Read-LiveCoreTextLf -Path (Join-Path $packageDir 'LICENSE')
        if ($null -eq $packageLicense) { $drift.Add("packages/$package/LICENSE is missing.") }
        elseif ($packageLicense -ne $rootLicense) { $drift.Add("packages/$package/LICENSE differs from the root AGPL LICENSE.") }
    }
}

$failed = $false

if (-not $coverage.Covered) {
    $failed = $true
    Write-Host 'NOTICE coverage FAILED: shipping dependencies with no attribution row in csv/third_party_notices.csv:' -ForegroundColor Red
    foreach ($name in $coverage.Missing) { Write-Host "  - $name" }
}

if ($drift.Count -gt 0) {
    $failed = $true
    Write-Host 'Third-party attribution drift detected:' -ForegroundColor Red
    foreach ($item in $drift) { Write-Host "  - $item" }
}

if ($failed) { exit 1 }

if ($Write) {
    Write-Host "Attribution inventory written; $($shipping.Count) shipping reference(s) all attributed." -ForegroundColor Green
}
else {
    Write-Host "Third-party attribution up to date: inventory, per-package LICENSE/NOTICE and coverage all consistent ($($notices.Count) component(s))." -ForegroundColor Green
}
exit 0
