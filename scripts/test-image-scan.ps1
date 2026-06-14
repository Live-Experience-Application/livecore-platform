#requires -Version 5.1

<#
.SYNOPSIS
    Tests the supply-chain publish gate (CORE-DEP-003): a seeded critical CVE
    fails the gate, a clean report passes, the failing severities are
    configurable, and only a usable SBOM is accepted.

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreImageScan.psm1 and
    assert-image-scan.ps1 - no external test framework and no Docker/registry, so
    it runs as a CI gate and locally on both pwsh and Windows PowerShell 5.1.
    Exits 0 when every assertion holds and 1 when any fails.

    This is the test for the two CORE-DEP-003 required cases: the publish path
    "produces an SBOM and a scan report" (the SBOM/scan-report parsers and the
    gate are exercised against seeded reports) and "a seeded critical CVE fails
    the gate" (the seeded-critical report blocks the publish here and through the
    assert-image-scan.ps1 CLI).

.EXAMPLE
    pwsh -NoProfile -File scripts/test-image-scan.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreImageScan.psm1') -Force
$assertScript = Join-Path $scriptDir 'assert-image-scan.ps1'

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

# --- Seeded fixtures (Trivy JSON scan reports and CycloneDX/SPDX SBOMs). The
#     content is product-neutral on purpose: only generic OS package names. ---

$criticalReport = @'
{
  "SchemaVersion": 2,
  "ArtifactName": "ghcr.io/example/livecore-api:0.0.0-test",
  "Results": [
    {
      "Target": "ghcr.io/example/livecore-api:0.0.0-test (debian 12)",
      "Class": "os-pkgs",
      "Type": "debian",
      "Vulnerabilities": [
        {
          "VulnerabilityID": "CVE-2099-0001",
          "PkgName": "openssl",
          "InstalledVersion": "3.0.0",
          "FixedVersion": "3.0.1",
          "Severity": "CRITICAL",
          "Title": "Seeded critical buffer overflow used to prove the gate blocks"
        },
        {
          "VulnerabilityID": "CVE-2099-0002",
          "PkgName": "zlib",
          "InstalledVersion": "1.2.11",
          "Severity": "HIGH",
          "Title": "Seeded high-severity out-of-bounds read"
        }
      ]
    },
    {
      "Target": "app/LiveCore.Api.dll",
      "Class": "lang-pkgs",
      "Type": "dotnet-core"
    }
  ]
}
'@

$cleanReport = @'
{
  "SchemaVersion": 2,
  "ArtifactName": "ghcr.io/example/livecore-api:0.0.0-clean",
  "Results": [
    {
      "Target": "ghcr.io/example/livecore-api:0.0.0-clean (debian 12)",
      "Class": "os-pkgs",
      "Type": "debian",
      "Vulnerabilities": []
    }
  ]
}
'@

$highOnlyReport = @'
{
  "SchemaVersion": 2,
  "ArtifactName": "ghcr.io/example/livecore-api:0.0.0-high",
  "Results": [
    {
      "Target": "ghcr.io/example/livecore-api:0.0.0-high (debian 12)",
      "Class": "os-pkgs",
      "Type": "debian",
      "Vulnerabilities": [
        { "VulnerabilityID": "CVE-2099-1001", "PkgName": "libxml2", "Severity": "HIGH" },
        { "VulnerabilityID": "CVE-2099-1002", "PkgName": "glibc", "Severity": "MEDIUM" }
      ]
    }
  ]
}
'@

$validCycloneDx = @'
{
  "bomFormat": "CycloneDX",
  "specVersion": "1.5",
  "serialNumber": "urn:uuid:00000000-0000-0000-0000-000000000001",
  "version": 1,
  "components": [
    { "type": "library", "name": "openssl", "version": "3.0.1" },
    { "type": "application", "name": "LiveCore.Api", "version": "0.0.0" }
  ]
}
'@

$emptyCycloneDx = @'
{ "bomFormat": "CycloneDX", "specVersion": "1.5", "components": [] }
'@

$validSpdx = @'
{
  "spdxVersion": "SPDX-2.3",
  "name": "livecore-api",
  "packages": [ { "name": "openssl", "versionInfo": "3.0.1" } ]
}
'@

$unknownDocument = @'
{ "note": "this is not an SBOM" }
'@

# --- Gate: a seeded critical CVE fails the gate. ---
$criticalModel = Get-LiveCoreScanReportModel -Content $criticalReport
$criticalGate = Test-LiveCoreScanGate -Model $criticalModel
AssertTrue (-not $criticalGate.Passed) 'a seeded CRITICAL CVE fails the publish gate'
AssertTrue ($criticalGate.Blocking.Count -eq 1) 'exactly the one CRITICAL finding blocks (the HIGH does not, under the default policy)'
AssertEqual 'CVE-2099-0001' $criticalGate.Blocking[0].VulnerabilityId 'the blocking finding is the seeded critical vulnerability'
AssertTrue ($criticalGate.Counts['CRITICAL'] -eq 1) 'the report counts one CRITICAL'
AssertTrue ($criticalGate.Counts['HIGH'] -eq 1) 'the report counts one HIGH'

