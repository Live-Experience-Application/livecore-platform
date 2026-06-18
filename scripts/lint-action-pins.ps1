#requires -Version 5.1

<#
.SYNOPSIS
    CI lint that fails the build on any GitHub Actions `uses:` reference that is
    not pinned to an immutable commit SHA (CORE-DEP-008).

.DESCRIPTION
    Scans .github/workflows/* and reconciles every `uses:` reference against the
    pinning policy: a third-party action must reference a full 40-char commit SHA
    with the readable version kept in a trailing comment, mirroring the digest
    pinning the Dockerfiles already apply to their base images. A mutable tag or
    branch ref (actions/checkout@v4), a comment-less SHA, or an undigested
    docker:// reference fails the build, so a compromised or retagged action can
    never silently run with the publish job's packages: write token.

    The companion .github/dependabot.yml keeps the pins current: Dependabot bumps a
    SHA and rewrites the readable version comment when a new action release ships,
    so immutability does not mean staleness.

    Exits 0 when every `uses:` is SHA-pinned, 1 when a reference is not, and 2 on a
    configuration error.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-action-pins.ps1
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    # $PSScriptRoot is not available in param() defaults on Windows PowerShell 5.1.
    $scriptDir = $PSScriptRoot
    if (-not $scriptDir) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $RepoRoot = Split-Path -Parent $scriptDir
}
else {
    $scriptDir = Join-Path $RepoRoot 'scripts'
}

$RepoRoot = (Resolve-Path -Path $RepoRoot).Path

Import-Module (Join-Path $scriptDir 'LiveCoreActionPinLint.psm1') -Force

$workflowDir = Join-Path $RepoRoot '.github/workflows'

if (-not (Test-Path -Path $workflowDir)) {
    Write-Error "Workflows directory not found: $workflowDir"
    exit 2
}

$review = Get-LiveCoreActionPinReview -WorkflowDirectory $workflowDir

Write-Host "GitHub Actions pin lint: scanned $($review.Total) 'uses:' reference(s) in .github/workflows/."

if ($review.IsClean) {
    Write-Host 'GitHub Actions pin lint passed: every uses: is pinned to a full 40-char commit SHA with a readable version comment.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host "GitHub Actions pin lint FAILED: $($review.Violations.Count) unpinned uses: reference(s)." -ForegroundColor Red

foreach ($violation in $review.Violations) {
    $reason = switch ($violation.Kind) {
        'Unpinned' { 'is not pinned to a 40-char commit SHA (a mutable tag or branch ref)' }
        'MissingComment' { 'is SHA-pinned but has no trailing "# version" comment (the readable version is required)' }
        'DockerUnpinned' { 'is a docker:// action with no @sha256: digest (a mutable image tag)' }
        default { 'is not pinned correctly' }
    }
    Write-Host ("  {0}:{1}  uses: {2}  ->  {3}" -f $violation.File, $violation.LineNumber, $violation.Reference, $reason)
}

Write-Host ''
Write-Host 'Every third-party action must be pinned to an immutable commit SHA, with the readable version in a'
Write-Host 'trailing comment - the same digest-pinning discipline the Dockerfiles apply to their base images'
Write-Host '(CORE-DEP-008, threat: a compromised/retagged action runs with the publish job''s packages: write token).'
Write-Host 'Resolve a tag to its commit SHA and pin it, for example:'
Write-Host '  git ls-remote https://github.com/actions/checkout refs/tags/v4.3.1'
Write-Host '  uses: actions/checkout@<resolved-40-char-sha> # v4.3.1'
Write-Host '.github/dependabot.yml keeps these pins current (it rewrites the SHA and the version comment on a new release).'
exit 1
