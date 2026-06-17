#requires -Version 5.1

<#
.SYNOPSIS
    CI lint that enforces SPDX license headers on first-party source (CORE-LIC-004).

.DESCRIPTION
    Every first-party, hand-authored source file that ships in a distribution
    artifact - the C# (.cs) that builds the container images and the TypeScript
    (.ts/.tsx) that builds the published packages - must carry a header:

        // SPDX-License-Identifier: AGPL-3.0-or-later
        // Copyright (c) <year> The LiveCore Platform contributors

    so a copied file keeps its license context (docs/16_LICENSING.md, CONTRIBUTING.md).

    Generated source is excluded: the EF Core migrations
    (apps/api/Persistence/Migrations) and the generated, drift-gated OpenAPI
    contract types (packages/contracts/src/openapi.ts). See
    LiveCoreContributorPolicy.psm1 for the exact scope.

    Files are enumerated with `git ls-files`, so only TRACKED source is checked and
    the enumeration is fail-closed.

    Run with -Fix to insert the header into every in-scope file that is missing it
    (preserving each file's existing line ending and body), the way a contributor
    fixes the lint locally before committing.

    Exits 0 when every in-scope file carries the header (or after -Fix), 1 when a
    file is missing it, and 2 on a configuration error.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-license-headers.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-license-headers.ps1 -Fix -Year 2026
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot,

    # Insert the header into in-scope files that are missing it.
    [switch]$Fix,

    # Copyright year written by -Fix. Defaults to 2026 (the year this policy was
    # introduced); a deterministic default keeps the inserted text reproducible.
    [int]$Year = 2026
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    # $PSScriptRoot is not available in param() defaults on Windows PowerShell 5.1.
    $scriptDir = $PSScriptRoot
    if (-not $scriptDir) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $RepoRoot = Split-Path -Parent $scriptDir
}
else {
    $scriptDir = Join-Path $RepoRoot 'scripts'
}

$RepoRoot = (Resolve-Path -Path $RepoRoot).Path

Import-Module (Join-Path $scriptDir 'LiveCoreContributorPolicy.psm1') -Force

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

if ($Fix) {
    $files = Get-LiveCoreHeaderSourceFile -RepoRoot $RepoRoot
    $fixedCount = 0
    foreach ($relativePath in $files) {
        $fullPath = Join-Path $RepoRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath)) { continue }
        $content = [System.IO.File]::ReadAllText($fullPath)
        if (Test-LiveCoreLicenseHeaderPresent -Content $content) { continue }
        $updated = Get-LiveCoreContentWithLicenseHeader -Content $content -Year $Year
        [System.IO.File]::WriteAllText($fullPath, $updated, $utf8NoBom)
        $fixedCount++
        Write-Host ("  +header {0}" -f $relativePath)
    }
    Write-Host "SPDX header fix: inserted the header into $fixedCount file(s)." -ForegroundColor Green
    exit 0
}

$report = Get-LiveCoreLicenseHeaderReport -RepoRoot $RepoRoot

Write-Host "SPDX header lint: $($report.Checked) in-scope source file(s) checked."

if ($report.IsClean) {
    Write-Host 'SPDX header lint passed: every shipped first-party source file carries the AGPL-3.0-or-later SPDX header.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host "SPDX header lint FAILED: $($report.Missing.Count) file(s) are missing the SPDX header." -ForegroundColor Red
foreach ($path in $report.Missing) {
    Write-Host ("  MISSING: {0}" -f $path)
}
Write-Host ''
Write-Host 'Every first-party .cs/.ts/.tsx source file that ships in an artifact must start with:'
Write-Host '  // SPDX-License-Identifier: AGPL-3.0-or-later'
Write-Host '  // Copyright (c) <year> The LiveCore Platform contributors'
Write-Host 'See docs/16_LICENSING.md and CONTRIBUTING.md. Add the headers automatically with:'
Write-Host '  pwsh -NoProfile -File scripts/lint-license-headers.ps1 -Fix'
exit 1
