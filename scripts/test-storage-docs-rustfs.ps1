#requires -Version 5.1

<#
.SYNOPSIS
    Tests the storage/self-hosting docs RustFS backstop (CORE-STOR-002).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreStorageDocsLint.psm1 - no external test
    framework, so it runs as a CI gate and locally on both pwsh and Windows PowerShell
    5.1. Exits 0 when every assertion holds and 1 when any fails.

    It proves the analysis over fixtures (a clean RustFS doc with the any-S3-compatible
    contract passes; a doc that still names MinIO is flagged; a doc that drops RustFS is
    flagged; the any-S3-compatible contract is required only where asked) and then guards
    the real repository state by invoking the runner against the checked-in docs.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-storage-docs-rustfs.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path -Path $scriptDir -ChildPath 'LiveCoreStorageDocsLint.psm1') -Force
$repoRoot = Split-Path -Parent $scriptDir

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

# Runs the analysis over a single fixture document and returns its findings as an array.
function Get-FixtureFinding {
    param([string]$Content, [bool]$RequireAnyS3 = $true)
    $document = [pscustomobject]@{ Name = 'fixture'; Content = $Content; RequireAnyS3 = $RequireAnyS3 }
    return @(Get-LiveCoreStorageDocFinding -Document @($document))
}

# --- A clean RustFS doc with the any-S3-compatible contract and no MinIO passes. ---
$clean = 'RustFS is the default example S3-compatible object store; any S3-compatible provider works (ADR 0006).'
AssertTrue ((Get-FixtureFinding -Content $clean).Count -eq 0) `
    'a RustFS doc with the any-S3-compatible contract and no MinIO passes'

# --- A doc that still names MinIO is flagged (the grep backstop), reporting the term. ---
$withMinio = "RustFS is the default. Run the minio-setup job and log into the MinIO console with minioadmin.`nany S3-compatible provider works."
$minioFindings = Get-FixtureFinding -Content $withMinio
AssertTrue ($minioFindings.Count -ge 1) 'a doc that still references MinIO is flagged'
AssertTrue ((($minioFindings -join "`n") -match '(?i)MinIO')) 'the MinIO finding names the leftover MinIO reference'

# --- A doc that drops RustFS entirely is flagged. ---
AssertTrue ((Get-FixtureFinding -Content 'any S3-compatible provider works with the S3 adapter.').Count -ge 1) `
    'a doc that does not name RustFS is flagged'

# --- The any-S3-compatible contract is required only where asked. ---
AssertTrue ((Get-FixtureFinding -Content 'RustFS is the bundled object store.' -RequireAnyS3 $true).Count -ge 1) `
    'a required doc missing the any-S3-compatible contract is flagged'
AssertTrue ((Get-FixtureFinding -Content 'RustFS is the bundled object store.' -RequireAnyS3 $false).Count -eq 0) `
    'a non-contract doc need not state the any-S3-compatible contract'

# --- Guard the real tree: the runner passes against the checked-in docs. ---
& (Join-Path -Path $scriptDir -ChildPath 'lint-storage-docs-rustfs.ps1') -RepoRoot $repoRoot
AssertTrue ($LASTEXITCODE -eq 0) 'the checked-in storage/self-hosting docs pass the RustFS backstop'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Storage-docs RustFS backstop tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Storage-docs RustFS backstop tests passed: RustFS default-example detection, the MinIO backstop and the any-S3-compatible contract behave as documented.' -ForegroundColor Green
exit 0
