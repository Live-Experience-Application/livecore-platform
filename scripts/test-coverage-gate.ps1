#requires -Version 5.1

<#
.SYNOPSIS
    Tests the code-coverage gate (CORE-TST-001, CORE-TST-009): a
    deliberately-untested new handler trips the threshold once blocking is
    enabled, coverage from several reports is merged without double counting,
    test/generated code is excluded from the production number, and the CI
    coverage job runs the gate BLOCKING at the documented line-coverage floor.

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreCoverage.psm1 and assert-coverage.ps1
    - no external test framework and no test run, so it runs as a CI gate and
    locally on both pwsh and Windows PowerShell 5.1. Exits 0 when every assertion
    holds and 1 when any fails.

    This is the test for the CORE-TST-001 required case: "a deliberately-untested
    new handler trips the threshold once blocking is enabled" is proved against
    seeded Cobertura reports here and through the assert-coverage.ps1 CLI (which
    blocks in the default mode and only warns under -ReportOnly).

    CORE-TST-009 made the gate BLOCKING. This test now also asserts the enforced
    floor as a value (coverage exactly at the floor passes, a hair below fails)
    and that the CI coverage job (.github/workflows/ci.yml) invokes
    assert-coverage.ps1 with no -ReportOnly and pins -MinimumLineCoverage to the
    documented floor - so re-adding -ReportOnly, or moving the floor without
    updating this test and the docs, fails CI.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-coverage-gate.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreCoverage.psm1') -Force
$assertScript = Join-Path $scriptDir 'assert-coverage.ps1'

$failures = New-Object System.Collections.Generic.List[string]

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because") }
}

function AssertEqual {
    param($Expected, $Actual, [string]$Because)
    if ([string]$Expected -ceq [string]$Actual) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because`n      expected: '$Expected'`n      actual:   '$Actual'") }
}

function AssertThrows {
    param([scriptblock]$Action, [string]$Because)
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    if ($threw) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because (expected a fail-closed error, but it succeeded)") }
}

