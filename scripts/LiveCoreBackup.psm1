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
        [string]$StorageBucket,

        # Optional, additive description of the at-rest encryption applied to the
        # artifacts (CORE-DR-001). When supplied it is recorded for audit/operator
        # confirmation; it does not weaken the coverage check, which still requires
        # every system of record to be present.
        [AllowNull()]
        [hashtable]$Encryption
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

    $manifest = [ordered]@{
        schemaVersion = 1
        createdAtUtc  = $CreatedAtUtc
        database      = $Database
        storageBucket = $StorageBucket
    }
    if ($PSBoundParameters.ContainsKey('Encryption') -and $null -ne $Encryption) {
        $manifest['encryption'] = [pscustomobject]$Encryption
    }
    $manifest['systemsOfRecord'] = $entries.ToArray()

    return [pscustomobject]$manifest
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

# --- Backup-at-rest encryption sink (CORE-DR-001) ----------------------------
#
# The PostgreSQL dump and the mirrored asset binaries hold the audit trail, the
# purchase ledger and all tenant data, so they must never land as plaintext at
# rest (docs/13 "Security"; threats T4/T5/T7). These helpers are the encryption
# sink the backup/restore scripts use: a self-contained, dependency-free
# AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC) file format, keyed by a
# PBKDF2-HMAC-SHA256 derivation of an operator-supplied passphrase. They use only
# the .NET base class library, so they run unchanged on Windows PowerShell 5.1
# (.NET Framework) and PowerShell 7+ (pwsh), stream the file rather than buffering
# it whole, and round-trip in the restore drill with no external tool. No key
# material is read from or written to source: the passphrase is supplied at run
# time (a value or a file), exactly like the database password.

$script:LiveCoreBackupEncryptionMagic = [byte[]]@(0x4C, 0x43, 0x42, 0x4B, 0x45, 0x4E, 0x43, 0x31) # "LCBKENC1"
$script:LiveCoreBackupKdfIterations = 200000
$script:LiveCoreBackupSaltLength = 16
$script:LiveCoreBackupIvLength = 16
$script:LiveCoreBackupMacLength = 32

function Test-LiveCoreFixedTimeEquals {
    # Length-independent, constant-time byte-array comparison for the MAC check.
    # CryptographicOperations.FixedTimeEquals is unavailable on .NET Framework
    # (Windows PowerShell 5.1), so the comparison is done here with an XOR
    # accumulation that never short-circuits.
    param(
        [byte[]]$Left,
        [byte[]]$Right
    )

    if ($null -eq $Left -or $null -eq $Right) { return $false }

    $max = [Math]::Max($Left.Length, $Right.Length)
    $diff = $Left.Length -bxor $Right.Length
    for ($i = 0; $i -lt $max; $i++) {
        $l = if ($i -lt $Left.Length) { $Left[$i] } else { 0 }
        $r = if ($i -lt $Right.Length) { $Right[$i] } else { 0 }
        $diff = $diff -bor ($l -bxor $r)
    }
    return ($diff -eq 0)
}

function Read-LiveCoreExactBytes {
    # Reads exactly $Count bytes into $Buffer; a short read of a backup artifact
    # means the file is truncated/corrupt, which fails closed.
    param(
        [System.IO.Stream]$Stream,
        [byte[]]$Buffer,
        [int]$Count
    )

    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($Buffer, $offset, $Count - $offset)
        if ($read -le 0) {
            throw 'Unexpected end of a LiveCore encrypted backup artifact while reading its header (CORE-DR-001).'
        }
        $offset += $read
    }
}

function Get-LiveCoreBackupKeyMaterial {
    # Derives a 32-byte AES-256 key and a 32-byte HMAC key from the passphrase and
    # a per-artifact random salt via PBKDF2-HMAC-SHA256.
    param(
        [string]$Passphrase,
        [byte[]]$Salt
    )

    $passwordBytes = [System.Text.Encoding]::UTF8.GetBytes($Passphrase)
    $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $passwordBytes, $Salt, $script:LiveCoreBackupKdfIterations,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $block = $kdf.GetBytes(64)
    }
    finally {
        $kdf.Dispose()
    }

    $aesKey = New-Object byte[] 32
    $macKey = New-Object byte[] 32
    [System.Array]::Copy($block, 0, $aesKey, 0, 32)
    [System.Array]::Copy($block, 32, $macKey, 0, 32)
    return @{ AesKey = $aesKey; MacKey = $macKey }
}

