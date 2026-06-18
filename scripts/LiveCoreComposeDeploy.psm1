#requires -Version 5.1

<#
.SYNOPSIS
    Validates the in-repo Docker Compose deployment manifest (CORE-DEP-001).

.DESCRIPTION
    The Core ships a runnable deployment stack at deploy/compose/docker-compose.yml
    so an operator can deploy from this repository alone (docs/13_SELF_HOSTING_
    REQUIREMENTS.md). Two pieces are easy to get wrong and used to exist only as
    prose:

      1. the migrate-before-API ordering, expressed as
         `depends_on: { migrate: { condition: service_completed_successfully } }`
         on the api and worker services, and
      2. the documented health/readiness/liveness probe endpoints.

    This module is the pure analysis behind the deployment smoke. It parses the
    compose manifest's predictable, hand-authored shape (indentation-aware, no
    external YAML dependency, so it runs anywhere with no Docker) into a small
    model and asserts the invariants the acceptance criteria require, so a manifest
    that drops the migrate gate, the postgres healthcheck dependency, a required
    service or a documented probe fails the build.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

function Get-LiveCoreComposeIndent {
    [CmdletBinding()]
    [OutputType([int])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line)

    $count = 0
    foreach ($ch in $Line.ToCharArray()) {
        if ($ch -eq ' ') { $count++ } else { break }
    }
    return $count
}

function Get-LiveCoreComposeModel {
    <#
    .SYNOPSIS
        Parses a compose manifest (from a path or literal content) into a model.
    .OUTPUTS
        A PSCustomObject with a RawText string and a Services hashtable keyed by
        service name. Each service value carries: Name, Image, Dockerfile,
        HasHealthcheck (bool), DependsOn (hashtable serviceName -> condition) and
        EnvironmentKeys (string[]).
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
        [string]$Path,

        [Parameter(Mandatory = $true, ParameterSetName = 'Content')]
        [AllowEmptyString()]
        [string]$Content
    )

    if ($PSCmdlet.ParameterSetName -eq 'Path') {
        if (-not (Test-Path -LiteralPath $Path)) {
            throw "Compose manifest not found: $Path"
        }
        $rawText = [System.IO.File]::ReadAllText($Path)
    }
    else {
        $rawText = $Content
    }

    $lines = $rawText -split "`r?`n"
    $services = @{}

    # Locate the `services:` mapping (indent 0) and the index where it ends (the
    # next indent-0 key, e.g. `volumes:`), so only service definitions are parsed.
    $servicesStart = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^services:\s*$') { $servicesStart = $i; break }
    }

    if ($servicesStart -ge 0) {
        $servicesEnd = $lines.Count
        for ($i = $servicesStart + 1; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ((Get-LiveCoreComposeIndent -Line $line) -eq 0) { $servicesEnd = $i; break }
        }

        # Service headers are at indent 2 (`  name:`). A service block runs until
        # the next indent-2 header or the end of the services mapping.
        $headerIndexes = New-Object System.Collections.Generic.List[int]
        for ($i = $servicesStart + 1; $i -lt $servicesEnd; $i++) {
            if ($lines[$i] -match '^  ([A-Za-z0-9_.-]+):\s*$') {
                $headerIndexes.Add($i)
            }
        }

        for ($h = 0; $h -lt $headerIndexes.Count; $h++) {
            $start = $headerIndexes[$h]
            $end = if ($h + 1 -lt $headerIndexes.Count) { $headerIndexes[$h + 1] } else { $servicesEnd }
            $null = $lines[$start] -match '^  ([A-Za-z0-9_.-]+):\s*$'
            $name = $Matches[1]
            $block = $lines[$start..($end - 1)]
            $services[$name] = Get-LiveCoreComposeServiceModel -Name $name -BlockLine $block
        }
    }

    return [pscustomobject]@{
        RawText  = $rawText
        Services = $services
    }
}

