#requires -Version 5.1

<#
.SYNOPSIS
    Enforces the code-coverage gate (CORE-TST-001): merges the Cobertura coverage
    reports produced by the test suite and fails when production line coverage is
    below the configured minimum.

.DESCRIPTION
    Collects one or more coverage.cobertura.xml reports (an explicit -ReportPath
    list and/or every *.cobertura.xml found under -ReportDirectory), merges them
    into a single de-duplicated, production-focused number (test assemblies and
    generated EF migrations excluded; see LiveCoreCoverage.psm1), prints a
    per-assembly and overall summary, then exits non-zero when the merged line
    coverage is below -MinimumLineCoverage.

    It is fail-closed: no reports found, or a missing/malformed report, blocks
    rather than passing silently.

    Pass -ReportOnly to print the same verdict without ever failing. The CI
    coverage job ran this way first so the gate started NON-BLOCKING (reporting
    and warning on a regression); it is now BLOCKING (CORE-TST-009) - the CI job
    dropped -ReportOnly and pins -MinimumLineCoverage to the enforced floor, so a
    regression below it fails the build. -ReportOnly remains for a local
    report-only run. This followed the same staged report-only -> blocking posture
    as the supply-chain dry-run (assert-image-scan.ps1).

    It ALSO prints a production branch-coverage figure alongside the line number
    (CORE-TST-012), so an untested branch on an otherwise line-covered line is
    visible (a covered multi-condition `if` the line number alone hides). The
    branch figure is REPORT-ONLY: it never fails the build. Only the LINE gate
    blocks - it is unchanged and still fail-closed. This is the same staged
    report-only -> blocking posture the line gate and the image scan followed; a
    follow-up can ratchet a branch floor once a baseline is set.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/assert-coverage.ps1 -ReportDirectory ./TestResults -MinimumLineCoverage 70 -ReportOnly

.EXAMPLE
    pwsh -File scripts/assert-coverage.ps1 -ReportPath ./TestResults/abc/coverage.cobertura.xml -MinimumLineCoverage 70
#>

[CmdletBinding()]
param(
    # Explicit Cobertura report file(s) to include.
    [string[]]$ReportPath,

    # Directory searched recursively for *.cobertura.xml reports.
    [string]$ReportDirectory,

    # Minimum required production line-coverage percent (0..100). Default 70.
    [double]$MinimumLineCoverage = 70,

    # Assemblies whose name matches this regex are excluded (the test projects).
    [string]$ExcludeAssemblyPattern = 'Tests$',

    # Files whose path matches this regex are excluded (generated EF migrations).
    [string]$ExcludeFilePattern = '(^|/)Migrations/',

    # Print the verdict without ever failing (the initial non-blocking posture).
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreCoverage.psm1') -Force

function Write-LiveCoreGateOutcome {
    param([bool]$Failed, [string]$Message, [bool]$IsReportOnly)
    if (-not $Failed) {
        Write-Host "COVERAGE GATE PASSED: $Message" -ForegroundColor Green
        exit 0
    }
    if ($IsReportOnly) {
        Write-Host "COVERAGE GATE WOULD FAIL (report-only, not blocking): $Message" -ForegroundColor Yellow
        exit 0
    }
    Write-Host "COVERAGE GATE FAILED: $Message" -ForegroundColor Red
    exit 1
}

# Gather the report files from the explicit list and the search directory.
$reports = New-Object System.Collections.Generic.List[string]
foreach ($singlePath in @($ReportPath)) {
    if ($singlePath) { $reports.Add($singlePath) }
}
if ($ReportDirectory) {
    if (-not (Test-Path -LiteralPath $ReportDirectory)) {
        Write-LiveCoreGateOutcome -Failed $true -Message "report directory not found: '$ReportDirectory'" -IsReportOnly $ReportOnly
    }
    Get-ChildItem -Path $ReportDirectory -Recurse -File -Filter '*.cobertura.xml' |
        ForEach-Object { $reports.Add($_.FullName) }
}

if ($reports.Count -eq 0) {
    Write-LiveCoreGateOutcome -Failed $true -Message 'no coverage reports found (fail-closed). Did the test run collect coverage?' -IsReportOnly $ReportOnly
}

Write-Host "Coverage reports ($($reports.Count)):" -ForegroundColor Cyan
foreach ($report in $reports) { Write-Host "  $report" }

# Build the merged model. Fail-closed: a missing or malformed report blocks.
try {
    $model = Get-LiveCoreCoverageModel -Path $reports.ToArray() `
        -ExcludeAssemblyPattern $ExcludeAssemblyPattern -ExcludeFilePattern $ExcludeFilePattern
}
catch {
    Write-LiveCoreGateOutcome -Failed $true -Message "could not read a coverage report: $($_.Exception.Message)" -IsReportOnly $ReportOnly
}

Write-Host "Measured assemblies: $((@($model.Assemblies) | Sort-Object) -join ', ')"
Write-Host ("Production line coverage: {0}% ({1}/{2} lines), minimum {3}%" -f `
        $model.LineCoveragePercent, $model.LinesCovered, $model.LinesValid, $MinimumLineCoverage)

# Branch coverage is surfaced alongside the line number but is NOT gated yet
# (CORE-TST-012): it makes an untested branch on an otherwise line-covered line
# visible. Report-only first - the same staged report-only -> blocking posture the
# line gate (CORE-TST-009) and the supply-chain image scan followed; a follow-up
# can ratchet a branch floor once a baseline is set. The LINE gate below stays
# blocking and is the only thing that can fail this script.
if ($model.BranchesMeasured) {
    Write-Host ("Production branch coverage (report-only, not gated): {0}% ({1}/{2} branches)" -f `
            $model.BranchCoveragePercent, $model.BranchesCovered, $model.BranchesValid) -ForegroundColor Cyan
}
else {
    Write-Host 'Production branch coverage (report-only, not gated): not measured (no branch data in the reports)' -ForegroundColor Cyan
}

$gate = Test-LiveCoreCoverageGate -Model $model -MinimumLineCoveragePercent $MinimumLineCoverage

if (-not $gate.Measured) {
    Write-LiveCoreGateOutcome -Failed $true -Message 'no production lines were measured (fail-closed). Check the assembly/file exclusions and that coverage was collected.' -IsReportOnly $ReportOnly
}

if (-not $gate.Passed) {
    Write-LiveCoreGateOutcome -Failed $true -Message ("line coverage {0}% is below the {1}% minimum (short by {2} points)" -f `
            $gate.ActualPercent, $gate.MinimumPercent, $gate.Shortfall) -IsReportOnly $ReportOnly
}

Write-LiveCoreGateOutcome -Failed $false -Message ("line coverage {0}% meets the {1}% minimum" -f `
        $gate.ActualPercent, $gate.MinimumPercent) -IsReportOnly $ReportOnly
