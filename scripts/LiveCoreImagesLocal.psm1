#requires -Version 5.1

<#
.SYNOPSIS
    Validates the `images:local` local-image convenience script (CORE-DXL-001).

.DESCRIPTION
    The base deployment manifest (deploy/compose/docker-compose.yml) already BUILDS
    the three Core runtime images from the in-repo Dockerfiles to stable local tags
    (livecore-api:local, livecore-worker:local, livecore-migrations:local). The
    `images:local` root pnpm script is a thin, ADDITIVE convenience over those
    existing build stanzas so a downstream vertical can build an unreleased Core and
    point its own end-to-end test harness at the :local tags WITHOUT a registry
    publish - it need not know the compose internals.

    This module is the pure analysis behind the images:local gate. It has no Docker
    dependency, so it runs anywhere as a CI gate and locally on both Windows
    PowerShell 5.1 and PowerShell 7+ (pwsh). It asserts three things the acceptance
    criteria require, so a regression fails the build rather than only the Docker smoke:

      1. the root package.json defines a well-formed, additive `images:local` script
         that builds the three Core services from source via the base manifest
         (Test-LiveCoreImagesLocalScript);
      2. the base manifest still builds those three services to the stable :local
         tags from the expected Dockerfiles, so the script's documented effect holds
         (Test-LiveCoreImagesLocalTags, consuming the LiveCoreComposeDeploy model); and
      3. deploy/compose/README.md documents the local-consume contract - the script,
         the three :local tags and the no-registry-publish harness use
         (Test-LiveCoreImagesLocalDocs).

    The compose model this module consumes is produced by Get-LiveCoreComposeModel in
    LiveCoreComposeDeploy.psm1, so the manifest parsing stays in one place.
#>

function Get-LiveCoreRootScript {
    <#
    .SYNOPSIS
        Reads a single script command from a package.json (by path or literal content).
    .OUTPUTS
        The script command string, or $null when the named script is absent.
    #>
    [CmdletBinding(DefaultParameterSetName = 'Path')]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
        [string]$Path,

        [Parameter(Mandatory = $true, ParameterSetName = 'Content')]
        [AllowEmptyString()]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($PSCmdlet.ParameterSetName -eq 'Path') {
        if (-not (Test-Path -LiteralPath $Path)) {
            throw "package.json not found: $Path"
        }
        $rawText = [System.IO.File]::ReadAllText($Path)
    }
    else {
        $rawText = $Content
    }

    $json = $rawText | ConvertFrom-Json
    if (-not ($json.PSObject.Properties.Name -contains 'scripts')) { return $null }
    $scripts = $json.scripts
    if ($null -eq $scripts) { return $null }
    $property = $scripts.PSObject.Properties[$Name]
    if (-not $property) { return $null }
    return [string]$property.Value
}

