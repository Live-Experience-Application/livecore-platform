#requires -Version 5.1

<#
.SYNOPSIS
    Enforces the supply-chain publish gate (CORE-DEP-003): validates that an
    image's CVE scan report carries no blocking vulnerability and that its SBOM
    is a usable bill of materials.

.DESCRIPTION
    Reads a Trivy JSON scan report (and, when given, a CycloneDX/SPDX SBOM),
    prints a severity summary and the blocking findings, then exits non-zero
    when the gate fails - so the CI publish job refuses to push an image with a
    critical vulnerability (the failing severities are configurable). It is
    fail-closed: a missing or malformed report blocks the publish rather than
    passing silently.

    Pass -ReportOnly to print the same summary without ever failing - the
    publish dry-run uses this so a transient base-image CVE never blocks an
    ordinary pull request, while the release publish runs the gate for real.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/assert-image-scan.ps1 -ReportPath api.scan.json -SbomPath api.sbom.cdx.json

.EXAMPLE
    pwsh -File scripts/assert-image-scan.ps1 -ReportPath api.scan.json -ReportOnly
#>

[CmdletBinding()]
param(
    # Trivy JSON scan report for the image under test.
    [Parameter(Mandatory = $true)]
    [string]$ReportPath,

    # CycloneDX or SPDX SBOM for the same image. When given, the gate also fails
    # if it is not a usable bill of materials.
    [string]$SbomPath,

    # Severities that block the publish (case-insensitive). Default: CRITICAL.
    [string[]]$FailOnSeverity = @('CRITICAL'),

    # Print the verdict without ever failing (used by the publish dry-run).
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreImageScan.psm1') -Force

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

# Parse the scan report. Fail-closed: a missing or malformed report must block
# the publish, never wave it through.
try {
    $reportModel = Get-LiveCoreScanReportModel -Path $ReportPath
}
catch {
    Write-LiveCoreGateOutcome -Failed $true -Message "could not read the scan report '$ReportPath': $($_.Exception.Message)" -IsReportOnly $ReportOnly
}

$gate = Test-LiveCoreScanGate -Model $reportModel -FailOnSeverity $FailOnSeverity

Write-Host "Scan report: $ReportPath" -ForegroundColor Cyan
if ($reportModel.ArtifactName) { Write-Host "  artifact: $($reportModel.ArtifactName)" }
Write-Host "  blocking severities: $($FailOnSeverity -join ', ')"
foreach ($severity in $gate.Counts.Keys) {
    Write-Host ("  {0,-8} {1}" -f $severity, $gate.Counts[$severity])
}

if ($gate.Blocking.Count -gt 0) {
    Write-Host 'Blocking vulnerabilities:' -ForegroundColor Red
    foreach ($finding in $gate.Blocking) {
        Write-Host ("  [{0}] {1} in {2} ({3})" -f $finding.Severity, $finding.VulnerabilityId, $finding.Package, $finding.Target)
    }
}

# Validate the SBOM when one is supplied.
$sbomFailed = $false
$sbomMessage = ''
if ($SbomPath) {
    try {
        $sbomModel = Get-LiveCoreSbomModel -Path $SbomPath
        $sbom = Test-LiveCoreSbom -Model $sbomModel
        Write-Host "SBOM: $SbomPath" -ForegroundColor Cyan
        Write-Host "  format: $($sbomModel.Format); components: $($sbomModel.ComponentCount)"
        if (-not $sbom.IsValid) {
            $sbomFailed = $true
            $sbomMessage = ($sbom.Findings -join ' ')
            foreach ($message in $sbom.Findings) { Write-Host "  $message" -ForegroundColor Red }
        }
    }
    catch {
        $sbomFailed = $true
        $sbomMessage = "could not read the SBOM '$SbomPath': $($_.Exception.Message)"
        Write-Host "  $sbomMessage" -ForegroundColor Red
    }
}

$reasons = New-Object System.Collections.Generic.List[string]
if (-not $gate.Passed) { $reasons.Add("$($gate.Blocking.Count) blocking vulnerability(ies)") }
if ($sbomFailed) { $reasons.Add("invalid SBOM ($sbomMessage)") }

if ($reasons.Count -gt 0) {
    Write-LiveCoreGateOutcome -Failed $true -Message ($reasons -join '; ') -IsReportOnly $ReportOnly
}

$summary = "no blocking vulnerabilities"
if ($SbomPath) { $summary += " and a usable SBOM" }
Write-LiveCoreGateOutcome -Failed $false -Message $summary -IsReportOnly $ReportOnly
