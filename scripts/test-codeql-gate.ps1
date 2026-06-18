#requires -Version 5.1

<#
.SYNOPSIS
    Tests the static-analysis gate (CORE-SEC-006): a seeded high/critical CodeQL
    finding fails the gate, a clean or medium/low-only report passes, the floor
    is configurable, severity is resolved from a tool extension's rules, and a
    missing/malformed/non-SARIF document fails closed.

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreCodeQL.psm1 and
    assert-codeql-findings.ps1 - no external test framework and no CodeQL CLI, so
    it runs as a CI gate and locally on both pwsh and Windows PowerShell 5.1.
    Exits 0 when every assertion holds and 1 when any fails.

    This is the CORE-SEC-006 required "fails closed on a high/critical finding"
    test: the seeded SARIF that carries a high (and a critical) security finding
    blocks the build here and through the assert-codeql-findings.ps1 CLI, proving
    the gate would fail closed when CodeQL surfaces such a finding in the
    first-party sources - without committing a real vulnerability to do so.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-codeql-gate.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreCodeQL.psm1') -Force
$assertScript = Join-Path $scriptDir 'assert-codeql-findings.ps1'

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

# --- Seeded SARIF fixtures. The content is product-neutral on purpose: only
#     generic CodeQL rule ids and file paths, no committed vulnerability. ---

# A SARIF with a high (7.5) and a critical (9.8) security finding, plus a
# non-security finding that carries no security-severity. The high finding's
# rule lives under tool.driver.rules; the critical finding's rule lives under a
# tool EXTENSION's rules, proving severity is resolved from both places.
$highAndCriticalSarif = @'
{
  "version": "2.1.0",
  "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
  "runs": [
    {
      "tool": {
        "driver": {
          "name": "CodeQL",
          "rules": [
            {
              "id": "cs/sql-injection",
              "properties": { "security-severity": "7.5", "tags": ["security"] },
              "defaultConfiguration": { "level": "error" }
            },
            {
              "id": "cs/useless-assignment-to-local",
              "properties": { "tags": ["maintainability"] },
              "defaultConfiguration": { "level": "note" }
            }
          ]
        },
        "extensions": [
          {
            "rules": [
              {
                "id": "cs/cleartext-storage-of-sensitive-information",
                "properties": { "security-severity": "9.8", "tags": ["security"] },
                "defaultConfiguration": { "level": "error" }
              }
            ]
          }
        ]
      },
      "results": [
        {
          "ruleId": "cs/sql-injection",
          "level": "error",
          "message": { "text": "Seeded high-severity finding used to prove the gate blocks." },
          "locations": [
            { "physicalLocation": { "artifactLocation": { "uri": "apps/api/Seeded.cs" }, "region": { "startLine": 42 } } }
          ]
        },
        {
          "ruleId": "cs/cleartext-storage-of-sensitive-information",
          "level": "error",
          "message": { "text": "Seeded critical-severity finding from a tool extension rule." },
          "locations": [
            { "physicalLocation": { "artifactLocation": { "uri": "apps/api/Seeded.cs" }, "region": { "startLine": 7 } } }
          ]
        },
        {
          "ruleId": "cs/useless-assignment-to-local",
          "level": "note",
          "message": { "text": "A non-security maintainability finding with no security-severity." }
        }
      ]
    }
  ]
}
'@

# A clean SARIF: CodeQL ran and found nothing.
$cleanSarif = @'
{
  "version": "2.1.0",
  "runs": [
    {
      "tool": { "driver": { "name": "CodeQL", "rules": [] } },
      "results": []
    }
  ]
}
'@

# A SARIF whose only security findings are medium (5.0) and low (3.1).
$mediumLowSarif = @'
{
  "version": "2.1.0",
  "runs": [
    {
      "tool": {
        "driver": {
          "name": "CodeQL",
          "rules": [
            { "id": "js/incomplete-sanitization", "properties": { "security-severity": "5.0" } },
            { "id": "js/trivial-conditional", "properties": { "security-severity": "3.1" } }
          ]
        }
      },
      "results": [
        { "ruleId": "js/incomplete-sanitization", "message": { "text": "Seeded medium finding." } },
        { "ruleId": "js/trivial-conditional", "message": { "text": "Seeded low finding." } }
      ]
    }
  ]
}
'@

# Not a SARIF log at all (no runs array).
$notSarif = @'
{ "note": "this is not a SARIF log" }
'@