function Test-LiveCoreImagesLocalScript {
    <#
    .SYNOPSIS
        Asserts the `images:local` script is well-formed and additive (CORE-DXL-001).
    .DESCRIPTION
        The script must build the three Core services from source via the base
        manifest's existing build stanzas, and must stay LOCAL and ADDITIVE - it may
        not push, publish or release (fail-closed: a script that would cross into the
        publish/release flow is rejected).
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]). Findings is
        empty exactly when every invariant holds.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][AllowNull()][AllowEmptyString()][string]$Command)

    $findings = New-Object System.Collections.Generic.List[string]

    if ([string]::IsNullOrWhiteSpace($Command)) {
        $findings.Add("MISSING: the root package.json must define an 'images:local' script (CORE-DXL-001)")
        return [pscustomobject]@{ IsValid = $false; Findings = $findings.ToArray() }
    }

    # 1. It invokes Docker Compose (v2 'docker compose' or v1 'docker-compose') - the
    #    trailing boundary stops the docker-compose.yml file reference matching here.
    if ($Command -notmatch 'docker(?:\s+|-)compose(?:\s|$)') {
        $findings.Add("COMPOSE: the images:local script must invoke Docker Compose (found '$Command')")
    }

    # 2. It targets the base manifest, where the build stanzas and :local tags live.
    if ($Command -notmatch [regex]::Escape('deploy/compose/docker-compose.yml')) {
        $findings.Add('MANIFEST: the images:local script must build via deploy/compose/docker-compose.yml (its existing build stanzas)')
    }

    # 3. It runs the compose 'build' subcommand - build from source, not pull/up.
    if ($Command -notmatch '\bbuild\b') {
        $findings.Add("BUILD: the images:local script must run the compose 'build' subcommand to build the images from source")
    }

    # 4. It covers all three Core services. Naming them all is fine; naming NONE is
    #    also fine (a bare `build` builds every service that has a build: stanza,
    #    i.e. exactly migrate/api/worker). Naming only a subset is rejected.
    $coreServices = @('migrate', 'api', 'worker')
    $named = @($coreServices | Where-Object { $Command -match "(?:^|\s)$_(?:\s|$)" })
    if ($named.Count -gt 0 -and $named.Count -lt $coreServices.Count) {
        $missing = @($coreServices | Where-Object { $named -notcontains $_ })
        $findings.Add("SERVICES: the images:local build must cover all three Core services (migrate, api, worker); it omits: $($missing -join ', ')")
    }

    # 5. ADDITIVE / fail-closed: it must stay local and not push, publish or release.
    $forbidden = @('push', 'publish', 'ghcr', 'release', 'login')
    foreach ($token in $forbidden) {
        if ($Command -match "\b$token\b") {
            $findings.Add("ADDITIVE: the images:local script must stay local and additive; it must not '$token' (it changes no build, publish or release flow)")
        }
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

function Test-LiveCoreImagesLocalTags {
    <#
    .SYNOPSIS
        Asserts the base manifest builds the three Core services from source to the
        stable :local tags from the expected Dockerfiles (CORE-DXL-001).
    .DESCRIPTION
        The images:local script's documented effect is "build livecore-{api,worker,
        migrations}:local from source via the base manifest". This validates that the
        base manifest still wires exactly that: each of migrate/api/worker carries a
        build: stanza, builds from its expected Dockerfile, and tags the stable :local
        image. It consumes the model produced by Get-LiveCoreComposeModel.
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]). Findings is
        empty exactly when every invariant holds.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][psobject]$Model)

    $findings = New-Object System.Collections.Generic.List[string]
    $services = $Model.Services

    $expected = @(
        [pscustomobject]@{ Name = 'migrate'; Image = 'livecore-migrations:local'; Dockerfile = 'apps/api/Migrations.Dockerfile' },
        [pscustomobject]@{ Name = 'api'; Image = 'livecore-api:local'; Dockerfile = 'apps/api/Dockerfile' },
        [pscustomobject]@{ Name = 'worker'; Image = 'livecore-worker:local'; Dockerfile = 'apps/worker/Dockerfile' }
    )

    foreach ($entry in $expected) {
        if (-not $services.ContainsKey($entry.Name)) {
            $findings.Add("MISSING SERVICE: the base manifest must define a '$($entry.Name)' service that builds the $($entry.Image) image (CORE-DXL-001)")
            continue
        }
        $service = $services[$entry.Name]
        if (-not $service.HasBuild) {
            $findings.Add("BUILD: the '$($entry.Name)' service must carry a build: stanza so images:local builds its image from source")
        }
        if ($service.Dockerfile -ne $entry.Dockerfile) {
            $findings.Add("DOCKERFILE: the '$($entry.Name)' service must build from $($entry.Dockerfile) (found '$($service.Dockerfile)')")
        }
        if ($service.Image -ne $entry.Image) {
            $findings.Add("TAG: the '$($entry.Name)' service must build to the stable local tag '$($entry.Image)' (found '$($service.Image)')")
        }
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

function Test-LiveCoreImagesLocalDocs {
    <#
    .SYNOPSIS
        Asserts deploy/compose/README.md documents the local-consume contract (CORE-DXL-001).
    .DESCRIPTION
        The README must give a downstream vertical the two facts it needs to run an
        unreleased Core locally without a registry publish: the `images:local` script
        and the three :local image tags it produces, and that a vertical can point its
        own test harness at those tags (no registry publish required).
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]). Findings is
        empty exactly when every required element is present.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$ReadmeText)

    $findings = New-Object System.Collections.Generic.List[string]

    if ($ReadmeText -notmatch 'images:local') {
        $findings.Add("DOCS: deploy/compose/README.md must document the 'images:local' script")
    }
    foreach ($tag in @('livecore-api:local', 'livecore-worker:local', 'livecore-migrations:local')) {
        if ($ReadmeText -notmatch [regex]::Escape($tag)) {
            $findings.Add("DOCS: deploy/compose/README.md must name the local image tag '$tag'")
        }
    }
    if ($ReadmeText -notmatch '(?i)harness') {
        $findings.Add('DOCS: deploy/compose/README.md must explain that a downstream vertical can point its own test harness at the :local tags')
    }
    if ($ReadmeText -notmatch '(?i)registry') {
        $findings.Add('DOCS: deploy/compose/README.md must state that this runs an unreleased Core without a registry publish')
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreRootScript, `
    Test-LiveCoreImagesLocalScript, `
    Test-LiveCoreImagesLocalTags, `
    Test-LiveCoreImagesLocalDocs