function Get-LiveCoreComposeServiceModel {
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$BlockLine
    )

    $image = ''
    $dockerfile = ''
    $hasHealthcheck = $false
    $dependsOn = @{}
    $environmentKeys = New-Object System.Collections.Generic.List[string]
    $environment = @{}
    $resourceLimits = @{}

    for ($i = 0; $i -lt $BlockLine.Count; $i++) {
        $line = $BlockLine[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $indent = Get-LiveCoreComposeIndent -Line $line
        $trimmed = $line.Trim()

        if ($indent -eq 4 -and $trimmed -match '^image:\s*(.+?)\s*$') {
            $image = $Matches[1]
            continue
        }
        if ($trimmed -match '^dockerfile:\s*(.+?)\s*$') {
            $dockerfile = $Matches[1]
            continue
        }
        if ($indent -eq 4 -and $trimmed -eq 'healthcheck:') {
            $hasHealthcheck = $true
            continue
        }
        if ($indent -eq 4 -and $trimmed -eq 'depends_on:') {
            $dependsOn = Get-LiveCoreComposeDependsOn -BlockLine $BlockLine -StartIndex ($i + 1)
            continue
        }
        if ($indent -eq 4 -and $trimmed -eq 'deploy:') {
            $resourceLimits = Get-LiveCoreComposeResourceLimits -BlockLine $BlockLine -StartIndex ($i + 1)
            continue
        }
        if ($indent -eq 4 -and $trimmed -eq 'environment:') {
            for ($j = $i + 1; $j -lt $BlockLine.Count; $j++) {
                $envLine = $BlockLine[$j]
                if ([string]::IsNullOrWhiteSpace($envLine)) { continue }
                if ((Get-LiveCoreComposeIndent -Line $envLine) -le 4) { break }
                $envTrim = $envLine.Trim()
                # Capture the key AND its value (a service may set an env value to a
                # ${VAR:-default} reference to another service, which the full-stack
                # wiring check inspects). Both the list form (`- KEY=value`) and the
                # mapping form (`KEY: value`) are supported.
                if ($envTrim -match '^-\s*([A-Za-z0-9_.:-]+)=(.*)$') {
                    $environmentKeys.Add($Matches[1]); $environment[$Matches[1]] = $Matches[2].Trim()
                }
                elseif ($envTrim -match '^([A-Za-z0-9_.:-]+):\s*(.*)$') {
                    $environmentKeys.Add($Matches[1]); $environment[$Matches[1]] = $Matches[2].Trim()
                }
            }
            continue
        }
    }

    return [pscustomobject]@{
        Name             = $Name
        Image            = $image
        Dockerfile       = $dockerfile
        HasHealthcheck   = $hasHealthcheck
        DependsOn        = $dependsOn
        EnvironmentKeys  = $environmentKeys.ToArray()
        Environment      = $environment
        ResourceLimits   = $resourceLimits
        HasResourceLimit = ($resourceLimits.Count -gt 0)
    }
}

function Get-LiveCoreComposeResourceLimits {
    # Parses the `deploy: > resources: > limits:` mapping (cpus/memory) that begins
    # at $StartIndex (the line after `deploy:`). `resources:` is at indent 6,
    # `limits:` at indent 8 and the `cpus:`/`memory:` ceilings at indent 10. The
    # deploy block ends when indentation returns to <= 4. A sibling of `limits:` at
    # the same indent (e.g. `reservations:`) closes the limits sub-block, so only the
    # hard ceilings are captured.
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$BlockLine,
        [Parameter(Mandatory = $true)][int]$StartIndex
    )

    $result = @{}
    $inLimits = $false
    for ($i = $StartIndex; $i -lt $BlockLine.Count; $i++) {
        $line = $BlockLine[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $indent = Get-LiveCoreComposeIndent -Line $line
        if ($indent -le 4) { break }
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('#')) { continue }
        if ($trimmed -eq 'limits:') { $inLimits = $true; continue }
        if ($inLimits -and $indent -le 8) { $inLimits = $false }
        if ($inLimits -and $trimmed -match '^(cpus|memory):\s*(.+?)\s*$') {
            # Strip surrounding quotes from the value (cpus is usually quoted).
            $result[$Matches[1]] = $Matches[2].Trim('"', "'")
        }
    }
    return $result
}

