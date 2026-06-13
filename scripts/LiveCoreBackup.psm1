#requires -Version 5.1

<#
.SYNOPSIS
    Backup/restore coverage and integrity logic for the LiveCore Core Platform
    systems of record (CORE-OPS-010).

.DESCRIPTION
    The Core holds tenant-isolated, append-only systems of record - the audit
    trail, the session-event stream and the store purchase records - plus the
    private object-storage bucket for asset binaries. A backup is only useful if
    a restore provably recovers every one of them, so this module owns the two
    pure, side-effect-free pieces the backup/restore scripts and the restore
    drill all share:

      - the catalog of systems of record a backup MUST cover, and
      - a fail-closed integrity check that a restore reproduced each one exactly
        (same row count and the same order-independent content checksum).

    Keeping the logic here - with no database or storage I/O of its own - is what
    lets the restore drill (scripts/test-backup-restore-drill.ps1) verify the
    safety property end to end without a live PostgreSQL or object store, exactly
    as LiveCoreImageTags.psm1 is exercised by test-image-tags.ps1.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

Set-StrictMode -Version Latest

function Get-LiveCoreSystemOfRecordCatalog {
    <#
    .SYNOPSIS
        The canonical set of systems of record every Core backup must cover.
    .DESCRIPTION
        Drawn from csv/database_tables.csv and docs/10_DATABASE_SCHEMA.md: the
        append-only audit, session-event and purchase tables (the records whose
        loss is unrecoverable) plus the private asset object store. A backup
        manifest that omits any of these is rejected (see Get-LiveCoreBackupManifest).
    #>
    [CmdletBinding()]
    [OutputType([psobject[]])]
    param()

    return @(
        [pscustomobject]@{ Name = 'audit_logs'; Kind = 'database'; AppendOnly = $true; Module = 'Audit'; Description = 'Append-only tenant audit trail.' }
        [pscustomobject]@{ Name = 'session_events'; Kind = 'database'; AppendOnly = $true; Module = 'Realtime'; Description = 'Append-only session event stream.' }
        [pscustomobject]@{ Name = 'purchase_transactions'; Kind = 'database'; AppendOnly = $false; Module = 'Store'; Description = 'Verified store purchase records (idempotent system of record).' }
        [pscustomobject]@{ Name = 'purchase_events'; Kind = 'database'; AppendOnly = $true; Module = 'Store'; Description = 'Append-only purchase state-change trail.' }
        [pscustomobject]@{ Name = 'store_notification_events'; Kind = 'database'; AppendOnly = $true; Module = 'Store'; Description = 'Append-only handled store-notification ledger.' }
        [pscustomobject]@{ Name = 'object-storage'; Kind = 'object-storage'; AppendOnly = $false; Module = 'Assets'; Description = 'Private S3-compatible asset bucket (binary content).' }
    )
}

function Get-LiveCoreContentChecksum {
    <#
    .SYNOPSIS
        A deterministic, order-independent SHA-256 over a set of record rows.
    .DESCRIPTION
        The rows are sorted with the ordinal comparer and joined with newlines
        before hashing, so a faithful restore that preserves the set of records
        - whatever order a logical dump reinserts them in - yields the same
        digest, while any added, dropped or altered record changes it. This is
        the integrity signal for the append-only systems of record.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [AllowNull()]
        [string[]]$Row
    )

    $sorted = [string[]]@($Row)
    [System.Array]::Sort($sorted, [System.StringComparer]::Ordinal)
    $joined = [string]::Join("`n", $sorted)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($joined)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $builder = New-Object System.Text.StringBuilder
    foreach ($byte in $hash) {
        [void]$builder.Append($byte.ToString('x2'))
    }
    return $builder.ToString()
}

function Get-LiveCoreConnectionSetting {
    <#
    .SYNOPSIS
        Parses a .NET/Npgsql connection string into its host/port/database fields.
    .DESCRIPTION
        The backup and restore scripts read the same ConnectionStrings:Database
        value the API and worker do, and hand the parsed fields to pg_dump/psql/
        pg_restore (the password via the PGPASSWORD environment variable, never on
        the command line). Fails closed when no host or database can be resolved,
        so a malformed or empty connection string never produces a half-targeted
        backup. The password is read but is not part of the returned summary used
        for logging by callers.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [AllowNull()]
        [string]$ConnectionString
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw 'No database connection string was supplied. Provide -ConnectionString or set ConnectionStrings__Database (CORE-OPS-010).'
    }

    $raw = @{}
    foreach ($part in $ConnectionString.Split(';')) {
        if ([string]::IsNullOrWhiteSpace($part)) { continue }
        $separator = $part.IndexOf('=')
        if ($separator -lt 1) { continue }
        $key = $part.Substring(0, $separator).Trim().ToLowerInvariant()
        $value = $part.Substring($separator + 1).Trim()
        $raw[$key] = $value
    }

    $resolve = {
        param([string[]]$Candidate)
        foreach ($name in $Candidate) {
            if ($raw.ContainsKey($name) -and -not [string]::IsNullOrWhiteSpace($raw[$name])) {
                return $raw[$name]
            }
        }
        return $null
    }

    $dbHost = & $resolve @('host', 'server', 'data source')
    $database = & $resolve @('database', 'db', 'initial catalog')
    $port = & $resolve @('port')
    $username = & $resolve @('username', 'user id', 'userid', 'user', 'uid')
    $password = & $resolve @('password', 'pwd')

    if ([string]::IsNullOrWhiteSpace($dbHost) -or [string]::IsNullOrWhiteSpace($database)) {
        throw "The connection string does not name a host and a database, so a backup/restore cannot be targeted safely. Expected 'Host=...;Port=...;Database=...;Username=...;Password=...' (CORE-OPS-010)."
    }

    if ([string]::IsNullOrWhiteSpace($port)) { $port = '5432' }

    return @{
        Host     = $dbHost
        Port     = $port
        Database = $database
        Username = $username
        Password = $password
    }
}

