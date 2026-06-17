#requires -Version 5.1

<#
.SYNOPSIS
    Release-gated package publish CLI (CORE-PUB-002): builds and publishes the
    four @livecore packages to the registry at the shared lockstep version on a
    release tag, and dry-runs the same path (packing all four, never pushing) on
    every push and pull request.

.DESCRIPTION
    The CI publish pipeline runs this script. It is fail-closed twice over:

      1. It builds the publish plan with Get-LiveCorePackagePublishPlan
         (LiveCorePackagePublish.psm1), which reuses the existing release-version
         gate (scripts/assert-release-version.ps1 / LiveCoreReleaseVersion.psm1,
         CORE-CMP-003): a release tag whose version disagrees with the packages'
         shared version, packages out of lockstep, or a non-release ref all throw
         before anything is built or pushed, so a mismatched or un-lockstepped
         version can never publish.

      2. On a real publish (not -DryRun), before publishing each package it
         queries the registry for the versions already published and REFUSES to
         republish an existing version (immutability) - mirroring the image
         publish job's immutable-tag guard, so a shipped version is never
         overwritten.

    With -DryRun it runs `pnpm publish --dry-run` for all four packages, which
    builds and packs each tarball (rewriting the workspace:* dependency to the
    resolved version) without contacting the registry or pushing anything, so the
    pipeline is proven on every push and pull request.

    Registry and access come from each package's publishConfig (the public npm
    registry under the @livecore scope, docs/23_PACKAGE_VERSIONING.md). No
    credential lives in the repository: the real publish authenticates with an
    ~/.npmrc the CI job writes from a secret (CORE-PUB-002).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/publish-packages.ps1 -Ref refs/tags/v0.1.0 -DryRun

.EXAMPLE
    pwsh -File scripts/publish-packages.ps1 -Ref refs/tags/v0.1.0
#>

[CmdletBinding()]
param(
    # The git ref being published, e.g. 'refs/tags/v0.1.0'.
    [Parameter(Mandatory = $true)]
    [string]$Ref,

    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepositoryRoot,

    # Dry-run the publish: build and pack all four packages without contacting the
    # registry or pushing. Used on every push and pull request.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $RepositoryRoot) {
    $RepositoryRoot = Split-Path -Parent $scriptDir
}
Import-Module (Join-Path $scriptDir 'LiveCorePackagePublish.psm1') -Force

# Fail-closed gate: a drifted, un-lockstepped or non-release ref throws here,
# before any package is built or published (CORE-PUB-002 / CORE-CMP-003).
$plan = Get-LiveCorePackagePublishPlan -Ref $Ref -RepositoryRoot $RepositoryRoot

# Immutability query: the versions already published for a package. A package that
# has never been published (npm 404) returns no versions, so its first publish is
# allowed. `npm view <name> versions --json` returns a JSON array (or a bare string
# when exactly one version exists).
$getPublishedVersions = {
    param($packageName)
    $raw = npm view $packageName versions --json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$raw)) {
        # Never published yet (404) or no versions: the first publish is allowed.
        return @()
    }
    $parsed = ($raw | Out-String) | ConvertFrom-Json
    if ($parsed -is [string]) {
        return @($parsed)
    }
    return @($parsed)
}

$mode = if ($DryRun) { 'dry-run' } else { 'publish' }
Write-Host "Release tag '$Ref' -> publishing $($plan.Count) packages at version '$($plan[0].Version)' ($mode)." -ForegroundColor Cyan

# Run pnpm from the repository root so the --filter selectors resolve against the
# workspace regardless of the caller's working directory.
Push-Location $RepositoryRoot
try {
    foreach ($package in $plan) {
        if ($DryRun) {
            Write-Host "Dry-running publish of $($package.Name)@$($package.Version) (built and packed, never pushed)..." -ForegroundColor Yellow
            # --no-git-checks: CI publishes from a detached release tag, not a branch.
            & pnpm --filter $package.Name publish --dry-run --no-git-checks
            if ($LASTEXITCODE -ne 0) {
                throw "Dry-run publish failed for $($package.Name) (CORE-PUB-002)."
            }
            continue
        }

        # Immutability: a release version is cut once, so a version already on the
        # registry must never be republished. Refuse rather than overwrite a shipped
        # package (mirrors the image publish job's immutable-tag guard).
        if (Test-LiveCorePackageVersionPublished -Name $package.Name -Version $package.Version -GetPublishedVersions $getPublishedVersions) {
            throw "Refusing to republish an already-published immutable version: $($package.Name)@$($package.Version) is already on the registry. A release version is cut once and never overwritten (CORE-PUB-002)."
        }

        Write-Host "Publishing $($package.Name)@$($package.Version)..." -ForegroundColor Green
        & pnpm --filter $package.Name publish --no-git-checks
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed for $($package.Name) (CORE-PUB-002)."
        }
        Write-Host "Published $($package.Name)@$($package.Version)." -ForegroundColor Green
    }
}
finally {
    Pop-Location
}

Write-Host "All $($plan.Count) packages handled at version '$($plan[0].Version)' ($mode)." -ForegroundColor Cyan
