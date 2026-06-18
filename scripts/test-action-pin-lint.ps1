#requires -Version 5.1

<#
.SYNOPSIS
    Tests the GitHub Actions commit-SHA pinning lint (CORE-DEP-008).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreActionPinLint.psm1 - no external test
    framework, so it runs as a CI gate and locally on both pwsh and Windows
    PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

    It proves the analysis logic over fixtures (a SHA-pinned ref with a version
    comment passes; a floating tag, a semver tag, a branch, a comment-less SHA and
    an undigested docker:// ref each fail; a reusable-workflow ref and a local
    in-repo action are handled; a `uses:` inside a run-block script or a comment is
    not mistaken for a step), proves the required directory-level behaviour (a
    seeded floating-tag workflow fails the review), and then guards the real
    repository state: every `uses:` in .github/workflows/* is SHA-pinned, so a new
    unpinned reference fails this test too.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-action-pin-lint.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreActionPinLint.psm1') -Force
$repoRoot = Split-Path -Parent $scriptDir

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

# A real 40-char commit SHA shape (actions/checkout@v4.3.1's commit) used as the
# pinned-fixture ref; the lint never resolves it, it only checks the SHA shape.
$sha = '34e114876b0b11c390a56381ad16ebd13914f8d5'

# --- Kind classification over single references. ---
AssertEqual 'Pinned' (Get-LiveCoreActionPinKind -Reference "actions/checkout@$sha" -Comment 'v4.3.1') `
    'a 40-char SHA ref with a version comment is Pinned (the required form)'
AssertEqual 'MissingComment' (Get-LiveCoreActionPinKind -Reference "actions/checkout@$sha" -Comment '') `
    'a SHA ref with no trailing comment is MissingComment (a violation)'
AssertEqual 'Unpinned' (Get-LiveCoreActionPinKind -Reference 'actions/checkout@v4' -Comment '') `
    'a floating major tag (@v4) is Unpinned (a violation)'
AssertEqual 'Unpinned' (Get-LiveCoreActionPinKind -Reference 'actions/checkout@v4.3.1' -Comment '') `
    'a pinned-looking semver tag (@v4.3.1) is still Unpinned (a tag is mutable)'
AssertEqual 'Unpinned' (Get-LiveCoreActionPinKind -Reference 'actions/checkout@main' -Comment '') `
    'a branch ref (@main) is Unpinned (a violation)'
AssertEqual 'Unpinned' (Get-LiveCoreActionPinKind -Reference 'actions/checkout' -Comment '') `
    'a ref-less action (defaults to the default branch) is Unpinned (a violation)'
AssertEqual 'Local' (Get-LiveCoreActionPinKind -Reference './.github/actions/setup' -Comment '') `
    'a first-party in-repo action (./...) is Local (allowed, nothing to pin)'
AssertEqual 'Pinned' (Get-LiveCoreActionPinKind -Reference "owner/repo/.github/workflows/wf.yml@$sha" -Comment 'v1.2.3') `
    'a reusable-workflow ref pinned to a SHA with a comment is Pinned'
AssertEqual 'DockerDigest' (Get-LiveCoreActionPinKind -Reference ('docker://ghcr.io/o/i@sha256:' + ('a' * 64)) -Comment 'v1') `
    'a docker:// action pinned by @sha256 digest is DockerDigest (allowed)'
AssertEqual 'DockerUnpinned' (Get-LiveCoreActionPinKind -Reference 'docker://ghcr.io/o/i:1.2.3' -Comment '') `
    'a docker:// action on a mutable image tag is DockerUnpinned (a violation)'

# --- Line parsing: a uses: key is recognized, others are not. ---
AssertEqual "actions/checkout@$sha" (Get-LiveCoreActionReference -Line "      - uses: actions/checkout@$sha # v4.3.1").Reference `
    'a list-item uses: key is parsed into its reference'
AssertEqual 'v4.3.1' (Get-LiveCoreActionReference -Line "      - uses: actions/checkout@$sha # v4.3.1").Comment `
    'the trailing version comment is parsed off the uses: line'
AssertEqual "actions/checkout@$sha" (Get-LiveCoreActionReference -Line "        uses: ""actions/checkout@$sha"" # v4.3.1").Reference `
    'a quoted uses: value is parsed without the quotes'
AssertTrue ($null -eq (Get-LiveCoreActionReference -Line '          echo "this run step mentions uses: actions/checkout@v4"')) `
    'a uses: inside a run-block script line is not parsed as a step key'
AssertTrue ($null -eq (Get-LiveCoreActionReference -Line '      # uses: actions/checkout@v4')) `
    'a commented-out uses: line is not parsed as a step key'
AssertTrue ($null -eq (Get-LiveCoreActionReference -Line '      - name: Check out repository')) `
    'a non-uses: step line is not parsed'

# --- Findings over a whole file fragment. ---
$cleanContent = @"
jobs:
  build:
    steps:
      - uses: actions/checkout@$sha # v4.3.1
      - uses: ./.github/actions/setup
      - uses: actions/setup-node@$sha # v4.4.0
"@
$cleanFindings = Get-LiveCoreActionPinFinding -Content $cleanContent
AssertTrue ($cleanFindings.Count -eq 3) 'all three uses: references in the fragment are found'
AssertTrue (@($cleanFindings | Where-Object { $_.IsViolation }).Count -eq 0) 'a fully SHA-pinned fragment has no violations'

$dirtyContent = @"
jobs:
  build:
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@$sha
"@
$dirtyFindings = Get-LiveCoreActionPinFinding -Content $dirtyContent
$violationKinds = @($dirtyFindings | Where-Object { $_.IsViolation } | ForEach-Object { $_.Kind } | Sort-Object)
AssertEqual 'MissingComment;Unpinned' ($violationKinds -join ';') `
    'a floating tag is flagged Unpinned and a comment-less SHA is flagged MissingComment'

# --- Directory-level review over a throwaway workflows directory. ---
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-action-pin-" + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
try {
    # A clean workflow plus a non-YAML file that must be ignored.
    Set-Content -LiteralPath (Join-Path $tempDir 'clean.yml') -Value $cleanContent -NoNewline
    Set-Content -LiteralPath (Join-Path $tempDir 'README.md') -Value 'uses: actions/checkout@v4' -NoNewline

    $clean = Get-LiveCoreActionPinReview -WorkflowDirectory $tempDir
    AssertTrue ($clean.IsClean) 'a workflows directory with only SHA-pinned uses: passes the review'
    AssertTrue ($clean.Total -eq 3) 'the review counts only the workflow YAML uses:, not the README line'

    # Seed a floating-tag ref - the required negative case.
    Set-Content -LiteralPath (Join-Path $tempDir 'floating.yml') -Value $dirtyContent -NoNewline
    $dirty = Get-LiveCoreActionPinReview -WorkflowDirectory $tempDir
    AssertTrue (-not $dirty.IsClean) 'a seeded floating-tag uses: makes the review fail'
    AssertTrue (@($dirty.Violations | Where-Object { $_.Reference -eq 'actions/checkout@v4' }).Count -eq 1) `
        'the floating-tag reference is reported as a violation'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Guard the real repository state. ---
$realWorkflows = Join-Path $repoRoot '.github/workflows'
$real = Get-LiveCoreActionPinReview -WorkflowDirectory $realWorkflows
AssertTrue ($real.Total -gt 0) 'the real workflows contain uses: references the lint scans'
AssertTrue ($real.IsClean) 'every uses: in .github/workflows/* is pinned to a full 40-char commit SHA with a version comment'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "GitHub Actions pin lint tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'GitHub Actions pin lint tests passed: SHA-pin detection and the real-tree guard behave as documented.' -ForegroundColor Green
exit 0
