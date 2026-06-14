#requires -Version 5.1

<#
.SYNOPSIS
    Real backup -> restore -> integrity round-trip against a live PostgreSQL
    (CORE-DR-002).

.DESCRIPTION
    The in-memory restore drill (scripts/test-backup-restore-drill.ps1) exercises
    the coverage and integrity LOGIC (LiveCoreBackup.psm1) with a fixture and no
    database, so the real backup-livecore.ps1 / restore-livecore.ps1 - the scripts
    that actually invoke pg_dump / pg_restore / psql and the to_jsonb content
    checksum - were never run against a real PostgreSQL in CI. A broken argument to
    those tools would ship green and the RTO/RPO claims would rest on an untested
    path.

    This driver closes that gap. Against a live PostgreSQL whose schema was applied
    by the real migrations, it:

      1. seeds representative rows into every system-of-record table the backup
         catalog covers (the append-only audit, session-event and purchase
         records),
      2. runs the REAL backup-livecore.ps1 (real pg_dump + the real to_jsonb
         row-count/content measurement, encrypted at rest per CORE-DR-001),
      3. creates a FRESH, empty target database,
      4. runs the REAL restore-livecore.ps1 into it (real pg_restore + the real
         re-measurement and fail-closed integrity verification), and
      5. proves the integrity check has teeth against real data: a faithful restore
         verifies, and a restore that silently lost an append-only audit row is
         rejected fail-closed.

    Seeding inserts a minimal but referentially VALID object graph (an organization,
    workspace and session for the tenant-scoped records to point at) in dependency
    order, so every foreign key is satisfied and the data round-trips cleanly through
    pg_dump / pg_restore - which re-validates the foreign-key constraints it recreates
    on restore, so referentially invalid rows would fail the restore. Both databases
    live in one PostgreSQL instance, so the source and restore measurements render
    to_jsonb identically.

    No credential lives in this script: the database password is read from the
    supplied connection strings (passed to psql/pg_dump/pg_restore via PGPASSWORD,
    never on the command line) and the backup-at-rest passphrase is read from
    configuration (Backup__Encryption__Passphrase), exactly like the real scripts.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh). Exits 0 when
    every assertion holds and 1 when any fails.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-backup-restore-postgres.ps1 `
        -SourceConnectionString  "Host=localhost;Port=5432;Database=livecore_backup_ci;Username=postgres;Password=postgres" `
        -RestoreConnectionString "Host=localhost;Port=5432;Database=livecore_restore_ci;Username=postgres;Password=postgres" `
        -EncryptionPassphrase    $env:Backup__Encryption__Passphrase
#>

[CmdletBinding()]
param(
    # The seeded source database the backup is taken from (its schema must already
    # be applied by the real migrations).
    [Parameter(Mandatory = $true)]
    [string]$SourceConnectionString,

    # The target the restore creates fresh and restores into. It is DROPped and
    # re-CREATEd by this driver, so point it at a throwaway database name.
    [Parameter(Mandatory = $true)]
    [string]$RestoreConnectionString,

    # Where the backup artifacts (encrypted dump + manifest + object inventory) are
    # written. Defaults to a unique temporary directory that is removed on exit.
    [string]$WorkDirectory,

    [string]$PsqlPath = 'psql',

    # Backup-at-rest encryption passphrase (CORE-DR-001). The real scripts refuse to
    # run without one, so the round-trip cannot be exercised without it either.
    [string]$EncryptionPassphrase = $env:Backup__Encryption__Passphrase
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreBackup.psm1') -Force

# Resolve the encryption passphrase fail-closed up front, the same way the real
# backup/restore scripts do (CORE-DR-001).
$passphrase = Get-LiveCoreBackupEncryptionSecret -Passphrase $EncryptionPassphrase -PassphraseFile $env:Backup__Encryption__PassphraseFile

$source = Get-LiveCoreConnectionSetting -ConnectionString $SourceConnectionString
$target = Get-LiveCoreConnectionSetting -ConnectionString $RestoreConnectionString

$createdWorkDir = $false
if ([string]::IsNullOrWhiteSpace($WorkDirectory)) {
    $WorkDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-dr-002-" + [System.Guid]::NewGuid().ToString('n'))
    $createdWorkDir = $true
}
if (-not (Test-Path -Path $WorkDirectory)) {
    New-Item -ItemType Directory -Path $WorkDirectory | Out-Null
}
$WorkDirectory = (Resolve-Path -Path $WorkDirectory).Path

$failures = New-Object System.Collections.Generic.List[string]

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because")
    }
}

function AssertFalse {
    param([bool]$Condition, [string]$Because)
    AssertTrue (-not $Condition) $Because
}

function Invoke-DrillPsql {
    # Runs psql against $Connection, returning its output lines and failing closed
    # on a non-zero exit. The password is passed via PGPASSWORD, never on the
    # command line - the same posture as the backup/restore scripts.
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Connection,

        [string]$Command,
        [string]$File
    )

    $psqlArgs = @(
        '--host', $Connection.Host,
        '--port', $Connection.Port,
        '--dbname', $Connection.Database,
        '--no-password',
        '--tuples-only',
        '--no-align'
    )
    if (-not [string]::IsNullOrWhiteSpace($Connection.Username)) {
        $psqlArgs = @('--username', $Connection.Username) + $psqlArgs
    }
    if (-not [string]::IsNullOrWhiteSpace($Command)) {
        $psqlArgs += @('--command', $Command)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($File)) {
        $psqlArgs += @('--file', $File)
    }
    else {
        throw 'Invoke-DrillPsql requires -Command or -File.'
    }

    $previous = $env:PGPASSWORD
    try {
        if (-not [string]::IsNullOrWhiteSpace($Connection.Password)) {
            $env:PGPASSWORD = $Connection.Password
        }
        $output = & $PsqlPath @psqlArgs
        if ($LASTEXITCODE -ne 0) {
            throw "psql failed (exit $LASTEXITCODE)."
        }
        return [string[]]@($output)
    }
    finally {
        $env:PGPASSWORD = $previous
    }
}

function Measure-RestoredCatalog {
    # Re-measures every system of record the same way restore-livecore.ps1 does (the
    # to_jsonb row-count and order-independent content checksum for the database
    # tables, the inventory listing for the object store), so the integrity check
    # can be exercised independently against the real restored database.
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Connection,

        [Parameter(Mandatory = $true)]
        [string]$InventoryFile
    )

    $measure = @{}
    foreach ($item in (Get-LiveCoreSystemOfRecordCatalog)) {
        if ($item.Kind -eq 'database') {
            $rows = Invoke-DrillPsql -Connection $Connection -Command "\copy (SELECT to_jsonb(t) FROM $($item.Name) t) TO STDOUT"
        }
        else {
            # The object-storage inventory is the fixed listing both the backup and
            # the restore read (the backup measured it via the same file), so reading
            # it back here reproduces the manifest's object-store checksum.
            $rows = [string[]]@(Get-Content -Path $InventoryFile)
        }
        $rows = [string[]]@($rows)
        $measure[$item.Name] = @{
            RowCount = $rows.Count
            Checksum = Get-LiveCoreContentChecksum -Row $rows
        }
    }
    return $measure
}

# The seeded rows per system-of-record table (used to assert the backup manifest
# actually measured real, non-empty data rather than silently certifying nothing).
$expectedRowCount = [ordered]@{
    'audit_logs'                = 3
    'session_events'            = 4
    'purchase_transactions'     = 2
    'purchase_events'           = 3
    'store_notification_events' = 2
}

Write-Host "Backup/restore round-trip: source '$($source.Database)' -> fresh restore '$($target.Database)' using psql '$PsqlPath'; artifacts in $WorkDirectory."

try {
    # --- 0. A fixed object-storage inventory both the backup and the restore read,
    #        so the asset-bucket coverage round-trips deterministically without a
    #        live object store (the database round-trip is the real subject here). ---
    $inventoryFile = Join-Path $WorkDirectory 'object-inventory.txt'
    Set-Content -Path $inventoryFile -Encoding UTF8 -Value @(
        '{"key":"assets/org-1/ws-1/a1b2","etag":"e1","size":10241}',
        '{"key":"assets/org-1/ws-1/c3d4","etag":"e2","size":2048}',
        '{"key":"assets/org-2/ws-9/e5f6","etag":"e3","size":512}'
    )

    # --- 1. Seed a minimal but referentially VALID object graph on the source
    #        database: an organization, workspace and session for the tenant-scoped
    #        records to reference, then every system-of-record table. Seeding in
    #        dependency order (no foreign key is bypassed) keeps the data valid, so it
    #        round-trips through pg_dump / pg_restore, which re-validates the foreign
    #        keys it recreates on restore. ---
    $org = '11111111-1111-1111-1111-111111111111'
    $workspace = '22222222-2222-2222-2222-222222222222'
    $session = '33333333-3333-3333-3333-333333333333'
    $ptx1 = 'aaaaaaaa-0000-0000-0000-000000000001'
    $ptx2 = 'aaaaaaaa-0000-0000-0000-000000000002'

    $seedSql = @"
INSERT INTO organizations (id, name, slug, created_at, updated_at) VALUES
  ('$org','Example Organization','example-org','2026-06-01T00:00:00Z','2026-06-01T00:00:00Z');

INSERT INTO workspaces (id, organization_id, name, slug, status, created_at, updated_at) VALUES
  ('$workspace','$org','Example Workspace','example-workspace','Active','2026-06-01T00:00:00Z','2026-06-01T00:00:00Z');

INSERT INTO sessions (id, organization_id, workspace_id, status, title, created_at, updated_at) VALUES
  ('$session','$org','$workspace','Ended','Example Session','2026-06-10T08:00:00Z','2026-06-10T10:00:00Z');

INSERT INTO audit_logs (id, organization_id, workspace_id, action, new_state, sequence, created_at) VALUES
  ('a0000000-0000-0000-0000-000000000001','$org','$workspace','MemberRemoved','Removed',1,'2026-06-10T10:00:00Z'),
  ('a0000000-0000-0000-0000-000000000002','$org','$workspace','WorkspaceArchived','Archived',2,'2026-06-10T11:00:00Z'),
  ('a0000000-0000-0000-0000-000000000003','$org',NULL,'SessionCancelled','Cancelled',3,'2026-06-10T12:30:00Z');

INSERT INTO session_events (event_id, organization_id, workspace_id, session_id, event_type, payload, schema_version, sequence, created_at) VALUES
  ('50000000-0000-0000-0000-000000000001','$org','$workspace','$session','SessionStarted','{}',1,1,'2026-06-10T09:00:00Z'),
  ('50000000-0000-0000-0000-000000000002','$org','$workspace','$session','ParticipantJoined','{}',1,2,'2026-06-10T09:01:00Z'),
  ('50000000-0000-0000-0000-000000000003','$org','$workspace','$session','ContentRevealed','{}',1,3,'2026-06-10T09:05:00Z'),
  ('50000000-0000-0000-0000-000000000004','$org','$workspace','$session','SessionEnded','{}',1,4,'2026-06-10T10:00:00Z');

INSERT INTO purchase_transactions (id, product_reference, provider, provider_transaction_id, status, recorded_at, updated_at) VALUES
  ('$ptx1','plan.pro.monthly','apple','A-1001','Verified','2026-06-01T00:00:00Z','2026-06-01T00:00:00Z'),
  ('$ptx2','plan.pro.monthly','google','G-2002','Verified','2026-06-09T00:00:00Z','2026-06-09T00:00:00Z');

INSERT INTO purchase_events (id, purchase_transaction_id, previous_status, new_status, created_at) VALUES
  ('be000000-0000-0000-0000-000000000001','$ptx1',NULL,'Verified','2026-06-01T00:00:00Z'),
  ('be000000-0000-0000-0000-000000000002','$ptx1','Verified','Renewed','2026-06-08T00:00:00Z'),
  ('be000000-0000-0000-0000-000000000003','$ptx2',NULL,'Verified','2026-06-09T00:00:00Z');

INSERT INTO store_notification_events (id, provider, provider_notification_id, provider_transaction_id, notification_type, applied_status, outcome, occurred_at, received_at) VALUES
  ('5e000000-0000-0000-0000-000000000001','apple','N-1','A-1001','DID_RENEW','Renewed','Applied','2026-06-08T00:00:01Z','2026-06-08T00:00:02Z'),
  ('5e000000-0000-0000-0000-000000000002','google','N-2','G-2002','SUBSCRIPTION_RENEWED','Renewed','Applied','2026-06-09T00:00:01Z','2026-06-09T00:00:02Z');
"@

    $seedFile = Join-Path $WorkDirectory 'seed-systems-of-record.sql'
    Set-Content -Path $seedFile -Value $seedSql -Encoding UTF8
    [void](Invoke-DrillPsql -Connection $source -File $seedFile)
    Write-Host "Seeded the systems-of-record tables on '$($source.Database)'."

    # --- 2. Run the REAL backup script: real pg_dump + the real to_jsonb row-count
    #        and content measurement, encrypted at rest (CORE-DR-001). ---
    $backupScript = Join-Path $scriptDir 'backup-livecore.ps1'
    & $backupScript `
        -OutputDirectory $WorkDirectory `
        -ConnectionString $SourceConnectionString `
        -StorageInventoryProgram 'cat' -StorageInventoryArgument @($inventoryFile) `
        -EncryptionPassphrase $passphrase

    $dumpFile = @(Get-ChildItem -Path $WorkDirectory -Filter '*.dump.enc' -File)
    AssertTrue ($dumpFile.Count -eq 1) 'the real backup wrote exactly one encrypted dump artifact'
    $manifestPath = Join-Path $WorkDirectory 'livecore-backup-manifest.json'
    AssertTrue (Test-Path -Path $manifestPath) 'the real backup wrote a coverage manifest'

    # The manifest must have measured the seeded rows - a real to_jsonb row count,
    # not zero - so a backup that captured nothing cannot pass green.
    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    $manifestCount = @{}
    foreach ($entry in $manifest.systemsOfRecord) {
        $manifestCount[[string]$entry.name] = [int]$entry.rowCount
    }
    foreach ($name in $expectedRowCount.Keys) {
        AssertTrue (
            $manifestCount.ContainsKey($name) -and $manifestCount[$name] -eq $expectedRowCount[$name]
        ) "the backup manifest measured $($expectedRowCount[$name]) row(s) of '$name' via the real to_jsonb checksum"
    }

    # --- 3. Create a FRESH, empty target database for the restore (the acceptance
    #        criterion: restore into a fresh database). DROP/CREATE run as separate
    #        statements because CREATE DATABASE cannot run inside a transaction. ---
    [void](Invoke-DrillPsql -Connection $source -Command "DROP DATABASE IF EXISTS ""$($target.Database)"";")
    [void](Invoke-DrillPsql -Connection $source -Command "CREATE DATABASE ""$($target.Database)"";")
    Write-Host "Created a fresh restore target database '$($target.Database)'."

    # --- 4. Run the REAL restore script: real pg_restore + the real re-measurement
    #        and fail-closed integrity verification. It THROWS on any mismatch, so a
    #        clean return is itself the round-trip integrity assertion. ---
    $restoreScript = Join-Path $scriptDir 'restore-livecore.ps1'
    & $restoreScript `
        -DumpPath $dumpFile[0].FullName `
        -ManifestPath $manifestPath `
        -ConnectionString $RestoreConnectionString `
        -StorageInventoryProgram 'cat' -StorageInventoryArgument @($inventoryFile) `
        -EncryptionPassphrase $passphrase
    AssertTrue $true 'the real backup -> restore round-trip verified every system of record (real pg_restore + to_jsonb integrity)'

    # --- 5. Prove the integrity check has teeth against the real restored data:
    #        a faithful re-measurement verifies, and after a single append-only
    #        audit row is lost the restore is rejected fail-closed. ---
    $baseline = Measure-RestoredCatalog -Connection $target -InventoryFile $inventoryFile
    $baselineVerdict = Test-LiveCoreRestoreIntegrity -SourceManifest $manifest -RestoredSystemOfRecord $baseline
    AssertTrue $baselineVerdict.IsFaithful 'an independent re-measurement of the restored database matches the backup manifest'
    AssertTrue ($baseline['audit_logs'].RowCount -eq $expectedRowCount['audit_logs']) 'the restored audit_logs has the seeded row count'

    [void](Invoke-DrillPsql -Connection $target -Command "SET session_replication_role = 'replica'; DELETE FROM audit_logs WHERE ctid IN (SELECT ctid FROM audit_logs LIMIT 1);")
    $lossy = Measure-RestoredCatalog -Connection $target -InventoryFile $inventoryFile
    $lossyVerdict = Test-LiveCoreRestoreIntegrity -SourceManifest $manifest -RestoredSystemOfRecord $lossy
    AssertFalse $lossyVerdict.IsFaithful 'a restored database that lost an append-only audit row is rejected fail-closed (data loss)'
    AssertTrue ([bool]($lossyVerdict.Failures -match 'audit_logs')) 'the fail-closed verdict names the audit_logs system of record'
}
catch {
    $failures.Add("ERROR: $($_.Exception.Message)")
    Write-Host "Backup/restore round-trip errored: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if ($createdWorkDir -and (Test-Path -Path $WorkDirectory)) {
        Remove-Item -Path $WorkDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Backup/restore round-trip FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Backup/restore round-trip passed: the real scripts backed up a seeded PostgreSQL, restored into a fresh database, and the to_jsonb integrity/row-count checks passed - and a lossy restore is rejected fail-closed.' -ForegroundColor Green
exit 0
