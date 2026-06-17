#requires -Version 5.1

<#
.SYNOPSIS
    CI lint that enforces DCO sign-off on contributed commits (CORE-LIC-004).

.DESCRIPTION
    Every contributed, non-merge commit must carry a Developer Certificate of
    Origin sign-off trailer whose email matches the commit author:

        Signed-off-by: Display Name <email@example.com>

    certifying the contributor has the right to submit the work under the project
    license and grants the dual-license right (CONTRIBUTING.md,
    DEVELOPER_CERTIFICATE_OF_ORIGIN, docs/16_LICENSING.md). `git commit -s` adds it.

    The lint validates exactly the commits introduced by the current push or pull
    request - the BaseSha..HeadSha range - not the project's whole history, so it
    binds new contributions without retroactively failing on pre-policy commits.
    Merge commits are skipped (the common DCO check exempts them).

    Range resolution:
      - BaseSha and HeadSha given      -> validate BaseSha..HeadSha.
      - BaseSha missing/zero/unknown   -> validate only HeadSha (or HEAD), so a
        brand-new branch or a shallow checkout fails closed onto the tip commit
        rather than scanning unrelated history.

    Exits 0 when every checked commit is signed off, 1 when one is not, and 2 on a
    configuration error (no git work tree).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-dco-signoff.ps1 -BaseSha $base -HeadSha $head

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-dco-signoff.ps1   # validates HEAD
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot,

    # Exclusive lower bound of the commit range (the base the PR/push started from).
    [string]$BaseSha,

    # Upper bound of the commit range. Defaults to HEAD.
    [string]$HeadSha
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

Import-Module (Join-Path $scriptDir 'LiveCoreContributorPolicy.psm1') -Force

function Test-GitObject {
    param([string]$Ref)
    if ([string]::IsNullOrWhiteSpace($Ref)) { return $false }
    # The all-zero SHA is git's "no such ref" sentinel (e.g. github.event.before on
    # a freshly pushed branch); treat it as absent.
    if ($Ref -match '^0+$') { return $false }
    & git -C $RepoRoot cat-file -e "$Ref^{commit}" 2>$null
    return ($LASTEXITCODE -eq 0)
}

if ([string]::IsNullOrWhiteSpace($HeadSha)) { $HeadSha = 'HEAD' }
if (-not (Test-GitObject -Ref $HeadSha)) {
    Write-Error "DCO sign-off lint: head commit '$HeadSha' is not a commit in $RepoRoot." -ErrorAction Continue
    exit 2
}

# Resolve the commit list. A usable base gives the BaseSha..HeadSha range; an
# absent/unknown base falls back to the single head commit (fail-closed).
if (Test-GitObject -Ref $BaseSha) {
    $range = "$BaseSha..$HeadSha"
    $shas = @(& git -C $RepoRoot rev-list $range)
}
else {
    if ($BaseSha) {
        Write-Host "DCO sign-off lint: base '$BaseSha' is not available; validating only the head commit."
    }
    $shas = @(& git -C $RepoRoot rev-list -n 1 $HeadSha)
}
if ($LASTEXITCODE -ne 0) {
    Write-Error "DCO sign-off lint: 'git rev-list' failed in $RepoRoot." -ErrorAction Continue
    exit 2
}

$shas = @($shas | Where-Object { $_ -and $_.Trim() -ne '' } | ForEach-Object { $_.Trim() })

$commits = New-Object System.Collections.Generic.List[object]
foreach ($sha in $shas) {
    $authorEmail = (& git -C $RepoRoot show -s --format='%ae' $sha).Trim()
    $message = & git -C $RepoRoot show -s --format='%B' $sha | Out-String
    $parents = (& git -C $RepoRoot show -s --format='%P' $sha).Trim()
    $isMerge = (@($parents -split '\s+' | Where-Object { $_ -ne '' }).Count -gt 1)
    $commits.Add([pscustomobject]@{
            Sha         = $sha
            AuthorEmail = $authorEmail
            Message     = $message
            IsMerge     = $isMerge
        })
}

$report = Get-LiveCoreSignOffReport -Commits $commits.ToArray()

Write-Host "DCO sign-off lint: $($report.Checked) non-merge commit(s) checked."

if ($report.IsClean) {
    Write-Host 'DCO sign-off lint passed: every checked commit carries a matching Signed-off-by trailer.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host "DCO sign-off lint FAILED: $($report.Unsigned.Count) commit(s) are missing a matching sign-off." -ForegroundColor Red
foreach ($commit in $report.Unsigned) {
    $subject = ($commit.Message -split "`r`n|`n|`r")[0]
    Write-Host ("  UNSIGNED: {0} ({1}) {2}" -f $commit.Sha.Substring(0, [Math]::Min(9, $commit.Sha.Length)), $commit.AuthorEmail, $subject)
}
Write-Host ''
Write-Host 'Every contributed commit must certify the Developer Certificate of Origin with a'
Write-Host 'Signed-off-by trailer matching the author. Add it by committing with:'
Write-Host '  git commit -s'
Write-Host 'and amend existing commits with: git rebase --signoff <base>. See CONTRIBUTING.md.'
exit 1
