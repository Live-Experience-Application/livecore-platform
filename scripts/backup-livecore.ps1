#requires -Version 5.1

<#
.SYNOPSIS
    Backs up the LiveCore PostgreSQL database and records a verifiable coverage
    manifest of the systems of record (CORE-OPS-010).

.DESCRIPTION
    Produces a point-in-time logical backup of the Core database with pg_dump
    (custom format, which restores with pg_restore) and, for each system of
    record, measures a row count and an order-independent content checksum with
    psql. The object-storage asset bucket is mirrored by a caller-supplied tool
    (aws / mc / rclone - the choice is the operator's) and inventoried the same
    way. It then writes livecore-backup-manifest.json next to the dump through
    LiveCoreBackup.psm1, which refuses to certify a backup that does not cover
    every append-only audit, session-event and purchase record plus the asset
    object store.

    No credential lives in this script: the database password is read from the
    same ConnectionStrings:Database value the API and worker use and is passed to
    pg_dump/psql via the PGPASSWORD environment variable, never on the command
    line, and object-storage credentials belong to the supplied mirror tool's own
    environment. The companion restore (scripts/restore-livecore.ps1) verifies a
    restore against the manifest this writes; scripts/test-backup-restore-drill.ps1
    exercises the coverage and integrity logic without a live database.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/backup-livecore.ps1 -OutputDirectory ./backups `
        -ConnectionString "Host=db;Port=5432;Database=livecore;Username=livecore;Password=$env:DB_PASSWORD" `
        -StorageBucket livecore-assets `
        -StorageMirrorProgram aws -StorageMirrorArgument @('s3','sync','s3://livecore-assets','./backups/assets') `
        -StorageInventoryProgram aws -StorageInventoryArgument @('s3api','list-objects-v2','--bucket','livecore-assets','--query','Contents[].{k:Key,e:ETag,s:Size}','--output','text')
#>

[CmdletBinding()]
param(
    # Directory the dump, the mirrored assets and the manifest are written to.
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    # The Core database connection string (defaults to ConnectionStrings__Database).
    [string]$ConnectionString = $env:ConnectionStrings__Database,

    # The private asset bucket recorded in the manifest (defaults to the storage
    # bucket setting, then the documented default).
    [string]$StorageBucket = $(if ($env:Assets__Storage__Bucket) { $env:Assets__Storage__Bucket } else { 'livecore-assets' }),

    [string]$PgDumpPath = 'pg_dump',
    [string]$PsqlPath = 'psql',

    # Object-storage mirror tool (e.g. aws / mc / rclone) and its arguments. When
    # supplied it is run before the inventory so the local mirror is current.
    [string]$StorageMirrorProgram,
    [string[]]$StorageMirrorArgument = @(),

    # Object-storage inventory tool: prints one line per object (key/etag/size).
    # Required so the manifest can checksum the asset bucket's contents.
    [string]$StorageInventoryProgram,
    [string[]]$StorageInventoryArgument = @(),

    # Backup timestamp recorded in the manifest (UTC ISO-8601).
    [string]$CreatedAtUtc = [System.DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreBackup.psm1') -Force

$connection = Get-LiveCoreConnectionSetting -ConnectionString $ConnectionString

if (-not (Test-Path -Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}
$OutputDirectory = (Resolve-Path -Path $OutputDirectory).Path

if (-not $StorageInventoryProgram) {
    throw 'Object-storage coverage is required: supply -StorageInventoryProgram (and its -StorageInventoryArgument) so the asset bucket is inventoried into the manifest. A backup must cover the asset object store, not just PostgreSQL (CORE-OPS-010).'
}

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
$dumpFileName = "livecore-postgres-$($CreatedAtUtc -replace '[:]', '').dump"
$dumpPath = Join-Path $OutputDirectory $dumpFileName

try {
    if (-not [string]::IsNullOrWhiteSpace($connection.Password)) {
        $env:PGPASSWORD = $connection.Password
    }

    Write-Host "Backing up database '$($connection.Database)' on $($connection.Host):$($connection.Port) -> $dumpPath"
    $pgDumpArgs = @(
        '--host', $connection.Host,
        '--port', $connection.Port,
        '--dbname', $connection.Database,
        '--no-password',
        '--format=custom',
        '--file', $dumpPath
    )
    if (-not [string]::IsNullOrWhiteSpace($connection.Username)) {
        $pgDumpArgs = @('--username', $connection.Username) + $pgDumpArgs
    }
    & $PgDumpPath @pgDumpArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pg_dump failed with exit code $LASTEXITCODE."
    }

    if ($StorageMirrorProgram) {
        Write-Host "Mirroring object storage with: $StorageMirrorProgram $($StorageMirrorArgument -join ' ')"
        & $StorageMirrorProgram @StorageMirrorArgument
        if ($LASTEXITCODE -ne 0) {
            throw "Object-storage mirror command failed with exit code $LASTEXITCODE."
        }
    }

    $measurement = @{}
    foreach ($item in (Get-LiveCoreSystemOfRecordCatalog)) {
        if ($item.Kind -eq 'database') {
            $rows = Invoke-PsqlValue -Executable $PsqlPath -Connection $connection -Command "\copy (SELECT to_jsonb(t) FROM $($item.Name) t) TO STDOUT"
        }
        else {
            Write-Host "Inventorying object storage with: $StorageInventoryProgram $($StorageInventoryArgument -join ' ')"
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
        Write-Host ("  {0,-26} {1,8} record(s)" -f $item.Name, $measurement[$item.Name].RowCount)
    }

    $manifest = Get-LiveCoreBackupManifest `
        -SystemOfRecord $measurement `
        -CreatedAtUtc $CreatedAtUtc `
        -Database $connection.Database `
        -StorageBucket $StorageBucket

    $manifestPath = Join-Path $OutputDirectory 'livecore-backup-manifest.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding UTF8

    Write-Host ''
    Write-Host "Backup complete. Dump: $dumpPath" -ForegroundColor Green
    Write-Host "Coverage manifest: $manifestPath" -ForegroundColor Green
}
finally {
    $env:PGPASSWORD = $previousPgPassword
}
