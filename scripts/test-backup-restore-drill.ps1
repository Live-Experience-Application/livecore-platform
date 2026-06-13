#requires -Version 5.1

<#
.SYNOPSIS
    Restore drill for the LiveCore backup/restore procedure (CORE-OPS-010).

.DESCRIPTION
    A self-contained, dependency-free restore drill: it runs a full
    backup -> persist -> restore -> verify round-trip over a fixture that models
    the Core systems of record (the append-only audit, session-event and purchase
    records plus the asset object store), exercising the SAME coverage and
    integrity logic (LiveCoreBackup.psm1) the real backup-livecore.ps1 and
    restore-livecore.ps1 use. It then proves the verification is fail-closed: a
    restore that silently drops a purchase record, tampers an audit record, loses
    the asset bucket, or is certified by an incomplete manifest is REJECTED, while
    a faithful restore (even with the records re-ordered) is accepted.

    Pure PowerShell with no external test framework, no database and no object
    store, so it runs as a CI gate and locally on both pwsh and Windows
    PowerShell 5.1 - the same shape as test-image-tags.ps1. Exits 0 when every
    assertion holds and 1 when any fails.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-backup-restore-drill.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreBackup.psm1') -Force

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

function AssertThrows {
    param([scriptblock]$Action, [string]$Because)
    $threw = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $threw = $true
    }
    if ($threw) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because (expected a fail-closed error, but it succeeded)")
    }
}

function Get-MeasurementSet {
    param([hashtable]$Content)
    $measure = @{}
    foreach ($name in @($Content.Keys)) {
        $rows = [string[]]@($Content[$name])
        $measure[$name] = @{
            RowCount = $rows.Count
            Checksum = Get-LiveCoreContentChecksum -Row $rows
        }
    }
    return $measure
}

function Copy-DrillContent {
    param([hashtable]$Content)
    $clone = @{}
    foreach ($name in @($Content.Keys)) {
        $clone[$name] = [string[]]@($Content[$name])
    }
    return $clone
}

