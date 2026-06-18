#requires -Version 5.1

<#
.SYNOPSIS
    Fails the build if the self-hosting/storage docs drop RustFS as the default example
    S3-compatible backend or keep leftover MinIO setup guidance (CORE-STOR-002).

.DESCRIPTION
    The grep backstop required by CORE-STOR-002. After the MinIO -> RustFS object-store
    migration, the documents below must keep RustFS named as the DEFAULT EXAMPLE
    S3-compatible backend, carry no MinIO setup/console/credential guidance, and keep the
    any-S3-compatible contract stated so an operator may still bring their own provider
    (ADR 0006). The analysis lives in scripts/LiveCoreStorageDocsLint.psm1 so it is shared
    with the seeded tests (scripts/test-storage-docs-rustfs.ps1).

    Exits 0 when every document satisfies the invariants, 1 when drift is found, and 2 on
    a configuration error (a doc that cannot be found).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh).

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-storage-docs-rustfs.ps1
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $scriptDir
}

Import-Module (Join-Path -Path $scriptDir -ChildPath 'LiveCoreStorageDocsLint.psm1') -Force

# The acceptance-criteria documents CORE-STOR-002 keeps pointed at RustFS. RequireAnyS3
# marks the docs that must keep the any-S3-compatible contract explicit (the storage,
# self-hosting, top-level and compose docs); the client example need only avoid MinIO.
$specifications = @(
    @{ Path = 'docs/12_STORAGE_ASSETS.md'; RequireAnyS3 = $true },
    @{ Path = 'docs/13_SELF_HOSTING_REQUIREMENTS.md'; RequireAnyS3 = $true },
    @{ Path = 'README.md'; RequireAnyS3 = $true },
    @{ Path = 'deploy/compose/README.md'; RequireAnyS3 = $true },
    @{ Path = 'examples/minimal-consumer/README.md'; RequireAnyS3 = $false }
)

$documents = New-Object System.Collections.Generic.List[object]
foreach ($specification in $specifications) {
    $fullPath = Join-Path -Path $RepoRoot -ChildPath $specification.Path
    if (-not (Test-Path -LiteralPath $fullPath)) {
        Write-Host "Storage-docs RustFS backstop ERROR: missing file $($specification.Path)" -ForegroundColor Red
        exit 2
    }

    $documents.Add([pscustomobject]@{
            Name = $specification.Path
            Content = (Get-Content -LiteralPath $fullPath -Raw)
            RequireAnyS3 = $specification.RequireAnyS3
        })
}

$findings = @(Get-LiveCoreStorageDocFinding -Document $documents)

if ($findings.Count -gt 0) {
    Write-Host "Storage-docs RustFS backstop FAILED: $($findings.Count) finding(s)." -ForegroundColor Red
    foreach ($finding in $findings) {
        Write-Host "  - $finding"
    }
    Write-Host 'RustFS is the default example S3-compatible backend (CORE-STOR-002): replace any remaining MinIO setup/console/credential guidance with the RustFS equivalent and keep the any-S3-compatible contract stated (ADR 0006).'
    exit 1
}

Write-Host "Storage-docs RustFS backstop passed: $($documents.Count) doc(s) name RustFS as the default example, carry no MinIO setup guidance, and keep the any-S3-compatible contract." -ForegroundColor Green
exit 0
