#requires -Version 5.1

<#
.SYNOPSIS
    Tests the destructive migration Down() lint (CORE-DR-004).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreMigrationLint.psm1 - no external test
    framework, so it runs as a CI gate and locally on both pwsh and Windows
    PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

    It proves the analysis logic over fixtures (a Down() dropping a column or a
    table is flagged; an Up() drop is ignored; an index/constraint-only Down()
    and an empty Down() are not flagged; the baseline distinguishes reviewed,
    unreviewed, changed and stale destructive Down()s) and then guards the real
    repository state: the checked-in migrations reconcile cleanly against
    csv/migration_destructive_down_review.csv, so a new unacknowledged destructive
    Down() fails this test too.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-migration-down-lint.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreMigrationLint.psm1') -Force
$repoRoot = Split-Path -Parent $scriptDir

$failures = New-Object System.Collections.Generic.List[string]

function AssertEqual {
    param([string]$Expected, [string]$Actual, [string]$Because)
    if ($Expected -ceq $Actual) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because`n      expected: '$Expected'`n      actual:   '$Actual'")
    }
}

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because")
    }
}

# Builds the source of a fixture migration class: a DropColumn in Up() (which
# must be IGNORED) and the given Down() body, including an array-initializer
# brace to prove brace matching.
function Get-FixtureMigrationSource {
    param([string]$DownBody)
    return @"
using Microsoft.EntityFrameworkCore.Migrations;

namespace LiveCore.Api.Persistence.Migrations;

public partial class Fixture : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ignored_in_up", table: "t");
        migrationBuilder.CreateIndex(name: "ix", table: "t", columns: new[] { "a", "b" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
$DownBody
    }
}
"@
}

# --- Analysis: a Down() dropping a column is flagged, and Up() is ignored. ---
$dropColumn = Get-FixtureMigrationSource -DownBody '        migrationBuilder.DropColumn(name: "status", table: "workspaces");'
AssertEqual 'DropColumn' (((Get-LiveCoreDestructiveDownOperation -Source $dropColumn)) -join ';') `
    'a Down() dropping a column is flagged as destructive (and the Up() DropColumn is ignored)'

# --- Analysis: a Down() dropping a table is flagged. ---
$dropTable = Get-FixtureMigrationSource -DownBody '        migrationBuilder.DropTable(name: "workspaces");'
AssertEqual 'DropTable' (((Get-LiveCoreDestructiveDownOperation -Source $dropTable)) -join ';') `
    'a Down() dropping a table is flagged as destructive'

# --- Analysis: both drops are reported, sorted and de-duplicated. ---
$dropBoth = Get-FixtureMigrationSource -DownBody @'
        migrationBuilder.DropColumn(name: "c", table: "t");
        migrationBuilder.DropTable(name: "t2");
        migrationBuilder.DropColumn(name: "d", table: "t");
'@
AssertEqual 'DropColumn;DropTable' (((Get-LiveCoreDestructiveDownOperation -Source $dropBoth)) -join ';') `
    'multiple destructive operations are reported sorted and de-duplicated'

# --- Analysis: an index/constraint-only Down() loses no data and is NOT flagged. ---
$nonDestructive = Get-FixtureMigrationSource -DownBody @'
        migrationBuilder.DropForeignKey(name: "fk", table: "t");
        migrationBuilder.DropIndex(name: "ix", table: "t");
        migrationBuilder.CreateIndex(name: "ix2", table: "t", column: "a");
'@
AssertEqual '' (((Get-LiveCoreDestructiveDownOperation -Source $nonDestructive)) -join ';') `
    'an index/foreign-key-only Down() is not flagged (no row data is lost)'

# --- Analysis: an empty Down() and a class with no Down() are not flagged. ---
$emptyDown = Get-FixtureMigrationSource -DownBody ''
AssertEqual '' (((Get-LiveCoreDestructiveDownOperation -Source $emptyDown)) -join ';') `
    'an empty Down() is not flagged'
AssertEqual '' (((Get-LiveCoreDestructiveDownOperation -Source 'public class X { }')) -join ';') `
    'a class with no Down() is not flagged'

