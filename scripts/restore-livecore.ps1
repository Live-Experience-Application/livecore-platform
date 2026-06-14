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

    The dump is decrypted (and integrity-verified) to a temporary plaintext file
    before pg_restore, and the locally mirrored asset binaries are decrypted before
    the restore tool runs and re-encrypted afterwards, using the same passphrase the
    backup used (CORE-DR-001). The restore refuses to run without the passphrase and
    fails closed on a wrong passphrase or a tampered artifact.

.EXAMPLE
    pwsh -File scripts/restore-livecore.ps1 `
        -DumpPath ./backups/livecore-postgres-20260613T000000Z.dump.enc `
        -ManifestPath ./backups/livecore-backup-manifest.json `
        -ConnectionString "Host=db;Port=5432;Database=livecore_restore;Username=livecore;Password=$env:DB_PASSWORD" `
        -StorageRestoreProgram aws -StorageRestoreArgument @('s3','sync','./backups/assets','s3://livecore-assets') `
        -AssetMirrorDirectory ./backups/assets `
        -StorageInventoryProgram aws -StorageInventoryArgument @('s3api','list-objects-v2','--bucket','livecore-assets','--query','Contents[].{k:Key,e:ETag,s:Size}','--output','text') `
        -EncryptionPassphrase $env:Backup__Encryption__Passphrase
#>

[CmdletBinding()]
param(
    # The encrypted (.dump.enc) pg_dump custom-format file produced by
    # backup-livecore.ps1. It is decrypted and integrity-verified before restore.
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
    [string[]]$StorageInventoryArgument = @(),

    # Backup-at-rest encryption passphrase (CORE-DR-001). The same passphrase the
    # backup used: the encrypted dump is decrypted (and integrity-verified) before
    # pg_restore, and any locally mirrored asset binaries are decrypted before the
    # restore tool runs. The restore refuses to run without it (fail-closed) and
    # fails closed on a wrong passphrase or a tampered artifact. Read from
    # configuration, never committed (threat T7); supply -EncryptionPassphraseFile
    # to read it from a file instead.
    [string]$EncryptionPassphrase = $env:Backup__Encryption__Passphrase,
    [string]$EncryptionPassphraseFile = $env:Backup__Encryption__PassphraseFile,

    # Local directory the encrypted asset mirror lives in (the same directory the
    # backup encrypted). Required when -StorageRestoreProgram restores assets from
    # a local mirror, so the binaries can be decrypted for the restore and
    # re-encrypted afterwards (CORE-DR-001).
    [string]$AssetMirrorDirectory
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

# Resolve the encryption sink up front: a restore cannot read an encrypted backup
# without the passphrase, so an unconfigured restore fails closed (CORE-DR-001).
$encryptionPassphrase = Get-LiveCoreBackupEncryptionSecret -Passphrase $EncryptionPassphrase -PassphraseFile $EncryptionPassphraseFile

if ($StorageRestoreProgram -and [string]::IsNullOrWhiteSpace($AssetMirrorDirectory)) {
    throw 'When -StorageRestoreProgram restores assets from a local mirror you must also pass -AssetMirrorDirectory (the directory holding the encrypted mirror) so the binaries can be decrypted for the restore (CORE-DR-001).'
}

$manifest = Get-Content -Path $ManifestPath -Raw | ConvertFrom-Json
$connection = Get-LiveCoreConnectionSetting -ConnectionString $ConnectionString
$dumpDirectory = Split-Path -Parent (Resolve-Path -Path $DumpPath).Path

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
$decryptedDumpPath = $null
$assetsDecrypted = $false
try {
    if (-not [string]::IsNullOrWhiteSpace($connection.Password)) {
        $env:PGPASSWORD = $connection.Password
    }

    # Decrypt the dump to a temporary plaintext file for pg_restore; this verifies
    # the artifact's integrity and fails closed on a wrong passphrase or tampering
    # (CORE-DR-001). The plaintext copy is removed in the finally block.
    $decryptedDumpPath = Join-Path $dumpDirectory ('livecore-restore-' + [System.Guid]::NewGuid().ToString('n') + '.dump')
    Write-Host "Decrypting and verifying the backup dump for restore -> (temporary plaintext)"
    [void](Unprotect-LiveCoreBackupFile -Path $DumpPath -Destination $decryptedDumpPath -Passphrase $encryptionPassphrase)

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
    $pgRestoreArgs += $decryptedDumpPath

    & $PgRestorePath @pgRestoreArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore failed with exit code $LASTEXITCODE."
    }

    if ($StorageRestoreProgram) {
        # Decrypt the encrypted mirror to plaintext just for the restore sync; the
        # finally block re-encrypts it so the local backup is left encrypted at
        # rest as it arrived (CORE-DR-001).
        $assetsDecrypted = $true
        Write-Host "Decrypting mirrored asset binaries for restore under: $AssetMirrorDirectory"
        [void](Unprotect-LiveCoreBackupDirectory -Path $AssetMirrorDirectory -Passphrase $encryptionPassphrase)

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
    # Remove the temporary plaintext dump so it never lingers at rest (CORE-DR-001).
    if ($decryptedDumpPath -and (Test-Path -Path $decryptedDumpPath)) {
        Remove-Item -Path $decryptedDumpPath -Force -ErrorAction SilentlyContinue
    }
    # Re-encrypt the local asset mirror so the backup copy is left encrypted at
    # rest exactly as it arrived; the plaintext existed only during the sync.
    if ($assetsDecrypted -and -not [string]::IsNullOrWhiteSpace($AssetMirrorDirectory) -and (Test-Path -Path $AssetMirrorDirectory -PathType Container)) {
        [void](Protect-LiveCoreBackupDirectory -Path $AssetMirrorDirectory -Passphrase $encryptionPassphrase)
    }
    $env:PGPASSWORD = $previousPgPassword
}
