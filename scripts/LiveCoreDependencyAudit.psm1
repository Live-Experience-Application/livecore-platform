#requires -Version 5.1

<#
.SYNOPSIS
    Source dependency-vulnerability audit gate logic (CORE-DEP-005): the
    pass/fail decision that turns a `dotnet list package --vulnerable` report and
    a `pnpm audit` report into a build-blocked or build-passed verdict at an
    agreed severity.

.DESCRIPTION
    CI already scanned the published container images for known CVEs (the Trivy
    image scan, CORE-DEP-003) and statically analyzed the first-party sources
    (CodeQL, CORE-SEC-006), but it never audited the project's own declared
    dependency graph for known-vulnerable packages. This module closes that gap:
    it parses the two ecosystems' audit reports into one normalized finding list
    and decides whether any finding meets the failing-severity bar.

    The audit reports are produced by external tools in CI - `dotnet list
    LiveCore.slnx package --vulnerable --include-transitive --format json` for the
    .NET projects and `pnpm audit --json` for the TypeScript workspace - but the
    *decision* that turns a report into a blocked-or-passed verdict is this
    module's pure functions, so it is deterministically testable from seeded
    fixtures without a network, a registry or a real restore
    (scripts/test-dependency-audit.ps1).

    The gate is fail-closed and configurable. The agreed failing severities are
    HIGH and CRITICAL by default - a high/critical advisory on a first-party
    direct or transitive dependency blocks the build, while a moderate/low one is
    reported but does not block - and the failing set can be widened (for example
    to also block MODERATE). Both ecosystems report severities from the same
    vocabulary (low / moderate / high / critical; npm/pnpm adds info), normalized
    here to upper case so one gate spans both.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

# The vulnerability severities an audit report uses, ordered low to high. The
# gate decision compares a finding's severity against the configured failing set.
$script:KnownSeverity = @('UNKNOWN', 'INFO', 'LOW', 'MODERATE', 'HIGH', 'CRITICAL')

function Get-LiveCoreDependencyAuditJsonValue {
    # Safe property read off a ConvertFrom-Json object: returns the value when the
    # named property exists, otherwise $null. Works the same on Windows PowerShell
    # 5.1 and pwsh, where ConvertFrom-Json yields PSCustomObjects.
    [CmdletBinding()]
    [OutputType([object])]
    param(
        [Parameter(Mandatory = $true)][AllowNull()]$InputObject,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function ConvertFrom-LiveCoreDependencyAuditText {
    # Parses JSON text into an object, throwing a clear error on malformed input
    # so the fail-closed callers turn an unparseable report into a block.
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        throw 'Empty document: expected JSON audit content.'
    }
    try {
        return $Text | ConvertFrom-Json
    }
    catch {
        throw "Malformed JSON document: $($_.Exception.Message)"
    }
}

function ConvertTo-LiveCoreAuditSeverity {
    # Normalizes a raw severity string from either ecosystem to upper case so a
    # single gate spans both. A blank/absent severity becomes UNKNOWN rather than
    # silently disappearing.
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][AllowNull()]$Value)

    $text = ([string]$Value).Trim()
    if ($text -eq '') { return 'UNKNOWN' }
    return $text.ToUpperInvariant()
}

function Read-LiveCoreAuditText {
    # Loads the report text from a path (fail-closed if it is missing) or returns
    # the literal content, so the two model getters share one source-of-text path.
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [string]$Path,
        [AllowEmptyString()][string]$Content,
        [bool]$FromPath,
        [string]$Kind
    )

    if ($FromPath) {
        if (-not (Test-Path -LiteralPath $Path)) {
            throw "$Kind report not found: $Path"
        }
        return [System.IO.File]::ReadAllText($Path)
    }
    return $Content
}

function Get-LiveCoreDotnetVulnerabilityModel {
    <#
    .SYNOPSIS
        Parses a `dotnet list package --vulnerable --format json` report (from a
        path or literal content) into a normalized finding list.
    .DESCRIPTION
        Walks every project's frameworks and both the top-level and the transitive
        package sets, projecting each recorded vulnerability into a finding. A
        project with no vulnerable packages carries no `frameworks` block and
        contributes nothing - the clean case.
    .OUTPUTS
        A PSCustomObject with Ecosystem ('nuget') and Findings (an array of
        PSCustomObjects carrying Ecosystem, Package, Version, Severity, Advisory,
        Scope ('top-level'|'transitive'), Source (the project) and Framework).
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

    $fromPath = ($PSCmdlet.ParameterSetName -eq 'Path')
    $text = Read-LiveCoreAuditText -Path $Path -Content $Content -FromPath $fromPath -Kind 'Dotnet audit'
    $report = ConvertFrom-LiveCoreDependencyAuditText -Text $text

    $findings = New-Object System.Collections.Generic.List[psobject]
    foreach ($project in @(Get-LiveCoreDependencyAuditJsonValue -InputObject $report -Name 'projects')) {
        if ($null -eq $project) { continue }
        $projectPath = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $project -Name 'path')
        foreach ($framework in @(Get-LiveCoreDependencyAuditJsonValue -InputObject $project -Name 'frameworks')) {
            if ($null -eq $framework) { continue }
            $frameworkName = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $framework -Name 'framework')
            foreach ($scope in @('topLevelPackages', 'transitivePackages')) {
                $scopeLabel = if ($scope -eq 'topLevelPackages') { 'top-level' } else { 'transitive' }
                foreach ($package in @(Get-LiveCoreDependencyAuditJsonValue -InputObject $framework -Name $scope)) {
                    if ($null -eq $package) { continue }
                    $packageId = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $package -Name 'id')
                    $version = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $package -Name 'resolvedVersion')
                    foreach ($vulnerability in @(Get-LiveCoreDependencyAuditJsonValue -InputObject $package -Name 'vulnerabilities')) {
                        if ($null -eq $vulnerability) { continue }
                        $findings.Add([pscustomobject]@{
                                Ecosystem = 'nuget'
                                Package   = $packageId
                                Version   = $version
                                Severity  = ConvertTo-LiveCoreAuditSeverity -Value (Get-LiveCoreDependencyAuditJsonValue -InputObject $vulnerability -Name 'severity')
                                Advisory  = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $vulnerability -Name 'advisoryurl')
                                Scope     = $scopeLabel
                                Source    = $projectPath
                                Framework = $frameworkName
                            })
                    }
                }
            }
        }
    }

    return [pscustomobject]@{
        Ecosystem = 'nuget'
        Findings  = $findings.ToArray()
    }
}

