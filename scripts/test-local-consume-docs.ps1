#requires -Version 5.1

<#
.SYNOPSIS
    Tests the local-consume contract docs gate (CORE-DXL-003).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreLocalConsumeDocs.psm1 - no external test
    framework, no Docker and no build, so it runs as a CI gate and locally on both
    pwsh and Windows PowerShell 5.1. Exits 0 when every assertion holds and 1 when any
    fails.

    It proves the docs-lint logic over fixtures (a README section missing the
    pack:local coordinate, a :local image tag, the keep-version rule, the release path
    or the versioning pointer is each rejected; a versioning doc missing the pointer
    section, a coordinate or the back-link is each rejected) and then guards the REAL
    checked-in docs:

      - deploy/compose/README.md documents the local-consume contract - both the
        images:local and pack:local coordinates, the three :local image tags and the
        four dist/*.tgz tarballs, the keep-the-version-unchanged lockstep rule, the
        real-version-bump release path and the pointer to docs/23; and
      - docs/23_PACKAGE_VERSIONING.md points back at that contract with the
        keep-version rule.

    This is the docs lint / link check the story's required test calls for; the
    forbidden-core-terms boundary scan and the other docs gates stay green because the
    change is documentation-only.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-local-consume-docs.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-local-consume-docs.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreLocalConsumeDocs.psm1') -Force
$repoRoot = Split-Path -Parent $scriptDir

$failures = New-Object System.Collections.Generic.List[string]

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because")
    }
}

# A minimal, well-formed deploy/compose/README.md fixture: a local-consume contract
# section carrying every required element, bounded by a following ## heading so the
# section extractor stops at the right place. Each switch perturbs exactly one
# invariant so a negative test isolates one finding.
function Get-FixtureReadme {
    param(
        [string]$PackScript = 'pack:local',
        [string]$MigrationsTag = 'livecore-migrations:local',
        [string]$KeepRule = 'the package version stays unchanged so the lockstep guard stays green',
        [string]$ReleasePath = 'a real version bump is the normal release path',
        [string]$Pointer = 'see docs/23_PACKAGE_VERSIONING.md',
        [switch]$OmitSection
    )

    if ($OmitSection) {
        return @'
# Compose

## Some other section

Nothing about the local-consume contract here.

## The next section
'@
    }

    return @"
# Compose

## The local-consume contract for a downstream vertical (CORE-DXL-003)

Run ``pnpm run images:local`` to build livecore-api:local, livecore-worker:local and
$MigrationsTag, and ``pnpm run $PackScript`` to pack the four dist/*.tgz tarballs
(@livecore/contracts, @livecore/sdk-ts, @livecore/design-tokens, @livecore/ui-core)
into dist/. $KeepRule. $ReleasePath ($Pointer).

## The migrate-before-API gate
"@
}

# A minimal, well-formed docs/23 pointer-section fixture.
function Get-FixtureVersioning {
    param(
        [string]$ImagesScript = 'images:local',
        [string]$PackScript = 'pack:local',
        [string]$KeepRule = 'the version number stays unchanged so the lockstep guard stays green',
        [string]$BackLink = 'see deploy/compose/README.md',
        [switch]$OmitSection
    )

    if ($OmitSection) {
        return @'
# Package Versioning

## Lockstep releases

The four packages share one version.

## Changelog
'@
    }

    return @"
# Package Versioning

## Consuming an unreleased Core locally (CORE-DXL-003)

A vertical runs an unreleased Core via ``pnpm run $ImagesScript`` and
``pnpm run $PackScript``. $KeepRule. A real version bump is the normal release path;
$BackLink for the full contract.

## Changelog
"@
}

# =============================================================================
# Test-LiveCoreLocalConsumeContractDocs - the deploy/compose/README.md contract
# =============================================================================

AssertTrue (Test-LiveCoreLocalConsumeContractDocs -ReadmeText (Get-FixtureReadme)).IsValid `
    'a README local-consume section naming both coordinates, the tags/tarballs, the keep-version rule, the release path and the versioning pointer is valid'

# --- Negative: a missing section is rejected. ---
$noSection = Test-LiveCoreLocalConsumeContractDocs -ReadmeText (Get-FixtureReadme -OmitSection)
AssertTrue (-not $noSection.IsValid) 'a README with no local-consume contract section is rejected'
AssertTrue (($noSection.Findings -join "`n") -match 'SECTION') 'the rejection names the missing section'

# --- Negative: a section that omits the pack:local coordinate is rejected. ---
$noPack = Test-LiveCoreLocalConsumeContractDocs -ReadmeText (Get-FixtureReadme -PackScript 'build')
AssertTrue (-not $noPack.IsValid) 'a local-consume section that omits the pack:local coordinate is rejected'
AssertTrue (($noPack.Findings -join "`n") -match 'pack:local') 'the rejection names the missing pack:local coordinate'

# --- Negative: a section that omits a :local image tag is rejected. ---
$noTag = Test-LiveCoreLocalConsumeContractDocs -ReadmeText (Get-FixtureReadme -MigrationsTag 'the migrations image')
AssertTrue (-not $noTag.IsValid) 'a local-consume section that omits the migrations :local tag is rejected'
AssertTrue (($noTag.Findings -join "`n") -match 'livecore-migrations:local') 'the rejection names the missing migrations tag'

# --- Negative: a section that omits the keep-version rule is rejected. ---
$noRule = Test-LiveCoreLocalConsumeContractDocs -ReadmeText (Get-FixtureReadme -KeepRule 'the packages just work')
AssertTrue (-not $noRule.IsValid) 'a local-consume section that omits the keep-the-version-unchanged lockstep rule is rejected'
AssertTrue (($noRule.Findings -join "`n") -match '(?i)unchanged|lockstep') 'the rejection names the missing keep-version rule'

# --- Negative: a section that omits the release path is rejected. ---
$noRelease = Test-LiveCoreLocalConsumeContractDocs -ReadmeText (Get-FixtureReadme -ReleasePath 'that is all')
AssertTrue (-not $noRelease.IsValid) 'a local-consume section that omits the normal release path is rejected'
AssertTrue (($noRelease.Findings -join "`n") -match '(?i)release') 'the rejection names the missing release path'

# --- Negative: a section that omits the versioning pointer is rejected. ---
$noPointer = Test-LiveCoreLocalConsumeContractDocs -ReadmeText (Get-FixtureReadme -Pointer 'see elsewhere')
AssertTrue (-not $noPointer.IsValid) 'a local-consume section that omits the docs/23 pointer is rejected'
AssertTrue (($noPointer.Findings -join "`n") -match 'docs/23_PACKAGE_VERSIONING.md') 'the rejection names the missing versioning pointer'

# =============================================================================
# Test-LiveCoreLocalConsumeVersioningPointer - the docs/23 pointer section
# =============================================================================

AssertTrue (Test-LiveCoreLocalConsumeVersioningPointer -VersioningText (Get-FixtureVersioning)).IsValid `
    'a docs/23 pointer section naming both coordinates, the keep-version rule and the back-link is valid'

# --- Negative: a missing pointer section is rejected. ---
$vNoSection = Test-LiveCoreLocalConsumeVersioningPointer -VersioningText (Get-FixtureVersioning -OmitSection)
AssertTrue (-not $vNoSection.IsValid) 'a docs/23 with no local-consume pointer section is rejected'
AssertTrue (($vNoSection.Findings -join "`n") -match 'SECTION') 'the rejection names the missing pointer section'

# --- Negative: a pointer that omits the images:local coordinate is rejected. ---
$vNoImages = Test-LiveCoreLocalConsumeVersioningPointer -VersioningText (Get-FixtureVersioning -ImagesScript 'build')
AssertTrue (-not $vNoImages.IsValid) 'a docs/23 pointer that omits the images:local coordinate is rejected'
AssertTrue (($vNoImages.Findings -join "`n") -match 'images:local') 'the rejection names the missing images:local coordinate'

# --- Negative: a pointer that omits the back-link to the README is rejected. ---
$vNoLink = Test-LiveCoreLocalConsumeVersioningPointer -VersioningText (Get-FixtureVersioning -BackLink 'see somewhere')
AssertTrue (-not $vNoLink.IsValid) 'a docs/23 pointer that omits the deploy/compose/README.md back-link is rejected'
AssertTrue (($vNoLink.Findings -join "`n") -match 'deploy/compose/README.md') 'the rejection names the missing README back-link'

# =============================================================================
# Guard the REAL checked-in docs.
# =============================================================================

# 1. deploy/compose/README.md documents the local-consume contract.
$readmePath = Join-Path $repoRoot 'deploy/compose/README.md'
AssertTrue (Test-Path -LiteralPath $readmePath) 'the deploy/compose/README.md exists'
$realReadme = [System.IO.File]::ReadAllText($readmePath)
$realReadmeResult = Test-LiveCoreLocalConsumeContractDocs -ReadmeText $realReadme
if (-not $realReadmeResult.IsValid) {
    foreach ($finding in $realReadmeResult.Findings) { $failures.Add("FAIL (real deploy/compose/README.md): $finding") }
}
else {
    Write-Host 'PASS: the real deploy/compose/README.md documents the local-consume contract - both coordinates (images:local + pack:local), the three :local tags, the four dist/*.tgz tarballs, the keep-version lockstep rule, the release path and the docs/23 pointer'
}

# 2. docs/23_PACKAGE_VERSIONING.md points back at the contract.
$versioningPath = Join-Path $repoRoot 'docs/23_PACKAGE_VERSIONING.md'
AssertTrue (Test-Path -LiteralPath $versioningPath) 'the docs/23_PACKAGE_VERSIONING.md exists'
$realVersioning = [System.IO.File]::ReadAllText($versioningPath)
$realPointerResult = Test-LiveCoreLocalConsumeVersioningPointer -VersioningText $realVersioning
if (-not $realPointerResult.IsValid) {
    foreach ($finding in $realPointerResult.Findings) { $failures.Add("FAIL (real docs/23_PACKAGE_VERSIONING.md): $finding") }
}
else {
    Write-Host 'PASS: the real docs/23_PACKAGE_VERSIONING.md points at the local-consume contract with the keep-version lockstep rule and the deploy/compose/README.md back-link'
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "local-consume contract docs tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'local-consume contract docs tests passed: deploy/compose/README.md documents the two coordinates (images:local + pack:local), the :local tags, the dist/*.tgz tarballs, the keep-the-version-unchanged lockstep rule and the real-version-bump release path, and docs/23_PACKAGE_VERSIONING.md points back at the contract (CORE-DXL-003).' -ForegroundColor Green
exit 0
