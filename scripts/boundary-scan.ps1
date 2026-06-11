#requires -Version 5.1
<#
.SYNOPSIS
    Boundary scan for the LiveCore Core Platform.

.DESCRIPTION
    Scans Core source directories (apps, packages, tests, scripts, .github)
    for forbidden vertical terms defined in csv/forbidden_core_terms.csv.

    The Core Platform must stay product-neutral; vertical domain language
    may appear only in documentation that explains the boundary (docs/, csv/,
    root README), never in Core source.

    Exits with code 0 when the tree is clean, 1 when violations are found,
    and 2 on configuration errors.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/boundary-scan.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/boundary-scan.ps1
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

$termsCsvPath = Join-Path $RepoRoot 'csv/forbidden_core_terms.csv'
if (-not (Test-Path -Path $termsCsvPath)) {
    Write-Error "Forbidden terms file not found: $termsCsvPath"
    exit 2
}

$terms = @(
    Import-Csv -Path $termsCsvPath |
        ForEach-Object { $_.term } |
        Where-Object { $_ -and $_.Trim() -ne '' } |
        ForEach-Object { $_.Trim() }
)

if ($terms.Count -eq 0) {
    Write-Error "No forbidden terms loaded from $termsCsvPath"
    exit 2
}

# Build one case-insensitive, word-bounded alternation.
# - Underscores in a CSV term also match an optional space, underscore or
#   hyphen, so a term of the form 'first_second' catches 'first_second',
#   'first second', 'first-second' and (after CamelCase splitting)
#   'FirstSecond'.
# - Simple plural forms are matched too ('...s', '...es', 'y' -> 'ies').
# Scanned lines are normalized by splitting CamelCase and uppercase acronym
# runs before matching, so compound identifiers cannot hide a forbidden term.
$patterns = foreach ($term in $terms) {
    $pattern = [regex]::Escape($term) -replace '_', '[ _-]?'
    if ($pattern.EndsWith('y')) {
        $pattern = $pattern.Substring(0, $pattern.Length - 1) + '(?:y|ies)'
    }
    else {
        $pattern = $pattern + '(?:e?s)?'
    }
    $pattern
}
$combinedPattern = '\b(?:' + ($patterns -join '|') + ')\b'
$combinedRegex = New-Object System.Text.RegularExpressions.Regex(
    $combinedPattern,
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
)
$camelSplitRegex = New-Object System.Text.RegularExpressions.Regex('([a-z0-9])([A-Z])')
# Splits an uppercase acronym run from a following capitalized word, so
# compound identifiers that start with an acronym are matched as words too
# (e.g. 'APIClient' -> 'API Client', 'XMLHttpRequest' -> 'XML Http Request').
$acronymSplitRegex = New-Object System.Text.RegularExpressions.Regex('([A-Z]+)([A-Z][a-z])')

# Core source locations. docs/ and csv/ are documentation and intentionally
# excluded: forbidden terms may appear there only to explain the boundary.
$sourceDirNames = @('apps', 'packages', 'tests', 'scripts', '.github')
$sourceDirs = @(
    $sourceDirNames |
        ForEach-Object { Join-Path $RepoRoot $_ } |
        Where-Object { Test-Path -Path $_ }
)

if ($sourceDirs.Count -eq 0) {
    Write-Error "None of the Core source directories ($($sourceDirNames -join ', ')) exist under $RepoRoot"
    exit 2
}

# Generated/vendor output that must not be scanned.
$excludedDirPattern = '[\\/](bin|obj|node_modules|dist|coverage|TestResults|\.git)([\\/]|$)'

# Text files considered Core source. Extensionless files are scanned too.
$sourceExtensions = @(
    '.cs', '.csproj', '.sln', '.slnx', '.props', '.targets', '.razor', '.cshtml',
    '.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs',
    '.json', '.yml', '.yaml', '.xml', '.config', '.resx',
    '.css', '.scss', '.html', '.svg',
    '.ps1', '.psm1', '.sh', '.sql', '.http', '.md', '.txt', '.env'
)

$violations = New-Object System.Collections.Generic.List[object]
$scannedFileCount = 0

foreach ($dir in $sourceDirs) {
    # -Force keeps Windows and Linux consistent: on Linux, pwsh treats
    # dot-prefixed files as hidden and would silently skip them otherwise.
    $files = Get-ChildItem -Path $dir -Recurse -File -Force | Where-Object {
        $_.FullName -notmatch $excludedDirPattern -and
        (
            $_.Extension -eq '' -or
            $sourceExtensions -contains $_.Extension.ToLowerInvariant()
        )
    }

    foreach ($file in $files) {
        $scannedFileCount++
        $relativePath = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
        $lines = @(Get-Content -Path $file.FullName)
        for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
            $line = $lines[$lineIndex]
            if ([string]::IsNullOrWhiteSpace($line)) { continue }

            # Match the raw line plus normalized variants so compound
            # identifiers cannot hide a forbidden term:
            # - CamelCase split alone keeps acronym plurals intact,
            # - acronym split before CamelCase split exposes terms hidden in
            #   leading uppercase runs (e.g. 'APIClient' -> 'API Client').
            $camelOnly = $camelSplitRegex.Replace($line, '$1 $2')
            $acronymThenCamel = $camelSplitRegex.Replace(
                $acronymSplitRegex.Replace($line, '$1 $2'), '$1 $2')
            $lineVariants = @($line, $camelOnly, $acronymThenCamel) |
                Select-Object -Unique

            # Report each forbidden term at most once per line, even when
            # several variants surface the same occurrence.
            $reportedTerms = @{}
            foreach ($variant in $lineVariants) {
                foreach ($match in $combinedRegex.Matches($variant)) {
                    $termKey = $match.Value.ToLowerInvariant()
                    if ($reportedTerms.ContainsKey($termKey)) { continue }
                    $reportedTerms[$termKey] = $true
                    $violations.Add([pscustomobject]@{
                        File = $relativePath
                        Line = $lineIndex + 1
                        Term = $match.Value
                        Text = $line.Trim()
                    })
                }
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "Boundary scan FAILED: $($violations.Count) forbidden term occurrence(s) found in Core source." -ForegroundColor Red
    foreach ($violation in $violations) {
        Write-Host ("  {0}:{1} -> '{2}' in: {3}" -f $violation.File, $violation.Line, $violation.Term, $violation.Text)
    }
    Write-Host 'Core source must stay product-neutral. See docs/04_PRODUCT_BOUNDARIES.md and csv/forbidden_core_terms.csv.'
    exit 1
}

Write-Host "Boundary scan passed: $scannedFileCount file(s) scanned, no forbidden vertical terms found." -ForegroundColor Green
exit 0
