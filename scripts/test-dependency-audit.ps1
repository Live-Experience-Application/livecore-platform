#requires -Version 5.1

<#
.SYNOPSIS
    Tests the source dependency-vulnerability audit gate (CORE-DEP-005): a seeded
    high/critical vulnerable package fails the gate, a clean or moderate/low-only
    report passes, the failing severities are configurable, both ecosystems'
    reports parse, and a malformed report fails closed.

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreDependencyAudit.psm1 and
    assert-dependency-audit.ps1 - no external test framework, no network, no
    restore and no real `dotnet list`/`pnpm audit`, so it runs as a CI gate and
    locally on both pwsh and Windows PowerShell 5.1. Exits 0 when every assertion
    holds and 1 when any fails.

    This is the CORE-DEP-005 required "a seeded vulnerable-package fixture proves
    it fails closed" test: the seeded `dotnet list package --vulnerable` and
    `pnpm audit` reports carrying a high (and a critical) advisory block the build
    here and through the assert-dependency-audit.ps1 CLI, proving the gate would
    fail closed when a real audit surfaces such a finding - without committing a
    real vulnerable dependency to do so.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-dependency-audit.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreDependencyAudit.psm1') -Force
$assertScript = Join-Path $scriptDir 'assert-dependency-audit.ps1'

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

# --- Seeded fixtures. The package names are synthetic and product-neutral on
#     purpose, and the advisory ids are obviously placeholder, so nothing here
#     claims a real CVE or commits a real vulnerable dependency. ---

# A `dotnet list package --vulnerable --format json` report: a CRITICAL and a
# MODERATE top-level package and a HIGH transitive package on one project; a
# second project with no vulnerable packages (no `frameworks` block) - the clean
# project shape this tool emits.
$dotnetVulnerableReport = @'
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "sources": [ "https://api.nuget.org/v3/index.json" ],
  "projects": [
    {
      "path": "apps/api/LiveCore.Api.csproj",
      "frameworks": [
        {
          "framework": "net10.0",
          "topLevelPackages": [
            {
              "id": "Example.Serialization",
              "requestedVersion": "1.0.0",
              "resolvedVersion": "1.0.0",
              "vulnerabilities": [
                { "severity": "Critical", "advisoryurl": "https://github.com/advisories/GHSA-0000-0000-0001" }
              ]
            },
            {
              "id": "Example.Logging",
              "requestedVersion": "2.0.0",
              "resolvedVersion": "2.0.0",
              "vulnerabilities": [
                { "severity": "Moderate", "advisoryurl": "https://github.com/advisories/GHSA-0000-0000-0002" }
              ]
            }
          ],
          "transitivePackages": [
            {
              "id": "Example.Net.Transport",
              "resolvedVersion": "3.1.0",
              "vulnerabilities": [
                { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-0000-0000-0003" }
              ]
            }
          ]
        }
      ]
    },
    {
      "path": "apps/worker/LiveCore.Worker.csproj"
    }
  ]
}
'@

# A clean `dotnet list package --vulnerable` report: every project carries only
# its path because none has a vulnerable package.
$dotnetCleanReport = @'
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "projects": [
    { "path": "apps/api/LiveCore.Api.csproj" },
    { "path": "apps/worker/LiveCore.Worker.csproj" }
  ]
}
'@

# A `pnpm audit --json` report with a HIGH advisory and a MODERATE advisory.
$pnpmHighAndModerateReport = @'
{
  "actions": [],
  "advisories": {
    "1000001": {
      "module_name": "example-yaml",
      "severity": "moderate",
      "title": "Seeded moderate-severity denial of service used to prove the gate reports but does not block",
      "url": "https://github.com/advisories/GHSA-0000-0000-1001",
      "vulnerable_versions": "<=4.1.1",
      "github_advisory_id": "GHSA-0000-0000-1001",
      "findings": [ { "version": "4.1.1", "paths": ["packages__contracts>example-tool>example-yaml"] } ]
    },
    "1000002": {
      "module_name": "example-parser",
      "severity": "high",
      "title": "Seeded high-severity prototype pollution used to prove the gate blocks",
      "url": "https://github.com/advisories/GHSA-0000-0000-1002",
      "vulnerable_versions": "<1.2.6",
      "github_advisory_id": "GHSA-0000-0000-1002",
      "findings": [ { "version": "1.2.0", "paths": ["packages__sdk-ts>example-parser"] } ]
    }
  },
  "metadata": { "vulnerabilities": { "info": 0, "low": 0, "moderate": 1, "high": 1, "critical": 0 } }
}
'@

