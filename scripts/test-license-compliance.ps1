#requires -Version 5.1

<#
.SYNOPSIS
    Tests the distribution license-compliance gate (CORE-LIC-003): a seeded
    disallowed license fails the gate, an unknown/absent license fails, a clean
    SBOM passes, the allow/deny policy is configurable, and the gate is fail-closed.

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreLicenseCompliance.psm1 and
    assert-license-compliance.ps1 - no external test framework and no Docker/
    registry, so it runs as a CI gate and locally on both pwsh and Windows
    PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

    This is the required "the license gate fails on a seeded disallowed license"
    test: the seeded SBOMs are scanned by the same gate the publish path runs over
    the real CycloneDX SBOM, and the fixtures cover a denied license, an unknown
    license, an absent license and an SPDX expression. The fixtures are
    product-neutral on purpose: only generic package and license names.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-license-compliance.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
Import-Module (Join-Path $scriptDir 'LiveCoreLicenseCompliance.psm1') -Force
$assertScript = Join-Path $scriptDir 'assert-license-compliance.ps1'

$failures = New-Object System.Collections.Generic.List[string]

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because") }
}

function AssertEqual {
    param([string]$Expected, [string]$Actual, [string]$Because)
    if ($Expected -ceq $Actual) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because`n      expected: '$Expected'`n      actual:   '$Actual'") }
}

function AssertThrows {
    param([scriptblock]$Action, [string]$Because)
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    if ($threw) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because (expected a fail-closed error, but it succeeded)") }
}

# --- Seeded CycloneDX/SPDX SBOM fixtures. Generic component names only. ---