function Get-LiveCoreComposeDependsOn {
    # Parses the long-form `depends_on:` mapping (service -> condition) that begins
    # at $StartIndex. Each dependency is at indent 6 (`      name:`) and carries a
    # `condition:` at indent 8. The mapping ends when indentation returns to <= 4.
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$BlockLine,
        [Parameter(Mandatory = $true)][int]$StartIndex
    )

    $result = @{}
    $current = $null
    for ($i = $StartIndex; $i -lt $BlockLine.Count; $i++) {
        $line = $BlockLine[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $indent = Get-LiveCoreComposeIndent -Line $line
        if ($indent -le 4) { break }
        $trimmed = $line.Trim()
        if ($indent -eq 6 -and $trimmed -match '^([A-Za-z0-9_.-]+):\s*$') {
            $current = $Matches[1]
            if (-not $result.ContainsKey($current)) { $result[$current] = '' }
        }
        elseif ($indent -ge 8 -and $current -and $trimmed -match '^condition:\s*(.+?)\s*$') {
            $result[$current] = $Matches[1]
        }
    }
    return $result
}

function Test-LiveCoreComposeDeployment {
    <#
    .SYNOPSIS
        Asserts the deployment invariants the acceptance criteria require.
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]). Findings is
        empty exactly when every invariant holds.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][psobject]$Model)

    $findings = New-Object System.Collections.Generic.List[string]
    $services = $Model.Services

    # 1. The required services are all present.
    $required = @('postgres', 'migrate', 'api', 'worker')
    foreach ($name in $required) {
        if (-not $services.ContainsKey($name)) {
            $findings.Add("MISSING SERVICE: the manifest does not define a '$name' service")
        }
    }

    # 2. PostgreSQL exposes a healthcheck (the dependency conditions build on it).
    if ($services.ContainsKey('postgres') -and -not $services['postgres'].HasHealthcheck) {
        $findings.Add('POSTGRES: the postgres service has no healthcheck for the dependency gates to wait on')
    }

    # 3. migrate waits for postgres to be healthy before applying the schema.
    if ($services.ContainsKey('migrate')) {
        if ($services['migrate'].DependsOn['postgres'] -ne 'service_healthy') {
            $findings.Add("MIGRATE GATE: migrate must depend on postgres with condition 'service_healthy'")
        }
    }

    # 4. THE migrate-before-API gate: api and worker start only after migrate
    #    completes (and after postgres is healthy).
    foreach ($name in @('api', 'worker')) {
        if (-not $services.ContainsKey($name)) { continue }
        $dep = $services[$name].DependsOn
        if ($dep['migrate'] -ne 'service_completed_successfully') {
            $findings.Add("MIGRATE GATE: $name must depend on migrate with condition 'service_completed_successfully' so it cannot start until migrations complete")
        }
        if ($dep['postgres'] -ne 'service_healthy') {
            $findings.Add("DEPENDENCY: $name must depend on postgres with condition 'service_healthy'")
        }
    }

    # 5. api and worker carry the connection string the migrations runner also uses.
    foreach ($name in @('api', 'worker', 'migrate')) {
        if (-not $services.ContainsKey($name)) { continue }
        if ($services[$name].EnvironmentKeys -notcontains 'ConnectionStrings__Database') {
            $findings.Add("CONFIG: $name must set ConnectionStrings__Database (the documented persistence key)")
        }
    }

    # 6. migrate/api/worker build from the in-repo Dockerfiles, so the stack is
    #    deployable from this repository alone.
    $expectedDockerfile = @{
        migrate = 'apps/api/Migrations.Dockerfile'
        api     = 'apps/api/Dockerfile'
        worker  = 'apps/worker/Dockerfile'
    }
    foreach ($name in $expectedDockerfile.Keys) {
        if (-not $services.ContainsKey($name)) { continue }
        if ($services[$name].Dockerfile -ne $expectedDockerfile[$name]) {
            $findings.Add("BUILD: $name must build from $($expectedDockerfile[$name]) (found '$($services[$name].Dockerfile)')")
        }
    }

    # 7. The documented health/readiness/liveness probe endpoints appear in the
    #    manifest (api liveness+readiness, worker per-loop liveness + metrics).
    $expectedProbe = @('/health/live', '/health/ready', '/metrics')
    foreach ($probe in $expectedProbe) {
        if ($Model.RawText -notmatch [regex]::Escape($probe)) {
            $findings.Add("PROBE: the documented endpoint '$probe' is not referenced in the manifest")
        }
    }

    # 8. Every service declares a container resource limit (CORE-DEP-007) so an
    #    unbounded process cannot starve a single-VPS host. The defaults are
    #    overridable via env; here we only assert each service carries a ceiling.
    foreach ($name in $required) {
        if (-not $services.ContainsKey($name)) { continue }
        if (-not $services[$name].HasResourceLimit) {
            $findings.Add("RESOURCE LIMIT: the '$name' service declares no deploy.resources.limits (cpus/memory) - an unbounded process can starve a single-VPS host (CORE-DEP-007)")
        }
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

function Add-LiveCoreFullStackReferenceFinding {
    <#
    .SYNOPSIS
        Adds a finding when a service does not wire a configuration key to the
        expected supporting service (CORE-DEP-006).
    .DESCRIPTION
        A pure helper for Test-LiveCoreComposeFullStack: the api/worker must SET the
        OIDC/storage/backplane key AND its value must REFERENCE the supporting
        service (so the wiring actually points at keycloak/rustfs/valkey rather than
        being declared empty). Appends a finding to $Findings when either is missing.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][psobject]$Service,
        [Parameter(Mandatory = $true)][string]$ServiceLabel,
        [Parameter(Mandatory = $true)][string]$Key,
        [AllowNull()][AllowEmptyString()][string]$Target,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$Findings
    )

    $value = $null
    if ($Service.Environment -and $Service.Environment.ContainsKey($Key)) {
        $value = $Service.Environment[$Key]
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        $Findings.Add("WIRING: the '$ServiceLabel' service must set '$Key' to reference the supporting service")
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($Target) -and ($value -notmatch [regex]::Escape($Target))) {
        $Findings.Add("WIRING: the '$ServiceLabel' service's '$Key' ('$value') must reference the '$Target' service")
    }
}