# A `pnpm audit --json` report whose only advisory is moderate - the agreed bar
# (HIGH/CRITICAL) reports but does not block it.
$pnpmModerateOnlyReport = @'
{
  "actions": [],
  "advisories": {
    "1000001": {
      "module_name": "example-yaml",
      "severity": "moderate",
      "title": "Seeded moderate-severity advisory",
      "url": "https://github.com/advisories/GHSA-0000-0000-1001",
      "vulnerable_versions": "<=4.1.1",
      "github_advisory_id": "GHSA-0000-0000-1001",
      "findings": [ { "version": "4.1.1", "paths": ["packages__contracts>example-tool>example-yaml"] } ]
    }
  },
  "metadata": { "vulnerabilities": { "info": 0, "low": 0, "moderate": 1, "high": 0, "critical": 0 } }
}
'@

# A clean `pnpm audit --json` report: no advisories.
$pnpmCleanReport = @'
{ "actions": [], "advisories": {}, "metadata": { "vulnerabilities": { "info": 0, "low": 0, "moderate": 0, "high": 0, "critical": 0 } } }
'@

# --- Gate: a seeded high/critical .NET vulnerability fails the gate. ---
$dotnetModel = Get-LiveCoreDotnetVulnerabilityModel -Content $dotnetVulnerableReport
$dotnetGate = Test-LiveCoreDependencyAuditGate -Model $dotnetModel
AssertTrue (-not $dotnetGate.Passed) 'a seeded high/critical .NET dependency vulnerability fails the audit gate'
AssertTrue ($dotnetGate.Blocking.Count -eq 2) 'the CRITICAL top-level and the HIGH transitive package block (the MODERATE one does not)'
AssertTrue ($dotnetGate.Counts['CRITICAL'] -eq 1) 'the report counts one CRITICAL'
AssertTrue ($dotnetGate.Counts['HIGH'] -eq 1) 'the report counts one HIGH'
AssertTrue ($dotnetGate.Counts['MODERATE'] -eq 1) 'the report counts one MODERATE'
$transitive = @($dotnetModel.Findings | Where-Object { $_.Scope -eq 'transitive' })
AssertTrue ($transitive.Count -eq 1) 'the transitive vulnerable package is captured (transitive included)'
AssertEqual 'Example.Net.Transport' $transitive[0].Package 'the transitive finding names the vulnerable package'
AssertEqual 'HIGH' $transitive[0].Severity 'the transitive finding severity is normalized to upper case'

# --- Gate: a clean .NET report passes. ---
$dotnetCleanGate = Test-LiveCoreDependencyAuditGate -Model (Get-LiveCoreDotnetVulnerabilityModel -Content $dotnetCleanReport)
AssertTrue ($dotnetCleanGate.Passed) 'a .NET report with no vulnerable packages passes the gate'
AssertTrue ($dotnetCleanGate.Blocking.Count -eq 0) 'a clean .NET report has no blocking findings'

# --- Gate: a seeded high pnpm advisory fails the gate; a moderate one does not. ---
$pnpmModel = Get-LiveCorePnpmVulnerabilityModel -Content $pnpmHighAndModerateReport
$pnpmGate = Test-LiveCoreDependencyAuditGate -Model $pnpmModel
AssertTrue (-not $pnpmGate.Passed) 'a seeded HIGH pnpm workspace advisory fails the audit gate'
AssertTrue ($pnpmGate.Blocking.Count -eq 1) 'only the HIGH advisory blocks under the agreed bar (the MODERATE does not)'
AssertEqual 'example-parser' $pnpmGate.Blocking[0].Package 'the blocking pnpm finding is the high-severity package'
AssertEqual 'npm' $pnpmGate.Blocking[0].Ecosystem 'the pnpm finding is tagged with the npm ecosystem'

