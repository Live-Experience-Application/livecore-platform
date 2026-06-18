#requires -Version 5.1
<#
.SYNOPSIS
    Validation logic for the example observability assets (CORE-OBS-008).

.DESCRIPTION
    Shared by scripts/test-observability-assets.ps1 (the gate-logic test and the
    real-file guard) and the observability-assets CI job. The unique value over
    `promtool check rules` is CONSISTENCY: every livecore_* series referenced by
    the example Prometheus rules and the Grafana dashboard must be either a series
    documented in docs/15_OBSERVABILITY.md or a recording rule defined in the rules
    file, so the rules, the dashboard and the docs cannot silently drift. It also
    lints the dashboard JSON and checks each alert carries a severity label and an
    annotation.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux; no
    external module (no YAML/JSON dependency beyond the built-in ConvertFrom-Json).
#>

Set-StrictMode -Version Latest

# A YAML/PromQL line with the leading and any inline comment stripped. YAML inline
# comments are introduced by a '#' at the start of the line or preceded by
# whitespace; the example assets never put a '#' inside an expression or string,
# so this is exact for the controlled files it guards.
function Get-LiveCoreUncommentedLine {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Line)
    return [regex]::Replace($Line, '(^|\s)#.*$', '$1')
}

<#
.SYNOPSIS
    The exported livecore_* series documented in docs/15_OBSERVABILITY.md.
.DESCRIPTION
    Reads the metrics table whose rows pair a dotted instrument name
    (livecore.api.request.duration) with its underscored exported series
    (livecore_api_request_duration_seconds). Extracting only from rows that carry
    a dotted instrument name locks onto that table and ignores prose or other
    tables (for example the SLO recording-rule table this story adds), so docs/15
    is the single source of truth for the known series set.
#>
function Get-LiveCoreKnownMetricSeries {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ObservabilityDocPath)

    if (-not (Test-Path -LiteralPath $ObservabilityDocPath)) {
        throw "Observability doc not found: $ObservabilityDocPath"
    }

    $series = New-Object System.Collections.Generic.List[string]
    $dottedInstrument = [regex]'livecore\.[a-z0-9_.]+'
    $exportedSeries = [regex]'livecore_[a-z0-9_]+'

    foreach ($line in (Get-Content -LiteralPath $ObservabilityDocPath)) {
        if (-not $dottedInstrument.IsMatch($line)) { continue }
        foreach ($match in $exportedSeries.Matches($line)) {
            if (-not $series.Contains($match.Value)) { $series.Add($match.Value) }
        }
    }

    return $series.ToArray()
}

<#
.SYNOPSIS
    The recording-rule names defined in a Prometheus rules file (the `record:`
    values), which a later alert or the dashboard may reference.
#>
function Get-LiveCoreRecordingRuleName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$RulesText)

    $names = New-Object System.Collections.Generic.List[string]
    foreach ($rawLine in ($RulesText -split "`n")) {
        $line = Get-LiveCoreUncommentedLine -Line $rawLine
        $m = [regex]::Match($line, '^\s*-?\s*record:\s*(\S+)\s*$')
        if ($m.Success) {
            $name = $m.Groups[1].Value
            if (-not $names.Contains($name)) { $names.Add($name) }
        }
    }
    return $names.ToArray()
}

<#
.SYNOPSIS
    The alert rules in a Prometheus rules file with whether each carries a
    severity label and at least one annotation (a light block scan; promtool
    validates the full structure).
#>
function Get-LiveCoreAlertRule {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$RulesText)

    $lines = @($RulesText -split "`n" | ForEach-Object { Get-LiveCoreUncommentedLine -Line $_ })

    # Boundaries are list items that start an alert or a recording rule; an alert
    # block runs until the next such boundary.
    $boundaries = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*-\s*(alert|record):') { $boundaries.Add($i) }
    }

    $alerts = New-Object System.Collections.Generic.List[object]
    for ($b = 0; $b -lt $boundaries.Count; $b++) {
        $start = $boundaries[$b]
        $alertMatch = [regex]::Match($lines[$start], '^\s*-\s*alert:\s*(\S+)')
        if (-not $alertMatch.Success) { continue }

        $end = if ($b + 1 -lt $boundaries.Count) { $boundaries[$b + 1] } else { $lines.Count }
        $block = ($lines[$start..($end - 1)] -join "`n")

        $alerts.Add([pscustomobject]@{
                Name          = $alertMatch.Groups[1].Value
                HasSeverity   = [bool]([regex]::IsMatch($block, '(?m)^\s*severity:\s*\S'))
                HasAnnotation = [bool]([regex]::IsMatch($block, '(?m)^\s*(summary|description):\s*\S'))
            })
    }
    return $alerts.ToArray()
}

# Reference tokens (Prometheus metric/series names, which may contain ':') that
# mention livecore, taken from text with comments and the rule/group declaration
# lines (name:/record:/alert:) removed so a group or rule NAME is never counted as
# a series REFERENCE.
function Get-LiveCoreReferencedSeriesFromRules {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$RulesText)

    $kept = foreach ($rawLine in ($RulesText -split "`n")) {
        $line = Get-LiveCoreUncommentedLine -Line $rawLine
        if ($line -match '^\s*-?\s*(name|record|alert)\s*:') { continue }
        $line
    }
    return Get-LiveCoreReferencedSeriesFromText -Text ($kept -join "`n")
}

