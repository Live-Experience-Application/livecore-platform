#requires -Version 5.1
<#
.SYNOPSIS
    Spec consistency check for the LiveCore Core Platform (CORE-DOC-001).

.DESCRIPTION
    Verifies that the route, table, event and epic specifications in docs/ and
    csv/ agree with each other and with the single source of truth per concern
    recorded in docs/24_SPEC_CONSISTENCY.md:

      1. every route in the docs/08 representative block is a row in
         csv/api_routes.csv;
      2. the docs/10 table list equals the table set in csv/database_tables.csv;
      3. every non-deferred table in csv/entitlement_database_tables.csv exists
         in csv/database_tables.csv;
      4. the docs/09 event table equals the event set in csv/event_catalog.csv;
      5. the docs/18 epic list equals the union of the epic columns of
         csv/core_epics_stories.csv and csv/core_phase2_epics_stories.csv.

    Exits 0 when every invariant holds, 1 when drift is found, and 2 on a
    configuration error (a spec file that cannot be found or parsed).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh).

.EXAMPLE
    pwsh -NoProfile -File scripts/spec-consistency.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/spec-consistency.ps1
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    # $PSScriptRoot is not available in param() defaults on Windows PowerShell 5.1.
    $scriptDir = $PSScriptRoot
    if (-not $scriptDir) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $RepoRoot = Split-Path -Parent $scriptDir
}
$RepoRoot = (Resolve-Path -Path $RepoRoot).Path

function Get-SpecFile {
    param([Parameter(Mandatory)][string]$RelativePath)
    $full = Join-Path -Path $RepoRoot -ChildPath $RelativePath
    if (-not (Test-Path -Path $full)) {
        throw "Spec file not found: $RelativePath"
    }
    return $full
}

function Get-FencedBlockAfterHeading {
    # Returns the non-empty, trimmed lines of the first ```fence block that
    # follows the given exact heading line, or throws on a missing/unterminated
    # block (a configuration error).
    param(
        [string[]]$Line,
        [Parameter(Mandatory)][string]$Heading,
        [Parameter(Mandatory)][string]$Source
    )
    $fence = '```'
    $start = -1
    for ($i = 0; $i -lt $Line.Count; $i++) {
        if ($Line[$i].Trim() -eq $Heading) { $start = $i; break }
    }
    if ($start -lt 0) {
        throw "Heading '$Heading' not found in $Source"
    }
    $open = -1
    for ($i = $start + 1; $i -lt $Line.Count; $i++) {
        if ($Line[$i].TrimStart().StartsWith($fence)) { $open = $i; break }
    }
    if ($open -lt 0) {
        throw "No fenced block found after '$Heading' in $Source"
    }
    $content = New-Object System.Collections.Generic.List[string]
    for ($i = $open + 1; $i -lt $Line.Count; $i++) {
        if ($Line[$i].TrimStart().StartsWith($fence)) {
            return , $content.ToArray()
        }
        $value = $Line[$i].Trim()
        if ($value -ne '') { $content.Add($value) }
    }
    throw "Unterminated fenced block after '$Heading' in $Source"
}

function Get-StringSet {
    param([string[]]$Value)
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    if ($Value) {
        foreach ($v in $Value) { [void]$set.Add($v) }
    }
    return , $set
}

$findings = New-Object System.Collections.Generic.List[string]
$checkCount = 0