$cleanCycloneDx = @'
{
  "bomFormat": "CycloneDX",
  "specVersion": "1.5",
  "serialNumber": "urn:uuid:00000000-0000-0000-0000-000000000010",
  "components": [
    { "type": "library", "name": "alpha", "version": "1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] },
    { "type": "library", "name": "beta", "version": "2.1.0", "licenses": [ { "license": { "id": "Apache-2.0" } } ] },
    { "type": "library", "name": "gamma", "version": "3.0.0", "licenses": [ { "license": { "id": "PostgreSQL" } } ] },
    { "type": "library", "name": "delta", "version": "0.9.0", "licenses": [ { "expression": "MIT OR Apache-2.0" } ] }
  ]
}
'@

$disallowedCycloneDx = @'
{
  "bomFormat": "CycloneDX",
  "specVersion": "1.5",
  "components": [
    { "type": "library", "name": "alpha", "version": "1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] },
    { "type": "library", "name": "evil", "version": "6.6.6", "licenses": [ { "license": { "id": "BUSL-1.1" } } ] }
  ]
}
'@

$unknownCycloneDx = @'
{
  "bomFormat": "CycloneDX",
  "specVersion": "1.5",
  "components": [
    { "type": "library", "name": "alpha", "version": "1.0.0", "licenses": [ { "license": { "id": "MIT" } } ] },
    { "type": "library", "name": "mystery", "version": "1.2.3", "licenses": [ { "license": { "name": "Custom Closed License" } } ] }
  ]
}
'@

$noLicenseCycloneDx = @'
{
  "bomFormat": "CycloneDX",
  "specVersion": "1.5",
  "components": [
    { "type": "library", "name": "nameless", "version": "1.0.0" }
  ]
}
'@

$cleanSpdx = @'
{
  "spdxVersion": "SPDX-2.3",
  "name": "fixture",
  "packages": [
    { "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "MIT" },
    { "name": "beta", "versionInfo": "2.0.0", "licenseConcluded": "Apache-2.0" }
  ]
}
'@

$deniedSpdx = @'
{
  "spdxVersion": "SPDX-2.3",
  "name": "fixture",
  "packages": [
    { "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "MIT" }
  ]
}
'@

# --- Expression/token helpers behave per operand. ---
AssertTrue ((Get-LiveCoreLicenseTokens -Expression 'MIT OR Apache-2.0').Count -eq 2) 'an OR expression splits into two tokens'
AssertTrue ((Get-LiveCoreLicenseTokens -Expression '(MIT AND BSD-3-Clause)').Count -eq 2) 'an AND expression with parens splits into two tokens'
$allowSet = @{}; foreach ($l in (Get-LiveCoreDefaultAllowedLicenses)) { $allowSet[$l.ToUpperInvariant()] = $true }
$emptyDeny = @{}
AssertEqual 'allow' (Test-LiveCoreLicenseExpression -Expression 'MIT OR Closed-Source' -AllowSet $allowSet -DenySet $emptyDeny) 'OR is allowed when any operand is allowed'
AssertEqual 'unknown' (Test-LiveCoreLicenseExpression -Expression 'MIT AND Closed-Source' -AllowSet $allowSet -DenySet $emptyDeny) 'AND is unknown when one operand is not allowed'
AssertEqual 'allow' (Test-LiveCoreLicenseExpression -Expression 'apache-2.0' -AllowSet $allowSet -DenySet $emptyDeny) 'matching is case-insensitive'

# --- Gate: a clean SBOM passes. ---
$cleanGate = Test-LiveCoreLicenseGate -Model (Get-LiveCoreSbomLicenseModel -Content $cleanCycloneDx)
AssertTrue ($cleanGate.Passed) 'a CycloneDX SBOM with only allowed licenses passes the gate'
AssertTrue ($cleanGate.Violations.Count -eq 0) 'a clean SBOM has no license violations'
AssertTrue ($cleanGate.Counts['allow'] -eq 4) 'all four clean components are allowed (incl. the OR expression)'

AssertTrue ((Test-LiveCoreLicenseGate -Model (Get-LiveCoreSbomLicenseModel -Content $cleanSpdx)).Passed) 'a clean SPDX SBOM passes the gate'

# --- Gate: a seeded disallowed (not-allow-listed) license fails. ---
$disallowedGate = Test-LiveCoreLicenseGate -Model (Get-LiveCoreSbomLicenseModel -Content $disallowedCycloneDx)
AssertTrue (-not $disallowedGate.Passed) 'a seeded disallowed license (BUSL-1.1) fails the gate'
AssertTrue ($disallowedGate.Violations.Count -eq 1) 'exactly the one disallowed component is flagged'
AssertEqual 'evil' $disallowedGate.Violations[0].Component 'the flagged component is the one carrying the disallowed license'

# --- Gate: an unknown (unrecognized name) license fails. ---
$unknownGate = Test-LiveCoreLicenseGate -Model (Get-LiveCoreSbomLicenseModel -Content $unknownCycloneDx)
AssertTrue (-not $unknownGate.Passed) 'an unrecognized license name fails the gate (unknown)'
AssertEqual 'unknown' $unknownGate.Violations[0].Reason 'the unrecognized license is classified unknown'

# --- Gate: an absent license fails (fail-closed). ---
$noLicenseGate = Test-LiveCoreLicenseGate -Model (Get-LiveCoreSbomLicenseModel -Content $noLicenseCycloneDx)
AssertTrue (-not $noLicenseGate.Passed) 'a component with no license at all fails the gate (fail-closed)'

# --- Gate: the deny-list always wins, even over an otherwise-allowed license. ---
$denyWinsGate = Test-LiveCoreLicenseGate -Model (Get-LiveCoreSbomLicenseModel -Content $cleanCycloneDx) -DenyLicense @('MIT')
AssertTrue (-not $denyWinsGate.Passed) 'denying MIT blocks an otherwise-clean SBOM (deny always wins)'
AssertTrue (@($denyWinsGate.Violations | Where-Object { $_.Reason -eq 'deny' }).Count -ge 1) 'the MIT component is blocked with reason deny'

# The deny-list also applies to an SPDX SBOM.
$deniedSpdxGate = Test-LiveCoreLicenseGate -Model (Get-LiveCoreSbomLicenseModel -Content $deniedSpdx) -DenyLicense @('MIT')
AssertTrue (-not $deniedSpdxGate.Passed) 'denying MIT blocks an SPDX SBOM that declares MIT (deny applies across formats)'

# --- Gate: the allow-list is configurable (a stricter list rejects Apache-2.0). ---
$strictGate = Test-LiveCoreLicenseGate -Model (Get-LiveCoreSbomLicenseModel -Content $cleanCycloneDx) -AllowLicense @('MIT', 'PostgreSQL')
AssertTrue (-not $strictGate.Passed) 'narrowing the allow-list to drop Apache-2.0 fails the same SBOM'

# --- Fail-closed: a malformed SBOM is rejected, never waved through. ---
AssertThrows { Get-LiveCoreSbomLicenseModel -Content 'not json {' } 'a malformed SBOM is rejected (fail-closed)'
$unknownDoc = Get-LiveCoreSbomLicenseModel -Content '{ "note": "not an sbom" }'
AssertEqual 'Unknown' $unknownDoc.Format 'an unrecognized document is not a recognized SBOM format'

# --- End to end: the assert-license-compliance.ps1 CLI enforces the gate. ---
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-license-" + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $tempDir
$psExe = (Get-Process -Id $PID).Path
try {
    $cleanPath = Join-Path $tempDir 'clean.sbom.cdx.json'
    $disallowedPath = Join-Path $tempDir 'disallowed.sbom.cdx.json'
    [System.IO.File]::WriteAllText($cleanPath, $cleanCycloneDx)
    [System.IO.File]::WriteAllText($disallowedPath, $disallowedCycloneDx)

    & $psExe -NoProfile -File $assertScript -SbomPath $disallowedPath *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'the CLI exits non-zero (blocks) on a seeded disallowed license'

    & $psExe -NoProfile -File $assertScript -SbomPath $cleanPath *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'the CLI exits zero on a clean SBOM'

    & $psExe -NoProfile -File $assertScript -SbomPath $disallowedPath -ReportOnly *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'report-only mode never blocks, even on a disallowed license (the initial posture)'

    & $psExe -NoProfile -File $assertScript -SbomPath (Join-Path $tempDir 'does-not-exist.json') *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'a missing SBOM blocks the gate (fail-closed)'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "License-compliance gate tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host $failure }
    exit 1
}

Write-Host ''
Write-Host 'License-compliance gate tests passed: a seeded disallowed license blocks, unknown/absent licenses block, the policy is configurable and the gate is fail-closed.' -ForegroundColor Green
exit 0