function Get-LiveCorePnpmVulnerabilityModel {
    <#
    .SYNOPSIS
        Parses a `pnpm audit --json` report (from a path or literal content) into a
        normalized finding list.
    .DESCRIPTION
        The report carries an `advisories` object keyed by advisory id; each value
        records the affected module, its severity and the advisory url. A report
        with no advisories is the clean case and contributes nothing.
    .OUTPUTS
        A PSCustomObject with Ecosystem ('npm') and Findings (an array of
        PSCustomObjects carrying Ecosystem, Package, Version (the vulnerable
        range), Severity, Advisory, Scope ('dependency'), Source (a dependency
        path) and Title).
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

    $fromPath = ($PSCmdlet.ParameterSetName -eq 'Path')
    $text = Read-LiveCoreAuditText -Path $Path -Content $Content -FromPath $fromPath -Kind 'Pnpm audit'
    $report = ConvertFrom-LiveCoreDependencyAuditText -Text $text

    $advisories = Get-LiveCoreDependencyAuditJsonValue -InputObject $report -Name 'advisories'

    $findings = New-Object System.Collections.Generic.List[psobject]
    if ($null -ne $advisories) {
        foreach ($entry in $advisories.PSObject.Properties) {
            $advisory = $entry.Value
            if ($null -eq $advisory) { continue }

            $url = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $advisory -Name 'url')
            if ([string]::IsNullOrWhiteSpace($url)) {
                $url = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $advisory -Name 'github_advisory_id')
            }

            # The first finding path, when present, helps a reader locate the
            # dependency edge that pulls the vulnerable module into the workspace.
            $source = ''
            $firstFinding = @(Get-LiveCoreDependencyAuditJsonValue -InputObject $advisory -Name 'findings') |
                Where-Object { $null -ne $_ } |
                Select-Object -First 1
            if ($null -ne $firstFinding) {
                $firstPath = @(Get-LiveCoreDependencyAuditJsonValue -InputObject $firstFinding -Name 'paths') |
                    Where-Object { $null -ne $_ } |
                    Select-Object -First 1
                if ($null -ne $firstPath) { $source = [string]$firstPath }
            }

            $findings.Add([pscustomobject]@{
                    Ecosystem = 'npm'
                    Package   = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $advisory -Name 'module_name')
                    Version   = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $advisory -Name 'vulnerable_versions')
                    Severity  = ConvertTo-LiveCoreAuditSeverity -Value (Get-LiveCoreDependencyAuditJsonValue -InputObject $advisory -Name 'severity')
                    Advisory  = $url
                    Scope     = 'dependency'
                    Source    = $source
                    Title     = [string](Get-LiveCoreDependencyAuditJsonValue -InputObject $advisory -Name 'title')
                })
        }
    }

    return [pscustomobject]@{
        Ecosystem = 'npm'
        Findings  = $findings.ToArray()
    }
}

function Test-LiveCoreDependencyAuditGate {
    <#
    .SYNOPSIS
        Decides whether the combined dependency-audit findings pass the gate.
    .DESCRIPTION
        The gate fails when any finding's severity is in the failing set (HIGH and
        CRITICAL by default), so a high/critical advisory on a first-party direct
        or transitive dependency blocks the build while a moderate/low one is
        reported but never blocks. Fail-closed upstream: a missing or malformed
        report throws in the model getters and the CLI turns that into a block.
    .OUTPUTS
        A PSCustomObject with Passed (bool), Blocking (the blocking findings) and
        Counts (an ordered hashtable of severity -> count over all findings).
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true)][psobject]$Model,
        [string[]]$FailOnSeverity = @('HIGH', 'CRITICAL')
    )

    $failSet = @($FailOnSeverity | ForEach-Object { $_.ToUpperInvariant() })

    $counts = [ordered]@{}
    foreach ($severity in $script:KnownSeverity) { $counts[$severity] = 0 }

    $blocking = New-Object System.Collections.Generic.List[psobject]
    foreach ($finding in @($Model.Findings)) {
        if ($null -eq $finding) { continue }
        $severity = $finding.Severity
        if ($counts.Contains($severity)) { $counts[$severity]++ } else { $counts[$severity] = 1 }
        if ($failSet -contains $severity) { $blocking.Add($finding) }
    }

    return [pscustomobject]@{
        Passed   = ($blocking.Count -eq 0)
        Blocking = $blocking.ToArray()
        Counts   = $counts
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreDotnetVulnerabilityModel, `
    Get-LiveCorePnpmVulnerabilityModel, `
    Test-LiveCoreDependencyAuditGate