# Reference tokens that mention livecore from an arbitrary text blob.
function Get-LiveCoreReferencedSeriesFromText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    $tokenRegex = [regex]'[A-Za-z_:][A-Za-z0-9_:]*'
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($match in $tokenRegex.Matches($Text)) {
        $token = $match.Value
        if ($token -notmatch 'livecore') { continue }
        if (-not $found.Contains($token)) { $found.Add($token) }
    }
    return $found.ToArray()
}

# Collect every `expr` string value anywhere in a parsed JSON object graph
# (Grafana panel/target expressions).
function Get-LiveCoreDashboardExpr {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()]$Node)

    $result = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Node) { return $result.ToArray() }

    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Node.PSObject.Properties) {
            if ($property.Name -eq 'expr' -and $property.Value -is [string]) {
                $result.Add($property.Value)
            }
            else {
                foreach ($child in (Get-LiveCoreDashboardExpr -Node $property.Value)) {
                    $result.Add($child)
                }
            }
        }
    }
    elseif ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($item in $Node) {
            foreach ($child in (Get-LiveCoreDashboardExpr -Node $item)) {
                $result.Add($child)
            }
        }
    }

    return $result.ToArray()
}

<#
.SYNOPSIS
    True when a referenced series is a known exported series or a defined
    recording rule.
.DESCRIPTION
    A reference is valid when it is, exactly, a recording-rule name, or an exported
    series in $KnownSeries - allowing the histogram suffixes the Prometheus
    exporter adds (_bucket/_sum/_count) on a documented histogram base series.
#>
function Test-LiveCoreSeriesReference {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Reference,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$KnownSeries,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$RecordingRuleName
    )

    if ($RecordingRuleName -contains $Reference) { return $true }
    if ($KnownSeries -contains $Reference) { return $true }

    foreach ($suffix in @('_bucket', '_sum', '_count')) {
        if ($Reference.EndsWith($suffix)) {
            $base = $Reference.Substring(0, $Reference.Length - $suffix.Length)
            if ($KnownSeries -contains $base) { return $true }
        }
    }
    return $false
}

<#
.SYNOPSIS
    Validates the example rules and dashboard against the documented series.
.OUTPUTS
    [pscustomobject] with IsValid (bool) and Findings (string[]).
#>
function Test-LiveCoreObservabilityAssets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$RulesText,
        [Parameter(Mandatory)][AllowEmptyString()][string]$DashboardJsonText,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$KnownSeries
    )

    $findings = New-Object System.Collections.Generic.List[string]

    $recordingRuleNames = @(Get-LiveCoreRecordingRuleName -RulesText $RulesText)
    if ($recordingRuleNames.Count -eq 0) {
        $findings.Add('the rules file defines no recording rules (expected the SLO recording rules)')
    }

    $alerts = @(Get-LiveCoreAlertRule -RulesText $RulesText)
    if ($alerts.Count -eq 0) {
        $findings.Add('the rules file defines no alert rules')
    }
    foreach ($alert in $alerts) {
        if (-not $alert.HasSeverity) {
            $findings.Add("alert '$($alert.Name)' has no severity label")
        }
        if (-not $alert.HasAnnotation) {
            $findings.Add("alert '$($alert.Name)' has no summary/description annotation")
        }
    }

    # Series referenced by the rule expressions.
    $references = New-Object System.Collections.Generic.List[string]
    foreach ($ref in (Get-LiveCoreReferencedSeriesFromRules -RulesText $RulesText)) {
        $references.Add($ref)
    }

    # Series referenced by the dashboard expressions (also lints the JSON).
    $dashboard = $null
    try {
        $dashboard = $DashboardJsonText | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        $findings.Add("the dashboard JSON is not valid: $($_.Exception.Message)")
    }
    if ($null -ne $dashboard) {
        foreach ($expr in (Get-LiveCoreDashboardExpr -Node $dashboard)) {
            foreach ($ref in (Get-LiveCoreReferencedSeriesFromText -Text $expr)) {
                $references.Add($ref)
            }
        }
    }

    $seen = @{}
    foreach ($reference in $references) {
        if ($seen.ContainsKey($reference)) { continue }
        $seen[$reference] = $true
        if (-not (Test-LiveCoreSeriesReference -Reference $reference -KnownSeries $KnownSeries -RecordingRuleName $recordingRuleNames)) {
            $findings.Add("referenced series '$reference' is not documented in docs/15_OBSERVABILITY.md and is not a recording rule defined in the rules file")
        }
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreKnownMetricSeries, `
    Get-LiveCoreRecordingRuleName, `
    Get-LiveCoreAlertRule, `
    Get-LiveCoreReferencedSeriesFromRules, `
    Get-LiveCoreReferencedSeriesFromText, `
    Get-LiveCoreDashboardExpr, `
    Test-LiveCoreSeriesReference, `
    Test-LiveCoreObservabilityAssets
