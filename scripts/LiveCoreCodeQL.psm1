#requires -Version 5.1

<#
.SYNOPSIS
    Static-analysis gate logic for the CodeQL CI job (CORE-SEC-006): the
    pass/fail decision that turns a CodeQL SARIF report into a build-blocked or
    build-passed verdict on a high/critical security finding.

.DESCRIPTION
    CI runs CodeQL over the first-party C# and TypeScript sources and CodeQL
    writes a SARIF result file per language. The SARIF is produced by an external
    tool (the CodeQL CLI via github/codeql-action), but the *decision* that turns
    a result set into a published-or-blocked verdict is this module's pure
    functions - so it is deterministically testable from seeded SARIF fixtures
    without running CodeQL, a network or a real analysis
    (scripts/test-codeql-gate.ps1).

    The severity gate keys on the CodeQL `security-severity` property, the
    CVSS-style score CodeQL attaches to a security query's rule. GitHub's
    documented thresholds map that score to a band:

        critical  9.0 - 10.0
        high      7.0 -  8.9
        medium    4.0 -  6.9
        low       0.1 -  3.9

    so the gate blocks the build on any result whose rule scores at or above the
    high floor (7.0 by default - high and critical). A quality/maintainability
    finding that carries no `security-severity` is never blocking: the gate is a
    SECURITY gate, exactly the "high/critical finding" the story names.

    The gate is fail-closed: a missing, empty or malformed SARIF file, or a
    document that is not a SARIF log at all, must block the build rather than
    pass silently (an analysis that produced no readable output is not a clean
    analysis).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

# The severity bands a security finding falls into, ordered low to high. The gate
# decision compares a finding's band against the configured failing floor.
$script:KnownBand = @('None', 'Low', 'Medium', 'High', 'Critical')

function Get-LiveCoreCodeQlJsonValue {
    # Safe property read off a ConvertFrom-Json object: returns the value when the
    # named property exists, otherwise $null. Works the same on Windows PowerShell
    # 5.1 and pwsh, where ConvertFrom-Json yields PSCustomObjects, and tolerates a
    # property name that carries a hyphen (for example 'security-severity').
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

function ConvertFrom-LiveCoreCodeQlText {
    # Parses JSON text into an object, throwing a clear error on malformed input
    # so the fail-closed callers turn an unparseable report into a block.
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        throw 'Empty document: expected SARIF JSON content.'
    }
    try {
        return $Text | ConvertFrom-Json
    }
    catch {
        throw "Malformed JSON document: $($_.Exception.Message)"
    }
}

function Get-LiveCoreCodeQlSecuritySeverity {
    # Parses a SARIF `security-severity` value (a string CVSS score such as "9.8")
    # into a double using the invariant culture, returning $null when the property
    # is absent or not a number (a non-security rule, or a malformed value).
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory = $true)][AllowNull()]$Value)

    if ($null -eq $Value) { return $null }
    $text = ([string]$Value).Trim()
    if ($text -eq '') { return $null }

    $parsed = 0.0
    $styles = [System.Globalization.NumberStyles]::Float
    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    if ([double]::TryParse($text, $styles, $culture, [ref]$parsed)) {
        return $parsed
    }
    return $null
}

function Get-LiveCoreCodeQlBand {
    <#
    .SYNOPSIS
        Maps a numeric security-severity score to its GitHub severity band.
    .OUTPUTS
        One of 'Critical', 'High', 'Medium', 'Low' or 'None' ($null score, i.e. a
        finding with no security-severity, is 'None').
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][AllowNull()]$SecuritySeverity)

    if ($null -eq $SecuritySeverity) { return 'None' }
    $score = [double]$SecuritySeverity
    if ($score -ge 9.0) { return 'Critical' }
    if ($score -ge 7.0) { return 'High' }
    if ($score -ge 4.0) { return 'Medium' }
    if ($score -gt 0.0) { return 'Low' }
    return 'None'
}

function Add-LiveCoreCodeQlRules {
    # Folds a SARIF rules array (driver.rules or an extension's rules) into the
    # rule-id -> security-severity map a run carries, so a result can resolve its
    # severity by rule id regardless of where the rule metadata was emitted.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Map,
        [Parameter(Mandatory = $true)][AllowNull()]$Rules
    )

    foreach ($rule in @($Rules)) {
        if ($null -eq $rule) { continue }
        $ruleId = [string](Get-LiveCoreCodeQlJsonValue -InputObject $rule -Name 'id')
        if ([string]::IsNullOrWhiteSpace($ruleId)) { continue }
        $properties = Get-LiveCoreCodeQlJsonValue -InputObject $rule -Name 'properties'
        $rawSeverity = Get-LiveCoreCodeQlJsonValue -InputObject $properties -Name 'security-severity'
        $Map[$ruleId] = Get-LiveCoreCodeQlSecuritySeverity -Value $rawSeverity
    }
}

