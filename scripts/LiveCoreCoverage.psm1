#requires -Version 5.1

<#
.SYNOPSIS
    Code-coverage gate logic for the LiveCore test suite (CORE-TST-001): merges
    one or more Cobertura coverage reports into a single production
    line-coverage number and decides whether it meets a configured minimum
    threshold.

.DESCRIPTION
    CI collects coverage with the already-referenced coverlet.collector
    (`dotnet test --collect:"XPlat Code Coverage"`), which writes a
    coverage.cobertura.xml per test project. The *decision* that turns those
    reports into a coverage percentage and a pass/fail verdict is this module's
    pure functions, so it is deterministically testable from seeded fixtures
    without running the test suite (scripts/test-coverage-gate.ps1).

    The number is production-focused and de-duplicated:
      * lines are merged across every report by (assembly, file, line) taking the
        maximum hit count, so a line exercised by the integration suite but not
        the unit suite counts as covered exactly once - there is no double
        counting when several test projects cover the same assembly;
      * test assemblies (name ending in "Tests") and generated EF migration files
        (a path under a Migrations directory) are excluded from the denominator,
        so the gate measures hand-written production code rather than generated or
        test code.

    Fail-closed: a missing or malformed report throws, and a report set that
    measures no production line is never a pass.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

function ConvertFrom-LiveCoreCoberturaText {
    # Parses Cobertura XML text into an XML document, throwing a clear error on
    # malformed input so the fail-closed callers turn an unparseable report into a
    # block rather than a silent pass.
    [CmdletBinding()]
    [OutputType([xml])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        throw 'Empty coverage report: expected Cobertura XML content.'
    }
    try {
        return [xml]$Text
    }
    catch {
        throw "Malformed Cobertura XML coverage report: $($_.Exception.Message)"
    }
}

function Get-LiveCoreCoverageModel {
    <#
    .SYNOPSIS
        Merges one or more Cobertura reports (from paths or literal content) into
        a single de-duplicated, production-focused line-coverage model.
    .OUTPUTS
        A PSCustomObject with LinesCovered (int), LinesValid (int),
        LineCoveragePercent (double, 0..100) and Assemblies (the measured
        assembly names).
    #>
    [CmdletBinding(DefaultParameterSetName = 'Path')]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
        [string[]]$Path,

        [Parameter(Mandatory = $true, ParameterSetName = 'Content')]
        [string[]]$Content,

        # Assemblies whose name matches this regex are excluded from the
        # production coverage number (the test projects, named "*Tests").
        [string]$ExcludeAssemblyPattern = 'Tests$',

        # Files whose normalized (forward-slash) path matches this regex are
        # excluded: generated EF migrations live under a Migrations directory and
        # are not hand-written production code.
        [string]$ExcludeFilePattern = '(^|/)Migrations/'
    )

    if ($PSCmdlet.ParameterSetName -eq 'Path') {
        $documents = foreach ($singlePath in $Path) {
            if (-not (Test-Path -LiteralPath $singlePath)) {
                throw "Coverage report not found: $singlePath"
            }
            ConvertFrom-LiveCoreCoberturaText -Text ([System.IO.File]::ReadAllText($singlePath))
        }
    }
    else {
        $documents = foreach ($singleContent in $Content) {
            ConvertFrom-LiveCoreCoberturaText -Text $singleContent
        }
    }

    # Merge line hits across every report by (assembly, type, source file, line).
    # A line is covered when ANY report recorded a hit, so overlapping coverage
    # from several test projects is counted once, not multiplied.
    #
    # The key deliberately uses the fully-qualified type name plus the file
    # *basename*, not the full path: a test project that references several apps
    # roots its recorded source paths at a common ancestor ("api/Assets/Asset.cs"),
    # while a project that references one app roots them at the project
    # ("Assets/Asset.cs"). Both forms denote the same line, so keying on the
    # build-invariant type name and basename merges them instead of double counting
    # the file once per path form. The basename still distinguishes the files of a
    # partial type, which share a type name but live in different files.
    $mergedHits = @{}
    $assemblies = New-Object System.Collections.Generic.HashSet[string]

    foreach ($document in $documents) {
        if ($null -eq $document -or $null -eq $document.coverage) { continue }
        foreach ($package in @($document.coverage.packages.package)) {
            if ($null -eq $package) { continue }
            $assemblyName = [string]$package.name
            if ($assemblyName -and $assemblyName -match $ExcludeAssemblyPattern) { continue }
            $null = $assemblies.Add($assemblyName)
            foreach ($class in @($package.classes.class)) {
                if ($null -eq $class) { continue }
                $fileName = ([string]$class.filename) -replace '\\', '/'
                if ($fileName -and $fileName -match $ExcludeFilePattern) { continue }
                $className = [string]$class.name
                $baseName = $fileName
                $lastSlash = $fileName.LastIndexOf('/')
                if ($lastSlash -ge 0) { $baseName = $fileName.Substring($lastSlash + 1) }
                foreach ($line in @($class.lines.line)) {
                    if ($null -eq $line) { continue }
                    $key = "$assemblyName|$className|$baseName|$($line.number)"
                    $hits = 0
                    [void][int]::TryParse([string]$line.hits, [ref]$hits)
                    if (-not $mergedHits.ContainsKey($key)) {
                        $mergedHits[$key] = $hits
                    }
                    elseif ($hits -gt $mergedHits[$key]) {
                        $mergedHits[$key] = $hits
                    }
                }
            }
        }
    }

    $linesValid = $mergedHits.Count
    $linesCovered = 0
    foreach ($value in $mergedHits.Values) {
        if ($value -gt 0) { $linesCovered++ }
    }

    if ($linesValid -gt 0) {
        $percent = [math]::Round(($linesCovered / [double]$linesValid) * 100, 2)
    }
    else {
        $percent = 0
    }

    return [pscustomobject]@{
        LinesCovered        = $linesCovered
        LinesValid          = $linesValid
        LineCoveragePercent = $percent
        Assemblies          = @($assemblies)
    }
}

function Test-LiveCoreCoverageGate {
    <#
    .SYNOPSIS
        Decides whether a merged coverage model meets the minimum line-coverage
        threshold.
    .DESCRIPTION
        Passes only when production lines were actually measured AND the measured
        percentage is at least the minimum. Fail-closed: a report set that
        measured no production line never passes.
    .OUTPUTS
        A PSCustomObject with Passed (bool), Measured (bool), ActualPercent,
        MinimumPercent, Shortfall, LinesCovered and LinesValid.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true)][psobject]$Model,
        [Parameter(Mandatory = $true)][double]$MinimumLineCoveragePercent
    )

    $measured = ($Model.LinesValid -gt 0)
    $passed = $measured -and ($Model.LineCoveragePercent -ge $MinimumLineCoveragePercent)
    if ($passed) {
        $shortfall = 0
    }
    else {
        $shortfall = [math]::Round($MinimumLineCoveragePercent - $Model.LineCoveragePercent, 2)
    }

    return [pscustomobject]@{
        Passed         = $passed
        Measured       = $measured
        ActualPercent  = $Model.LineCoveragePercent
        MinimumPercent = $MinimumLineCoveragePercent
        Shortfall      = $shortfall
        LinesCovered   = $Model.LinesCovered
        LinesValid     = $Model.LinesValid
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreCoverageModel, `
    Test-LiveCoreCoverageGate
