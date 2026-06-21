#requires -Version 5.1

<#
.SYNOPSIS
    CI gate (CORE-REL-004): fail when a published package's typed surface changed
    since the last release tag with no CHANGELOG entry newer than that tag.

.DESCRIPTION
    Reads the last release tag (the highest reachable `v*` SemVer tag, unless
    -BaseTag overrides it), determines which of the four published packages changed
    their `src/` since that tag, and requires each changed package's `CHANGELOG.md`
    AND the root `CHANGELOG.md` to carry an entry NEWER than the tag - an
    `## [Unreleased]` section with a bullet, or a dated `## [X.Y.Z]` entry above the
    tag (a release already cut but not yet tagged). The pure decision lives in
    LiveCoreChangelogCompleteness.psm1 and is unit-tested by
    test-changelog-completeness.ps1.

    This binds a surface change to its changelog the same way the OpenAPI/event/
    sdk-parity drift gates bind the published types to the server - so a new field,
    export or SDK method cannot ship undocumented (docs/23_PACKAGE_VERSIONING.md,
    "Enforcement"). When no `v*` tag exists yet, the gate passes (there is no baseline
    to compare against).

    Exits 0 when clean, 1 when a changed package surface is undocumented, and 2 on a
    configuration error (git range unavailable). Compatible with Windows PowerShell
    5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-changelog-completeness.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-changelog-completeness.ps1 -BaseTag v0.2.0
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot,

    # Override the baseline release tag. Defaults to the highest reachable v* tag.
    [string]$BaseTag,

    # Upper bound of the comparison range. Defaults to HEAD.
    [string]$HeadRef = 'HEAD'
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $scriptDir = $PSScriptRoot
    if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = Split-Path -Parent $scriptDir
}
else {
    $scriptDir = Join-Path $RepoRoot 'scripts'
}
$RepoRoot = (Resolve-Path -Path $RepoRoot).Path

Import-Module (Join-Path $scriptDir 'LiveCoreChangelogCompleteness.psm1') -Force

# The four published packages that share one lockstep version (docs/23).
$publishedPackages = @('contracts', 'sdk-ts', 'design-tokens', 'ui-core')

function Get-HighestReleaseTag {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][string]$Root)

    $tags = @(& git -C $Root tag --list 'v*')
    $best = $null
    foreach ($tag in $tags) {
        $candidate = $tag.Trim()
        if (-not $candidate) { continue }
        try {
            if ($null -eq $best) {
                $best = $candidate
            }
            elseif ((Compare-LiveCoreSemVer -Left $candidate.Substring(1) -Right $best.Substring(1)) -gt 0) {
                $best = $candidate
            }
        }
        catch {
            Write-Verbose "Ignoring non-SemVer v* tag '$candidate': $($_.Exception.Message)"
        }
    }
    return $best
}

$baseTag = $BaseTag
if (-not $baseTag) { $baseTag = Get-HighestReleaseTag -Root $RepoRoot }

if (-not $baseTag) {
    Write-Host 'Changelog completeness: no release tag yet; nothing to enforce.' -ForegroundColor Green
    exit 0
}

$baseVersion = $baseTag
if ($baseVersion -match '^[vV]') { $baseVersion = $baseVersion.Substring(1) }

# Determine which published packages changed their src/ since the baseline tag.
$changed = New-Object System.Collections.Generic.List[string]
foreach ($package in $publishedPackages) {
    $changedFiles = @(& git -C $RepoRoot diff --name-only "$baseTag..$HeadRef" -- "packages/$package/src/")
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Changelog completeness: 'git diff $baseTag..$HeadRef' failed (is the tag fetched? use fetch-depth: 0)." -ErrorAction Continue
        exit 2
    }
    $changedFiles = @($changedFiles | Where-Object { $_ -and $_.Trim() -ne '' })
    if ($changedFiles.Count -gt 0) { $changed.Add($package) }
}

# Read the changelogs the report needs (root always; each changed package).
$byScope = @{}
$rootChangelog = Join-Path $RepoRoot 'CHANGELOG.md'
if (Test-Path -LiteralPath $rootChangelog) { $byScope['root'] = Get-Content -LiteralPath $rootChangelog -Raw } else { $byScope['root'] = '' }
foreach ($package in $changed) {
    $path = Join-Path $RepoRoot (Join-Path 'packages' (Join-Path $package 'CHANGELOG.md'))
    if (Test-Path -LiteralPath $path) { $byScope[$package] = Get-Content -LiteralPath $path -Raw } else { $byScope[$package] = '' }
}

$report = Get-LiveCoreChangelogCompletenessReport -LastTagVersion $baseVersion -ChangedPackages $changed.ToArray() -ChangelogContentByScope $byScope

Write-Host "Changelog completeness: baseline $baseTag; $($changed.Count) published package(s) changed source since it."

if ($report.IsClean) {
    Write-Host 'Changelog completeness gate passed: every changed package surface is documented in a changelog entry newer than the baseline tag.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host "Changelog completeness gate FAILED: $($report.Violations.Count) undocumented change(s)." -ForegroundColor Red
foreach ($violation in $report.Violations) {
    Write-Host "  $violation"
}
Write-Host ''
Write-Host 'Add an entry under "## [Unreleased]" (with a ### Added/Changed/... bullet) to each'
Write-Host 'flagged CHANGELOG.md, or cut a release that dates a new version above the baseline tag.'
Write-Host 'See docs/23_PACKAGE_VERSIONING.md ("Enforcement").'
exit 1