# Builds a Cobertura <lines> block: line numbers $From..$To with the given hits.
function Get-CoberturaLineXml {
    param([int]$From, [int]$To, [int]$Hits)
    $sb = New-Object System.Text.StringBuilder
    for ($number = $From; $number -le $To; $number++) {
        [void]$sb.AppendLine("          <line number=`"$number`" hits=`"$Hits`" />")
    }
    return $sb.ToString()
}

# --- Seeded fixtures (Cobertura XML). Product-neutral generic names only. ---

# Baseline production code, fully covered: one 10-line class, all lines hit.
$baselineReport = @"
<?xml version="1.0"?>
<coverage line-rate="1" version="1.9">
  <packages>
    <package name="LiveCore.Sample">
      <classes>
        <class name="LiveCore.Sample.SampleService" filename="Sample/SampleService.cs">
          <lines>
$(Get-CoberturaLineXml -From 1 -To 10 -Hits 1)
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"@

# The same baseline PLUS a deliberately-untested new handler (15 uncovered
# lines). Production lines become 10 covered of 25 -> 40%.
$untestedHandlerReport = @"
<?xml version="1.0"?>
<coverage line-rate="0.4" version="1.9">
  <packages>
    <package name="LiveCore.Sample">
      <classes>
        <class name="LiveCore.Sample.SampleService" filename="Sample/SampleService.cs">
          <lines>
$(Get-CoberturaLineXml -From 1 -To 10 -Hits 1)
          </lines>
        </class>
        <class name="LiveCore.Sample.SampleUntestedHandler" filename="Sample/SampleUntestedHandler.cs">
          <lines>
$(Get-CoberturaLineXml -From 1 -To 15 -Hits 0)
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"@

# Two reports over the SAME file with complementary coverage, to prove the merge:
# report A covers lines 1-9 (line 10 missed); report B covers line 10 only.
$mergeReportA = @"
<?xml version="1.0"?>
<coverage version="1.9">
  <packages>
    <package name="LiveCore.Sample">
      <classes>
        <class name="LiveCore.Sample.SampleService" filename="Sample/SampleService.cs">
          <lines>
$(Get-CoberturaLineXml -From 1 -To 9 -Hits 1)
            <line number="10" hits="0" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"@

$mergeReportB = @"
<?xml version="1.0"?>
<coverage version="1.9">
  <packages>
    <package name="LiveCore.Sample">
      <classes>
        <class name="LiveCore.Sample.SampleService" filename="Sample/SampleService.cs">
          <lines>
$(Get-CoberturaLineXml -From 1 -To 9 -Hits 0)
            <line number="10" hits="1" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"@

# Production code (10 covered) alongside a test assembly (50 covered) and a
# generated EF migration file (40 uncovered). Only the 10 production lines must
# count: a test assembly or a migration must change neither numerator nor
# denominator.
$exclusionReport = @"
<?xml version="1.0"?>
<coverage version="1.9">
  <packages>
    <package name="LiveCore.Sample">
      <classes>
        <class name="LiveCore.Sample.SampleService" filename="Sample/SampleService.cs">
          <lines>
$(Get-CoberturaLineXml -From 1 -To 10 -Hits 1)
          </lines>
        </class>
        <class name="LiveCore.Sample.Init" filename="Persistence/Migrations/20260101000000_Init.cs">
          <lines>
$(Get-CoberturaLineXml -From 1 -To 40 -Hits 0)
          </lines>
        </class>
      </classes>
    </package>
    <package name="LiveCore.Sample.UnitTests">
      <classes>
        <class name="LiveCore.Sample.UnitTests.SampleServiceTests" filename="Sample/SampleServiceTests.cs">
          <lines>
$(Get-CoberturaLineXml -From 1 -To 50 -Hits 1)
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"@

# A report whose only content is a test assembly: no production line is measured.
$testOnlyReport = @"
<?xml version="1.0"?>
<coverage version="1.9">
  <packages>
    <package name="LiveCore.Sample.UnitTests">
      <classes>
        <class name="LiveCore.Sample.UnitTests.SampleServiceTests" filename="Sample/SampleServiceTests.cs">
          <lines>
$(Get-CoberturaLineXml -From 1 -To 20 -Hits 1)
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"@

# --- Model: a fully covered baseline reports 100%. ---
$baselineModel = Get-LiveCoreCoverageModel -Content $baselineReport
AssertEqual 10 $baselineModel.LinesValid 'the baseline measures 10 production lines'
AssertEqual 10 $baselineModel.LinesCovered 'the baseline covers all 10 lines'
AssertEqual 100 $baselineModel.LineCoveragePercent 'the baseline reports 100% line coverage'

# --- Gate: an untested new handler drops coverage and trips the threshold. ---
$untestedModel = Get-LiveCoreCoverageModel -Content $untestedHandlerReport
AssertEqual 25 $untestedModel.LinesValid 'the untested handler adds 15 lines to the denominator'
AssertEqual 10 $untestedModel.LinesCovered 'the untested handler adds no covered lines'
AssertEqual 40 $untestedModel.LineCoveragePercent 'coverage falls to 40% with the untested handler'

$baselineGate = Test-LiveCoreCoverageGate -Model $baselineModel -MinimumLineCoveragePercent 80
AssertTrue ($baselineGate.Passed) 'the fully covered baseline passes an 80% gate'

$untestedGate = Test-LiveCoreCoverageGate -Model $untestedModel -MinimumLineCoveragePercent 80
AssertTrue (-not $untestedGate.Passed) 'a deliberately-untested new handler trips the 80% gate'
AssertEqual 40 $untestedGate.Shortfall 'the gate reports the coverage shortfall (80 - 40)'

# --- Merge: a line covered in any report counts as covered, once. ---
$mergedModel = Get-LiveCoreCoverageModel -Content @($mergeReportA, $mergeReportB)
AssertEqual 10 $mergedModel.LinesValid 'the same 10 lines across two reports are not double counted'
AssertEqual 10 $mergedModel.LinesCovered 'a line covered in either report counts as covered after merge'
AssertEqual 100 $mergedModel.LineCoveragePercent 'complementary coverage from two reports merges to 100%'

# --- Exclusions: test assemblies and generated migrations do not count. ---
$exclusionModel = Get-LiveCoreCoverageModel -Content $exclusionReport
AssertEqual 10 $exclusionModel.LinesValid 'test assemblies and migration files are excluded from the denominator'
AssertEqual 10 $exclusionModel.LinesCovered 'only the production lines count toward covered'
AssertEqual 100 $exclusionModel.LineCoveragePercent 'excluding test and generated code yields the production number'
AssertTrue (@($exclusionModel.Assemblies) -contains 'LiveCore.Sample') 'the production assembly is measured'
AssertTrue (-not (@($exclusionModel.Assemblies) -contains 'LiveCore.Sample.UnitTests')) 'the test assembly is not measured'

# --- Fail-closed: a report measuring no production line never passes. ---
$testOnlyModel = Get-LiveCoreCoverageModel -Content $testOnlyReport
AssertEqual 0 $testOnlyModel.LinesValid 'a test-only report measures zero production lines'
$testOnlyGate = Test-LiveCoreCoverageGate -Model $testOnlyModel -MinimumLineCoveragePercent 0
AssertTrue (-not $testOnlyGate.Passed) 'a report measuring no production line never passes, even at a 0% minimum (fail-closed)'

# --- Fail-closed: a malformed report is rejected, never waved through. ---
AssertThrows { Get-LiveCoreCoverageModel -Content 'not xml <' } 'a malformed coverage report is rejected (fail-closed)'

# --- End to end: the assert-coverage.ps1 CLI enforces the gate. ---
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-coverage-" + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $tempDir
$psExe = (Get-Process -Id $PID).Path
try {
    $baselinePath = Join-Path $tempDir 'baseline.cobertura.xml'
    $untestedPath = Join-Path $tempDir 'untested.cobertura.xml'
    [System.IO.File]::WriteAllText($baselinePath, $baselineReport)
    [System.IO.File]::WriteAllText($untestedPath, $untestedHandlerReport)

    & $psExe -NoProfile -File $assertScript -ReportPath $baselinePath -MinimumLineCoverage 80 *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'the CLI exits zero when coverage meets the minimum'

    & $psExe -NoProfile -File $assertScript -ReportPath $untestedPath -MinimumLineCoverage 80 *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'the CLI exits non-zero (blocks) when an untested handler drops coverage below the minimum'

    & $psExe -NoProfile -File $assertScript -ReportPath $untestedPath -MinimumLineCoverage 80 -ReportOnly *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'report-only mode never blocks, even below the minimum (the initial non-blocking posture)'

    # A directory search finds the report(s) and merges them.
    & $psExe -NoProfile -File $assertScript -ReportDirectory $tempDir -MinimumLineCoverage 80 *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'the directory search merges every report (baseline + untested) and blocks below the minimum'

    & $psExe -NoProfile -File $assertScript -ReportDirectory (Join-Path $tempDir 'does-not-exist') -MinimumLineCoverage 80 *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'a missing report directory blocks (fail-closed)'

    $emptyDir = Join-Path $tempDir 'empty'
    $null = New-Item -ItemType Directory -Path $emptyDir
    & $psExe -NoProfile -File $assertScript -ReportDirectory $emptyDir -MinimumLineCoverage 80 *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'no coverage reports found blocks (fail-closed)'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- The enforced floor: blocking posture and the documented value (CORE-TST-009).
# The gate was flipped from report-only to BLOCKING and the floor was set just
# below the production coverage measured at that time (92.79%). This is the
# single documented floor value; docs/14, docs/17 and the README quote the same
# number. Asserting it here means re-adding -ReportOnly, or moving the floor in
# ci.yml without updating this test (and the docs), fails CI.
$EnforcedFloorPercent = 90

# The floor is enforced as a VALUE: a model exactly at the floor passes, and one a
# hair below it fails. (Test-LiveCoreCoverageGate reads LinesValid and
# LineCoveragePercent off the model.)
$atFloorModel = [pscustomobject]@{ LinesValid = 10000; LinesCovered = 9000; LineCoveragePercent = $EnforcedFloorPercent }
$belowFloorModel = [pscustomobject]@{ LinesValid = 10000; LinesCovered = 8999; LineCoveragePercent = ([double]$EnforcedFloorPercent - 0.01) }
$atFloorGate = Test-LiveCoreCoverageGate -Model $atFloorModel -MinimumLineCoveragePercent $EnforcedFloorPercent
$belowFloorGate = Test-LiveCoreCoverageGate -Model $belowFloorModel -MinimumLineCoveragePercent $EnforcedFloorPercent
AssertTrue ($atFloorGate.Passed) "coverage exactly at the enforced $EnforcedFloorPercent% floor passes the gate"
AssertTrue (-not $belowFloorGate.Passed) "coverage a hair below the enforced $EnforcedFloorPercent% floor fails the gate (the floor is enforced as a value)"

# The CI coverage job runs the gate BLOCKING at the documented floor. Read the
# real workflow and assert the one assert-coverage.ps1 invocation drops -ReportOnly
# and pins -MinimumLineCoverage to the documented floor.
$repoRoot = Split-Path -Parent $scriptDir
$ciWorkflowPath = Join-Path $repoRoot '.github/workflows/ci.yml'
AssertTrue (Test-Path -LiteralPath $ciWorkflowPath) 'the CI workflow file exists'
if (Test-Path -LiteralPath $ciWorkflowPath) {
    $ciLines = Get-Content -LiteralPath $ciWorkflowPath
    # Only the `run:` step that invokes assert-coverage.ps1 is the gate invocation;
    # comment lines that merely mention the script are not matched.
    $gateInvocations = @($ciLines | Where-Object { $_ -match '^\s*run:\s*.*assert-coverage\.ps1' })
    AssertEqual 1 $gateInvocations.Count 'the CI coverage job invokes assert-coverage.ps1 exactly once'
    if ($gateInvocations.Count -eq 1) {
        $gateLine = $gateInvocations[0]
        AssertTrue (-not ($gateLine -match '-ReportOnly')) 'the CI coverage gate runs BLOCKING (no -ReportOnly): a report below the floor fails the build'
        $ciFloor = $null
        if ($gateLine -match '-MinimumLineCoverage\s+(\d+(?:\.\d+)?)') { $ciFloor = [double]$Matches[1] }
        AssertEqual $EnforcedFloorPercent $ciFloor "the CI coverage gate enforces the documented $EnforcedFloorPercent% floor"
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Coverage gate tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host $failure }
    exit 1
}

Write-Host ''
Write-Host 'Coverage gate tests passed: an untested new handler trips the threshold, coverage merges without double counting, test/generated code is excluded, and the CI gate is blocking at the documented floor.' -ForegroundColor Green
exit 0
