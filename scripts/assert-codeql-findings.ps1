#requires -Version 5.1

<#
.SYNOPSIS
    Enforces the static-analysis gate (CORE-SEC-006): fails the build when a
    CodeQL SARIF report carries a high/critical security finding.

.DESCRIPTION
    Reads one or more CodeQL SARIF result files (passed explicitly with
    -SarifPath, or discovered as *.sarif under -ResultsDirectory), prints a
    per-band finding summary and the blocking findings, then exits non-zero when
    the gate fails - so the CI codeql job fails closed on a high/critical
    security finding in the first-party C# or TypeScript sources.

    The gate is fail-closed: a missing/empty/malformed SARIF file, or a results
    directory that contains no SARIF at all, blocks the build rather than passing
    silently (an analysis that produced no readable output is not a clean
    analysis). The blocking floor is the high/critical band by default
    (security-severity >= 7.0) and is configurable with -MinimumSecuritySeverity.

    Pass -ReportOnly to print the same verdict without ever failing - symmetry
    with the supply-chain and coverage gates; the CI job runs it BLOCKING (no
    -ReportOnly), because the story requires the build to fail on a high/critical
    finding.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/assert-codeql-findings.ps1 -ResultsDirectory codeql-results

.EXAMPLE
    pwsh -File scripts/assert-codeql-findings.ps1 -SarifPath csharp.sarif -SarifPath javascript-typescript.sarif
#>

[CmdletBinding(DefaultParameterSetName = 'Directory')]
param(
    # A directory whose *.sarif files are all gated. Fail-closed: no SARIF found
    # under it blocks the build.
    [Parameter(Mandatory = $true, ParameterSetName = 'Directory')]
    [string]$ResultsDirectory,

    # One or more SARIF files to gate explicitly.
    [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
    [string[]]$SarifPath,

    # The security-severity floor at or above which a finding blocks the build.
    # Default 7.0 - the high/critical band (CORE-SEC-006).
    [double]$MinimumSecuritySeverity = 7.0,

    # Print the verdict without ever failing (symmetry with the other gates).
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreCodeQL.psm1') -Force

function Write-LiveCoreGateOutcome {
    param([bool]$Failed, [string]$Message, [bool]$IsReportOnly)
    if (-not $Failed) {
        Write-Host "GATE PASSED: $Message" -ForegroundColor Green
        exit 0
    }
    if ($IsReportOnly) {
        Write-Host "GATE WOULD FAIL (report-only, not blocking): $Message" -ForegroundColor Yellow
        exit 0
    }
    Write-Host "GATE FAILED: $Message" -ForegroundColor Red
    exit 1
}

# Resolve the SARIF file set. Fail-closed: an empty set (a missing directory, or
# one with no SARIF) must block, never wave the build through unanalyzed.
if ($PSCmdlet.ParameterSetName -eq 'Directory') {
    if (-not (Test-Path -LiteralPath $ResultsDirectory)) {
        Write-LiveCoreGateOutcome -Failed $true -Message "the CodeQL results directory '$ResultsDirectory' does not exist (no analysis output to gate)" -IsReportOnly $ReportOnly
    }
    $sarifFiles = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.sarif' -File -Recurse |
            Sort-Object -Property FullName |
            ForEach-Object { $_.FullName })
}
else {
    $sarifFiles = @($SarifPath)
}

if ($sarifFiles.Count -eq 0) {
    Write-LiveCoreGateOutcome -Failed $true -Message 'no CodeQL SARIF file found to gate (fail-closed)' -IsReportOnly $ReportOnly
}

$blockingTotal = 0
$totalsByBand = [ordered]@{ Critical = 0; High = 0; Medium = 0; Low = 0; None = 0 }
$readFailures = New-Object System.Collections.Generic.List[string]

foreach ($sarif in $sarifFiles) {
    Write-Host "SARIF report: $sarif" -ForegroundColor Cyan

    # Fail-closed: a missing or malformed report blocks the build.
    try {
        $model = Get-LiveCoreCodeQlSarifModel -Path $sarif
    }
    catch {
        $readFailures.Add("could not read the SARIF report '$sarif': $($_.Exception.Message)")
        Write-Host "  unreadable: $($_.Exception.Message)" -ForegroundColor Red
        continue
    }

    $gate = Test-LiveCoreCodeQlGate -Model $model -MinimumSecuritySeverity $MinimumSecuritySeverity

    if ($model.ToolName) { Write-Host "  tool: $($model.ToolName)" }
    Write-Host "  blocking floor: security-severity >= $MinimumSecuritySeverity (high/critical)"
    foreach ($band in $gate.Counts.Keys) {
        Write-Host ("  {0,-9} {1}" -f $band, $gate.Counts[$band])
        if ($totalsByBand.Contains($band)) { $totalsByBand[$band] += [int]$gate.Counts[$band] }
    }

    if ($gate.Blocking.Count -gt 0) {
        $blockingTotal += $gate.Blocking.Count
        Write-Host '  Blocking security findings:' -ForegroundColor Red
        foreach ($finding in $gate.Blocking) {
            $where = if ($finding.Location) { $finding.Location } else { '(no location)' }
            Write-Host ("    [{0} {1}] {2} at {3}" -f $finding.Band, $finding.SecuritySeverity, $finding.RuleId, $where) -ForegroundColor Red
        }
    }
}

$reasons = New-Object System.Collections.Generic.List[string]
if ($readFailures.Count -gt 0) { foreach ($failure in $readFailures) { $reasons.Add($failure) } }
if ($blockingTotal -gt 0) { $reasons.Add("$blockingTotal high/critical security finding(s)") }

if ($reasons.Count -gt 0) {
    Write-LiveCoreGateOutcome -Failed $true -Message ($reasons -join '; ') -IsReportOnly $ReportOnly
}

$summary = "no high/critical security findings across $($sarifFiles.Count) SARIF report(s)"
$mediumLow = [int]$totalsByBand['Medium'] + [int]$totalsByBand['Low']
if ($mediumLow -gt 0) { $summary += " ($mediumLow medium/low finding(s) reported, not blocking)" }
Write-LiveCoreGateOutcome -Failed $false -Message $summary -IsReportOnly $ReportOnly
