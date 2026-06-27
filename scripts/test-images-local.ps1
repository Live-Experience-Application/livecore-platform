#requires -Version 5.1

<#
.SYNOPSIS
    Tests the images:local local-image convenience script gate (CORE-DXL-001).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreImagesLocal.psm1 - no external test
    framework and no Docker, so it runs as a CI gate and locally on both pwsh and
    Windows PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

    It proves the validation logic over fixtures (a missing/partial/non-additive
    `images:local` script, a base manifest whose service no longer tags the stable
    :local image, and a README missing a piece of the local-consume contract are
    each rejected) and then guards the REAL checked-in artifacts:

      - the root package.json defines a well-formed, additive `images:local` script;
      - deploy/compose/docker-compose.yml still builds migrate/api/worker from the
        expected Dockerfiles to the livecore-{migrations,api,worker}:local tags; and
      - deploy/compose/README.md documents the local-consume contract.

    This is the no-Docker "validates" half of the smoke; the images-local-smoke CI
    job adds the real "builds + runs" half (the script produces the three :local
    images, a re-run is idempotent, and the api answers /health/ready from the
    :local image).

.EXAMPLE
    pwsh -NoProfile -File scripts/test-images-local.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-images-local.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
# The compose-manifest parser (Get-LiveCoreComposeModel) lives in the deploy module;
# reuse it so manifest parsing stays in one place.
Import-Module (Join-Path $scriptDir 'LiveCoreComposeDeploy.psm1') -Force
Import-Module (Join-Path $scriptDir 'LiveCoreImagesLocal.psm1') -Force
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

