#requires -Version 5.1

<#
.SYNOPSIS
    Tests the changelog-completeness gate logic (CORE-REL-004): a changed package
    surface with no changelog entry newer than the last release tag fails, while an
    Unreleased entry or a dated entry above the tag passes.

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreChangelogCompleteness.psm1 - no external
    test framework - so it runs as a CI gate and locally on both pwsh and Windows
    PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-changelog-completeness.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreChangelogCompleteness.psm1') -Force

$failures = New-Object System.Collections.Generic.List[string]

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) { Write-Host "PASS: $Because" } else { $failures.Add("FAIL: $Because") }
}

# --- SemVer comparison is numeric, not lexical ---
AssertTrue ((Compare-LiveCoreSemVer -Left '0.3.0' -Right '0.2.0') -gt 0) '0.3.0 is greater than 0.2.0'
AssertTrue ((Compare-LiveCoreSemVer -Left '0.2.0' -Right '0.2.0') -eq 0) '0.2.0 equals 0.2.0'
AssertTrue ((Compare-LiveCoreSemVer -Left '0.9.0' -Right '0.10.0') -lt 0) '0.9.0 is less than 0.10.0 (numeric, not lexical)'
AssertTrue ((Compare-LiveCoreSemVer -Left '1.0.0' -Right '0.99.99') -gt 0) '1.0.0 is greater than 0.99.99'

# --- Changelog "newer than tag" detection ---
$unreleasedWithContent = @"
# Changelog

## [Unreleased]

### Added

- A new optional field.

## [0.2.0] - 2026-06-19
"@
AssertTrue (Test-LiveCoreChangelogNewerThanTag -ChangelogContent $unreleasedWithContent -LastTagVersion '0.2.0') `
    'an Unreleased section with a bullet counts as newer than the tag'

$emptyUnreleasedThenTag = @"
# Changelog

## [Unreleased]

## [0.2.0] - 2026-06-19

### Added

- Shipped already.
"@
AssertTrue (-not (Test-LiveCoreChangelogNewerThanTag -ChangelogContent $emptyUnreleasedThenTag -LastTagVersion '0.2.0')) `
    'an empty Unreleased above the current tag is NOT newer than the tag'

$datedAboveTag = @"
# Changelog

## [0.3.0] - 2026-06-21

### Added

- The release was cut but not yet tagged.

## [0.2.0] - 2026-06-19
"@
AssertTrue (Test-LiveCoreChangelogNewerThanTag -ChangelogContent $datedAboveTag -LastTagVersion '0.2.0') `
    'a dated entry above the tag counts as newer (a cut not yet tagged)'

$topEqualsTag = @"
# Changelog

## [0.2.0] - 2026-06-19

### Added

- Shipped already.
"@
AssertTrue (-not (Test-LiveCoreChangelogNewerThanTag -ChangelogContent $topEqualsTag -LastTagVersion '0.2.0')) `
    'a top dated entry equal to the tag is NOT newer than the tag'

$emptyUnreleasedThenNewerDated = @"
# Changelog

## [Unreleased]

## [0.3.0] - 2026-06-21

### Added

- Cut above the tag.
"@
AssertTrue (Test-LiveCoreChangelogNewerThanTag -ChangelogContent $emptyUnreleasedThenNewerDated -LastTagVersion '0.2.0') `
    'an empty Unreleased falls through to a newer dated entry below it'

AssertTrue (-not (Test-LiveCoreChangelogNewerThanTag -ChangelogContent "# Changelog`n`nNo entries yet." -LastTagVersion '0.2.0')) `
    'a changelog with no version entries is not newer than the tag'

# --- Full report over changed packages ---
$current = $unreleasedWithContent
$stale = $topEqualsTag

$cleanReport = Get-LiveCoreChangelogCompletenessReport -LastTagVersion '0.2.0' `
    -ChangedPackages @('contracts') `
    -ChangelogContentByScope @{ root = $current; contracts = $current }
AssertTrue ($cleanReport.IsClean) 'a changed package documented in both its and the root changelog is clean'

$stalePackageReport = Get-LiveCoreChangelogCompletenessReport -LastTagVersion '0.2.0' `
    -ChangedPackages @('contracts') `
    -ChangelogContentByScope @{ root = $current; contracts = $stale }
AssertTrue (-not $stalePackageReport.IsClean) 'a changed package with a stale changelog is a violation'
AssertTrue (@($stalePackageReport.Violations | Where-Object { $_ -match 'packages/contracts/CHANGELOG' }).Count -eq 1) `
    'the violation names the stale package changelog'

$staleRootReport = Get-LiveCoreChangelogCompletenessReport -LastTagVersion '0.2.0' `
    -ChangedPackages @('contracts') `
    -ChangelogContentByScope @{ root = $stale; contracts = $current }
AssertTrue (-not $staleRootReport.IsClean) 'a current package but stale root changelog is a violation'
AssertTrue (@($staleRootReport.Violations | Where-Object { $_ -match 'root' }).Count -eq 1) `
    'the violation names the stale root changelog'

$noChangeReport = Get-LiveCoreChangelogCompletenessReport -LastTagVersion '0.2.0' `
    -ChangedPackages @() `
    -ChangelogContentByScope @{ root = $stale }
AssertTrue ($noChangeReport.IsClean) 'no changed package means nothing to document (clean even with a stale changelog)'

$datedLockstepReport = Get-LiveCoreChangelogCompletenessReport -LastTagVersion '0.2.0' `
    -ChangedPackages @('ui-core') `
    -ChangelogContentByScope @{ root = $datedAboveTag; 'ui-core' = $datedAboveTag }
AssertTrue ($datedLockstepReport.IsClean) 'a lockstep package whose changelog dates a version above the tag is clean'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Changelog completeness gate tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host $failure }
    exit 1
}

Write-Host ''
Write-Host 'Changelog completeness gate tests passed: an undocumented changed surface fails, a documented one passes.' -ForegroundColor Green
exit 0
