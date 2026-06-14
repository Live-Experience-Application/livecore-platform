#requires -Version 5.1

<#
.SYNOPSIS
    Flags destructive EF Core migration Down() methods for review (CORE-DR-004).

.DESCRIPTION
    The Core applies migrations FORWARD ONLY (the migrations runner image's
    efbundle entrypoint), and the documented rollback policy is roll-forward-only
    plus restore-from-backup: a Down() is never run in production because every
    checked-in Down() drops the table or column its Up() added and would discard
    committed tenant data and the append-only systems of record
    (see docs/13_SELF_HOSTING_REQUIREMENTS.md, "Migration rollback policy").

    This module is the pure analysis behind the CI lint. It parses a migration
    class's Down() body (never its Up()), reports the data-destroying operations
    it performs, and reconciles them against an acknowledgement baseline so a NEW
    destructive Down() cannot merge without a conscious review under the policy.

    A "data-destroying" operation is one that discards committed rows or columns
    irreversibly: DropColumn and DropTable. Index/constraint drops (DropIndex,
    DropForeignKey, DropPrimaryKey) lose no row data and are rebuildable, and
    AlterColumn is not unconditionally destructive, so neither is flagged - the
    lint targets exactly the operations the acceptance criterion names ("a Down()
    dropping a column").

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

# The data-destroying Down() operations the lint flags for review.
$script:DestructiveDownOperation = @('DropColumn', 'DropTable')

function Get-LiveCoreMigrationDownBody {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Source
    )

    # Isolate the body of the Down(MigrationBuilder) method by brace matching, so
    # operations in Up() are never counted. Balanced inner braces (array/object
    # initializers such as 'columns: new[] { ... }') are handled by the depth
    # counter; the bodies carry no unbalanced braces inside string literals.
    # Returns '' when the class has no Down() method.
    $match = [regex]::Match($Source, 'void\s+Down\s*\(')
    if (-not $match.Success) {
        return ''
    }

    $open = $Source.IndexOf('{', $match.Index + $match.Length)
    if ($open -lt 0) {
        return ''
    }

    $depth = 0
    for ($i = $open; $i -lt $Source.Length; $i++) {
        $ch = $Source[$i]
        if ($ch -eq '{') {
            $depth++
        }
        elseif ($ch -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return $Source.Substring($open + 1, $i - $open - 1)
            }
        }
    }

    throw 'Could not find the end of the Down() method body (unbalanced braces).'
}

function Get-LiveCoreMigrationDownOperation {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Source
    )

    $body = Get-LiveCoreMigrationDownBody -Source $Source
    if ([string]::IsNullOrWhiteSpace($body)) {
        return , @()
    }

    $names = New-Object System.Collections.Generic.List[string]
    foreach ($match in [regex]::Matches($body, 'migrationBuilder\.([A-Za-z]+)\s*\(')) {
        $names.Add($match.Groups[1].Value)
    }
    return , $names.ToArray()
}

function Get-LiveCoreDestructiveDownOperation {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Source
    )

    $operations = Get-LiveCoreMigrationDownOperation -Source $Source
    $destructive = @(
        $operations |
            Where-Object { $script:DestructiveDownOperation -contains $_ } |
            Select-Object -Unique |
            Sort-Object
    )
    return , $destructive
}

function Get-LiveCoreMigrationDownReview {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        # Directory holding the EF Core migration classes.
        [Parameter(Mandatory = $true)]
        [string]$MigrationsDirectory,

        # CSV baseline of acknowledged destructive Down() migrations
        # (columns: migration, operations, policy). An empty path means "no
        # baseline", so every destructive Down() is reported as unreviewed.
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$BaselinePath
    )

    if (-not (Test-Path -Path $MigrationsDirectory)) {
        throw "Migrations directory not found: $MigrationsDirectory"
    }

    # Migration classes only: skip the EF *.Designer.cs designers and the model
    # snapshot, which carry no Up()/Down().
    $files = @(
        Get-ChildItem -Path $MigrationsDirectory -Filter '*.cs' -File |
            Where-Object {
                $_.Name -notlike '*.Designer.cs' -and
                $_.Name -notlike '*ModelSnapshot.cs'
            } |
            Sort-Object -Property Name
    )

    $destructive = New-Object System.Collections.Generic.List[object]
    foreach ($file in $files) {
        $source = Get-Content -Path $file.FullName -Raw
        $operations = Get-LiveCoreDestructiveDownOperation -Source $source
        if ($operations.Count -gt 0) {
            $destructive.Add([pscustomobject]@{
                    Migration  = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
                    Operations = ($operations -join ';')
                })
        }
    }

    # The acknowledgement baseline: migrations whose destructive Down() has been
    # reviewed and accepted under the roll-forward-only policy.
    $baseline = @{}
    if ($BaselinePath -and (Test-Path -Path $BaselinePath)) {
        foreach ($row in (Import-Csv -Path $BaselinePath)) {
            if ($row.migration) {
                $baseline[$row.migration.Trim()] = ([string]$row.operations).Trim()
            }
        }
    }

    $actual = @{}
    foreach ($item in $destructive) {
        $actual[$item.Migration] = $item.Operations
    }

    # Unreviewed: a destructive Down() with no baseline row (a new one to review).
    # Changed: a baseline row whose destructive operations no longer match (the
    # Down() was edited; re-review it). Both fail the lint.
    $unreviewed = New-Object System.Collections.Generic.List[object]
    $changed = New-Object System.Collections.Generic.List[object]
    foreach ($item in $destructive) {
        if (-not $baseline.ContainsKey($item.Migration)) {
            $unreviewed.Add($item)
        }
        elseif ($baseline[$item.Migration] -ne $item.Operations) {
            $changed.Add([pscustomobject]@{
                    Migration  = $item.Migration
                    Operations = $item.Operations
                    Baseline   = $baseline[$item.Migration]
                })
        }
    }

    # Stale: a baseline row for a migration that no longer has a destructive
    # Down() (or was removed). Keeps the baseline honest rather than growing only.
    $stale = New-Object System.Collections.Generic.List[string]
    foreach ($key in $baseline.Keys) {
        if (-not $actual.ContainsKey($key)) {
            $stale.Add($key)
        }
    }

    $isClean = ($unreviewed.Count -eq 0 -and $changed.Count -eq 0 -and $stale.Count -eq 0)

    return [pscustomobject]@{
        Destructive = $destructive.ToArray()
        Unreviewed  = $unreviewed.ToArray()
        Changed     = $changed.ToArray()
        Stale       = $stale.ToArray()
        IsClean     = $isClean
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreMigrationDownBody, `
    Get-LiveCoreMigrationDownOperation, `
    Get-LiveCoreDestructiveDownOperation, `
    Get-LiveCoreMigrationDownReview
