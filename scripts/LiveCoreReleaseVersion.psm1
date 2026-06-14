#requires -Version 5.1

<#
.SYNOPSIS
    Reads the four published TypeScript packages' shared version and compares a
    release tag against it, so a published image/release tag can never drift from
    the package version (CORE-CMP-003).

.DESCRIPTION
    The four @livecore packages are released together and always share one
    version (docs/23_PACKAGE_VERSIONING.md). The .NET API and worker host images
    are versioned by the release TAG (LiveCoreImageTags.psm1), not by a manifest
    of their own, so the intended relationship at release time is that the
    release tag equals the packages' shared version - one coherent repository
    release. This module derives the shared package version fail-closed (the four
    must agree) and compares it to the release tag's version, so the publish gate
    can fail when they drift.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
# Reuse the fail-closed release-tag derivation (CORE-OPS-009) so the tag-vs-version
# gate and the image-tag derivation agree on what a valid release version is.
Import-Module (Join-Path $scriptDir 'LiveCoreImageTags.psm1') -Force

# The four published packages that share one lockstep version (docs/23).
$script:PublishedPackages = @('contracts', 'sdk-ts', 'design-tokens', 'ui-core')

# A SemVer version (optional prerelease, no build metadata) - the same shape a
# release tag carries, so a package version is directly comparable to one.
$script:PackageVersionPattern = '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?$'

function Get-LiveCorePackageVersion {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    if (-not (Test-Path -Path $RepositoryRoot)) {
        throw "Refusing to read the shared package version: repository root '$RepositoryRoot' does not exist (CORE-CMP-003)."
    }

    $versionsByPackage = [ordered]@{}
    foreach ($package in $script:PublishedPackages) {
        $manifestPath = Join-Path $RepositoryRoot (Join-Path 'packages' (Join-Path $package 'package.json'))
        if (-not (Test-Path -Path $manifestPath)) {
            throw "Refusing to derive the shared package version: '$manifestPath' is missing (CORE-CMP-003)."
        }

        $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
        $version = [string]$manifest.version
        if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch $script:PackageVersionPattern) {
            throw "Refusing to use package '@livecore/$package' version '$version': it is not a well-formed SemVer release version (CORE-CMP-003)."
        }
        $versionsByPackage[$package] = $version
    }

    $distinct = @($versionsByPackage.Values | Select-Object -Unique)
    if ($distinct.Count -ne 1) {
        $detail = ($versionsByPackage.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
        throw "The four published packages are not in version lockstep ($detail): they must share one version (docs/23_PACKAGE_VERSIONING.md, CORE-CMP-003)."
    }

    return [string]$distinct[0]
}

function Test-LiveCoreReleaseTagVersion {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Ref,

        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    # Fail closed on a non-release ref: only a release tag publishes (CORE-OPS-009).
    $releaseVersion = Get-LiveCoreReleaseVersion -Ref $Ref
    $packageVersion = Get-LiveCorePackageVersion -RepositoryRoot $RepositoryRoot

    return [pscustomobject]@{
        ReleaseVersion = $releaseVersion
        PackageVersion = $packageVersion
        IsMatch        = ($releaseVersion -ceq $packageVersion)
    }
}

Export-ModuleMember -Function Get-LiveCorePackageVersion, Test-LiveCoreReleaseTagVersion
