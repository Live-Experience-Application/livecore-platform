#requires -Version 5.1

<#
.SYNOPSIS
    Enforces the distribution license-compliance gate (CORE-LIC-003): scans an
    image's SBOM dependency closure and fails on a disallowed or unknown license.

.DESCRIPTION
    Reads a CycloneDX or SPDX SBOM (the same SBOM the CORE-DEP-003 supply-chain
    step already produces with Trivy - reuse it, no extra scan), classifies every
    component's license against the policy, prints a verdict summary and the
    offending components, then exits non-zero when the gate fails. It is
    fail-closed: a missing or malformed SBOM blocks, and any license that is not on
    the allow-list - including an absent or NOASSERTION license - is treated as
    unknown and blocks. A license on the deny-list always blocks.

    The allow-list/deny-list are configurable. The defaults
    (scripts/LiveCoreLicenseCompliance.psm1) allow the permissive and
    AGPL-compatible licenses common in the .NET/Debian closure.

    Pass -ReportOnly to print the same verdict without ever failing. The license
    gate starts in this report-only posture (like the coverage gate, docs/17) so a
    first real SBOM documents any license the allow-list does not yet cover without
    blocking development; drop -ReportOnly to make it blocking once the allow-list
    is validated against the published closure.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/assert-license-compliance.ps1 -SbomPath api.sbom.cdx.json

.EXAMPLE
    pwsh -File scripts/assert-license-compliance.ps1 -SbomPath api.sbom.cdx.json -ReportOnly
#>

[CmdletBinding()]
param(
    # CycloneDX or SPDX SBOM for the image/artifact under test.
    [Parameter(Mandatory = $true)]
    [string]$SbomPath,

    # SPDX identifiers permitted in the distribution. Defaults to the module's
    # AGPL-compatible allow-list.
    [string[]]$AllowLicense,

    # SPDX identifiers explicitly forbidden (a deny always wins). Defaults to empty.
    [string[]]$DenyLicense = @(),

    # Print the verdict without ever failing (the initial, non-blocking posture).
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
Import-Module (Join-Path $scriptDir 'LiveCoreLicenseCompliance.psm1') -Force

if (-not $PSBoundParameters.ContainsKey('AllowLicense') -or $null -eq $AllowLicense) {
    $AllowLicense = Get-LiveCoreDefaultAllowedLicenses
}

function Write-LiveCoreLicenseOutcome {
    param([bool]$Failed, [string]$Message, [bool]$IsReportOnly)
    if (-not $Failed) {
        Write-Host "LICENSE GATE PASSED: $Message" -ForegroundColor Green
        exit 0
    }
    if ($IsReportOnly) {
        Write-Host "LICENSE GATE WOULD FAIL (report-only, not blocking): $Message" -ForegroundColor Yellow
        exit 0
    }
    Write-Host "LICENSE GATE FAILED: $Message" -ForegroundColor Red
    exit 1
}

# Fail-closed: a missing or malformed SBOM blocks the publish.
try {
    $model = Get-LiveCoreSbomLicenseModel -Path $SbomPath
}
catch {
    Write-LiveCoreLicenseOutcome -Failed $true -Message "could not read the SBOM '$SbomPath': $($_.Exception.Message)" -IsReportOnly $ReportOnly
}

if ($model.Format -eq 'Unknown') {
    Write-LiveCoreLicenseOutcome -Failed $true -Message "the SBOM '$SbomPath' is not a recognized CycloneDX or SPDX document" -IsReportOnly $ReportOnly
}

$gate = Test-LiveCoreLicenseGate -Model $model -AllowLicense $AllowLicense -DenyLicense $DenyLicense

Write-Host "SBOM: $SbomPath" -ForegroundColor Cyan
Write-Host "  format: $($model.Format); components: $(@($model.Components).Count)"
foreach ($verdict in $gate.Counts.Keys) {
    Write-Host ("  {0,-8} {1}" -f $verdict, $gate.Counts[$verdict])
}

if ($gate.Violations.Count -gt 0) {
    Write-Host 'License policy violations:' -ForegroundColor Red
    foreach ($violation in $gate.Violations) {
        Write-Host ("  [{0}] {1} {2} -> {3}" -f $violation.Reason, $violation.Component, $violation.Version, $violation.License)
    }
    $denied = @($gate.Violations | Where-Object { $_.Reason -eq 'deny' }).Count
    $unknown = @($gate.Violations | Where-Object { $_.Reason -eq 'unknown' }).Count
    Write-LiveCoreLicenseOutcome -Failed $true -Message "$denied disallowed and $unknown unknown license(s)" -IsReportOnly $ReportOnly
}

Write-LiveCoreLicenseOutcome -Failed $false -Message "all $(@($model.Components).Count) component license(s) are allowed" -IsReportOnly $ReportOnly
