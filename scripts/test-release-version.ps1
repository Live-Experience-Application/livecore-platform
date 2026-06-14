#requires -Version 5.1

<#
.SYNOPSIS
    Tests the release-tag-vs-package-version publish gate (CORE-CMP-003): a
    release tag matching the four packages' shared version passes, a mismatching
    tag fails, and the four packages drifting out of lockstep fails closed.

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreReleaseVersion.psm1 and
    assert-release-version.ps1 - no external test framework - so it runs as a CI
    gate and locally on both pwsh and Windows PowerShell 5.1. Exits 0 when every
    assertion holds and 1 when any fails.

    This is the required CORE-CMP-003 test "a release tag mismatching the package
    version fails the publish gate", run on every push and pull request (the CI
    publish-dry-run job), with the real gate running on a release tag in the
    publish job.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-release-version.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$repoRoot = Split-Path -Parent $scriptDir
Import-Module (Join-Path $scriptDir 'LiveCoreReleaseVersion.psm1') -Force
$assertScript = Join-Path $scriptDir 'assert-release-version.ps1'

$failures = New-Object System.Collections.Generic.List[string]

function AssertEqual {
    param([string]$Expected, [string]$Actual, [string]$Because)
    if ($Expected -ceq $Actual) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because`n      expected: '$Expected'`n      actual:   '$Actual'")
    }
}

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because")
    }
}

function AssertThrows {
    param([scriptblock]$Action, [string]$Because)
    $threw = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $threw = $true
    }
    if ($threw) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because (expected a fail-closed error, but it succeeded)")
    }
}

# The real repository is in lockstep, so the shared version reads cleanly...
$sharedVersion = Get-LiveCorePackageVersion -RepositoryRoot $repoRoot
AssertTrue ($sharedVersion -cmatch '^\d+\.\d+\.\d+') `
    'the four published packages share one well-formed version'

# ...and a release tag cut from that version matches the gate.
$matched = Test-LiveCoreReleaseTagVersion -Ref "refs/tags/v$sharedVersion" -RepositoryRoot $repoRoot
AssertTrue ($matched.IsMatch) 'a release tag equal to the shared package version matches'
AssertEqual $sharedVersion $matched.ReleaseVersion 'the gate reports the release tag version'
AssertEqual $sharedVersion $matched.PackageVersion 'the gate reports the shared package version'

# A release tag that does NOT equal the package version is detected as drift.
$mismatched = Test-LiveCoreReleaseTagVersion -Ref 'refs/tags/v99.99.99' -RepositoryRoot $repoRoot
AssertTrue (-not $mismatched.IsMatch) 'a release tag mismatching the package version is detected as drift'

# Fail closed for a non-release ref: only a release tag publishes (CORE-OPS-009).
AssertThrows { Test-LiveCoreReleaseTagVersion -Ref 'refs/heads/main' -RepositoryRoot $repoRoot } `
    'the gate fails closed for a non-release ref'

# The CLI gate passes for a release tag matching the package version...
$cliThrew = $false
try {
    & $assertScript -Ref "refs/tags/v$sharedVersion" -RepositoryRoot $repoRoot | Out-Null
}
catch {
    $cliThrew = $true
}
AssertTrue (-not $cliThrew) 'the publish gate CLI passes for a release tag matching the package version'

# ...and FAILS CLOSED for a release tag mismatching the package version (the
# required CORE-CMP-003 test), and for a non-release ref.
AssertThrows { & $assertScript -Ref 'refs/tags/v99.99.99' -RepositoryRoot $repoRoot } `
    'the publish gate CLI fails for a release tag mismatching the package version'
AssertThrows { & $assertScript -Ref 'refs/heads/main' -RepositoryRoot $repoRoot } `
    'the publish gate CLI fails closed for a non-release ref'

# Seed a one-package version bump in a throwaway tree: the four packages drift
# out of lockstep, so deriving the shared version fails closed and the gate
# refuses to publish - the cross-package guarantee the per-package tests miss.
$seedRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-lockstep-" + [Guid]::NewGuid().ToString('N'))
try {
    $seededVersions = [ordered]@{
        'contracts'     = '0.2.0'
        'sdk-ts'        = '0.1.0'
        'design-tokens' = '0.1.0'
        'ui-core'       = '0.1.0'
    }
    foreach ($package in $seededVersions.Keys) {
        $packageDir = Join-Path $seedRoot (Join-Path 'packages' $package)
        New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
        $manifest = [pscustomobject]@{ name = "@livecore/$package"; version = $seededVersions[$package] }
        $manifest | ConvertTo-Json | Set-Content -Path (Join-Path $packageDir 'package.json') -Encoding UTF8
    }

    AssertThrows { Get-LiveCorePackageVersion -RepositoryRoot $seedRoot } `
        'a single-package version bump fails closed (the four packages are not in lockstep)'
    AssertThrows { Test-LiveCoreReleaseTagVersion -Ref 'refs/tags/v0.2.0' -RepositoryRoot $seedRoot } `
        'the gate fails closed when the four packages are not in version lockstep'
}
finally {
    if (Test-Path -Path $seedRoot) {
        Remove-Item -Path $seedRoot -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Release version gate tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Release version gate tests passed: the release tag must equal the packages'' shared version, and drift fails closed.' -ForegroundColor Green
exit 0
