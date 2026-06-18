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
        [switch]$OmitWorker,
        [switch]$OmitApiResourceLimit
    )

    $postgresHealthcheck = if ($OmitPostgresHealthcheck) { '' } else { @'
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U livecore -d livecore"]
'@ }

    # Every service carries a deploy.resources.limits ceiling (CORE-DEP-007); the
    # api's is conditional so a negative test can drop exactly one and still leave the
    # rest well-formed.
    $apiResourceLimit = if ($OmitApiResourceLimit) { '' } else { @'
    deploy:
      resources:
        limits:
          cpus: "1.0"
          memory: 1024M
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
    deploy:
      resources:
        limits:
          cpus: "0.75"
          memory: 768M
'@ }

    return @"
name: livecore
services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_USER: livecore
$postgresHealthcheck
    deploy:
      resources:
        limits:
          cpus: "1.0"
          memory: 1024M
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
    deploy:
      resources:
        limits:
          cpus: "0.5"
          memory: 512M
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
$apiResourceLimit
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

# --- Parser: the container resource limits are read (CORE-DEP-007). ---
AssertTrue ($validModel.Services['api'].HasResourceLimit) `
    'the parser reads that the api service carries a deploy.resources.limits ceiling'
AssertTrue ($validModel.Services['api'].ResourceLimits['memory'] -eq '1024M') `
    'the parser reads the api memory limit value'
AssertTrue ($validModel.Services['api'].ResourceLimits['cpus'] -eq '1.0') `
    'the parser reads the api cpus limit value (surrounding quotes stripped)'

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

# --- Negative: a service with no container resource limit is rejected (CORE-DEP-007). ---
$noLimit = Get-LiveCoreComposeModel -Content (Get-FixtureCompose -OmitApiResourceLimit)
$noLimitResult = Test-LiveCoreComposeDeployment -Model $noLimit
AssertTrue (-not $noLimitResult.IsValid) 'a manifest whose api declares no container resource limit is rejected'
AssertTrue (($noLimitResult.Findings -join "`n") -match "RESOURCE LIMIT: .*'api'") `
    'the rejection names the service missing a resource limit'

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

# Spot-check that every service in the real manifest carries a container resource
# limit (CORE-DEP-007), so no unbounded process can starve a single-VPS host.
foreach ($limitedService in @('postgres', 'migrate', 'api', 'worker')) {
    AssertTrue ($realModel.Services[$limitedService].HasResourceLimit) `
        "the real $limitedService service declares a deploy.resources.limits ceiling (CORE-DEP-007)"
}

# =============================================================================
# Full local stack overlay (docker-compose.full.yml, CORE-DEP-006)
# =============================================================================
# The optional overlay brings up the Core together with an OIDC provider, S3-
# compatible object storage and a Redis/Valkey backplane, pre-wired so the
# platform runs end-to-end locally. Test-LiveCoreComposeFullStack statically
# validates the overlay defines those three supporting services and that the
# merged api/worker reference them.

# A minimal, well-formed full-stack overlay fixture. Stage- and indentation-
# faithful to the real overlay; each switch perturbs exactly one invariant.
function Get-FixtureFullStack {
    param(
        [switch]$OmitBackplaneService,
        [switch]$BreakApiOidc,
        [switch]$OmitWorkerStorage
    )

    $backplane = if ($OmitBackplaneService) { '' } else { @'
  valkey:
    image: valkey/valkey:8
    healthcheck:
      test: ["CMD", "valkey-cli", "ping"]
'@ }

    $apiAuthority = if ($BreakApiOidc) {
        'Authentication__Oidc__Authority: ${Authentication__Oidc__Authority:-}'
    }
    else {
        'Authentication__Oidc__Authority: ${Authentication__Oidc__Authority:-http://keycloak:8080/realms/livecore}'
    }

    $workerStorage = if ($OmitWorkerStorage) { '' } else { @'
    environment:
      Assets__Storage__Endpoint: ${Assets__Storage__Endpoint:-http://rustfs:9000}
'@ }

    return @"
name: livecore
services:
  keycloak:
    image: quay.io/keycloak/keycloak:26.1
    command: ["start-dev", "--import-realm"]
  rustfs:
    image: rustfs/rustfs:1.0.0-beta.8
  rustfs-setup:
    image: amazon/aws-cli:2.35.8
    depends_on:
      rustfs:
        condition: service_started
$backplane
  api:
    environment:
      $apiAuthority
      Assets__Storage__Endpoint: `${Assets__Storage__Endpoint:-http://rustfs:9000}
      Realtime__Backplane__ConnectionString: `${Realtime__Backplane__ConnectionString:-valkey:6379}
    depends_on:
      keycloak:
        condition: service_started
      valkey:
        condition: service_healthy
      rustfs-setup:
        condition: service_completed_successfully
  worker:
$workerStorage
volumes:
  livecore-rustfs-data:
"@
}

# --- Parser + validator: the well-formed overlay fixture is valid. ---
$fullModel = Get-LiveCoreComposeModel -Content (Get-FixtureFullStack)
AssertTrue ($fullModel.Services.ContainsKey('keycloak')) 'the parser finds the keycloak (OIDC) service in the overlay'
AssertTrue ($fullModel.Services['api'].Environment['Realtime__Backplane__ConnectionString'] -match 'valkey') `
    'the parser reads the api -> backplane env value'
AssertTrue ((Test-LiveCoreComposeFullStack -Model $fullModel).IsValid) `
    'a well-formed full-stack overlay passes validation (OIDC + storage + backplane wired, api/worker reference them)'

# --- Negative: dropping the backplane service is rejected. ---
$noBackplane = Get-LiveCoreComposeModel -Content (Get-FixtureFullStack -OmitBackplaneService)
$noBackplaneResult = Test-LiveCoreComposeFullStack -Model $noBackplane
AssertTrue (-not $noBackplaneResult.IsValid) 'a full-stack overlay missing the backplane service is rejected'
AssertTrue (($noBackplaneResult.Findings -join "`n") -match 'BACKPLANE') `
    'the rejection names the missing backplane service'

# --- Negative: an api whose OIDC authority does not reference the OIDC service is rejected. ---
$brokenOidc = Get-LiveCoreComposeModel -Content (Get-FixtureFullStack -BreakApiOidc)
$brokenOidcResult = Test-LiveCoreComposeFullStack -Model $brokenOidc
AssertTrue (-not $brokenOidcResult.IsValid) 'a full-stack overlay whose api does not wire the OIDC authority to keycloak is rejected'
AssertTrue (($brokenOidcResult.Findings -join "`n") -match 'Authentication__Oidc__Authority') `
    'the rejection names the unwired OIDC authority'

# --- Negative: a worker that does not reference object storage is rejected. ---
$noWorkerStorage = Get-LiveCoreComposeModel -Content (Get-FixtureFullStack -OmitWorkerStorage)
$noWorkerStorageResult = Test-LiveCoreComposeFullStack -Model $noWorkerStorage
AssertTrue (-not $noWorkerStorageResult.IsValid) 'a full-stack overlay whose worker does not wire object storage is rejected'
AssertTrue (($noWorkerStorageResult.Findings -join "`n") -match "worker.*Assets__Storage__Endpoint") `
    'the rejection names the worker object-storage wiring'

# --- Guard the real checked-in overlay. ---
$fullComposePath = Join-Path $repoRoot 'deploy/compose/docker-compose.full.yml'
AssertTrue (Test-Path -LiteralPath $fullComposePath) 'the deploy/compose/docker-compose.full.yml overlay exists'
$realFullModel = Get-LiveCoreComposeModel -Path $fullComposePath
$realFullResult = Test-LiveCoreComposeFullStack -Model $realFullModel

if (-not $realFullResult.IsValid) {
    foreach ($finding in $realFullResult.Findings) {
        $failures.Add("FAIL (real overlay): $finding")
    }
}
else {
    Write-Host 'PASS: the real deploy/compose/docker-compose.full.yml wires the OIDC, object-storage and backplane services and the api/worker reference them'
}

# Spot-check that the real overlay gates the api on the supporting services (so they
# are up before the api starts) and that the worker shares the object storage.
AssertTrue ($realFullModel.Services['api'].DependsOn.ContainsKey('keycloak')) `
    'the real overlay api depends on the keycloak (OIDC) service'
AssertTrue ($realFullModel.Services['api'].DependsOn['rustfs-setup'] -eq 'service_completed_successfully') `
    'the real overlay api gates on the object-storage bucket setup completing'
AssertTrue ($realFullModel.Services['api'].DependsOn['valkey'] -eq 'service_healthy') `
    'the real overlay api waits for the backplane to be healthy'
AssertTrue ($realFullModel.Services['worker'].Environment['Assets__Storage__Endpoint'] -match 'rustfs') `
    'the real overlay worker references the object-storage endpoint'

# =============================================================================
# Production overlay (docker-compose.prod.yml, CORE-OPS-011)
# =============================================================================
# The base manifest defaults ASPNETCORE_ENVIRONMENT to Development so the bundled
# stack comes up green with no identity provider - but in Development the production
# readiness gate is inert and the OIDC audience guard does not trip, so copying the
# defaults to a server yields a green but unauthenticated API. The opt-in production
# overlay forces the Production posture on the api and worker so a production
# deployment cannot accidentally run in Development. Test-LiveCoreComposeProdOverlay
# statically validates that both services set ASPNETCORE_ENVIRONMENT to the literal
# "Production".

# A minimal, well-formed production-overlay fixture; each switch perturbs exactly
# one invariant (a service left in/defaulted to Development, or omitted).
function Get-FixtureProdOverlay {
    param(
        [switch]$ApiDefaultsDevelopment,
        [switch]$OmitWorker
    )

    $apiEnv = if ($ApiDefaultsDevelopment) {
        'ASPNETCORE_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:-Development}'
    }
    else {
        'ASPNETCORE_ENVIRONMENT: Production'
    }

    $worker = if ($OmitWorker) { '' } else { @'
  worker:
    environment:
      ASPNETCORE_ENVIRONMENT: Production
'@ }

    return @"
name: livecore
services:
  api:
    environment:
      $apiEnv
$worker
"@
}

# --- Parser + validator: the well-formed overlay fixture is valid. ---
$prodModel = Get-LiveCoreComposeModel -Content (Get-FixtureProdOverlay)
AssertTrue ($prodModel.Services['api'].Environment['ASPNETCORE_ENVIRONMENT'] -eq 'Production') `
    'the parser reads the api ASPNETCORE_ENVIRONMENT value from the production overlay'
AssertTrue ((Test-LiveCoreComposeProdOverlay -Model $prodModel).IsValid) `
    'a well-formed production overlay passes validation (api + worker forced to Production)'

# --- Negative: an api left on the interpolated Development default is rejected. ---
$prodApiDev = Get-LiveCoreComposeModel -Content (Get-FixtureProdOverlay -ApiDefaultsDevelopment)
$prodApiDevResult = Test-LiveCoreComposeProdOverlay -Model $prodApiDev
AssertTrue (-not $prodApiDevResult.IsValid) `
    'a production overlay whose api does not set the literal Production posture is rejected'
AssertTrue (($prodApiDevResult.Findings -join "`n") -match 'api.*Production') `
    'the rejection names the api service that is not forced to Production'

# --- Negative: an overlay missing the worker is rejected. ---
$prodNoWorker = Get-LiveCoreComposeModel -Content (Get-FixtureProdOverlay -OmitWorker)
$prodNoWorkerResult = Test-LiveCoreComposeProdOverlay -Model $prodNoWorker
AssertTrue (-not $prodNoWorkerResult.IsValid) 'a production overlay missing the worker service is rejected'
AssertTrue (($prodNoWorkerResult.Findings -join "`n") -match "worker") `
    'the rejection names the missing worker service'

# --- Guard the real checked-in overlay. ---
$prodComposePath = Join-Path $repoRoot 'deploy/compose/docker-compose.prod.yml'
AssertTrue (Test-Path -LiteralPath $prodComposePath) 'the deploy/compose/docker-compose.prod.yml overlay exists'
$realProdModel = Get-LiveCoreComposeModel -Path $prodComposePath
$realProdResult = Test-LiveCoreComposeProdOverlay -Model $realProdModel

if (-not $realProdResult.IsValid) {
    foreach ($finding in $realProdResult.Findings) {
        $failures.Add("FAIL (real prod overlay): $finding")
    }
}
else {
    Write-Host 'PASS: the real deploy/compose/docker-compose.prod.yml forces ASPNETCORE_ENVIRONMENT=Production on the api and worker'
}

# Spot-check the exact posture on the real overlay (the acceptance criterion: the
# overlay sets Production for both runtime services).
AssertTrue ($realProdModel.Services['api'].Environment['ASPNETCORE_ENVIRONMENT'] -eq 'Production') `
    'the real production overlay sets the api to ASPNETCORE_ENVIRONMENT=Production'
AssertTrue ($realProdModel.Services['worker'].Environment['ASPNETCORE_ENVIRONMENT'] -eq 'Production') `
    'the real production overlay sets the worker to ASPNETCORE_ENVIRONMENT=Production'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Compose deployment manifest tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Compose deployment manifest tests passed: the minimal stack wires the migrate-before-API gate, the postgres healthcheck, the documented probes and a container resource limit on every service (CORE-DEP-007), the full local stack overlay wires the OIDC/storage/backplane services with the api/worker referencing them, and the production overlay forces ASPNETCORE_ENVIRONMENT=Production on the api and worker.' -ForegroundColor Green
exit 0