function Get-LiveCoreBackupEncryptionSecret {
    <#
    .SYNOPSIS
        Resolves the backup encryption passphrase, fail-closed (CORE-DR-001).
    .DESCRIPTION
        Returns the operator-supplied passphrase from -Passphrase, or from the
        file named by -PassphraseFile, trimming only a trailing newline. When
        neither is configured it THROWS rather than return nothing, so the
        backup/restore scripts refuse to run without an encryption sink instead of
        writing the audit trail, purchase ledger and tenant data as plaintext. The
        passphrase is supplied at run time and is never committed (threat T7).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Passphrase,

        [AllowNull()]
        [AllowEmptyString()]
        [string]$PassphraseFile
    )

    if (-not [string]::IsNullOrWhiteSpace($Passphrase)) {
        return $Passphrase
    }

    if (-not [string]::IsNullOrWhiteSpace($PassphraseFile)) {
        if (-not (Test-Path -Path $PassphraseFile)) {
            throw "Backup encryption passphrase file not found: $PassphraseFile (CORE-DR-001)."
        }
        $fromFile = Get-Content -Path $PassphraseFile -Raw
        if ($null -ne $fromFile) {
            $fromFile = $fromFile.TrimEnd("`r", "`n")
        }
        if ([string]::IsNullOrWhiteSpace($fromFile)) {
            throw "Backup encryption passphrase file is empty: $PassphraseFile (CORE-DR-001)."
        }
        return $fromFile
    }

    throw 'Refusing to run without a backup encryption sink: no passphrase is configured, so the dump and mirrored assets would be written as plaintext. Set Backup__Encryption__Passphrase (or Backup__Encryption__PassphraseFile), or pass -EncryptionPassphrase / -EncryptionPassphraseFile. Backups hold the audit trail, purchase ledger and all tenant data and must be encrypted at rest (CORE-DR-001).'
}

function Protect-LiveCoreBackupFile {
    <#
    .SYNOPSIS
        Encrypts a backup artifact at rest with AES-256-CBC + HMAC-SHA256.
    .DESCRIPTION
        Streams $Path into $Destination as MAGIC || salt || iv || AES-256-CBC
        ciphertext, then appends an HMAC-SHA256 over all of that (encrypt-then-MAC),
        so neither the dump nor an asset binary is ever written as plaintext at
        rest. The source is removed when -RemoveSource is set. Returns $Destination.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$Passphrase,

        [switch]$RemoveSource
    )

    if (-not (Test-Path -Path $Path)) {
        throw "Cannot encrypt a file that does not exist: $Path (CORE-DR-001)."
    }
    if ([string]::IsNullOrWhiteSpace($Passphrase)) {
        throw 'Refusing to encrypt a backup artifact with an empty passphrase (CORE-DR-001).'
    }

    $salt = New-Object byte[] $script:LiveCoreBackupSaltLength
    $iv = New-Object byte[] $script:LiveCoreBackupIvLength
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($salt)
        $rng.GetBytes($iv)
    }
    finally {
        $rng.Dispose()
    }

    $material = Get-LiveCoreBackupKeyMaterial -Passphrase $Passphrase -Salt $salt

    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.KeySize = 256
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $material.AesKey
        $aes.IV = $iv

        # Pass 1: write the header and stream the AES-CBC ciphertext to disk, so a
        # large dump is never buffered whole in memory.
        $output = [System.IO.File]::Create($Destination)
        $outputDisposed = $false
        try {
            $output.Write($script:LiveCoreBackupEncryptionMagic, 0, $script:LiveCoreBackupEncryptionMagic.Length)
            $output.Write($salt, 0, $salt.Length)
            $output.Write($iv, 0, $iv.Length)

            $encryptor = $aes.CreateEncryptor()
            try {
                $crypto = New-Object System.Security.Cryptography.CryptoStream($output, $encryptor, [System.Security.Cryptography.CryptoStreamMode]::Write)
                $source = [System.IO.File]::OpenRead($Path)
                try {
                    $source.CopyTo($crypto)
                }
                finally {
                    $source.Dispose()
                }
                $crypto.FlushFinalBlock()
                $crypto.Dispose() # also disposes $output
                $outputDisposed = $true
            }
            finally {
                $encryptor.Dispose()
            }
        }
        finally {
            if (-not $outputDisposed) {
                $output.Dispose()
            }
        }

        # Pass 2: append the HMAC-SHA256 over magic||salt||iv||ciphertext.
        $macStream = New-Object System.IO.FileStream($Destination, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite)
        try {
            $hmac = New-Object System.Security.Cryptography.HMACSHA256(, $material.MacKey)
            try {
                $macStream.Position = 0
                $mac = $hmac.ComputeHash($macStream)
            }
            finally {
                $hmac.Dispose()
            }
            $macStream.Seek(0, [System.IO.SeekOrigin]::End) | Out-Null
            $macStream.Write($mac, 0, $mac.Length)
        }
        finally {
            $macStream.Dispose()
        }
    }
    finally {
        $aes.Dispose()
    }

    if ($RemoveSource) {
        Remove-Item -Path $Path -Force
    }

    return $Destination
}