# --- Gate: a seeded high/critical finding fails the gate. ---
$model = Get-LiveCoreCodeQlSarifModel -Content $highAndCriticalSarif
$gate = Test-LiveCoreCodeQlGate -Model $model
AssertTrue (-not $gate.Passed) 'a seeded high/critical CodeQL finding fails the static-analysis gate'
AssertTrue ($gate.Blocking.Count -eq 2) 'both the high and the critical finding block (the maintainability note does not)'
AssertTrue ($gate.Counts['Critical'] -eq 1) 'the report counts one CRITICAL'
AssertTrue ($gate.Counts['High'] -eq 1) 'the report counts one HIGH'
AssertTrue ($gate.Counts['None'] -eq 1) 'the non-security maintainability finding is counted in the None band'
AssertEqual 'CodeQL' $model.ToolName 'the SARIF tool name is read'
$critical = @($gate.Blocking | Where-Object { $_.Band -eq 'Critical' })
AssertEqual 'cs/cleartext-storage-of-sensitive-information' $critical[0].RuleId 'the critical finding resolves its severity from the tool EXTENSION rules'
AssertEqual 'apps/api/Seeded.cs:7' $critical[0].Location 'the critical finding carries its source location'

# --- Gate: a clean report passes. ---
$cleanGate = Test-LiveCoreCodeQlGate -Model (Get-LiveCoreCodeQlSarifModel -Content $cleanSarif)
AssertTrue ($cleanGate.Passed) 'a SARIF with no findings passes the gate'
AssertTrue ($cleanGate.Blocking.Count -eq 0) 'a clean report has no blocking findings'

# --- Gate: medium/low-only passes the default high floor, and the floor is configurable. ---
$mediumLowModel = Get-LiveCoreCodeQlSarifModel -Content $mediumLowSarif
AssertTrue ((Test-LiveCoreCodeQlGate -Model $mediumLowModel).Passed) 'a medium/low-only report passes the default high/critical gate'
$strictGate = Test-LiveCoreCodeQlGate -Model $mediumLowModel -MinimumSecuritySeverity 4.0
AssertTrue (-not $strictGate.Passed) 'lowering the floor to 4.0 blocks the medium finding'
AssertTrue ($strictGate.Blocking.Count -eq 1) 'only the medium finding (>= 4.0) blocks at the lowered floor, not the low one'

# --- Band mapping boundaries. ---
AssertEqual 'Critical' (Get-LiveCoreCodeQlBand -SecuritySeverity 9.0) '9.0 is the critical floor'
AssertEqual 'High' (Get-LiveCoreCodeQlBand -SecuritySeverity 7.0) '7.0 is the high floor'
AssertEqual 'Medium' (Get-LiveCoreCodeQlBand -SecuritySeverity 6.9) '6.9 is still medium'
AssertEqual 'None' (Get-LiveCoreCodeQlBand -SecuritySeverity $null) 'no security-severity is the None band'

# --- Fail-closed: a malformed or non-SARIF document is rejected, never waved through. ---
AssertThrows { Get-LiveCoreCodeQlSarifModel -Content 'not json {' } 'a malformed SARIF document is rejected (fail-closed)'
AssertThrows { Get-LiveCoreCodeQlSarifModel -Content $notSarif } 'a document with no runs array is not a SARIF log (fail-closed)'
AssertThrows { Get-LiveCoreCodeQlSarifModel -Content '' } 'an empty document is rejected (fail-closed)'

# --- End to end: the assert-codeql-findings.ps1 CLI enforces the gate. ---
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-codeql-" + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $tempRoot
$psExe = (Get-Process -Id $PID).Path
try {
    $highDir = Join-Path $tempRoot 'high'
    $cleanDir = Join-Path $tempRoot 'clean'
    $emptyDir = Join-Path $tempRoot 'empty'
    $null = New-Item -ItemType Directory -Path $highDir
    $null = New-Item -ItemType Directory -Path $cleanDir
    $null = New-Item -ItemType Directory -Path $emptyDir
    [System.IO.File]::WriteAllText((Join-Path $highDir 'csharp.sarif'), $highAndCriticalSarif)
    [System.IO.File]::WriteAllText((Join-Path $cleanDir 'csharp.sarif'), $cleanSarif)

    & $psExe -NoProfile -File $assertScript -ResultsDirectory $highDir *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'the CLI exits non-zero (fails the build) on a seeded high/critical finding'

    & $psExe -NoProfile -File $assertScript -ResultsDirectory $cleanDir *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'the CLI exits zero on a clean SARIF'

    & $psExe -NoProfile -File $assertScript -ResultsDirectory $highDir -ReportOnly *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'report-only mode never blocks, even on a high/critical finding'

    & $psExe -NoProfile -File $assertScript -ResultsDirectory $emptyDir *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'a directory with no SARIF blocks the build (fail-closed)'

    & $psExe -NoProfile -File $assertScript -ResultsDirectory (Join-Path $tempRoot 'does-not-exist') *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'a missing results directory blocks the build (fail-closed)'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "CodeQL gate tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host $failure }
    exit 1
}

Write-Host ''
Write-Host 'CodeQL gate tests passed: a seeded high/critical finding fails closed, a clean report passes, the floor is configurable and a malformed/non-SARIF document is rejected.' -ForegroundColor Green
exit 0