# A fixture production dataset: every system of record from the catalog, seeded
# with representative append-only records and an asset-bucket inventory. The row
# strings are canonical JSON like a logical dump / object listing would produce.
$source = [ordered]@{
    'audit_logs'                = @(
        '{"id":"aud-1","organization_id":"org-1","action":"MemberRemoved","actor":"usr-9","created_at":"2026-06-10T10:00:00Z"}',
        '{"id":"aud-2","organization_id":"org-1","action":"WorkspaceArchived","actor":"usr-9","created_at":"2026-06-10T11:00:00Z"}',
        '{"id":"aud-3","organization_id":"org-2","action":"SessionCancelled","actor":"usr-4","created_at":"2026-06-10T12:30:00Z"}'
    )
    'session_events'            = @(
        '{"event_id":"evt-1","session_id":"ses-1","event_type":"SessionStarted","created_at":"2026-06-10T09:00:00Z"}',
        '{"event_id":"evt-2","session_id":"ses-1","event_type":"ParticipantJoined","created_at":"2026-06-10T09:01:00Z"}',
        '{"event_id":"evt-3","session_id":"ses-1","event_type":"ContentRevealed","created_at":"2026-06-10T09:05:00Z"}',
        '{"event_id":"evt-4","session_id":"ses-1","event_type":"SessionEnded","created_at":"2026-06-10T10:00:00Z"}'
    )
    'purchase_transactions'     = @(
        '{"id":"ptx-1","provider":"apple","provider_transaction_id":"A-1001","subject":"usr-2","status":"verified"}',
        '{"id":"ptx-2","provider":"google","provider_transaction_id":"G-2002","subject":"usr-7","status":"verified"}'
    )
    'purchase_events'           = @(
        '{"id":"pev-1","purchase_transaction_id":"ptx-1","kind":"verified","created_at":"2026-06-01T00:00:00Z"}',
        '{"id":"pev-2","purchase_transaction_id":"ptx-1","kind":"renewed","created_at":"2026-06-08T00:00:00Z"}',
        '{"id":"pev-3","purchase_transaction_id":"ptx-2","kind":"verified","created_at":"2026-06-09T00:00:00Z"}'
    )
    'store_notification_events' = @(
        '{"id":"sne-1","provider":"apple","provider_notification_id":"N-1","kind":"DID_RENEW","created_at":"2026-06-08T00:00:01Z"}',
        '{"id":"sne-2","provider":"google","provider_notification_id":"N-2","kind":"SUBSCRIPTION_RENEWED","created_at":"2026-06-09T00:00:01Z"}'
    )
    'object-storage'            = @(
        '{"key":"assets/org-1/ws-1/a1b2","etag":"e1","size":10241}',
        '{"key":"assets/org-1/ws-1/c3d4","etag":"e2","size":2048}',
        '{"key":"assets/org-2/ws-9/e5f6","etag":"e3","size":512}'
    )
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-restore-drill-" + [System.Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    # --- Catalog covers the systems of record the acceptance criterion names. ---
    $catalogNames = @((Get-LiveCoreSystemOfRecordCatalog) | ForEach-Object { $_.Name })
    foreach ($required in @('audit_logs', 'session_events', 'purchase_transactions', 'purchase_events', 'object-storage')) {
        AssertTrue ($catalogNames -contains $required) "the systems-of-record catalog covers '$required'"
    }

    # --- 1. Back up: measure each system of record and build the manifest. ---
    $sourceMeasure = Get-MeasurementSet -Content $source
    $manifest = Get-LiveCoreBackupManifest `
        -SystemOfRecord $sourceMeasure `
        -CreatedAtUtc '2026-06-13T00:00:00Z' `
        -Database 'livecore' `
        -StorageBucket 'livecore-assets'

    # --- 2. Persist the backup artifacts to disk (the round-trip through JSON the
    #        real backup writes and the real restore reads). ---
    $manifestPath = Join-Path $tempRoot 'livecore-backup-manifest.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding UTF8
    $payloadPath = Join-Path $tempRoot 'systems-of-record.payload.json'
    ([pscustomobject]$source) | ConvertTo-Json -Depth 6 | Set-Content -Path $payloadPath -Encoding UTF8
    AssertTrue (Test-Path $manifestPath) 'the backup writes a coverage manifest to disk'

    # --- 3. Restore: read the artifacts back, re-measure, and verify. ---
    $restoredManifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    $restoredPayload = Get-Content -Path $payloadPath -Raw | ConvertFrom-Json
    $restoredContent = @{}
    foreach ($property in $restoredPayload.PSObject.Properties) {
        $restoredContent[$property.Name] = [string[]]@($property.Value)
    }
    $restoredMeasure = Get-MeasurementSet -Content $restoredContent

    $faithful = Test-LiveCoreRestoreIntegrity -SourceManifest $restoredManifest -RestoredSystemOfRecord $restoredMeasure
    AssertTrue $faithful.IsFaithful 'a faithful backup -> restore round-trip recovers every system of record'

    # --- A re-ordered-but-complete restore is still faithful (the integrity
    #     signal is set-based, so a restore is not falsely failed for row order). ---
    $reordered = Copy-DrillContent -Content $source
    $shuffled = [string[]]@($reordered['session_events'])
    [System.Array]::Reverse($shuffled)
    $reordered['session_events'] = $shuffled
    $reorderVerdict = Test-LiveCoreRestoreIntegrity -SourceManifest $manifest -RestoredSystemOfRecord (Get-MeasurementSet -Content $reordered)
    AssertTrue $reorderVerdict.IsFaithful 'a restore that preserves every record but re-orders them is still accepted'

    # --- Negative: a restore that silently DROPPED a purchase record is rejected. ---
    $dropped = Copy-DrillContent -Content $source
    $dropped['purchase_events'] = [string[]]@($dropped['purchase_events'] | Select-Object -First 2)
    $dropVerdict = Test-LiveCoreRestoreIntegrity -SourceManifest $manifest -RestoredSystemOfRecord (Get-MeasurementSet -Content $dropped)
    AssertFalse $dropVerdict.IsFaithful 'a restore that drops a purchase record is rejected (data loss)'
    AssertTrue ([bool]($dropVerdict.Failures -match 'purchase_events')) 'the dropped-record failure names purchase_events'

    # --- Negative: a restore that TAMPERED an append-only audit record (same row
    #     count, altered content) is rejected by the content checksum. ---
    $tampered = Copy-DrillContent -Content $source
    $auditRows = [string[]]@($tampered['audit_logs'])
    $auditRows[0] = '{"id":"aud-1","organization_id":"org-1","action":"MemberRemoved","actor":"usr-1","created_at":"2026-06-10T10:00:00Z"}'
    $tampered['audit_logs'] = $auditRows
    $tamperMeasure = Get-MeasurementSet -Content $tampered
    $tamperVerdict = Test-LiveCoreRestoreIntegrity -SourceManifest $manifest -RestoredSystemOfRecord $tamperMeasure
    AssertFalse $tamperVerdict.IsFaithful 'a restore that alters an append-only audit record is rejected (tamper)'
    AssertTrue ($tamperMeasure['audit_logs'].RowCount -eq $sourceMeasure['audit_logs'].RowCount) 'the tamper kept the row count identical, so only the checksum catches it'
    AssertTrue ([bool]($tamperVerdict.Failures -match 'audit_logs')) 'the tamper failure names audit_logs'

    # --- Negative: a restore that LOST the asset object store entirely is rejected. ---
    $noBucket = Get-MeasurementSet -Content $source
    [void]$noBucket.Remove('object-storage')
    $noBucketVerdict = Test-LiveCoreRestoreIntegrity -SourceManifest $manifest -RestoredSystemOfRecord $noBucket
    AssertFalse $noBucketVerdict.IsFaithful 'a restore missing the asset object store is rejected'
    AssertTrue ([bool]($noBucketVerdict.Failures -match 'object-storage')) 'the missing-store failure names object-storage'

    # --- Negative: the backup itself is fail-closed - a manifest cannot be built
    #     when a system of record is not covered. ---
    $incompleteMeasure = Get-MeasurementSet -Content $source
    [void]$incompleteMeasure.Remove('audit_logs')
    AssertThrows {
        Get-LiveCoreBackupManifest -SystemOfRecord $incompleteMeasure -CreatedAtUtc '2026-06-13T00:00:00Z' -Database 'livecore' -StorageBucket 'livecore-assets'
    } 'a backup manifest cannot be written without audit_logs coverage'

    # --- Negative: a truncated/empty manifest certifies nothing, even against a
    #     perfect restore. ---
    $truncatedManifest = [pscustomobject][ordered]@{
        schemaVersion   = 1
        createdAtUtc    = '2026-06-13T00:00:00Z'
        database        = 'livecore'
        storageBucket   = 'livecore-assets'
        systemsOfRecord = @($manifest.systemsOfRecord | Select-Object -First 2)
    }
    $truncatedVerdict = Test-LiveCoreRestoreIntegrity -SourceManifest $truncatedManifest -RestoredSystemOfRecord $sourceMeasure
    AssertFalse $truncatedVerdict.IsFaithful 'an incomplete manifest cannot certify a restore, even a perfect one'

    $nullVerdict = Test-LiveCoreRestoreIntegrity -SourceManifest $null -RestoredSystemOfRecord $sourceMeasure
    AssertFalse $nullVerdict.IsFaithful 'a missing manifest certifies nothing (fail-closed)'

    # --- The connection-string parser the orchestrators rely on. ---
    $parsed = Get-LiveCoreConnectionSetting -ConnectionString 'Host=db.example.com;Port=6543;Database=livecore;Username=svc;Password=s3cr3t'
    AssertTrue ($parsed.Host -eq 'db.example.com' -and $parsed.Port -eq '6543' -and $parsed.Database -eq 'livecore' -and $parsed.Username -eq 'svc') 'the connection-string parser resolves host/port/database/username'
    $defaultPort = Get-LiveCoreConnectionSetting -ConnectionString 'Host=db;Database=livecore'
    AssertTrue ($defaultPort.Port -eq '5432') 'the connection-string parser defaults the port to 5432'
    AssertThrows { Get-LiveCoreConnectionSetting -ConnectionString '' } 'an empty connection string fails closed'
    AssertThrows { Get-LiveCoreConnectionSetting -ConnectionString 'Port=5432;Username=svc' } 'a connection string with no host/database fails closed'

    # --- The backup and restore orchestrators are valid, parseable PowerShell. ---
    foreach ($orchestrator in @('backup-livecore.ps1', 'restore-livecore.ps1')) {
        $orchestratorPath = Join-Path $scriptDir $orchestrator
        AssertTrue (Test-Path $orchestratorPath) "$orchestrator exists alongside the drill"
        $parseErrors = $null
        $parseTokens = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($orchestratorPath, [ref]$parseTokens, [ref]$parseErrors)
        AssertTrue ($parseErrors.Count -eq 0) "$orchestrator parses without syntax errors"
    }
}
finally {
    Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Restore drill FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Restore drill passed: a faithful backup/restore recovers every system of record, and a lossy or tampered restore is rejected fail-closed.' -ForegroundColor Green
exit 0
