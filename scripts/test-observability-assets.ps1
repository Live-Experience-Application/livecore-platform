#requires -Version 5.1

<#
.SYNOPSIS
    Tests the example observability assets and guards the real files (CORE-OBS-008).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreObservabilityAssets.psm1 - no external
    test framework and no Prometheus/Grafana, so it runs as a CI gate and locally
    on both pwsh and Windows PowerShell 5.1. Exits 0 when every assertion holds and
    1 when any fails.

    It proves the validation logic over fixtures (a rules file that references an
    undocumented series, an alert with no severity, and a malformed dashboard JSON
    are each rejected; a well-formed set passes) and then guards the real checked-in
    assets under deploy/observability/: every livecore_* series the rules and the
    dashboard reference must be a series documented in docs/15_OBSERVABILITY.md or a
    recording rule defined in the rules file, the dashboard JSON must be valid, and
    every alert must carry a severity and an annotation. The promtool structural
    validation is the complementary half added by the observability-assets CI job.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-observability-assets.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-observability-assets.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreObservabilityAssets.psm1') -Force
$repoRoot = Split-Path -Parent $scriptDir

$failures = New-Object System.Collections.Generic.List[string]

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because")
    }
}

# Fixture known-series set (a subset of the documented series, enough to exercise
# the validator).
$fixtureKnown = @(
    'livecore_api_request_errors_total',
    'livecore_api_request_duration_seconds'
)

$goodRules = @'
groups:
  - name: livecore_fixture_recording
    rules:
      - record: job:livecore_api_request_errors:rate5m
        expr: sum by (job) (rate(livecore_api_request_errors_total[5m]))
  - name: livecore_fixture_alerts
    rules:
      - alert: FixtureErrorRate
        expr: job:livecore_api_request_errors:rate5m > 0
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: fixture error-rate alert
'@

$goodDashboard = '{ "panels": [ { "targets": [ { "expr": "sum(rate(livecore_api_request_errors_total[5m]))" }, { "expr": "histogram_quantile(0.95, rate(livecore_api_request_duration_seconds_bucket[5m]))" } ] } ] }'

# --- Positive: a well-formed set passes, and the suffix/recording-rule rules hold. ---
$goodResult = Test-LiveCoreObservabilityAssets -RulesText $goodRules -DashboardJsonText $goodDashboard -KnownSeries $fixtureKnown
AssertTrue ($goodResult.IsValid) 'a well-formed rules + dashboard set passes validation'

AssertTrue (Test-LiveCoreSeriesReference -Reference 'livecore_api_request_duration_seconds_bucket' -KnownSeries $fixtureKnown -RecordingRuleName @()) `
    'a histogram _bucket suffix on a documented base series is accepted'
AssertTrue (Test-LiveCoreSeriesReference -Reference 'job:livecore_api_request_errors:rate5m' -KnownSeries $fixtureKnown -RecordingRuleName @('job:livecore_api_request_errors:rate5m')) `
    'a reference to a defined recording rule is accepted'
AssertTrue (-not (Test-LiveCoreSeriesReference -Reference 'livecore_made_up_total' -KnownSeries $fixtureKnown -RecordingRuleName @())) `
    'an undocumented series is rejected'

# --- Negative: a reference to an undocumented series is rejected. ---
$bogusRules = $goodRules + @'

      - alert: FixtureBogus
        expr: rate(livecore_bogus_total[5m]) > 0
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: bogus
'@
$bogusResult = Test-LiveCoreObservabilityAssets -RulesText $bogusRules -DashboardJsonText $goodDashboard -KnownSeries $fixtureKnown
AssertTrue (-not $bogusResult.IsValid) 'a rules file referencing an undocumented series is rejected'
AssertTrue (($bogusResult.Findings -join "`n") -match 'livecore_bogus_total') `
    'the rejection names the undocumented series'

# --- Negative: an alert with no severity label is rejected. ---
$noSeverityRules = @'
groups:
  - name: livecore_fixture_alerts
    rules:
      - alert: FixtureNoSeverity
        expr: job:livecore_api_request_errors:rate5m > 0
        for: 5m
        annotations:
          summary: missing severity
      - record: job:livecore_api_request_errors:rate5m
        expr: sum by (job) (rate(livecore_api_request_errors_total[5m]))
'@
$noSeverityResult = Test-LiveCoreObservabilityAssets -RulesText $noSeverityRules -DashboardJsonText $goodDashboard -KnownSeries $fixtureKnown
AssertTrue (-not $noSeverityResult.IsValid) 'an alert with no severity label is rejected'
AssertTrue (($noSeverityResult.Findings -join "`n") -match 'severity') 'the rejection names the missing severity'

# --- Negative: a malformed dashboard JSON is rejected. ---
$malformedDashboard = '{ "panels": [ { "targets": [ '
$malformedResult = Test-LiveCoreObservabilityAssets -RulesText $goodRules -DashboardJsonText $malformedDashboard -KnownSeries $fixtureKnown
AssertTrue (-not $malformedResult.IsValid) 'a malformed dashboard JSON is rejected'
AssertTrue (($malformedResult.Findings -join "`n") -match 'not valid') 'the rejection reports the invalid dashboard JSON'

# --- Guard the real checked-in assets. ---
$docPath = Join-Path $repoRoot 'docs/15_OBSERVABILITY.md'
$rulesPath = Join-Path $repoRoot 'deploy/observability/prometheus/rules/livecore.rules.yml'
$scrapePath = Join-Path $repoRoot 'deploy/observability/prometheus/prometheus.yml'
$dashboardPath = Join-Path $repoRoot 'deploy/observability/grafana/dashboards/livecore-overview.json'

AssertTrue (Test-Path -LiteralPath $rulesPath) 'the example Prometheus rules file exists'
AssertTrue (Test-Path -LiteralPath $scrapePath) 'the example Prometheus scrape config exists'
AssertTrue (Test-Path -LiteralPath $dashboardPath) 'the starter Grafana dashboard exists'

$knownSeries = @(Get-LiveCoreKnownMetricSeries -ObservabilityDocPath $docPath)
AssertTrue ($knownSeries.Count -ge 13) "docs/15_OBSERVABILITY.md documents the livecore_* series set (found $($knownSeries.Count))"

$realRules = Get-Content -LiteralPath $rulesPath -Raw
$realDashboard = Get-Content -LiteralPath $dashboardPath -Raw
$realResult = Test-LiveCoreObservabilityAssets -RulesText $realRules -DashboardJsonText $realDashboard -KnownSeries $knownSeries

if (-not $realResult.IsValid) {
    foreach ($finding in $realResult.Findings) {
        $failures.Add("FAIL (real assets): $finding")
    }
}
else {
    Write-Host 'PASS: the real rules and dashboard reference only documented series / defined recording rules, the dashboard JSON is valid, and every alert has a severity and annotation'
}

# The scrape config loads the rules file by a path that resolves.
$scrapeText = Get-Content -LiteralPath $scrapePath -Raw
AssertTrue ($scrapeText -match 'rules/livecore\.rules\.yml') 'the scrape config loads the example rules file (rule_files)'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Observability-assets tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Observability-assets tests passed: the example rules, scrape config and dashboard are valid and consistent with docs/15_OBSERVABILITY.md.' -ForegroundColor Green
exit 0
