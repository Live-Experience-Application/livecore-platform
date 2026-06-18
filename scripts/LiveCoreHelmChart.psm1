#requires -Version 5.1

<#
.SYNOPSIS
    Validates the in-repo Kubernetes Helm chart (CORE-DEP-004).

.DESCRIPTION
    The Core ships a Helm chart at deploy/helm/livecore so an operator can deploy
    the Core runtime (API + worker + migrations runner) to Kubernetes - the third
    production option in docs/13_SELF_HOSTING_REQUIREMENTS.md. The chart MIRRORS the
    migrate-before-API contract the Docker Compose stack already enforces
    (deploy/compose/docker-compose.yml, CORE-DEP-001). Several pieces are easy to
    get wrong and would otherwise exist only as prose:

      1. the migrate-before-API gate, expressed as a pre-install/pre-upgrade Helm
         hook Job that must succeed before the API/worker roll out,
      2. the documented liveness/readiness probe endpoints, and
      3. all configuration externalized into a ConfigMap/Secret with NO baked secret.

    This module is the pure, no-tooling analysis behind the helm-chart smoke. It
    reads the chart's files into a small in-memory model and asserts the invariants
    the acceptance criteria require, so a chart that drops the pre-install migrate
    Job, a documented probe, the ConfigMap/Secret externalization, or that hardcodes
    a secret fails the build - WITHOUT needing helm/kubeconform installed. The
    helm-chart CI job adds the real render half (helm lint + helm template +
    kubeconform schema validation).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

function Get-LiveCoreHelmIndent {
    [CmdletBinding()]
    [OutputType([int])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line)

    $count = 0
    foreach ($ch in $Line.ToCharArray()) {
        if ($ch -eq ' ') { $count++ } else { break }
    }
    return $count
}

function Get-LiveCoreHelmChartFiles {
    <#
    .SYNOPSIS
        Reads every file under a chart directory into a hashtable.
    .OUTPUTS
        A hashtable of repo-relative-style path (forward slashes, relative to the
        chart root, e.g. 'templates/migrate-job.yaml') -> file content (string).
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Helm chart directory not found: $Path"
    }

    $root = (Resolve-Path -LiteralPath $Path).Path
    $files = @{}
    Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
        $files[$relative] = [System.IO.File]::ReadAllText($_.FullName)
    }
    return $files
}

function Get-LiveCoreHelmContentWithoutComments {
    <#
    .SYNOPSIS
        Returns chart text with full-line `#` comments removed, so an assertion can
        never be satisfied by a documentation comment alone (only by real content).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content)

    $kept = foreach ($line in ($Content -split "`r?`n")) {
        if ($line.TrimStart().StartsWith('#')) { continue }
        $line
    }
    return ($kept -join "`n")
}

function Get-LiveCoreHelmValuesSecretValue {
    <#
    .SYNOPSIS
        Extracts the scalar values nested under the top-level `secrets:` mapping of
        a values.yaml document, so the "no hardcoded secret" rule can be asserted.
    .OUTPUTS
        A hashtable of secret key -> raw scalar value (unquoted, trimmed). Nested
        maps (e.g. existingSecret blocks) are flattened by leaf key.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$ValuesContent)

    $result = @{}
    $lines = $ValuesContent -split "`r?`n"

    $inSecrets = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $indent = Get-LiveCoreHelmIndent -Line $line
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('#')) { continue }

        if (-not $inSecrets) {
            if ($indent -eq 0 -and $trimmed -match '^secrets:\s*$') { $inSecrets = $true }
            continue
        }

        # The secrets mapping ends at the next top-level (indent 0) key.
        if ($indent -eq 0) { break }

        # A "key: value" entry inside the secrets block. Capture the scalar value
        # (empty when the value is omitted). Strip a trailing inline comment.
        if ($trimmed -match '^([A-Za-z0-9_.:-]+):\s*(.*)$') {
            $key = $Matches[1]
            $value = $Matches[2]
            if ($value -match '^(.*?)\s+#.*$') { $value = $Matches[1] }
            $value = $value.Trim().Trim('"', "'")
            $result[$key] = $value
        }
    }
    return $result
}

