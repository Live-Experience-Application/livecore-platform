#requires -Version 5.1

<#
.SYNOPSIS
    Publish gate (CORE-CMP-003): fails the release publish when the release tag's
    version does not equal the four published packages' shared version, so a
    published image/release tag can never drift from the package version.

.DESCRIPTION
    The CI publish job runs this on a release tag before building or pushing any
    image. It derives the release version from the git ref (reusing the
    fail-closed release-tag derivation, CORE-OPS-009) and the shared package
    version from packages/*/package.json (fail-closed if the four disagree), and
    exits non-zero when they differ - so drift cannot ship. The .NET host images
    are versioned by the release tag, not a manifest of their own; the asserted
    relationship is that that tag equals the packages' shared version - one
    coherent repository release (docs/23_PACKAGE_VERSIONING.md).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/assert-release-version.ps1 -Ref refs/tags/v0.1.0
#>

[CmdletBinding()]
param(
    # The git ref being published, e.g. 'refs/tags/v0.1.0'.
    [Parameter(Mandatory = $true)]
    [string]$Ref,

    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $RepositoryRoot) {
    $RepositoryRoot = Split-Path -Parent $scriptDir
}
Import-Module (Join-Path $scriptDir 'LiveCoreReleaseVersion.psm1') -Force

$result = Test-LiveCoreReleaseTagVersion -Ref $Ref -RepositoryRoot $RepositoryRoot
if (-not $result.IsMatch) {
    throw "Refusing to publish: release tag version '$($result.ReleaseVersion)' does not match the published packages' shared version '$($result.PackageVersion)'. Cut the release tag from the same version the packages declare (docs/23_PACKAGE_VERSIONING.md, CORE-CMP-003)."
}

Write-Host "Release tag version '$($result.ReleaseVersion)' matches the published packages' shared version '$($result.PackageVersion)'." -ForegroundColor Green
