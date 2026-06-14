#requires -Version 5.1

<#
.SYNOPSIS
    Tests the in-repo Docker Compose deployment manifest (CORE-DEP-001).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreComposeDeploy.psm1 - no external test
    framework and no Docker, so it runs as a CI gate and locally on both pwsh and
    Windows PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

    It proves the validation logic over fixtures (a manifest that drops the
    migrate-before-API gate, the postgres healthcheck or a required service is
    rejected) and then guards the real checked-in manifest:
    deploy/compose/docker-compose.yml must wire postgres + the migrations runner +
    api + worker with the migrate-before-API gate
    (depends_on: service_completed_successfully) and the documented
    health/readiness/liveness probe endpoints. This is the "renders/validates"
    half of the smoke; the compose-smoke CI job adds the real "starts" half by
    bringing the stack up against Docker and asserting the API only starts after
    migrations complete.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-compose-deploy.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-compose-deploy.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreComposeDeploy.psm1') -Force
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

# A minimal, well-formed manifest fixture. Stage- and indentation-faithful to the
# real one so the parser is exercised the same way; the helper lets each negative
# test perturb exactly one invariant.
function Get-FixtureCompose {
    param(
        [string]$ApiMigrateCondition = 'service_completed_successfully',
        [switch]$OmitPostgresHealthcheck,
        [switch]$OmitWorker
    )

    $postgresHealthcheck = if ($OmitPostgresHealthcheck) { '' } else { @'
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U livecore -d livecore"]
'@ }

    $worker = if ($OmitWorker) { '' } else { @'
  worker:
    build:
      context: ../..
      dockerfile: apps/worker/Dockerfile
    image: livecore-worker:local
    environment:
      ConnectionStrings__Database: "Host=postgres;Port=5432;Database=livecore;Username=livecore;Password=livecore"
    depends_on:
      postgres:
        condition: service_healthy
      migrate:
        condition: service_completed_successfully
'@ }

    return @"
name: livecore
services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_USER: livecore
$postgresHealthcheck
  migrate:
    build:
      context: ../..
      dockerfile: apps/api/Migrations.Dockerfile
    image: livecore-migrations:local
    environment:
      ConnectionStrings__Database: "Host=postgres;Port=5432;Database=livecore;Username=livecore;Password=livecore"
    depends_on:
      postgres:
        condition: service_healthy
  api:
    build:
      context: ../..
      dockerfile: apps/api/Dockerfile
    image: livecore-api:local
    # probes: /health/live /health/ready /metrics
    environment:
      ConnectionStrings__Database: "Host=postgres;Port=5432;Database=livecore;Username=livecore;Password=livecore"
    depends_on:
      postgres:
        condition: service_healthy
      migrate:
        condition: $ApiMigrateCondition
$worker
volumes:
  livecore-postgres-data:
"@
}

# --- Parser: the well-formed fixture is valid. ---
$validModel = Get-LiveCoreComposeModel -Content (Get-FixtureCompose)
AssertTrue ($validModel.Services.Count -eq 4) 'the parser finds all four services (postgres, migrate, api, worker)'
AssertTrue ($validModel.Services['api'].DependsOn['migrate'] -eq 'service_completed_successfully') `
    'the parser reads the api -> migrate gate condition'
AssertTrue ((Test-LiveCoreComposeDeployment -Model $validModel).IsValid) `
    'a well-formed manifest passes validation'

# --- Negative: dropping the migrate gate is rejected. ---
$noGate = Get-LiveCoreComposeModel -Content (Get-FixtureCompose -ApiMigrateCondition 'service_started')
$noGateResult = Test-LiveCoreComposeDeployment -Model $noGate
AssertTrue (-not $noGateResult.IsValid) 'a manifest whose api does not gate on migrate completion is rejected'
AssertTrue (($noGateResult.Findings -join "`n") -match 'service_completed_successfully') `
    'the rejection names the missing migrate-before-API gate'

# --- Negative: a missing postgres healthcheck is rejected. ---
$noHealthcheck = Get-LiveCoreComposeModel -Content (Get-FixtureCompose -OmitPostgresHealthcheck)
$noHealthcheckResult = Test-LiveCoreComposeDeployment -Model $noHealthcheck
AssertTrue (-not $noHealthcheckResult.IsValid) 'a manifest with no postgres healthcheck is rejected'
AssertTrue (($noHealthcheckResult.Findings -join "`n") -match 'healthcheck') `
    'the rejection names the missing postgres healthcheck'

# --- Negative: a missing required service is rejected. ---
$noWorker = Get-LiveCoreComposeModel -Content (Get-FixtureCompose -OmitWorker)
$noWorkerResult = Test-LiveCoreComposeDeployment -Model $noWorker
AssertTrue (-not $noWorkerResult.IsValid) 'a manifest missing the worker service is rejected'
AssertTrue (($noWorkerResult.Findings -join "`n") -match "MISSING SERVICE: .*'worker'") `
    'the rejection names the missing worker service'

# --- Guard the real checked-in manifest. ---
$composePath = Join-Path $repoRoot 'deploy/compose/docker-compose.yml'
AssertTrue (Test-Path -LiteralPath $composePath) 'the deploy/compose/docker-compose.yml manifest exists'
$realModel = Get-LiveCoreComposeModel -Path $composePath
$realResult = Test-LiveCoreComposeDeployment -Model $realModel

if (-not $realResult.IsValid) {
    foreach ($finding in $realResult.Findings) {
        $failures.Add("FAIL (real manifest): $finding")
    }
}
else {
    Write-Host 'PASS: the real deploy/compose/docker-compose.yml wires the migrate gate, the postgres healthcheck, all four services and the documented probes'
}

# Spot-check the exact gate conditions on the real manifest.
AssertTrue ($realModel.Services['api'].DependsOn['migrate'] -eq 'service_completed_successfully') `
    'the real api service gates on migrate completion (service_completed_successfully)'
AssertTrue ($realModel.Services['worker'].DependsOn['migrate'] -eq 'service_completed_successfully') `
    'the real worker service gates on migrate completion (service_completed_successfully)'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Compose deployment manifest tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Compose deployment manifest tests passed: the migrate-before-API gate, the postgres healthcheck and the documented probes are wired as required.' -ForegroundColor Green
exit 0
