#requires -Version 5.1

<#
.SYNOPSIS
    Enforces the source dependency-vulnerability audit gate (CORE-DEP-005): fails
    the build when a `dotnet list package --vulnerable` report or a `pnpm audit`
    report carries a high/critical advisory on a first-party direct or transitive
    dependency.

.DESCRIPTION
    Reads the .NET audit report (`-DotnetReportPath`, the JSON from
    `dotnet list LiveCore.slnx package --vulnerable --include-transitive --format json`)
    and/or the pnpm workspace audit report (`-PnpmReportPath`, the JSON from
    `pnpm audit --json`), prints a per-severity summary and the blocking findings,
    then exits non-zero when the gate fails - so the CI dependency-audit job fails
    closed on a high/critical known-vulnerable dependency.

    The gate is fail-closed: a missing or malformed report blocks the build rather
    than passing silently (an audit that produced no readable output is not a
    clean audit), and at least one report must be supplied. The blocking
    severities are HIGH and CRITICAL by default (the agreed bar) and are
    configurable with -FailOnSeverity.

    Pass -ReportOnly to print the same verdict without ever failing - symmetry
    with the supply-chain image-scan, coverage and CodeQL gates; the CI job runs
    it BLOCKING (no -ReportOnly), because the story requires CI to fail on a
    known-vulnerable dependency.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/assert-dependency-audit.ps1 -DotnetReportPath dotnet-vulnerable.json -PnpmReportPath pnpm-audit.json

.EXAMPLE
    pwsh -File scripts/assert-dependency-audit.ps1 -PnpmReportPath pnpm-audit.json -FailOnSeverity CRITICAL,HIGH,MODERATE
#>

[CmdletBinding()]
param(
    # The .NET `dotnet list package --vulnerable --format json` report.
    [string]$DotnetReportPath,

    # The `pnpm audit --json` workspace report.
    [string]$PnpmReportPath,

    # Severities that block the build (case-insensitive). Default: HIGH, CRITICAL.
    [string[]]$FailOnSeverity = @('HIGH', 'CRITICAL'),

    # Print the verdict without ever failing (symmetry with the other gates).
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreDependencyAudit.psm1') -Force

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

# Fail-closed: with no report supplied there is nothing to audit, so block rather
# than wave the build through unaudited.
if (-not $DotnetReportPath -and -not $PnpmReportPath) {
    Write-LiveCoreGateOutcome -Failed $true -Message 'no audit report supplied (pass -DotnetReportPath and/or -PnpmReportPath)' -IsReportOnly $ReportOnly
}

$allFindings = New-Object System.Collections.Generic.List[psobject]
$readFailures = New-Object System.Collections.Generic.List[string]

# Parse each supplied report. Fail-closed: a missing or malformed report blocks.
if ($DotnetReportPath) {
    Write-Host ".NET audit report: $DotnetReportPath" -ForegroundColor Cyan
    try {
        $dotnetModel = Get-LiveCoreDotnetVulnerabilityModel -Path $DotnetReportPath
        foreach ($finding in @($dotnetModel.Findings)) { $allFindings.Add($finding) }
        Write-Host "  vulnerable packages: $(@($dotnetModel.Findings).Count)"
    }
    catch {
        $readFailures.Add("could not read the .NET audit report '$DotnetReportPath': $($_.Exception.Message)")
        Write-Host "  unreadable: $($_.Exception.Message)" -ForegroundColor Red
    }
}

if ($PnpmReportPath) {
    Write-Host "pnpm audit report: $PnpmReportPath" -ForegroundColor Cyan
    try {
        $pnpmModel = Get-LiveCorePnpmVulnerabilityModel -Path $PnpmReportPath
        foreach ($finding in @($pnpmModel.Findings)) { $allFindings.Add($finding) }
        Write-Host "  vulnerable packages: $(@($pnpmModel.Findings).Count)"
    }
    catch {
        $readFailures.Add("could not read the pnpm audit report '$PnpmReportPath': $($_.Exception.Message)")
        Write-Host "  unreadable: $($_.Exception.Message)" -ForegroundColor Red
    }
}

$model = [pscustomobject]@{ Findings = $allFindings.ToArray() }
$gate = Test-LiveCoreDependencyAuditGate -Model $model -FailOnSeverity $FailOnSeverity

Write-Host "Blocking severities: $($FailOnSeverity -join ', ')"
foreach ($severity in $gate.Counts.Keys) {
    if ([int]$gate.Counts[$severity] -gt 0) {
        Write-Host ("  {0,-9} {1}" -f $severity, $gate.Counts[$severity])
    }
}

if ($gate.Blocking.Count -gt 0) {
    Write-Host 'Blocking vulnerabilities:' -ForegroundColor Red
    foreach ($finding in $gate.Blocking) {
        $advisory = if ($finding.Advisory) { $finding.Advisory } else { '(no advisory url)' }
        Write-Host ("  [{0}] {1} {2} ({3} {4}) {5}" -f `
                $finding.Severity, $finding.Ecosystem, $finding.Package, $finding.Scope, $finding.Version, $advisory) -ForegroundColor Red
    }
}

$reasons = New-Object System.Collections.Generic.List[string]
if ($readFailures.Count -gt 0) { foreach ($failure in $readFailures) { $reasons.Add($failure) } }
if ($gate.Blocking.Count -gt 0) { $reasons.Add("$($gate.Blocking.Count) high/critical dependency vulnerability(ies)") }

if ($reasons.Count -gt 0) {
    Write-LiveCoreGateOutcome -Failed $true -Message ($reasons -join '; ') -IsReportOnly $ReportOnly
}

$reported = [int]$gate.Counts['MODERATE'] + [int]$gate.Counts['LOW'] + [int]$gate.Counts['INFO']
$summary = "no high/critical dependency vulnerabilities"
if ($reported -gt 0) { $summary += " ($reported moderate/low/info advisory(ies) reported, not blocking)" }
Write-LiveCoreGateOutcome -Failed $false -Message $summary -IsReportOnly $ReportOnly