try {
    # --- Check 1: docs/08 representative routes are a subset of api_routes.csv ---
    $checkCount++
    $routeSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($row in (Import-Csv -Path (Get-SpecFile 'csv/api_routes.csv'))) {
        [void]$routeSet.Add(('{0} {1}' -f $row.method.Trim().ToUpperInvariant(), $row.route.Trim()))
    }
    $apiDocLine = @(Get-Content -Path (Get-SpecFile 'docs/08_API_CONTRACTS.md'))
    $routeBlock = Get-FencedBlockAfterHeading -Line $apiDocLine -Heading '## Core endpoints' -Source 'docs/08_API_CONTRACTS.md'
    foreach ($entry in $routeBlock) {
        $parts = $entry -split '\s+'
        if ($parts.Count -lt 2) { continue }
        $key = '{0} {1}' -f $parts[0].ToUpperInvariant(), $parts[1]
        if (-not $routeSet.Contains($key)) {
            $findings.Add("ROUTE: docs/08 lists '$entry' which is not a row in csv/api_routes.csv")
        }
    }

    # --- Check 2: docs/10 table list equals database_tables.csv ---
    $checkCount++
    $schemaTableSet = Get-StringSet -Value (@(Import-Csv -Path (Get-SpecFile 'csv/database_tables.csv')) | ForEach-Object { $_.table.Trim() })
    $schemaDocLine = @(Get-Content -Path (Get-SpecFile 'docs/10_DATABASE_SCHEMA.md'))
    $docTableList = Get-FencedBlockAfterHeading -Line $schemaDocLine -Heading '## Core tables' -Source 'docs/10_DATABASE_SCHEMA.md'
    $docTableSet = Get-StringSet -Value $docTableList
    foreach ($t in $docTableList) {
        if (-not $schemaTableSet.Contains($t)) {
            $findings.Add("TABLE: docs/10 lists '$t' which is not in csv/database_tables.csv")
        }
    }
    foreach ($t in $schemaTableSet) {
        if (-not $docTableSet.Contains($t)) {
            $findings.Add("TABLE: csv/database_tables.csv has '$t' which is missing from the docs/10 table list")
        }
    }

    # --- Check 3: non-deferred entitlement tables exist in the schema ---
    $checkCount++
    foreach ($row in (Import-Csv -Path (Get-SpecFile 'csv/entitlement_database_tables.csv'))) {
        $notes = if ($null -ne $row.notes) { [string]$row.notes } else { '' }
        if ($notes -match 'DEFERRED') { continue }
        $t = $row.table.Trim()
        if (-not $schemaTableSet.Contains($t)) {
            $findings.Add("TABLE: csv/entitlement_database_tables.csv lists non-deferred table '$t' which is not in csv/database_tables.csv")
        }
    }

    # --- Check 4: docs/09 event table equals event_catalog.csv ---
    $checkCount++
    $csvEventSet = Get-StringSet -Value (@(Import-Csv -Path (Get-SpecFile 'csv/event_catalog.csv')) | ForEach-Object { $_.event.Trim() })
    $eventDocLine = @(Get-Content -Path (Get-SpecFile 'docs/09_EVENT_CATALOG.md'))
    $docEventValue = New-Object System.Collections.Generic.List[string]
    foreach ($line in $eventDocLine) {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith('|')) { continue }
        $firstCell = ($trimmed.Trim('|') -split '\|')[0].Trim()
        if ($firstCell -eq 'Event') { continue }
        if ($firstCell -match '^[A-Z][A-Za-z]+$') { $docEventValue.Add($firstCell) }
    }
    $docEventSet = Get-StringSet -Value $docEventValue.ToArray()
    foreach ($e in $docEventSet) {
        if (-not $csvEventSet.Contains($e)) {
            $findings.Add("EVENT: docs/09 lists '$e' which is not in csv/event_catalog.csv")
        }
    }
    foreach ($e in $csvEventSet) {
        if (-not $docEventSet.Contains($e)) {
            $findings.Add("EVENT: csv/event_catalog.csv has '$e' which is missing from the docs/09 event table")
        }
    }

    # --- Check 5: docs/18 epics equal the union of both epic CSVs ---
    $checkCount++
    $epicValue = New-Object System.Collections.Generic.List[string]
    foreach ($epicCsv in @('csv/core_epics_stories.csv', 'csv/core_phase2_epics_stories.csv')) {
        foreach ($row in (Import-Csv -Path (Get-SpecFile $epicCsv))) {
            $epicValue.Add($row.epic.Trim())
        }
    }
    $csvEpicSet = Get-StringSet -Value $epicValue.ToArray()
    $epicDocLine = @(Get-Content -Path (Get-SpecFile 'docs/18_EPICS_AND_STORIES.md'))
    $docEpicValue = New-Object System.Collections.Generic.List[string]
    foreach ($line in $epicDocLine) {
        if ($line -match '^\s*\d+\.\s+(.+?)\s*$') { $docEpicValue.Add($Matches[1].Trim()) }
    }
    $docEpicSet = Get-StringSet -Value $docEpicValue.ToArray()
    foreach ($epic in $docEpicSet) {
        if (-not $csvEpicSet.Contains($epic)) {
            $findings.Add("EPIC: docs/18 lists '$epic' which is not an epic in either epic CSV")
        }
    }
    foreach ($epic in $csvEpicSet) {
        if (-not $docEpicSet.Contains($epic)) {
            $findings.Add("EPIC: epic '$epic' is in an epic CSV but missing from docs/18")
        }
    }
}
catch {
    Write-Host "Spec consistency ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

if ($findings.Count -gt 0) {
    Write-Host "Spec consistency FAILED: $($findings.Count) drift finding(s) across $checkCount check(s)." -ForegroundColor Red
    foreach ($finding in $findings) {
        Write-Host "  - $finding"
    }
    Write-Host 'Reconcile to the single source of truth per concern (see docs/24_SPEC_CONSISTENCY.md).'
    exit 1
}

Write-Host "Spec consistency passed: $checkCount check(s), route/table/event/epic specs agree." -ForegroundColor Green
exit 0