# --- Gate: a clean report passes. ---
$cleanGate = Test-LiveCoreScanGate -Model (Get-LiveCoreScanReportModel -Content $cleanReport)
AssertTrue ($cleanGate.Passed) 'a report with no vulnerabilities passes the gate'
AssertTrue ($cleanGate.Blocking.Count -eq 0) 'a clean report has no blocking findings'

# --- Gate: the failing severities are configurable. ---
$highModel = Get-LiveCoreScanReportModel -Content $highOnlyReport
AssertTrue ((Test-LiveCoreScanGate -Model $highModel).Passed) 'a HIGH/MEDIUM-only report passes the default CRITICAL-only gate'
$strictGate = Test-LiveCoreScanGate -Model $highModel -FailOnSeverity @('CRITICAL', 'HIGH')
AssertTrue (-not $strictGate.Passed) 'widening the gate to HIGH blocks the same HIGH/MEDIUM report'
AssertTrue ($strictGate.Blocking.Count -eq 1) 'only the HIGH finding blocks when failing on CRITICAL+HIGH'

# --- Fail-closed: a malformed report is rejected, never waved through. ---
AssertThrows { Get-LiveCoreScanReportModel -Content 'not json {' } 'a malformed scan report is rejected (fail-closed)'

# --- SBOM: only a usable bill of materials is accepted. ---
$cdxModel = Get-LiveCoreSbomModel -Content $validCycloneDx
AssertEqual 'CycloneDX' $cdxModel.Format 'a CycloneDX SBOM is recognized'
AssertTrue ($cdxModel.ComponentCount -eq 2) 'the CycloneDX SBOM records two components'
AssertTrue ((Test-LiveCoreSbom -Model $cdxModel).IsValid) 'a CycloneDX SBOM with components is valid'

$spdxModel = Get-LiveCoreSbomModel -Content $validSpdx
AssertEqual 'SPDX' $spdxModel.Format 'an SPDX SBOM is recognized'
AssertTrue ((Test-LiveCoreSbom -Model $spdxModel).IsValid) 'an SPDX SBOM with packages is valid'

AssertTrue (-not (Test-LiveCoreSbom -Model (Get-LiveCoreSbomModel -Content $emptyCycloneDx)).IsValid) 'an empty SBOM (no components) is rejected'
$unknownModel = Get-LiveCoreSbomModel -Content $unknownDocument
AssertEqual 'Unknown' $unknownModel.Format 'an unrecognized document is not an SBOM format'
AssertTrue (-not (Test-LiveCoreSbom -Model $unknownModel).IsValid) 'an unrecognized document is rejected as an SBOM'

# --- End to end: the assert-image-scan.ps1 CLI enforces the gate. ---
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-scan-" + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $tempDir
$psExe = (Get-Process -Id $PID).Path
try {
    $criticalReportPath = Join-Path $tempDir 'critical.scan.json'
    $cleanReportPath = Join-Path $tempDir 'clean.scan.json'
    $sbomPath = Join-Path $tempDir 'sbom.cdx.json'
    $emptySbomPath = Join-Path $tempDir 'empty.sbom.json'
    [System.IO.File]::WriteAllText($criticalReportPath, $criticalReport)
    [System.IO.File]::WriteAllText($cleanReportPath, $cleanReport)
    [System.IO.File]::WriteAllText($sbomPath, $validCycloneDx)
    [System.IO.File]::WriteAllText($emptySbomPath, $emptyCycloneDx)

    & $psExe -NoProfile -File $assertScript -ReportPath $criticalReportPath -SbomPath $sbomPath *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'the CLI exits non-zero (blocks the publish) on a seeded critical CVE'

    & $psExe -NoProfile -File $assertScript -ReportPath $cleanReportPath -SbomPath $sbomPath *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'the CLI exits zero on a clean report with a usable SBOM'

    & $psExe -NoProfile -File $assertScript -ReportPath $criticalReportPath -SbomPath $sbomPath -ReportOnly *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'report-only mode never blocks, even on a critical CVE (the dry-run posture)'

    & $psExe -NoProfile -File $assertScript -ReportPath (Join-Path $tempDir 'does-not-exist.json') *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'a missing scan report blocks the publish (fail-closed)'

    & $psExe -NoProfile -File $assertScript -ReportPath $cleanReportPath -SbomPath $emptySbomPath *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'a clean scan but an empty SBOM still blocks the publish'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Image scan gate tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host $failure }
    exit 1
}

Write-Host ''
Write-Host 'Image scan gate tests passed: a seeded critical CVE blocks the publish, the gate is configurable and only a usable SBOM is accepted.' -ForegroundColor Green
exit 0