function Test-LiveCoreHelmChart {
    <#
    .SYNOPSIS
        Asserts the chart invariants the acceptance criteria require.
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]). Findings is
        empty exactly when every invariant holds.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][hashtable]$Files)

    $findings = New-Object System.Collections.Generic.List[string]

    # 1. The required chart files are all present.
    $required = @(
        'Chart.yaml',
        'values.yaml',
        'templates/migrate-job.yaml',
        'templates/api-deployment.yaml',
        'templates/api-service.yaml',
        'templates/worker-deployment.yaml',
        'templates/configmap.yaml',
        'templates/secret.yaml',
        'templates/ingress.yaml'
    )
    foreach ($name in $required) {
        if (-not $Files.ContainsKey($name)) {
            $findings.Add("MISSING FILE: the chart does not contain '$name'")
        }
    }

    # 2. Chart.yaml declares the Helm v2 chart API.
    if ($Files.ContainsKey('Chart.yaml') -and $Files['Chart.yaml'] -notmatch '(?m)^\s*apiVersion:\s*v2\s*$') {
        $findings.Add("CHART: Chart.yaml must declare 'apiVersion: v2'")
    }

    # 3. THE migrate-before-API gate: the migrations runner is a pre-install AND
    #    pre-upgrade Helm hook Job, so the API rolls out only after it succeeds.
    if ($Files.ContainsKey('templates/migrate-job.yaml')) {
        $migrate = $Files['templates/migrate-job.yaml']
        if ($migrate -notmatch '(?m)^\s*kind:\s*Job\s*$') {
            $findings.Add('MIGRATE GATE: migrate-job.yaml must define a Job')
        }
        $hookLine = ''
        if ($migrate -match '(?m)helm\.sh/hook"?:\s*(.+)$') { $hookLine = $Matches[1] }
        if ($hookLine -notmatch 'pre-install' -or $hookLine -notmatch 'pre-upgrade') {
            $findings.Add("MIGRATE GATE: the migrate Job must carry the 'helm.sh/hook: pre-install,pre-upgrade' annotation so it runs to completion before the API/worker roll out")
        }
        if ($migrate -notmatch 'envFrom' -and $migrate -notmatch 'livecore\.envFrom') {
            $findings.Add('MIGRATE CONFIG: the migrate Job must project the configuration (envFrom) so it reads the same ConnectionStrings__Database the API/worker do')
        }
    }

    # 4. The documented probes are wired (api liveness + readiness, worker liveness).
    #    Comments are stripped first (so a doc comment can never satisfy a check),
    #    and the probe PATHS are searched in the template plus values.yaml together,
    #    because the chart drives the probe blocks from values (toYaml .Values.<c>.*).
    $valuesText = if ($Files.ContainsKey('values.yaml')) { Get-LiveCoreHelmContentWithoutComments -Content $Files['values.yaml'] } else { '' }
    if ($Files.ContainsKey('templates/api-deployment.yaml')) {
        $api = Get-LiveCoreHelmContentWithoutComments -Content $Files['templates/api-deployment.yaml']
        $apiCorpus = $api + "`n" + $valuesText
        if ($api -notmatch 'livenessProbe' -or $apiCorpus -notmatch '/health/live') {
            $findings.Add('PROBE: the API Deployment must wire a livenessProbe to /health/live')
        }
        if ($api -notmatch 'readinessProbe' -or $apiCorpus -notmatch '/health/ready') {
            $findings.Add('PROBE: the API Deployment must wire a readinessProbe to /health/ready')
        }
        if ($api -notmatch 'envFrom' -and $api -notmatch 'livecore\.envFrom') {
            $findings.Add('CONFIG: the API Deployment must project the ConfigMap/Secret configuration (envFrom)')
        }
    }
    if ($Files.ContainsKey('templates/worker-deployment.yaml')) {
        $worker = Get-LiveCoreHelmContentWithoutComments -Content $Files['templates/worker-deployment.yaml']
        $workerCorpus = $worker + "`n" + $valuesText
        if ($worker -notmatch 'livenessProbe' -or $workerCorpus -notmatch '/health/live') {
            $findings.Add('PROBE: the worker Deployment must wire a livenessProbe to /health/live (per-loop heartbeat liveness)')
        }
        if ($worker -notmatch 'envFrom' -and $worker -notmatch 'livecore\.envFrom') {
            $findings.Add('CONFIG: the worker Deployment must project the ConfigMap/Secret configuration (envFrom)')
        }
    }

    # 5. Configuration is externalized into a ConfigMap and a Secret.
    if ($Files.ContainsKey('templates/configmap.yaml') -and $Files['templates/configmap.yaml'] -notmatch '(?m)^\s*kind:\s*ConfigMap\s*$') {
        $findings.Add('CONFIG: configmap.yaml must define a ConfigMap')
    }
    if ($Files.ContainsKey('templates/secret.yaml')) {
        $secret = $Files['templates/secret.yaml']
        if ($secret -notmatch '(?m)^\s*kind:\s*Secret\s*$') {
            $findings.Add('CONFIG: secret.yaml must define a Secret')
        }
        # The Secret must render from .Values.secrets (templated), never literals.
        if ($secret -notmatch '\.Values\.secrets') {
            $findings.Add('SECRET: secret.yaml must render its data from .Values.secrets (externalized), not from literals')
        }
    }

    # 6. NO BAKED SECRET: every value under the values.yaml `secrets:` block is empty.
    if ($Files.ContainsKey('values.yaml')) {
        $secretValues = Get-LiveCoreHelmValuesSecretValue -ValuesContent $Files['values.yaml']
        if ($secretValues.Count -eq 0) {
            $findings.Add('SECRET: values.yaml has no `secrets:` block to externalize the credentials into a Secret')
        }
        foreach ($key in $secretValues.Keys) {
            if (-not [string]::IsNullOrEmpty($secretValues[$key])) {
                $findings.Add("HARDCODED SECRET: values.yaml secrets.$key has a non-empty default value; secret values must ship empty and be supplied at install time")
            }
        }
        # 7. The documented persistence key is externalized as a secret (reused from
        #    the Compose contract: ConnectionStrings__Database).
        if (-not $secretValues.ContainsKey('ConnectionStrings__Database')) {
            $findings.Add('CONTRACT: values.yaml secrets must include ConnectionStrings__Database (the documented persistence key reused from the Compose stack)')
        }
        # 8. The documented non-secret keys are reused (OIDC + CORS at least).
        $values = $Files['values.yaml']
        foreach ($configKey in @('Authentication__Oidc__Authority', 'Cors__AllowedOrigins__0')) {
            if ($values -notmatch [regex]::Escape($configKey)) {
                $findings.Add("CONTRACT: values.yaml should reuse the documented configuration key '$configKey'")
            }
        }
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreHelmIndent, `
    Get-LiveCoreHelmChartFiles, `
    Get-LiveCoreHelmContentWithoutComments, `
    Get-LiveCoreHelmValuesSecretValue, `
    Test-LiveCoreHelmChart