function Unprotect-LiveCoreBackupFile {
    <#
    .SYNOPSIS
        Decrypts a backup artifact and verifies its integrity, fail-closed.
    .DESCRIPTION
        Reads the MAGIC || salt || iv header, recomputes the HMAC-SHA256 over
        magic||salt||iv||ciphertext and compares it (constant time) with the stored
        MAC BEFORE decrypting a byte. A wrong passphrase or any tampering makes it
        THROW rather than emit a plaintext file, and a file that is not in the
        LiveCore encrypted-backup format (for example a legacy plaintext dump) is
        refused. Returns $Destination on success (CORE-DR-001).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$Passphrase,

        [switch]$RemoveSource
    )

    if (-not (Test-Path -Path $Path)) {
        throw "Cannot decrypt a file that does not exist: $Path (CORE-DR-001)."
    }
    if ([string]::IsNullOrWhiteSpace($Passphrase)) {
        throw 'Refusing to decrypt a backup artifact with an empty passphrase (CORE-DR-001).'
    }

    $headerLength = $script:LiveCoreBackupEncryptionMagic.Length + $script:LiveCoreBackupSaltLength + $script:LiveCoreBackupIvLength
    $minLength = $headerLength + $script:LiveCoreBackupMacLength

    $artifactStream = [System.IO.File]::OpenRead($Path)
    try {
        if ($artifactStream.Length -lt $minLength) {
            throw "File '$Path' is not a LiveCore encrypted backup artifact (too short); a restore is refused fail-closed (CORE-DR-001)."
        }

        $magic = New-Object byte[] $script:LiveCoreBackupEncryptionMagic.Length
        Read-LiveCoreExactBytes -Stream $artifactStream -Buffer $magic -Count $magic.Length
        for ($i = 0; $i -lt $magic.Length; $i++) {
            if ($magic[$i] -ne $script:LiveCoreBackupEncryptionMagic[$i]) {
                throw "File '$Path' is not a LiveCore encrypted backup artifact (bad header); a restore is refused fail-closed (CORE-DR-001)."
            }
        }

        $salt = New-Object byte[] $script:LiveCoreBackupSaltLength
        Read-LiveCoreExactBytes -Stream $artifactStream -Buffer $salt -Count $salt.Length
        $iv = New-Object byte[] $script:LiveCoreBackupIvLength
        Read-LiveCoreExactBytes -Stream $artifactStream -Buffer $iv -Count $iv.Length

        $material = Get-LiveCoreBackupKeyMaterial -Passphrase $Passphrase -Salt $salt
        $macOffset = $artifactStream.Length - $script:LiveCoreBackupMacLength

        # Verify the MAC over magic||salt||iv||ciphertext before decrypting a byte:
        # a wrong passphrase or any tampering fails closed here (encrypt-then-MAC).
        $hmac = New-Object System.Security.Cryptography.HMACSHA256(, $material.MacKey)
        $computedMac = $null
        try {
            $artifactStream.Position = 0
            $buffer = New-Object byte[] 65536
            $remaining = $macOffset
            while ($remaining -gt 0) {
                $toRead = [Math]::Min([long]$buffer.Length, $remaining)
                $read = $artifactStream.Read($buffer, 0, [int]$toRead)
                if ($read -le 0) { break }
                [void]$hmac.TransformBlock($buffer, 0, $read, $buffer, 0)
                $remaining -= $read
            }
            [void]$hmac.TransformFinalBlock([byte[]]@(), 0, 0)
            $computedMac = $hmac.Hash
        }
        finally {
            $hmac.Dispose()
        }

        $storedMac = New-Object byte[] $script:LiveCoreBackupMacLength
        $artifactStream.Position = $macOffset
        Read-LiveCoreExactBytes -Stream $artifactStream -Buffer $storedMac -Count $storedMac.Length

        if (-not (Test-LiveCoreFixedTimeEquals -Left $computedMac -Right $storedMac)) {
            throw "Backup artifact '$Path' failed its integrity/authentication check: the passphrase is wrong or the file was tampered with. A restore is refused fail-closed (CORE-DR-001)."
        }

        $aes = [System.Security.Cryptography.Aes]::Create()
        try {
            $aes.KeySize = 256
            $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
            $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
            $aes.Key = $material.AesKey
            $aes.IV = $iv

            $decryptor = $aes.CreateDecryptor()
            try {
                $output = [System.IO.File]::Create($Destination)
                $crypto = New-Object System.Security.Cryptography.CryptoStream($output, $decryptor, [System.Security.Cryptography.CryptoStreamMode]::Write)
                try {
                    $artifactStream.Position = $headerLength
                    $remaining = $macOffset - $headerLength
                    $buffer = New-Object byte[] 65536
                    while ($remaining -gt 0) {
                        $toRead = [Math]::Min([long]$buffer.Length, $remaining)
                        $read = $artifactStream.Read($buffer, 0, [int]$toRead)
                        if ($read -le 0) { break }
                        $crypto.Write($buffer, 0, $read)
                        $remaining -= $read
                    }
                    $crypto.FlushFinalBlock()
                }
                finally {
                    $crypto.Dispose() # also disposes $output
                }
            }
            finally {
                $decryptor.Dispose()
            }
        }
        finally {
            $aes.Dispose()
        }
    }
    finally {
        $artifactStream.Dispose()
    }

    if ($RemoveSource) {
        Remove-Item -Path $Path -Force
    }

    return $Destination
}

