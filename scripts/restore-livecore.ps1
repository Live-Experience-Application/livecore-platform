#requires -Version 5.1

<#
.SYNOPSIS
    Restores the LiveCore PostgreSQL database and object storage from a backup and
    verifies every system of record was reproduced faithfully (CORE-OPS-010).

.DESCRIPTION
    Restores a pg_dump custom-format backup with pg_restore into the target
    database, restores the asset object store from its mirror with a
    caller-supplied tool, then re-measures each system of record (row count and
    order-independent content checksum, the same way the backup did) and verifies
    it against the backup's livecore-backup-manifest.json through
    LiveCoreBackup.psm1. Verification is fail-closed: if the manifest is missing
    or incomplete, or any append-only audit, session-event or purchase record (or
    the asset bucket) comes back with a different count or content, the restore is
    reported as FAILED with a non-zero exit code rather than silently accepted.

    This is the runnable restore step of the runbook in
    docs/13_SELF_HOSTING_REQUIREMENTS.md. It carries no credential: the database
    password is read from the same ConnectionStrings:Database value the API uses
    and passed via the PGPASSWORD environment variable, and object-storage
    credentials belong to the supplied restore tool's own environment.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/restore-livecore.ps1 `
        -DumpPath ./backups/livecore-postgres-20260613T000000Z.dump `
        -ManifestPath ./backups/livecore-backup-manifest.json `
        -ConnectionString "Host=db;Port=5432;Database=livecore_restore;Username=livecore;Password=$env:DB_PASSWORD" `
        -StorageRestoreProgram aws -StorageRestoreArgument @('s3','sync','./backups/assets','s3://livecore-assets') `
        -StorageInventoryProgram aws -StorageInventoryArgument @('s3api','list-objects-v2','--bucket','livecore-assets','--query','Contents[].{k:Key,e:ETag,s:Size}','--output','text')
#>

[CmdletBinding()]
param(
    # The pg_dump custom-format file produced by backup-livecore.ps1.
    [Parameter(Mandatory = $true)]
    [string]$DumpPath,

    # The coverage manifest produced alongside the dump.
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    # The TARGET database to restore into (defaults to ConnectionStrings__Database).
    # Point this at a freshly created, empty database for a drill or a recovery.
    [string]$ConnectionString = $env:ConnectionStrings__Database,

    [string]$PgRestorePath = 'pg_restore',
    [string]$PsqlPath = 'psql',

    # Drop-and-recreate objects before restoring (use when restoring over an
    # existing database; omit when restoring into a fresh, empty one).
    [switch]$Clean,

    # Object-storage restore tool (e.g. aws / mc / rclone) and its arguments.
    [string]$StorageRestoreProgram,
    [string[]]$StorageRestoreArgument = @(),

    # Object-storage inventory tool: prints one line per object (key/etag/size),
    # identical to the one used by the backup. Required to re-measure the bucket.
    [string]$StorageInventoryProgram,
    [string[]]$StorageInventoryArgument = @()
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreBackup.psm1') -Force

if (-not (Test-Path -Path $DumpPath)) {
    throw "Backup dump not found: $DumpPath"
}
if (-not (Test-Path -Path $ManifestPath)) {
    throw "Backup manifest not found: $ManifestPath. A restore cannot be verified without it, so it is refused (CORE-OPS-010)."
}
if (-not $StorageInventoryProgram) {
    throw 'Object-storage verification is required: supply -StorageInventoryProgram (and its -StorageInventoryArgument) so the restored asset bucket is re-measured against the manifest (CORE-OPS-010).'
}

$manifest = Get-Content -Path $ManifestPath -Raw | ConvertFrom-Json
$connection = Get-LiveCoreConnectionSetting -ConnectionString $ConnectionString

function Invoke-PsqlValue {
    param(
        [string]$Executable,
        [hashtable]$Connection,
        [string]$Command
    )

    $psqlArgs = @(
        '--host', $Connection.Host,
        '--port', $Connection.Port,
        '--dbname', $Connection.Database,
        '--no-password',
        '--tuples-only',
        '--no-align',
        '--command', $Command
    )
    if (-not [string]::IsNullOrWhiteSpace($Connection.Username)) {
        $psqlArgs = @('--username', $Connection.Username) + $psqlArgs
    }

    $output = & $Executable @psqlArgs
    if ($LASTEXITCODE -ne 0) {
        throw "psql failed (exit $LASTEXITCODE) running: $Command"
    }
    return [string[]]@($output)
}

$previousPgPassword = $env:PGPASSWORD
try {
    if (-not [string]::IsNullOrWhiteSpace($connection.Password)) {
        $env:PGPASSWORD = $connection.Password
    }

    Write-Host "Restoring database '$($connection.Database)' on $($connection.Host):$($connection.Port) from $DumpPath"
    $pgRestoreArgs = @(
        '--host', $connection.Host,
        '--port', $connection.Port,
        '--dbname', $connection.Database,
        '--no-password',
        '--no-owner'
    )
    if (-not [string]::IsNullOrWhiteSpace($connection.Username)) {
        $pgRestoreArgs = @('--username', $connection.Username) + $pgRestoreArgs
    }
    if ($Clean) {
        $pgRestoreArgs += @('--clean', '--if-exists')
    }
    $pgRestoreArgs += $DumpPath

    & $PgRestorePath @pgRestoreArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore failed with exit code $LASTEXITCODE."
    }

    if ($StorageRestoreProgram) {
        Write-Host "Restoring object storage with: $StorageRestoreProgram $($StorageRestoreArgument -join ' ')"
        & $StorageRestoreProgram @StorageRestoreArgument
        if ($LASTEXITCODE -ne 0) {
            throw "Object-storage restore command failed with exit code $LASTEXITCODE."
        }
    }

    $measurement = @{}
    foreach ($item in (Get-LiveCoreSystemOfRecordCatalog)) {
        if ($item.Kind -eq 'database') {
            $rows = Invoke-PsqlValue -Executable $PsqlPath -Connection $connection -Command "\copy (SELECT to_jsonb(t) FROM $($item.Name) t) TO STDOUT"
        }
        else {
            $rows = & $StorageInventoryProgram @StorageInventoryArgument
            if ($LASTEXITCODE -ne 0) {
                throw "Object-storage inventory command failed with exit code $LASTEXITCODE."
            }
            $rows = [string[]]@($rows)
        }

        $measurement[$item.Name] = @{
            RowCount = ([string[]]@($rows)).Count
            Checksum = Get-LiveCoreContentChecksum -Row ([string[]]@($rows))
        }
    }

    $verdict = Test-LiveCoreRestoreIntegrity -SourceManifest $manifest -RestoredSystemOfRecord $measurement

    Write-Host ''
    if (-not $verdict.IsFaithful) {
        Write-Host 'Restore verification FAILED - the restored systems of record do not match the backup:' -ForegroundColor Red
        foreach ($failure in $verdict.Failures) {
            Write-Host "  - $failure"
        }
        throw 'Restore did not reproduce every system of record faithfully; treat this restore as invalid (CORE-OPS-010).'
    }

    Write-Host 'Restore verified: every system of record matches the backup manifest (row counts and content checksums).' -ForegroundColor Green
    foreach ($entry in $manifest.systemsOfRecord) {
        Write-Host ("  {0,-26} {1,8} record(s)  OK" -f $entry.name, $entry.rowCount)
    }
}
finally {
    $env:PGPASSWORD = $previousPgPassword
}
