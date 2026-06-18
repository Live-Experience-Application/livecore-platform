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

function Get-LiveCoreHelmApiReplicaCount {
    <#
    .SYNOPSIS
        Reads the default `api.replicaCount` scalar from a values.yaml document, so
        the "no silent multi-replica on the in-process backplane" rule (CORE-DEP-009)
        can be asserted over the default install.
    .OUTPUTS
        The integer default, or $null when the key is absent or unparseable.
    #>
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$ValuesContent)

    $lines = $ValuesContent -split "`r?`n"

    $inApi = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $indent = Get-LiveCoreHelmIndent -Line $line
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('#')) { continue }

        if (-not $inApi) {
            if ($indent -eq 0 -and $trimmed -match '^api:\s*$') { $inApi = $true }
            continue
        }

        # The api mapping ends at the next top-level (indent 0) key.
        if ($indent -eq 0) { break }

        if ($trimmed -match '^replicaCount:\s*([0-9]+)\s*(#.*)?$') {
            return [int]$Matches[1]
        }
    }
    return $null
}

function Get-LiveCoreHelmComponentResources {
    <#
    .SYNOPSIS
        Extracts the resources.requests/limits cpu/memory scalars of a top-level
        component (migrations/api/worker) from a values.yaml document, so the
        "every workload ships a request AND a limit ceiling" rule (CORE-DEP-007 /
        CORE-DEP-010) can be asserted over the default install.
    .OUTPUTS
        A hashtable keyed 'requests.cpu','requests.memory','limits.cpu',
        'limits.memory' -> raw scalar value (unquoted, trimmed). Keys absent from the
        document are absent from the hashtable.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$ValuesContent,
        [Parameter(Mandatory = $true)][string]$Component
    )

    $result = @{}
    $lines = $ValuesContent -split "`r?`n"

    $inComponent = $false
    $resourcesIndent = -1
    $section = ''
    $sectionIndent = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $indent = Get-LiveCoreHelmIndent -Line $line
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('#')) { continue }

        if (-not $inComponent) {
            if ($indent -eq 0 -and $trimmed -match "^$([regex]::Escape($Component)):\s*$") { $inComponent = $true }
            continue
        }

        # The component mapping ends at the next top-level (indent 0) key.
        if ($indent -eq 0) { break }

        # Walk forward (skipping the component's other keys and their nested blocks)
        # until the `resources:` mapping opens.
        if ($resourcesIndent -lt 0) {
            if ($trimmed -match '^resources:\s*(.*)$' -and ($indent -gt 0)) { $resourcesIndent = $indent }
            continue
        }

        # The resources block ends when we dedent back to (or above) its own indent.
        if ($indent -le $resourcesIndent) { break }

        # A requests:/limits: subsection header.
        if ($trimmed -match '^(requests|limits):\s*$') {
            $section = $Matches[1]
            $sectionIndent = $indent
            continue
        }

        # A cpu:/memory: leaf inside the current subsection.
        if ($section -and $indent -gt $sectionIndent -and $trimmed -match '^(cpu|memory):\s*(.*)$') {
            $field = $Matches[1]
            $value = $Matches[2]
            if ($value -match '^(.*?)\s+#.*$') { $value = $Matches[1] }
            $value = $value.Trim().Trim('"', "'")
            $result["$section.$field"] = $value
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

        # 9. CORE-DEP-009: the chart fails safe when multiple API replicas run without
        #    a realtime backplane. SignalR tracks hub-group membership per-process, so
        #    multiple API pods on the in-process backplane silently drop cross-pod
        #    realtime delivery (CORE-OPS-007). Two invariants together prevent that:
        #    (a) the DEFAULT api.replicaCount must not exceed 1, so a default install
        #        never silently runs multiple pods on the in-process backplane; and
        $apiReplicas = Get-LiveCoreHelmApiReplicaCount -ValuesContent $Files['values.yaml']
        if ($null -eq $apiReplicas) {
            $findings.Add('REPLICAS: values.yaml must set a default api.replicaCount')
        }
        elseif ($apiReplicas -gt 1) {
            $findings.Add("REPLICAS: the default api.replicaCount is $apiReplicas; a default install must not run multiple API replicas on the in-process backplane - default it to 1 and require a backplane to scale up (CORE-DEP-009)")
        }
    }

    # 10. CORE-DEP-007 / CORE-DEP-010: every workload ships a non-empty resource
    #     ceiling by default, so the resource-ceiling guarantee holds on the
    #     Kubernetes path and a runaway pod cannot starve the node (the exact failure
    #     CORE-DEP-007 prevents on Compose). Two invariants together prove it:
    #     (a) values.yaml sets a non-empty resources.requests AND resources.limits
    #         (cpu + memory) default for the migrate Job, the API and the worker; and
    if ($Files.ContainsKey('values.yaml')) {
        foreach ($component in @('migrations', 'api', 'worker')) {
            $res = Get-LiveCoreHelmComponentResources -ValuesContent $Files['values.yaml'] -Component $component
            foreach ($field in @('requests.cpu', 'requests.memory', 'limits.cpu', 'limits.memory')) {
                if (-not $res.ContainsKey($field) -or [string]::IsNullOrEmpty($res[$field])) {
                    $kind = $field.Split('.')[0]
                    $findings.Add("RESOURCES: values.yaml $component.resources.$field must ship a non-empty default so the rendered $component Pod carries a resource $kind ceiling (CORE-DEP-007/CORE-DEP-010)")
                }
            }
        }
    }

    # (b) the consuming templates must render the ceiling from .Values, so the default
    #     value actually reaches the rendered Pod spec.
    $resourceTemplates = @(
        'templates/migrate-job.yaml',
        'templates/api-deployment.yaml',
        'templates/worker-deployment.yaml'
    )
    foreach ($tplName in $resourceTemplates) {
        if ($Files.ContainsKey($tplName)) {
            $tpl = Get-LiveCoreHelmContentWithoutComments -Content $Files[$tplName]
            if ($tpl -notmatch '(?m)^\s*resources:' -and $tpl -notmatch '\.resources') {
                $findings.Add("RESOURCES: $tplName must render the resource requests/limits ceiling from .Values (CORE-DEP-010)")
            }
        }
    }

    # (b) a render-time guard must tie api.replicaCount > 1 to the realtime backplane
    #     connection string - a `fail` referencing both - so scaling up without a
    #     backplane is rejected instead of silently shipping broken realtime. The guard
    #     can live in any template (a helper invoked from a Deployment, or inline), so
    #     search the whole template corpus.
    $templateCorpus = ($Files.GetEnumerator() | Where-Object { $_.Key -like 'templates/*' } | ForEach-Object { $_.Value }) -join "`n"
    $hasFailGuard = $templateCorpus -match 'fail\s*[("]'
    $tiesReplicasToBackplane = ($templateCorpus -match 'replicaCount') -and ($templateCorpus -match 'Realtime__Backplane__ConnectionString')
    if (-not ($hasFailGuard -and $tiesReplicasToBackplane)) {
        $findings.Add('BACKPLANE GUARD: the chart must fail the render when api.replicaCount > 1 without a realtime backplane - a guard tying api.replicaCount to Realtime__Backplane__ConnectionString via `fail` (CORE-DEP-009)')
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
    Get-LiveCoreHelmApiReplicaCount, `
    Get-LiveCoreHelmComponentResources, `
    Test-LiveCoreHelmChart