function Get-LiveCoreBackupManifest {
    <#
    .SYNOPSIS
        Builds the backup manifest, refusing to certify incomplete coverage.
    .DESCRIPTION
        Given a per-system measurement table (name -> @{ RowCount; Checksum }),
        produces the manifest the restore step verifies against. It is
        fail-closed on coverage: if any system of record from the catalog is
        absent (or lacks a row count/checksum) it throws rather than emit a
        manifest that silently omits the audit, session-event or purchase
        records.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$SystemOfRecord,

        [Parameter(Mandatory = $true)]
        [string]$CreatedAtUtc,

        [Parameter(Mandatory = $true)]
        [string]$Database,

        [Parameter(Mandatory = $true)]
        [string]$StorageBucket
    )

    $catalog = Get-LiveCoreSystemOfRecordCatalog
    $entries = New-Object System.Collections.Generic.List[object]
    $missing = New-Object System.Collections.Generic.List[string]

    foreach ($item in $catalog) {
        if (-not $SystemOfRecord.ContainsKey($item.Name)) {
            $missing.Add($item.Name)
            continue
        }
        $measurement = $SystemOfRecord[$item.Name]
        if ($null -eq $measurement -or -not $measurement.ContainsKey('RowCount') -or -not $measurement.ContainsKey('Checksum')) {
            $missing.Add($item.Name)
            continue
        }
        $entries.Add([pscustomobject][ordered]@{
                name       = $item.Name
                kind       = $item.Kind
                appendOnly = [bool]$item.AppendOnly
                rowCount   = [int]$measurement.RowCount
                checksum   = [string]$measurement.Checksum
            })
    }

    if ($missing.Count -gt 0) {
        throw "Refusing to write a backup manifest that does not cover every system of record: missing $([string]::Join(', ', $missing)). A backup must capture the append-only audit, session-event and purchase records and the asset object store (CORE-OPS-010)."
    }

    return [pscustomobject][ordered]@{
        schemaVersion   = 1
        createdAtUtc    = $CreatedAtUtc
        database        = $Database
        storageBucket   = $StorageBucket
        systemsOfRecord = $entries.ToArray()
    }
}

function Test-LiveCoreRestoreIntegrity {
    <#
    .SYNOPSIS
        Verifies a restore reproduced every captured system of record exactly.
    .DESCRIPTION
        Compares a backup manifest against the measurements taken from the
        restored database and object store. It is fail-closed in every direction:
        a missing or empty manifest certifies nothing; a manifest that does not
        list the full catalog certifies nothing; and any restored system that is
        absent, has a different row count, or has a different content checksum
        fails verification. Returns @{ IsFaithful; Failures }.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$SourceManifest,

        [Parameter(Mandatory = $true)]
        [hashtable]$RestoredSystemOfRecord
    )

    $failures = New-Object System.Collections.Generic.List[string]

    $listed = $null
    if ($null -ne $SourceManifest -and ($SourceManifest.PSObject.Properties.Name -contains 'systemsOfRecord')) {
        $listed = $SourceManifest.systemsOfRecord
    }

    if ($null -eq $listed) {
        $failures.Add('The backup manifest is missing or carries no systemsOfRecord, so a restore cannot be verified and is rejected fail-closed.')
        return [pscustomobject]@{ IsFaithful = $false; Failures = $failures.ToArray() }
    }

    $listed = @($listed)
    $listedNames = @($listed | ForEach-Object { [string]$_.name })

    # Re-check coverage: a truncated or tampered manifest must never certify a
    # restore as faithful just because the rows it happens to list matched.
    foreach ($item in (Get-LiveCoreSystemOfRecordCatalog)) {
        if ($listedNames -notcontains $item.Name) {
            $failures.Add("The backup manifest does not cover the system of record '$($item.Name)', so a restore cannot be certified from it.")
        }
    }

    foreach ($entry in $listed) {
        $name = [string]$entry.name
        if (-not $RestoredSystemOfRecord.ContainsKey($name)) {
            $failures.Add("The restored database/object store is missing the system of record '$name' that the backup captured.")
            continue
        }

        $restored = $RestoredSystemOfRecord[$name]
        $expectedCount = [int]$entry.rowCount
        $actualCount = [int]$restored.RowCount
        if ($actualCount -ne $expectedCount) {
            $failures.Add("System of record '$name' row count changed across restore: backup captured $expectedCount, restore has $actualCount.")
        }

        $expectedChecksum = [string]$entry.checksum
        $actualChecksum = [string]$restored.Checksum
        if ($actualChecksum -cne $expectedChecksum) {
            $failures.Add("System of record '$name' content checksum changed across restore: a record was added, dropped or altered.")
        }
    }

    return [pscustomobject]@{
        IsFaithful = ($failures.Count -eq 0)
        Failures   = $failures.ToArray()
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreSystemOfRecordCatalog, `
    Get-LiveCoreContentChecksum, `
    Get-LiveCoreConnectionSetting, `
    Get-LiveCoreBackupManifest, `
    Test-LiveCoreRestoreIntegrity
