#requires -Version 5.1

<#
.SYNOPSIS
    Tests the release-gated package publish gate (CORE-PUB-002): the publish plan
    is built only for a release tag whose version equals the four packages' shared
    lockstep version, a mismatched / un-lockstepped / non-release ref fails closed,
    and an already-published version is refused (immutability).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCorePackagePublish.psm1 and
    publish-packages.ps1 - no external test framework and no registry I/O - so it
    runs as a CI gate on every push and pull request and locally on both pwsh and
    Windows PowerShell 5.1. Exits 0 when every assertion holds and 1 when any
    fails.

    This is the required CORE-PUB-002 test: the gate refuses a version that
    disagrees with the release tag or is out of lockstep (fail-closed), and a
    republish of an existing version is refused. The real publish runs the same
    gate against the actual release tag in the publish job; the per-PR dry-run
    builds and packs all four packages.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-package-publish.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$repoRoot = Split-Path -Parent $scriptDir
Import-Module (Join-Path $scriptDir 'LiveCorePackagePublish.psm1') -Force
Import-Module (Join-Path $scriptDir 'LiveCoreReleaseVersion.psm1') -Force
$publishScript = Join-Path $scriptDir 'publish-packages.ps1'

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

# The real repository is in lockstep, so a release tag cut from the shared version
# yields a complete, ordered publish plan at that one version.
$sharedVersion = Get-LiveCorePackageVersion -RepositoryRoot $repoRoot
$plan = Get-LiveCorePackagePublishPlan -Ref "refs/tags/v$sharedVersion" -RepositoryRoot $repoRoot

AssertEqual '4' ([string]$plan.Count) 'the publish plan covers all four packages'
AssertTrue (@($plan | Where-Object { $_.Version -cne $sharedVersion }).Count -eq 0) `
    'every package in the plan is published at the one shared version'
AssertEqual '@livecore/contracts' $plan[0].Name `
    'contracts publishes first (a dependency is published before its dependents)'
AssertEqual '@livecore/sdk-ts' $plan[$plan.Count - 1].Name `
    'sdk-ts publishes last (it depends on contracts)'
$expectedNames = @('@livecore/contracts', '@livecore/design-tokens', '@livecore/ui-core', '@livecore/sdk-ts')
AssertEqual ($expectedNames -join ',') (($plan | ForEach-Object { $_.Name }) -join ',') `
    'the plan lists exactly the four published packages in dependency order'

# Fail closed: a release tag that does NOT equal the shared package version cannot
# produce a plan (the required "disagrees with the release tag" case).
AssertThrows { Get-LiveCorePackagePublishPlan -Ref 'refs/tags/v99.99.99' -RepositoryRoot $repoRoot } `
    'the plan fails closed when the release tag mismatches the shared package version'

# Fail closed: only a release tag publishes (CORE-OPS-009), so a branch ref cannot
# produce a plan.
AssertThrows { Get-LiveCorePackagePublishPlan -Ref 'refs/heads/main' -RepositoryRoot $repoRoot } `
    'the plan fails closed for a non-release ref'

# Immutability decision: a version already on the registry is refused; a new
# version, or a never-published package (empty list), is publishable. The stubs
# stand in for the registry query, which ignores its package-name argument here.
$publishedStub = { @('0.0.1', '0.1.0') }
AssertTrue (Test-LiveCorePackageVersionPublished -Name '@livecore/contracts' -Version '0.1.0' -GetPublishedVersions $publishedStub) `
    'a version already on the registry is detected as published (a republish is refused)'
AssertTrue (-not (Test-LiveCorePackageVersionPublished -Name '@livecore/contracts' -Version '0.2.0' -GetPublishedVersions $publishedStub)) `
    'a not-yet-published version is publishable'
$neverPublishedStub = { @() }
AssertTrue (-not (Test-LiveCorePackageVersionPublished -Name '@livecore/contracts' -Version '0.1.0' -GetPublishedVersions $neverPublishedStub)) `
    'the first publish of a never-published package is allowed'
$singleVersionStub = { '0.1.0' }
AssertTrue (Test-LiveCorePackageVersionPublished -Name '@livecore/contracts' -Version '0.1.0' -GetPublishedVersions $singleVersionStub) `
    'a package with exactly one published version (a bare string) is handled'

# Seed a one-package version bump in a throwaway tree: the four packages drift out
# of lockstep, so the plan fails closed (the cross-package guarantee the
# per-package pack tests miss) - the required "out of lockstep" case.
$seedRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-pubplan-" + [Guid]::NewGuid().ToString('N'))
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

    AssertThrows { Get-LiveCorePackagePublishPlan -Ref 'refs/tags/v0.2.0' -RepositoryRoot $seedRoot } `
        'the plan fails closed when the four packages are not in version lockstep'
}
finally {
    if (Test-Path -Path $seedRoot) {
        Remove-Item -Path $seedRoot -Recurse -Force
    }
}

# The publish CLI fails closed for a mismatching tag and for a non-release ref:
# the plan throws before any package is built or published, so no registry I/O or
# pnpm invocation is reached.
AssertThrows { & $publishScript -Ref 'refs/tags/v99.99.99' -RepositoryRoot $repoRoot -DryRun } `
    'the publish CLI fails closed for a release tag mismatching the package version'
AssertThrows { & $publishScript -Ref 'refs/heads/main' -RepositoryRoot $repoRoot -DryRun } `
    'the publish CLI fails closed for a non-release ref'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Package publish gate tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Package publish gate tests passed: only a release tag equal to the packages'' shared lockstep version publishes, and drift, lockstep breaks and a republish are refused fail-closed.' -ForegroundColor Green
exit 0
