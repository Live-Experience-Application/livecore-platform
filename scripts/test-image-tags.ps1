#requires -Version 5.1

<#
.SYNOPSIS
    Tests that the CI publish job derives immutable, versioned container image
    references and fails closed for every non-release ref (CORE-OPS-009).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreImageTags.psm1 and
    derive-image-tags.ps1 - no external test framework, so it runs as a CI gate
    and locally on both pwsh and Windows PowerShell 5.1. Exits 0 when every
    assertion holds and 1 when any fails.

    This is the test for the two CORE-OPS-009 required test cases: "image tags
    are immutable and versioned" and the "CI dry-run of the publish job"
    (verifying the exact derivation the publish job uses, without registry I/O).

.EXAMPLE
    pwsh -NoProfile -File scripts/test-image-tags.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreImageTags.psm1') -Force
$deriveScript = Join-Path $scriptDir 'derive-image-tags.ps1'

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

$registry = 'ghcr.io'
$owner = 'Live-Experience-Application'

# Versioned: a release tag pins the exact version as the image tag.
AssertEqual 'ghcr.io/live-experience-application/livecore-api:1.2.3' `
    (Get-LiveCoreImageReference -Ref 'refs/tags/v1.2.3' -Registry $registry -Owner $owner -Component 'api') `
    'an API release tag derives the version-pinned API image reference'

AssertEqual 'ghcr.io/live-experience-application/livecore-worker:1.2.3' `
    (Get-LiveCoreImageReference -Ref 'refs/tags/v1.2.3' -Registry $registry -Owner $owner -Component 'worker') `
    'a worker release tag derives the version-pinned worker image reference'

# The owner segment is lowercased (container repositories are lowercase).
AssertTrue ((Get-LiveCoreImageReference -Ref 'refs/tags/v1.2.3' -Registry $registry -Owner $owner -Component 'api') -clike '*/live-experience-application/*') `
    'the owner segment is lowercased'

# A SemVer prerelease is still immutable and versioned.
AssertEqual 'ghcr.io/live-experience-application/livecore-api:1.4.0-rc.2' `
    (Get-LiveCoreImageReference -Ref 'refs/tags/v1.4.0-rc.2' -Registry $registry -Owner $owner -Component 'api') `
    'a prerelease release tag derives a version-pinned reference'

# Immutable: the derived tag is the release version, never a moving tag.
$reference = Get-LiveCoreImageReference -Ref 'refs/tags/v2.0.0' -Registry $registry -Owner $owner -Component 'api'
$derivedTag = $reference.Substring($reference.LastIndexOf(':') + 1)
AssertEqual '2.0.0' $derivedTag 'the image tag equals the release version (immutable, not moving)'
AssertTrue ($derivedTag -cne 'latest') 'the image tag is never the moving tag latest'
AssertTrue ($derivedTag -cmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,127}$') 'the image tag is a valid container tag'

# Deterministic: the same ref always derives the same reference.
AssertEqual `
    (Get-LiveCoreImageReference -Ref 'refs/tags/v3.1.4' -Registry $registry -Owner $owner -Component 'api') `
    (Get-LiveCoreImageReference -Ref 'refs/tags/v3.1.4' -Registry $registry -Owner $owner -Component 'api') `
    'derivation is deterministic for a given ref'

# Fail closed: never publish from a non-release ref, so a pull request or a
# branch push can never produce an image, and a moving or malformed tag is
# rejected rather than mapped to a mutable image tag.
$rejectedRefs = @(
    'refs/heads/main',
    'refs/pull/7/merge',
    'refs/tags/latest',
    'refs/tags/nightly',
    'refs/tags/v1.2',
    'refs/tags/v1.2.3.4',
    'refs/tags/1.2.3',
    'refs/tags/v1.2.3+build.9',
    'refs/tags/v01.2.3',
    'refs/tags/vlatest',
    ''
)
foreach ($badRef in $rejectedRefs) {
    $label = if ($badRef -eq '') { '(empty ref)' } else { $badRef }
    AssertThrows { Get-LiveCoreImageReference -Ref $badRef -Registry $registry -Owner $owner -Component 'api' } `
        "fails closed for non-release ref '$label'"
}

# Fail closed: only the api and worker images publish; any other component is rejected.
AssertThrows { Get-LiveCoreImageReference -Ref 'refs/tags/v1.2.3' -Registry $registry -Owner $owner -Component 'database' } `
    'fails closed for an unknown image component'

# The CLI wrapper emits both version-pinned references for a release tag...
$cliOutput = & $deriveScript -Ref 'refs/tags/v5.6.7' -Registry $registry -Owner $owner
AssertTrue ($cliOutput -contains 'api=ghcr.io/live-experience-application/livecore-api:5.6.7') `
    'the CLI emits the version-pinned API reference'
AssertTrue ($cliOutput -contains 'worker=ghcr.io/live-experience-application/livecore-worker:5.6.7') `
    'the CLI emits the version-pinned worker reference'

# ...and the CLI itself fails closed for a non-release ref.
AssertThrows { & $deriveScript -Ref 'refs/heads/main' -Registry $registry -Owner $owner } `
    'the CLI fails closed for a branch ref'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Image tag tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Image tag tests passed: image references are immutable, versioned and fail closed off a release tag.' -ForegroundColor Green
exit 0