$pnpmModerateGate = Test-LiveCoreDependencyAuditGate -Model (Get-LiveCorePnpmVulnerabilityModel -Content $pnpmModerateOnlyReport)
AssertTrue ($pnpmModerateGate.Passed) 'a moderate-only pnpm report passes the agreed HIGH/CRITICAL gate (reported, not blocking)'

# --- Gate: the combined model across both ecosystems blocks on the union. ---
$combined = [pscustomobject]@{ Findings = @($dotnetModel.Findings) + @($pnpmModel.Findings) }
$combinedGate = Test-LiveCoreDependencyAuditGate -Model $combined
AssertTrue (-not $combinedGate.Passed) 'the combined .NET + pnpm model fails when either ecosystem has a high/critical advisory'
AssertTrue ($combinedGate.Blocking.Count -eq 3) 'all three high/critical findings across both ecosystems block'

# --- Gate: the failing severities are configurable. ---
$strictGate = Test-LiveCoreDependencyAuditGate -Model (Get-LiveCorePnpmVulnerabilityModel -Content $pnpmModerateOnlyReport) -FailOnSeverity @('MODERATE', 'HIGH', 'CRITICAL')
AssertTrue (-not $strictGate.Passed) 'widening the gate to MODERATE blocks the same moderate-only report'
AssertTrue ($strictGate.Blocking.Count -eq 1) 'the moderate advisory blocks once MODERATE is in the failing set'

# --- Fail-closed: a malformed report is rejected, never waved through. ---
AssertThrows { Get-LiveCoreDotnetVulnerabilityModel -Content 'not json {' } 'a malformed .NET audit report is rejected (fail-closed)'
AssertThrows { Get-LiveCorePnpmVulnerabilityModel -Content 'not json {' } 'a malformed pnpm audit report is rejected (fail-closed)'
AssertThrows { Get-LiveCoreDotnetVulnerabilityModel -Content '' } 'an empty .NET audit report is rejected (fail-closed)'

# --- End to end: the assert-dependency-audit.ps1 CLI enforces the gate. ---
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-depaudit-" + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $tempDir
$psExe = (Get-Process -Id $PID).Path
try {
    $dotnetVulnPath = Join-Path $tempDir 'dotnet-vulnerable.json'
    $dotnetCleanPath = Join-Path $tempDir 'dotnet-clean.json'
    $pnpmModeratePath = Join-Path $tempDir 'pnpm-moderate.json'
    $pnpmCleanPath = Join-Path $tempDir 'pnpm-clean.json'
    [System.IO.File]::WriteAllText($dotnetVulnPath, $dotnetVulnerableReport)
    [System.IO.File]::WriteAllText($dotnetCleanPath, $dotnetCleanReport)
    [System.IO.File]::WriteAllText($pnpmModeratePath, $pnpmModerateOnlyReport)
    [System.IO.File]::WriteAllText($pnpmCleanPath, $pnpmCleanReport)

    & $psExe -NoProfile -File $assertScript -DotnetReportPath $dotnetVulnPath -PnpmReportPath $pnpmCleanPath *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'the CLI exits non-zero (fails the build) on a seeded high/critical .NET vulnerability'

    & $psExe -NoProfile -File $assertScript -DotnetReportPath $dotnetCleanPath -PnpmReportPath $pnpmModeratePath *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'the CLI exits zero on a clean .NET report and a moderate-only pnpm report'

    & $psExe -NoProfile -File $assertScript -DotnetReportPath $dotnetVulnPath -PnpmReportPath $pnpmCleanPath -ReportOnly *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'report-only mode never blocks, even on a high/critical vulnerability'

    & $psExe -NoProfile -File $assertScript -DotnetReportPath (Join-Path $tempDir 'does-not-exist.json') *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'a missing audit report blocks the build (fail-closed)'

    & $psExe -NoProfile -File $assertScript *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'supplying no audit report at all blocks the build (fail-closed)'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Dependency audit gate tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host $failure }
    exit 1
}

Write-Host ''
Write-Host 'Dependency audit gate tests passed: a seeded high/critical vulnerable package fails closed across both ecosystems, a clean or moderate-only report passes, the failing set is configurable and a malformed report is rejected.' -ForegroundColor Green
exit 0
