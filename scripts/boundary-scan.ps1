#requires -Version 5.1
<#
.SYNOPSIS
    Boundary scan for the LiveCore Core Platform.

.DESCRIPTION
    Scans every TRACKED Core source file for the forbidden vertical and
    brand/platform terms defined in csv/forbidden_core_terms.csv.

    The Core Platform must stay product-neutral: vertical domain language and the
    brand/platform names (the product, platform and tabletop vertical names the
    terms file lists) may appear only in the documentation that explains the
    boundary - the docs/ and csv/ trees and the root documentation files
    (README.md, AGENTS.md, LICENSE, CHANGELOG.md) - never in Core source.

    Files are enumerated with `git ls-files`, so only TRACKED source is scanned.
    Gitignored local tooling (for example scripts/story-loop.ps1, whose prompt
    template legitimately names the verticals) is never seen, and a stray
    working-tree file can neither widen nor narrow the scan. The scan covers
    every tracked text source - including Dockerfiles (apps/*/Dockerfile and
    *.Dockerfile such as apps/api/Migrations.Dockerfile) - and excludes only the
    documentation above and machine-generated dependency manifests.

    Exits with code 0 when the tree is clean, 1 when violations are found, and 2
    on configuration errors (terms file missing/empty, or no git work tree to
    enumerate tracked source). The git enumeration is fail-closed: if the tracked
    file set cannot be determined the scan errors out rather than passing.

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
    # -ErrorAction Continue so the diagnostic does not raise a terminating error
    # under $ErrorActionPreference='Stop' (which would exit 1); these are the
    # documented fail-closed configuration errors and must exit 2.
    Write-Error "Forbidden terms file not found: $termsCsvPath" -ErrorAction Continue
    exit 2
}

$terms = @(
    Import-Csv -Path $termsCsvPath |
        ForEach-Object { $_.term } |
        Where-Object { $_ -and $_.Trim() -ne '' } |
        ForEach-Object { $_.Trim() }
)

if ($terms.Count -eq 0) {
    Write-Error "No forbidden terms loaded from $termsCsvPath" -ErrorAction Continue
    exit 2
}

# Build one case-insensitive, word-bounded alternation.
# - Underscores in a CSV term also match an optional space, underscore or
#   hyphen, so a term of the form 'first_second' catches 'first_second',
#   'first second', 'first-second' and (after CamelCase splitting)
#   'FirstSecond'.
# - Simple plural forms are matched too ('...s', '...es', 'y' -> 'ies').
# Scanned lines are normalized by splitting CamelCase, uppercase acronym
# runs and snake_case (underscores) before matching, so compound identifiers
# cannot hide a forbidden term.
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

# Enumerate TRACKED files only, via `git ls-files`. This is the whole point of
# the boundary scan's coverage guarantee: gitignored local tooling (such as
# scripts/story-loop.ps1, whose prompt template legitimately names the
# verticals) is excluded because git never tracks it, and the scan can only ever
# see committed/staged Core source. It is fail-closed: if git is unavailable or
# the directory is not a work tree, the scan errors out (exit 2) rather than
# silently passing on an unscanned tree.
$trackedFiles = $null
try {
    $trackedFiles = @(& git -C $RepoRoot ls-files)
}
catch {
    Write-Error "Unable to enumerate tracked files via 'git ls-files' in ${RepoRoot}: $($_.Exception.Message)" -ErrorAction Continue
    exit 2
}
if ($LASTEXITCODE -ne 0) {
    Write-Error "git ls-files exited with code $LASTEXITCODE in $RepoRoot; the boundary scan needs a git work tree." -ErrorAction Continue
    exit 2
}

$trackedFiles = @(
    $trackedFiles |
        Where-Object { $_ -and $_.Trim() -ne '' } |
        ForEach-Object { $_.Trim() }
)
if ($trackedFiles.Count -eq 0) {
    Write-Error "git ls-files returned no tracked files under $RepoRoot" -ErrorAction Continue
    exit 2
}

# Generated/vendor output that must not be scanned. git ls-files does not return
# gitignored build output, so this is defensive: a force-tracked artifact under
# one of these directories is still skipped.
$excludedDirPattern = '(^|[\\/])(bin|obj|node_modules|dist|coverage|TestResults|\.git)([\\/]|$)'

# Generated dependency manifests: machine-written, not authored Core source.
# Their base64 content hashes carry incidental two-letter uppercase runs that can
# look like a forbidden acronym after the CamelCase/acronym splitting below, so
# they are excluded like the generated output above (NuGet packages.lock.json
# from CORE-DEP-003, and the pnpm lock file).
$excludedFileNames = @('packages.lock.json', 'pnpm-lock.yaml')

# Documentation that is ALLOWED to name the forbidden terms in order to explain
# the boundary: the docs/ and csv/ trees, and the four root documentation files
# that set out or narrate the rule (AGENTS.md and README.md state it; LICENSE
# and CHANGELOG.md are narrative). git ls-files paths are repo-relative with
# forward slashes.
$excludedDocPathPattern = '^(?:docs|csv)/'
$excludedDocRootFiles = @('README.md', 'AGENTS.md', 'LICENSE', 'CHANGELOG.md')

# Text files considered Core source. Extensionless files (for example a plain
# Dockerfile) are scanned too, and *.Dockerfile (the migrations runner image,
# apps/api/Migrations.Dockerfile) is in scope.
$sourceExtensions = @(
    '.cs', '.csproj', '.sln', '.slnx', '.props', '.targets', '.razor', '.cshtml',
    '.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs',
    '.json', '.yml', '.yaml', '.xml', '.config', '.resx',
    '.css', '.scss', '.html', '.svg',
    '.ps1', '.psm1', '.sh', '.sql', '.http', '.md', '.txt', '.env', '.dockerfile'
)

$violations = New-Object System.Collections.Generic.List[object]
$scannedFileCount = 0

foreach ($relativePath in $trackedFiles) {
    if ($relativePath -match $excludedDirPattern) { continue }
    if ($relativePath -match $excludedDocPathPattern) { continue }
    if ($excludedDocRootFiles -contains $relativePath) { continue }

    $fileName = [System.IO.Path]::GetFileName($relativePath)
    if ($excludedFileNames -contains $fileName) { continue }

    $extension = [System.IO.Path]::GetExtension($fileName)
    if (-not ($extension -eq '' -or $sourceExtensions -contains $extension.ToLowerInvariant())) {
        continue
    }

    $fullPath = Join-Path $RepoRoot $relativePath
    # A tracked path can be absent on disk (e.g. mid-rename); skip rather than throw.
    if (-not (Test-Path -LiteralPath $fullPath)) { continue }

    $scannedFileCount++
    $lines = @(Get-Content -LiteralPath $fullPath)
    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $line = $lines[$lineIndex]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        # Match the raw line plus normalized variants so compound
        # identifiers cannot hide a forbidden term:
        # - CamelCase split alone keeps acronym plurals intact,
        # - acronym split before CamelCase split exposes terms hidden in
        #   leading uppercase runs (e.g. 'APIClient' -> 'API Client'),
        # - snake_case split exposes terms hidden between underscores
        #   (e.g. 'prefix_term_suffix'): '_' is a word character, so the
        #   word-bounded pattern cannot match a term flanked by underscores
        #   until the underscores are turned into spaces. The acronym +
        #   CamelCase split is also applied to the snake-split variant so
        #   mixed snake_case + CamelCase identifiers are caught too
        #   (e.g. 'prefix_termSuffix').
        $camelOnly = $camelSplitRegex.Replace($line, '$1 $2')
        $acronymThenCamel = $camelSplitRegex.Replace(
            $acronymSplitRegex.Replace($line, '$1 $2'), '$1 $2')
        $snakeSplit = $line -replace '_', ' '
        $snakeThenCamel = $camelSplitRegex.Replace(
            $acronymSplitRegex.Replace($snakeSplit, '$1 $2'), '$1 $2')
        $lineVariants = @(
            $line, $camelOnly, $acronymThenCamel, $snakeSplit, $snakeThenCamel) |
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
