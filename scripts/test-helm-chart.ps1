#requires -Version 5.1

<#
.SYNOPSIS
    Tests the in-repo Kubernetes Helm chart (CORE-DEP-004).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreHelmChart.psm1 - no external test
    framework and no helm/kubeconform, so it runs as a CI gate and locally on both
    pwsh and Windows PowerShell 5.1. Exits 0 when every assertion holds and 1 when
    any fails.

    It proves the validation logic over in-memory fixtures (a chart that drops the
    pre-install migrate Job hook, a documented probe, or that hardcodes a secret is
    rejected) and then guards the real checked-in chart: deploy/helm/livecore must
    wire the migrate-before-API gate (a pre-install/pre-upgrade hook Job), the
    documented liveness/readiness probes, the ConfigMap/Secret externalization and
    NO baked secret. This is the "no-tooling" half of the smoke; the helm-chart CI
    job adds the real render half (helm lint + helm template + kubeconform schema
    validation).

.EXAMPLE
    pwsh -NoProfile -File scripts/test-helm-chart.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-helm-chart.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreHelmChart.psm1') -Force
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

# A minimal, well-formed chart fixture as an in-memory file map. Faithful enough to
# the real chart to exercise the analysis the same way; each switch perturbs exactly
# one invariant so the negative tests are independent.
function Get-FixtureHelmChart {
    param(
        [switch]$OmitMigrateHook,
        [switch]$OmitApiReadinessProbe,
        [switch]$HardcodeSecret,
        [switch]$DefaultMultiReplica,
        [switch]$OmitBackplaneGuard,
        [switch]$OmitResources
    )

    $migrateHook = if ($OmitMigrateHook) {
        '    "helm.sh/hook-weight": "-5"'
    }
    else {
        @'
    "helm.sh/hook": pre-install,pre-upgrade
    "helm.sh/hook-weight": "-5"
'@
    }

    $apiReadiness = if ($OmitApiReadinessProbe) { '' } else { @'
          readinessProbe:
            httpGet:
              path: /health/ready
              port: http
'@ }

    $secretConnString = if ($HardcodeSecret) {
        '  ConnectionStrings__Database: "Host=db;Username=livecore;Password=hunter2"'
    }
    else {
        '  ConnectionStrings__Database: ""'
    }

    # The default install must not silently run multiple API replicas on the
    # in-process backplane (CORE-DEP-009).
    $apiReplicaCount = if ($DefaultMultiReplica) { 2 } else { 1 }

    # The render-time guard that ties api.replicaCount > 1 to the realtime backplane.
    $backplaneGuard = if ($OmitBackplaneGuard) { '' } else { @'
{{- if and (gt (int .Values.api.replicaCount) 1) (not .Values.secrets.Realtime__Backplane__ConnectionString) }}
{{- fail "api.replicaCount > 1 requires Realtime__Backplane__ConnectionString" }}
{{- end }}
'@ }

    # The default resource ceilings (CORE-DEP-007/CORE-DEP-010): a non-empty
    # requests/limits cpu+memory block per component in values.yaml (indent 2, under
    # the top-level component key) and the matching `resources:` render in each
    # consuming template (indent 10, inside the container spec).
    $migrationsResources = if ($OmitResources) { '' } else { @'
  resources:
    requests:
      cpu: 250m
      memory: 256Mi
    limits:
      cpu: 500m
      memory: 512Mi
'@ }
    $apiResources = if ($OmitResources) { '' } else { @'
  resources:
    requests:
      cpu: 500m
      memory: 512Mi
    limits:
      cpu: 1000m
      memory: 1024Mi
'@ }
    $workerResources = if ($OmitResources) { '' } else { @'
  resources:
    requests:
      cpu: 250m
      memory: 384Mi
    limits:
      cpu: 750m
      memory: 768Mi
'@ }
    $resourcesTpl = if ($OmitResources) { '' } else { @'
          resources:
            requests:
              cpu: 250m
              memory: 256Mi
            limits:
              cpu: 500m
              memory: 512Mi
'@ }

    return @{
        'Chart.yaml'                       = "apiVersion: v2`nname: livecore`nversion: 0.1.0`n"
        'values.yaml'                      = @"
config:
  Authentication__Oidc__Authority: ""
  Cors__AllowedOrigins__0: ""
secrets:
  existingSecret: ""
$secretConnString
  Assets__Storage__AccessKeyId: ""
  Realtime__Backplane__ConnectionString: ""
migrations:
  enabled: true
$migrationsResources
api:
  replicaCount: $apiReplicaCount
$apiResources
worker:
  replicaCount: 1
$workerResources
"@
        'templates/migrate-job.yaml'       = @"
apiVersion: batch/v1
kind: Job
metadata:
  name: migrate
  annotations:
$migrateHook
spec:
  template:
    spec:
      containers:
        - name: migrate
          envFrom:
            - secretRef:
                name: livecore-secret
$resourcesTpl
"@
        'templates/api-deployment.yaml'    = @"
$backplaneGuard
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
        - name: api
          envFrom:
            - configMapRef:
                name: livecore-config
          livenessProbe:
            httpGet:
              path: /health/live
              port: http
$apiReadiness
$resourcesTpl
"@
        'templates/api-service.yaml'       = "apiVersion: v1`nkind: Service`n"
        'templates/worker-deployment.yaml' = @"
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
        - name: worker
          envFrom:
            - configMapRef:
                name: livecore-config
          livenessProbe:
            httpGet:
              path: /health/live
              port: metrics
$resourcesTpl
"@
        'templates/configmap.yaml'         = "apiVersion: v1`nkind: ConfigMap`n"
        'templates/secret.yaml'            = "apiVersion: v1`nkind: Secret`ntype: Opaque`nstringData:`n  {{- range `$k, `$v := omit .Values.secrets `"existingSecret`" }}`n  {{ `$k }}: {{ `$v | quote }}`n  {{- end }}`n"
        'templates/ingress.yaml'           = "{{- if .Values.ingress.enabled }}`napiVersion: networking.k8s.io/v1`nkind: Ingress`n{{- end }}`n"
    }
}

# --- Positive: the well-formed fixture passes. ---
$valid = Test-LiveCoreHelmChart -Files (Get-FixtureHelmChart)
AssertTrue ($valid.IsValid) 'a well-formed chart passes validation'

# --- Negative: dropping the pre-install/pre-upgrade migrate hook is rejected. ---
$noHook = Test-LiveCoreHelmChart -Files (Get-FixtureHelmChart -OmitMigrateHook)
AssertTrue (-not $noHook.IsValid) 'a chart whose migrate Job is not a pre-install/pre-upgrade hook is rejected'
AssertTrue (($noHook.Findings -join "`n") -match 'pre-install') `
    'the rejection names the missing migrate-before-API hook'

# --- Negative: a missing API readiness probe is rejected. ---
$noReady = Test-LiveCoreHelmChart -Files (Get-FixtureHelmChart -OmitApiReadinessProbe)
AssertTrue (-not $noReady.IsValid) 'a chart whose API has no readiness probe is rejected'
AssertTrue (($noReady.Findings -join "`n") -match '/health/ready') `
    'the rejection names the missing readiness probe'

# --- Negative: a hardcoded secret value is rejected. ---
$baked = Test-LiveCoreHelmChart -Files (Get-FixtureHelmChart -HardcodeSecret)
AssertTrue (-not $baked.IsValid) 'a chart with a non-empty default secret value is rejected'
AssertTrue (($baked.Findings -join "`n") -match 'HARDCODED SECRET') `
    'the rejection flags the hardcoded secret'

# --- Negative: defaulting to multiple API replicas (CORE-DEP-009) is rejected. ---
$multiReplica = Test-LiveCoreHelmChart -Files (Get-FixtureHelmChart -DefaultMultiReplica)
AssertTrue (-not $multiReplica.IsValid) 'a chart defaulting api.replicaCount above 1 (silent multi-replica on the in-process backplane) is rejected'
AssertTrue (($multiReplica.Findings -join "`n") -match 'REPLICAS') `
    'the rejection names the unsafe default api.replicaCount'

# --- Negative: dropping the multi-replica realtime-backplane guard is rejected. ---
$noGuard = Test-LiveCoreHelmChart -Files (Get-FixtureHelmChart -OmitBackplaneGuard)
AssertTrue (-not $noGuard.IsValid) 'a chart with no multi-replica realtime-backplane fail guard is rejected'
AssertTrue (($noGuard.Findings -join "`n") -match 'BACKPLANE GUARD') `
    'the rejection flags the missing backplane fail-safe guard'

# --- Negative: dropping the default resource requests/limits (CORE-DEP-010) is rejected. ---
$noResources = Test-LiveCoreHelmChart -Files (Get-FixtureHelmChart -OmitResources)
AssertTrue (-not $noResources.IsValid) 'a chart whose workloads ship no default resources.requests/limits ceiling is rejected'
AssertTrue (($noResources.Findings -join "`n") -match 'RESOURCES') `
    'the rejection flags the missing resource requests/limits ceiling'

# --- Negative: a missing required template is rejected. ---
$incomplete = Get-FixtureHelmChart
$incomplete.Remove('templates/worker-deployment.yaml')
$noWorker = Test-LiveCoreHelmChart -Files $incomplete
AssertTrue (-not $noWorker.IsValid) 'a chart missing the worker Deployment template is rejected'
AssertTrue (($noWorker.Findings -join "`n") -match "MISSING FILE: .*worker-deployment") `
    'the rejection names the missing worker Deployment template'

# --- Guard the real checked-in chart. ---
$chartPath = Join-Path $repoRoot 'deploy/helm/livecore'
AssertTrue (Test-Path -LiteralPath $chartPath) 'the deploy/helm/livecore chart directory exists'
$realFiles = Get-LiveCoreHelmChartFiles -Path $chartPath
$realResult = Test-LiveCoreHelmChart -Files $realFiles

if (-not $realResult.IsValid) {
    foreach ($finding in $realResult.Findings) {
        $failures.Add("FAIL (real chart): $finding")
    }
}
else {
    Write-Host 'PASS: the real deploy/helm/livecore chart wires the pre-install migrate Job, the probes, the ConfigMap/Secret externalization and bakes no secret'
}

# Spot-check the exact migrate hook on the real chart.
$realMigrate = $realFiles['templates/migrate-job.yaml']
AssertTrue ($realMigrate -match 'pre-install' -and $realMigrate -match 'pre-upgrade') `
    'the real migrate Job carries the pre-install,pre-upgrade hook (the migrate-before-API gate)'

# Spot-check no baked secret in the real values.yaml.
$realSecretValues = Get-LiveCoreHelmValuesSecretValue -ValuesContent $realFiles['values.yaml']
$realBaked = @($realSecretValues.Keys | Where-Object { -not [string]::IsNullOrEmpty($realSecretValues[$_]) })
AssertTrue ($realBaked.Count -eq 0) 'the real values.yaml bakes no secret (every secrets.* default is empty)'

# Spot-check the fail-safe multi-replica default and guard on the real chart (CORE-DEP-009).
$realReplicas = Get-LiveCoreHelmApiReplicaCount -ValuesContent $realFiles['values.yaml']
AssertTrue ($realReplicas -eq 1) 'the real chart defaults api.replicaCount to 1 (no silent multi-replica on the in-process backplane)'
$realApiDeployment = $realFiles['templates/api-deployment.yaml']
AssertTrue ($realApiDeployment -match 'livecore\.validateRealtimeBackplane') `
    'the real API Deployment invokes the realtime-backplane validation guard'
$realHelpers = $realFiles['templates/_helpers.tpl']
AssertTrue ($realHelpers -match 'fail' -and $realHelpers -match 'Realtime__Backplane__ConnectionString' -and $realHelpers -match 'replicaCount') `
    'the real chart helper fails the render for multi-replica without a backplane'

# Spot-check the default resource requests/limits ceiling on the real chart (CORE-DEP-007/CORE-DEP-010).
foreach ($component in @('migrations', 'api', 'worker')) {
    $realResources = Get-LiveCoreHelmComponentResources -ValuesContent $realFiles['values.yaml'] -Component $component
    $hasAll = @('requests.cpu', 'requests.memory', 'limits.cpu', 'limits.memory') |
        ForEach-Object { $realResources.ContainsKey($_) -and -not [string]::IsNullOrEmpty($realResources[$_]) }
    AssertTrue ((@($hasAll | Where-Object { -not $_ }).Count) -eq 0) `
        "the real chart ships a non-empty resources.requests+limits (cpu+memory) default for $component (CORE-DEP-007/CORE-DEP-010)"
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Helm chart tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Helm chart tests passed: the pre-install migrate Job, the documented probes, the ConfigMap/Secret externalization and the no-baked-secret rule are all wired as required.' -ForegroundColor Green
exit 0