function Test-LiveCoreComposeFullStack {
    <#
    .SYNOPSIS
        Asserts the full local stack overlay wires OIDC, object storage and the
        realtime backplane and that the api/worker reference them (CORE-DEP-006).
    .DESCRIPTION
        The optional overlay (deploy/compose/docker-compose.full.yml) brings up the
        Core together with an OIDC provider (Keycloak), S3-compatible object storage
        (RustFS by default, or MinIO) and a Redis/Valkey backplane, pre-wired so the platform runs
        end-to-end locally (authenticated traffic, asset upload/download, multi-
        instance realtime). This statically validates the overlay defines those three
        supporting services and that the merged api/worker actually reference them, so
        an overlay that drops a supporting service or leaves a seam unwired fails the
        build. It is the full-stack sibling of Test-LiveCoreComposeDeployment, which
        guards the minimal stack.
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]). Findings is
        empty exactly when every invariant holds.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][psobject]$Model)

    $findings = New-Object System.Collections.Generic.List[string]
    $services = $Model.Services

    # The supporting services the full local stack adds. docs/02_ARCHITECTURE.md names
    # Keycloak for OIDC, RustFS (the Apache-2.0 default) or MinIO for S3-compatible
    # storage and Valkey/Redis for the backplane, so each dependency accepts either
    # documented option by service name. RustFS is listed first as the default backend.
    $oidcCandidates = @('keycloak')
    $storageCandidates = @('rustfs', 'minio')
    $backplaneCandidates = @('valkey', 'redis')

    $oidcService = $oidcCandidates | Where-Object { $services.ContainsKey($_) } | Select-Object -First 1
    $storageService = $storageCandidates | Where-Object { $services.ContainsKey($_) } | Select-Object -First 1
    $backplaneService = $backplaneCandidates | Where-Object { $services.ContainsKey($_) } | Select-Object -First 1

    if (-not $oidcService) {
        $findings.Add('MISSING OIDC SERVICE: the full-stack overlay must define an OIDC provider service (keycloak)')
    }
    if (-not $storageService) {
        $findings.Add('MISSING STORAGE SERVICE: the full-stack overlay must define an S3-compatible object storage service (rustfs or minio)')
    }
    if (-not $backplaneService) {
        $findings.Add('MISSING BACKPLANE SERVICE: the full-stack overlay must define a Redis/Valkey backplane service (valkey or redis)')
    }

    # The api references all three seams: the OIDC authority (authenticated traffic),
    # the object-storage endpoint (assets) and the backplane connection string
    # (multi-instance realtime), each pointing at the supporting service.
    if (-not $services.ContainsKey('api')) {
        $findings.Add("API: the full-stack overlay must extend the 'api' service to wire OIDC, object storage and the backplane")
    }
    else {
        $api = $services['api']
        Add-LiveCoreFullStackReferenceFinding -Service $api -ServiceLabel 'api' `
            -Key 'Authentication__Oidc__Authority' -Target $oidcService -Findings $findings
        Add-LiveCoreFullStackReferenceFinding -Service $api -ServiceLabel 'api' `
            -Key 'Assets__Storage__Endpoint' -Target $storageService -Findings $findings
        Add-LiveCoreFullStackReferenceFinding -Service $api -ServiceLabel 'api' `
            -Key 'Realtime__Backplane__ConnectionString' -Target $backplaneService -Findings $findings
    }

    # The worker shares the object storage (its asset-cleanup loop deletes objects).
    if (-not $services.ContainsKey('worker')) {
        $findings.Add("WORKER: the full-stack overlay must extend the 'worker' service to wire object storage")
    }
    else {
        Add-LiveCoreFullStackReferenceFinding -Service $services['worker'] -ServiceLabel 'worker' `
            -Key 'Assets__Storage__Endpoint' -Target $storageService -Findings $findings
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

function Test-LiveCoreComposeProdOverlay {
    <#
    .SYNOPSIS
        Asserts the production overlay forces the Production posture on the api and
        worker (CORE-OPS-011).
    .DESCRIPTION
        The base manifest defaults ASPNETCORE_ENVIRONMENT to Development so the
        bundled stack comes up green with no identity provider - but in Development
        the production readiness gate is inert (CORE-OPS-005) and the OIDC audience
        guard does not trip (CORE-OPS-004), so copying the defaults to a real server
        yields a green but unauthenticated API. The opt-in production overlay
        (deploy/compose/docker-compose.prod.yml) closes that footgun by setting
        ASPNETCORE_ENVIRONMENT to the LITERAL "Production" (not an interpolated
        ${...:-Development} default that a stray .env could override back to
        Development) on BOTH the api and worker, so a production deployment cannot
        accidentally run the Development posture. This statically validates exactly
        that. It is the production-posture sibling of Test-LiveCoreComposeDeployment
        (minimal stack) and Test-LiveCoreComposeFullStack (full local stack).
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]). Findings is
        empty exactly when every invariant holds.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][psobject]$Model)

    $findings = New-Object System.Collections.Generic.List[string]
    $services = $Model.Services

    foreach ($name in @('api', 'worker')) {
        if (-not $services.ContainsKey($name)) {
            $findings.Add("PROD OVERLAY: the production overlay must extend the '$name' service to set ASPNETCORE_ENVIRONMENT=Production")
            continue
        }

        $value = $null
        if ($services[$name].Environment -and $services[$name].Environment.ContainsKey('ASPNETCORE_ENVIRONMENT')) {
            $value = $services[$name].Environment['ASPNETCORE_ENVIRONMENT']
        }

        # The value must be the LITERAL "Production" - an interpolated default
        # (${ASPNETCORE_ENVIRONMENT:-...}) could be overridden back to Development,
        # which is the very footgun the overlay exists to remove.
        if ($value -ne 'Production') {
            $findings.Add("PROD OVERLAY: the '$name' service must set ASPNETCORE_ENVIRONMENT to the literal 'Production' (found '$value')")
        }
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreComposeModel, `
    Get-LiveCoreComposeServiceModel, `
    Get-LiveCoreComposeDependsOn, `
    Get-LiveCoreComposeResourceLimits, `
    Test-LiveCoreComposeDeployment, `
    Test-LiveCoreComposeFullStack, `
    Test-LiveCoreComposeProdOverlay, `
    Get-LiveCoreComposeIndent