# A minimal, well-formed base-manifest fixture: postgres (no build) plus the three
# Core services building from their Dockerfiles to the stable :local tags. Each
# switch perturbs exactly one invariant so a negative test isolates one finding.
function Get-FixtureBaseCompose {
    param(
        [string]$ApiImage = 'livecore-api:local',
        [switch]$OmitWorker,
        [switch]$WorkerOmitBuild
    )

    $worker = if ($OmitWorker) { '' }
    elseif ($WorkerOmitBuild) { @'
  worker:
    image: livecore-worker:local
'@ }
    else { @'
  worker:
    build:
      context: ../..
      dockerfile: apps/worker/Dockerfile
    image: livecore-worker:local
'@ }

    return @"
name: livecore
services:
  postgres:
    image: postgres:17
  migrate:
    build:
      context: ../..
      dockerfile: apps/api/Migrations.Dockerfile
    image: livecore-migrations:local
  api:
    build:
      context: ../..
      dockerfile: apps/api/Dockerfile
    image: $ApiImage
$worker
volumes:
  livecore-postgres-data:
"@
}

# =============================================================================
# Test-LiveCoreImagesLocalScript - the package.json convenience script
# =============================================================================

# --- The real-shaped command is well-formed (names all three services). ---
$goodNamed = 'docker compose -f deploy/compose/docker-compose.yml build migrate api worker'
AssertTrue (Test-LiveCoreImagesLocalScript -Command $goodNamed).IsValid `
    'a docker compose build of all three Core services via the base manifest is valid'

# --- A bare `build` (no services) builds every build: service - also valid. ---
$goodBare = 'docker compose -f deploy/compose/docker-compose.yml build'
AssertTrue (Test-LiveCoreImagesLocalScript -Command $goodBare).IsValid `
    'a bare compose build (which builds every service that has a build: stanza) is valid'

# --- Negative: a missing script is rejected. ---
$emptyResult = Test-LiveCoreImagesLocalScript -Command ''
AssertTrue (-not $emptyResult.IsValid) 'a missing images:local script is rejected'
AssertTrue (($emptyResult.Findings -join "`n") -match 'MISSING') 'the rejection names the missing script'

# --- Negative: a command that does not invoke compose is rejected. ---
$noCompose = Test-LiveCoreImagesLocalScript -Command 'docker build -f apps/api/Dockerfile -t livecore-api:local .'
AssertTrue (-not $noCompose.IsValid) 'an images:local script that does not invoke Docker Compose is rejected'
AssertTrue (($noCompose.Findings -join "`n") -match 'COMPOSE') 'the rejection names the missing compose invocation'

# --- Negative: building via the wrong manifest is rejected. ---
$wrongManifest = Test-LiveCoreImagesLocalScript -Command 'docker compose -f deploy/compose/docker-compose.prod.yml build migrate api worker'
AssertTrue (-not $wrongManifest.IsValid) 'an images:local script that builds via the wrong manifest is rejected'
AssertTrue (($wrongManifest.Findings -join "`n") -match 'MANIFEST') 'the rejection names the required base manifest'

# --- Negative: not running the build subcommand is rejected. ---
$noBuild = Test-LiveCoreImagesLocalScript -Command 'docker compose -f deploy/compose/docker-compose.yml up -d'
AssertTrue (-not $noBuild.IsValid) 'an images:local script that does not run the compose build subcommand is rejected'
AssertTrue (($noBuild.Findings -join "`n") -match 'BUILD') 'the rejection names the missing build subcommand'

# --- Negative: covering only a subset of the three Core services is rejected. ---
$partial = Test-LiveCoreImagesLocalScript -Command 'docker compose -f deploy/compose/docker-compose.yml build api'
AssertTrue (-not $partial.IsValid) 'an images:local script that builds only a subset of the Core services is rejected'
AssertTrue (($partial.Findings -join "`n") -match 'SERVICES') 'the rejection names the omitted services'

# --- Negative: a script that pushes/publishes is rejected (additive, fail-closed). ---
$pushes = Test-LiveCoreImagesLocalScript -Command 'docker compose -f deploy/compose/docker-compose.yml build migrate api worker && docker compose -f deploy/compose/docker-compose.yml push'
AssertTrue (-not $pushes.IsValid) 'an images:local script that pushes/publishes is rejected (it must stay additive)'
AssertTrue (($pushes.Findings -join "`n") -match 'ADDITIVE') 'the rejection flags the non-additive push/publish'

# =============================================================================
# Test-LiveCoreImagesLocalTags - the base manifest builds the :local tags
# =============================================================================

# --- The well-formed fixture builds all three services to the :local tags. ---
$goodModel = Get-LiveCoreComposeModel -Content (Get-FixtureBaseCompose)
AssertTrue (Test-LiveCoreImagesLocalTags -Model $goodModel).IsValid `
    'a base manifest that builds migrate/api/worker to the :local tags from the expected Dockerfiles is valid'

# --- Negative: a service that tags a non-:local image is rejected. ---
$wrongTagModel = Get-LiveCoreComposeModel -Content (Get-FixtureBaseCompose -ApiImage 'livecore-api:latest')
$wrongTagResult = Test-LiveCoreImagesLocalTags -Model $wrongTagModel
AssertTrue (-not $wrongTagResult.IsValid) 'a base manifest whose api does not tag the stable :local image is rejected'
AssertTrue (($wrongTagResult.Findings -join "`n") -match 'TAG') 'the rejection names the wrong :local tag'

# --- Negative: a missing build service is rejected. ---
$noWorkerModel = Get-LiveCoreComposeModel -Content (Get-FixtureBaseCompose -OmitWorker)
$noWorkerResult = Test-LiveCoreImagesLocalTags -Model $noWorkerModel
AssertTrue (-not $noWorkerResult.IsValid) 'a base manifest missing the worker build service is rejected'
AssertTrue (($noWorkerResult.Findings -join "`n") -match "MISSING SERVICE: .*'worker'") 'the rejection names the missing worker service'

# --- Negative: a service that pulls instead of building is rejected. ---
$noBuildModel = Get-LiveCoreComposeModel -Content (Get-FixtureBaseCompose -WorkerOmitBuild)
$noBuildResult = Test-LiveCoreImagesLocalTags -Model $noBuildModel
AssertTrue (-not $noBuildResult.IsValid) 'a base manifest whose worker has no build: stanza is rejected (images:local must build it from source)'
AssertTrue (($noBuildResult.Findings -join "`n") -match "BUILD: .*'worker'") 'the rejection names the worker that does not build from source'

# =============================================================================
# Test-LiveCoreImagesLocalDocs - the README documents the local-consume contract
# =============================================================================

$goodReadme = @'
Run `pnpm run images:local` to build livecore-api:local, livecore-worker:local and
livecore-migrations:local. A downstream vertical can point its own test harness at
those :local tags to run an unreleased Core without a registry publish.
'@
AssertTrue (Test-LiveCoreImagesLocalDocs -ReadmeText $goodReadme).IsValid `
    'a README naming the script, the three :local tags and the no-registry-publish harness use is valid'

$missingTagReadme = @'
Run `pnpm run images:local` to build the api and worker images. A downstream
vertical can point its test harness at the tags without a registry publish.
'@
$missingTagResult = Test-LiveCoreImagesLocalDocs -ReadmeText $missingTagReadme
AssertTrue (-not $missingTagResult.IsValid) 'a README that omits a :local tag is rejected'
AssertTrue (($missingTagResult.Findings -join "`n") -match 'livecore-migrations:local') 'the rejection names the missing migrations tag'

# =============================================================================
# Guard the REAL checked-in artifacts.
# =============================================================================

# 1. The root package.json defines a well-formed, additive images:local script.
$packageJsonPath = Join-Path $repoRoot 'package.json'
AssertTrue (Test-Path -LiteralPath $packageJsonPath) 'the root package.json exists'
$imagesLocalCommand = Get-LiveCoreRootScript -Path $packageJsonPath -Name 'images:local'
AssertTrue ($null -ne $imagesLocalCommand) "the root package.json defines an 'images:local' script"
$realScriptResult = Test-LiveCoreImagesLocalScript -Command $imagesLocalCommand
if (-not $realScriptResult.IsValid) {
    foreach ($finding in $realScriptResult.Findings) { $failures.Add("FAIL (real images:local script): $finding") }
}
else {
    Write-Host 'PASS: the real images:local script builds the three Core services from source via the base manifest and is additive'
}

# 2. The base manifest still builds the three services to the stable :local tags.
$composePath = Join-Path $repoRoot 'deploy/compose/docker-compose.yml'
AssertTrue (Test-Path -LiteralPath $composePath) 'the deploy/compose/docker-compose.yml manifest exists'
$realModel = Get-LiveCoreComposeModel -Path $composePath
$realTagsResult = Test-LiveCoreImagesLocalTags -Model $realModel
if (-not $realTagsResult.IsValid) {
    foreach ($finding in $realTagsResult.Findings) { $failures.Add("FAIL (real manifest): $finding") }
}
else {
    Write-Host 'PASS: the real base manifest builds migrate/api/worker from the expected Dockerfiles to the livecore-{migrations,api,worker}:local tags'
}

# Spot-check the exact :local tags on the real manifest (the acceptance criterion).
AssertTrue ($realModel.Services['api'].Image -eq 'livecore-api:local') 'the real api service builds the livecore-api:local tag'
AssertTrue ($realModel.Services['worker'].Image -eq 'livecore-worker:local') 'the real worker service builds the livecore-worker:local tag'
AssertTrue ($realModel.Services['migrate'].Image -eq 'livecore-migrations:local') 'the real migrate service builds the livecore-migrations:local tag'

# 3. deploy/compose/README.md documents the local-consume contract.
$readmePath = Join-Path $repoRoot 'deploy/compose/README.md'
AssertTrue (Test-Path -LiteralPath $readmePath) 'the deploy/compose/README.md exists'
$realReadme = [System.IO.File]::ReadAllText($readmePath)
$realDocsResult = Test-LiveCoreImagesLocalDocs -ReadmeText $realReadme
if (-not $realDocsResult.IsValid) {
    foreach ($finding in $realDocsResult.Findings) { $failures.Add("FAIL (real README): $finding") }
}
else {
    Write-Host 'PASS: the real deploy/compose/README.md documents the images:local script, the three :local tags and the no-registry-publish harness use'
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "images:local convenience script tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'images:local convenience script tests passed: the root package.json builds the three Core services from source via the base manifest to the stable :local tags (additive - no push/publish/release), the base manifest still wires those build stanzas and tags, and deploy/compose/README.md documents the local-consume contract (CORE-DXL-001).' -ForegroundColor Green
exit 0