# --- Review verdict over a throwaway migrations directory and baseline. ---
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-down-lint-" + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
try {
    # One destructive migration, one harmless one, plus a designer file that must
    # be ignored.
    Set-Content -LiteralPath (Join-Path $tempDir '20260101000000_AddThing.cs') -Value $dropTable -NoNewline
    Set-Content -LiteralPath (Join-Path $tempDir '20260102000000_TuneIndex.cs') -Value $nonDestructive -NoNewline
    Set-Content -LiteralPath (Join-Path $tempDir '20260101000000_AddThing.Designer.cs') -Value 'public class D { void Down() { } }' -NoNewline

    $baselinePath = Join-Path $tempDir 'baseline.csv'

    # No baseline -> the destructive Down() is unreviewed and the tree is dirty.
    $noBaseline = Get-LiveCoreMigrationDownReview -MigrationsDirectory $tempDir -BaselinePath ''
    AssertTrue ($noBaseline.Destructive.Count -eq 1) 'exactly one destructive migration is found (the designer file is ignored)'
    AssertTrue ($noBaseline.Unreviewed.Count -eq 1) 'with no baseline the destructive Down() is unreviewed'
    AssertTrue (-not $noBaseline.IsClean) 'an unreviewed destructive Down() makes the lint fail'

    # Acknowledged baseline -> clean.
    Set-Content -LiteralPath $baselinePath -Value "migration,operations,policy`n20260101000000_AddThing,DropTable,roll-forward-only`n" -NoNewline
    $acknowledged = Get-LiveCoreMigrationDownReview -MigrationsDirectory $tempDir -BaselinePath $baselinePath
    AssertTrue ($acknowledged.IsClean) 'an acknowledged destructive Down() passes the lint'
    AssertTrue ($acknowledged.Unreviewed.Count -eq 0) 'an acknowledged destructive Down() is not reported as unreviewed'

    # Baseline with the wrong operations -> changed (re-review).
    Set-Content -LiteralPath $baselinePath -Value "migration,operations,policy`n20260101000000_AddThing,DropColumn,roll-forward-only`n" -NoNewline
    $changed = Get-LiveCoreMigrationDownReview -MigrationsDirectory $tempDir -BaselinePath $baselinePath
    AssertTrue ($changed.Changed.Count -eq 1) 'a baseline whose operations no longer match is flagged as changed'
    AssertTrue (-not $changed.IsClean) 'a changed destructive Down() makes the lint fail'

    # Baseline acknowledging a migration with no destructive Down() -> stale.
    Set-Content -LiteralPath $baselinePath -Value "migration,operations,policy`n20260101000000_AddThing,DropTable,roll-forward-only`n20260102000000_TuneIndex,DropTable,roll-forward-only`n" -NoNewline
    $stale = Get-LiveCoreMigrationDownReview -MigrationsDirectory $tempDir -BaselinePath $baselinePath
    AssertTrue ($stale.Stale.Count -eq 1) 'a baseline row for a non-destructive migration is flagged as stale'
    AssertTrue (-not $stale.IsClean) 'a stale baseline row makes the lint fail'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Guard the real repository state against the checked-in baseline. ---
$realMigrations = Join-Path $repoRoot 'apps/api/Persistence/Migrations'
$realBaseline = Join-Path $repoRoot 'csv/migration_destructive_down_review.csv'
$real = Get-LiveCoreMigrationDownReview -MigrationsDirectory $realMigrations -BaselinePath $realBaseline
AssertTrue ($real.Destructive.Count -gt 0) 'the real migrations contain destructive Down() methods the lint detects'
AssertTrue ($real.IsClean) 'every real destructive Down() is acknowledged in csv/migration_destructive_down_review.csv'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Migration Down() lint tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Migration Down() lint tests passed: destructive Down() detection and baseline reconciliation behave as documented.' -ForegroundColor Green
exit 0