function Protect-LiveCoreBackupDirectory {
    <#
    .SYNOPSIS
        Encrypts every file in a mirrored-asset directory at rest (CORE-DR-001).
    .DESCRIPTION
        Recursively encrypts each plaintext file under $Path to a sibling
        <name><EncryptedExtension> artifact and removes the plaintext, so the
        locally mirrored asset binaries never remain as plaintext at rest. Files
        already carrying the encrypted extension are skipped, so the operation is
        safe to re-run. Returns the count of files encrypted.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Passphrase,

        [string]$EncryptedExtension = '.enc'
    )

    if (-not (Test-Path -Path $Path -PathType Container)) {
        throw "Cannot encrypt the asset mirror: directory not found: $Path (CORE-DR-001)."
    }

    $count = 0
    $files = @(
        Get-ChildItem -Path $Path -Recurse -File -Force |
            Where-Object { -not $_.Name.EndsWith($EncryptedExtension, [System.StringComparison]::OrdinalIgnoreCase) }
    )
    foreach ($file in $files) {
        $destination = $file.FullName + $EncryptedExtension
        [void](Protect-LiveCoreBackupFile -Path $file.FullName -Destination $destination -Passphrase $Passphrase -RemoveSource)
        $count++
    }
    return $count
}

function Unprotect-LiveCoreBackupDirectory {
    <#
    .SYNOPSIS
        Decrypts every encrypted file in a mirrored-asset directory (CORE-DR-001).
    .DESCRIPTION
        The inverse of Protect-LiveCoreBackupDirectory: recursively decrypts each
        <name><EncryptedExtension> artifact under $Path back to <name> and removes
        the encrypted copy, verifying each one's integrity fail-closed. Returns the
        count of files decrypted.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Passphrase,

        [string]$EncryptedExtension = '.enc'
    )

    if (-not (Test-Path -Path $Path -PathType Container)) {
        throw "Cannot decrypt the asset mirror: directory not found: $Path (CORE-DR-001)."
    }

    $count = 0
    $files = @(
        Get-ChildItem -Path $Path -Recurse -File -Force |
            Where-Object { $_.Name.EndsWith($EncryptedExtension, [System.StringComparison]::OrdinalIgnoreCase) }
    )
    foreach ($file in $files) {
        $destination = $file.FullName.Substring(0, $file.FullName.Length - $EncryptedExtension.Length)
        [void](Unprotect-LiveCoreBackupFile -Path $file.FullName -Destination $destination -Passphrase $Passphrase -RemoveSource)
        $count++
    }
    return $count
}

Export-ModuleMember -Function `
    Get-LiveCoreSystemOfRecordCatalog, `
    Get-LiveCoreContentChecksum, `
    Get-LiveCoreConnectionSetting, `
    Get-LiveCoreBackupManifest, `
    Test-LiveCoreRestoreIntegrity, `
    Get-LiveCoreBackupEncryptionSecret, `
    Protect-LiveCoreBackupFile, `
    Unprotect-LiveCoreBackupFile, `
    Protect-LiveCoreBackupDirectory, `
    Unprotect-LiveCoreBackupDirectory