function Get-LiveCoreCodeQlSarifModel {
    <#
    .SYNOPSIS
        Parses a CodeQL SARIF log (from a path or literal content) into a
        normalized finding list.
    .DESCRIPTION
        Reads every run's rule metadata (from tool.driver.rules and each
        tool.extensions[].rules) to build a rule-id -> security-severity map, then
        projects each result into a finding carrying its rule id, resolved
        security-severity score and band, and a source location. Fail-closed: a
        document with no `runs` array is not a SARIF log and is rejected.
    .OUTPUTS
        A PSCustomObject with ToolName (string), RunCount (int) and Findings (an
        array of PSCustomObjects carrying RuleId, SecuritySeverity (double|null),
        Band, Level, Message and Location).
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
            throw "SARIF report not found: $Path"
        }
        $text = [System.IO.File]::ReadAllText($Path)
    }
    else {
        $text = $Content
    }

    $document = ConvertFrom-LiveCoreCodeQlText -Text $text

    $runsProperty = $document.PSObject.Properties['runs']
    if ($null -eq $runsProperty) {
        throw "Not a SARIF log: the document has no 'runs' array."
    }
    $runs = @($runsProperty.Value)

    $toolName = ''
    $findings = New-Object System.Collections.Generic.List[psobject]

    foreach ($run in $runs) {
        if ($null -eq $run) { continue }

        $tool = Get-LiveCoreCodeQlJsonValue -InputObject $run -Name 'tool'
        $driver = Get-LiveCoreCodeQlJsonValue -InputObject $tool -Name 'driver'
        if ($toolName -eq '') {
            $toolName = [string](Get-LiveCoreCodeQlJsonValue -InputObject $driver -Name 'name')
        }

        $ruleSeverity = @{}
        Add-LiveCoreCodeQlRules -Map $ruleSeverity -Rules (Get-LiveCoreCodeQlJsonValue -InputObject $driver -Name 'rules')
        foreach ($extension in @(Get-LiveCoreCodeQlJsonValue -InputObject $tool -Name 'extensions')) {
            Add-LiveCoreCodeQlRules -Map $ruleSeverity -Rules (Get-LiveCoreCodeQlJsonValue -InputObject $extension -Name 'rules')
        }

        foreach ($result in @(Get-LiveCoreCodeQlJsonValue -InputObject $run -Name 'results')) {
            if ($null -eq $result) { continue }

            $ruleId = [string](Get-LiveCoreCodeQlJsonValue -InputObject $result -Name 'ruleId')
            if ([string]::IsNullOrWhiteSpace($ruleId)) {
                $rule = Get-LiveCoreCodeQlJsonValue -InputObject $result -Name 'rule'
                $ruleId = [string](Get-LiveCoreCodeQlJsonValue -InputObject $rule -Name 'id')
            }

            $severity = $null
            if ($ruleSeverity.ContainsKey($ruleId)) { $severity = $ruleSeverity[$ruleId] }

            $messageObject = Get-LiveCoreCodeQlJsonValue -InputObject $result -Name 'message'
            $message = [string](Get-LiveCoreCodeQlJsonValue -InputObject $messageObject -Name 'text')

            $location = ''
            $firstLocation = @(Get-LiveCoreCodeQlJsonValue -InputObject $result -Name 'locations') |
                Where-Object { $null -ne $_ } |
                Select-Object -First 1
            if ($null -ne $firstLocation) {
                $physical = Get-LiveCoreCodeQlJsonValue -InputObject $firstLocation -Name 'physicalLocation'
                $artifact = Get-LiveCoreCodeQlJsonValue -InputObject $physical -Name 'artifactLocation'
                $uri = [string](Get-LiveCoreCodeQlJsonValue -InputObject $artifact -Name 'uri')
                $region = Get-LiveCoreCodeQlJsonValue -InputObject $physical -Name 'region'
                $startLine = Get-LiveCoreCodeQlJsonValue -InputObject $region -Name 'startLine'
                if ($uri -ne '') {
                    $location = $uri
                    if ($null -ne $startLine) { $location = "${uri}:${startLine}" }
                }
            }

            $findings.Add([pscustomobject]@{
                    RuleId           = $ruleId
                    SecuritySeverity = $severity
                    Band             = Get-LiveCoreCodeQlBand -SecuritySeverity $severity
                    Level            = [string](Get-LiveCoreCodeQlJsonValue -InputObject $result -Name 'level')
                    Message          = $message
                    Location         = $location
                })
        }
    }

    return [pscustomobject]@{
        ToolName = $toolName
        RunCount = $runs.Count
        Findings = $findings.ToArray()
    }
}

function Test-LiveCoreCodeQlGate {
    <#
    .SYNOPSIS
        Decides whether a parsed SARIF model passes the static-analysis gate.
    .DESCRIPTION
        The gate fails when the model carries any security finding whose
        security-severity score is at or above the configured floor (7.0 by
        default, so high and critical block; medium, low and non-security
        findings do not). Fail-closed by construction upstream: a model can only
        be built from a readable SARIF log.
    .OUTPUTS
        A PSCustomObject with Passed (bool), Blocking (the blocking findings) and
        Counts (an ordered hashtable of band -> count over all findings).
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true)][psobject]$Model,

        # The security-severity floor at or above which a finding blocks the
        # build. Default 7.0 - the high/critical band (CORE-SEC-006).
        [double]$MinimumSecuritySeverity = 7.0
    )

    $counts = [ordered]@{}
    foreach ($band in $script:KnownBand) { $counts[$band] = 0 }

    $blocking = New-Object System.Collections.Generic.List[psobject]
    foreach ($finding in @($Model.Findings)) {
        if ($null -eq $finding) { continue }
        $band = $finding.Band
        if ($counts.Contains($band)) { $counts[$band]++ } else { $counts[$band] = 1 }
        if ($null -ne $finding.SecuritySeverity -and [double]$finding.SecuritySeverity -ge $MinimumSecuritySeverity) {
            $blocking.Add($finding)
        }
    }

    return [pscustomobject]@{
        Passed   = ($blocking.Count -eq 0)
        Blocking = $blocking.ToArray()
        Counts   = $counts
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreCodeQlBand, `
    Get-LiveCoreCodeQlSarifModel, `
    Test-LiveCoreCodeQlGate
