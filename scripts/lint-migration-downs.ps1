#requires -Version 5.1

<#
.SYNOPSIS
    CI lint that flags destructive EF Core migration Down() methods for review
    (CORE-DR-004).

.DESCRIPTION
    Scans apps/api/Persistence/Migrations for migration classes whose Down()
    drops a table or a column (a data-destroying operation) and reconciles them
    against the acknowledgement baseline csv/migration_destructive_down_review.csv.

    The documented rollback policy is roll-forward-only plus restore-from-backup
    (docs/13_SELF_HOSTING_REQUIREMENTS.md): a Down() is never run in production
    because it would discard committed data, so this lint does not forbid a
    destructive Down() - it forces every one to be consciously reviewed. The
    build passes when every destructive Down() is acknowledged in the baseline,
    and fails when a NEW destructive Down() is unreviewed, an acknowledged one's
    operations changed, or a baseline row is stale.

    Run with -UpdateBaseline to (re)write the baseline from the current
    migrations - the way a reviewer acknowledges a new destructive Down() after
    confirming it follows the expand/contract guidance.

    Exits 0 when the tree is clean (or after -UpdateBaseline), 1 when a
    destructive Down() needs review, and 2 on a configuration error.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-migration-downs.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-migration-downs.ps1 -UpdateBaseline
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot,

    # Regenerate the baseline from the current destructive Down() set.
    [switch]$UpdateBaseline
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
else {
    $scriptDir = Join-Path $RepoRoot 'scripts'
}

$RepoRoot = (Resolve-Path -Path $RepoRoot).Path

Import-Module (Join-Path $scriptDir 'LiveCoreMigrationLint.psm1') -Force

$migrationsDir = Join-Path $RepoRoot 'apps/api/Persistence/Migrations'
$baselinePath = Join-Path $RepoRoot 'csv/migration_destructive_down_review.csv'

if (-not (Test-Path -Path $migrationsDir)) {
    Write-Error "Migrations directory not found: $migrationsDir"
    exit 2
}

if ($UpdateBaseline) {
    # Acknowledge the current destructive Down() set: derive it directly from the
    # migrations (no baseline input) and write it back as the reviewed baseline.
    $current = Get-LiveCoreMigrationDownReview -MigrationsDirectory $migrationsDir -BaselinePath ''
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('migration,operations,policy')
    foreach ($item in $current.Destructive) {
        $lines.Add(('{0},{1},{2}' -f $item.Migration, $item.Operations, 'roll-forward-only'))
    }
    # Write LF, UTF-8 without BOM, to match the repository's line-ending policy
    # (.gitattributes) regardless of host platform.
    $content = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText($baselinePath, $content, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Wrote baseline: $($current.Destructive.Count) acknowledged destructive Down() migration(s) -> csv/migration_destructive_down_review.csv" -ForegroundColor Green
    exit 0
}

$review = Get-LiveCoreMigrationDownReview -MigrationsDirectory $migrationsDir -BaselinePath $baselinePath

Write-Host "Migration Down() review: $($review.Destructive.Count) migration(s) with a destructive Down() (DropColumn/DropTable)."
foreach ($item in $review.Destructive) {
    Write-Host ("  {0} -> {1}" -f $item.Migration, $item.Operations)
}

if ($review.IsClean) {
    Write-Host 'Migration Down() lint passed: every destructive Down() is acknowledged under the roll-forward-only policy (csv/migration_destructive_down_review.csv).' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host 'Migration Down() lint FAILED: a destructive Down() needs review.' -ForegroundColor Red

foreach ($item in $review.Unreviewed) {
    Write-Host ("  UNREVIEWED: {0} drops data in Down() ({1}) and is not acknowledged in the baseline." -f $item.Migration, $item.Operations)
}
foreach ($item in $review.Changed) {
    Write-Host ("  CHANGED: {0} destructive Down() is now '{1}' but the baseline acknowledged '{2}'." -f $item.Migration, $item.Operations, $item.Baseline)
}
foreach ($key in $review.Stale) {
    Write-Host ("  STALE: baseline acknowledges {0} but it no longer has a destructive Down()." -f $key)
}

Write-Host ''
Write-Host 'A destructive Down() is never run in production (roll-forward-only + restore-from-backup; see'
Write-Host 'docs/13_SELF_HOSTING_REQUIREMENTS.md, "Migration rollback policy"). Confirm the schema change follows'
Write-Host 'the expand/contract guidance, then acknowledge it by running:'
Write-Host '  pwsh -NoProfile -File scripts/lint-migration-downs.ps1 -UpdateBaseline'
exit 1
