#requires -Version 5.1

<#
.SYNOPSIS
    Enforces the image signature + SBOM attestation gate (CORE-SEC-008): validates
    that a published (or locally-built) image carries a verified cosign signature
    and a verified CycloneDX SBOM attestation.

.DESCRIPTION
    The CI publish path runs `cosign verify` and `cosign verify-attestation`
    against an image digest and redirects their JSON output to files; this CLI
    makes the published-or-blocked decision over those files. cosign's own exit
    code is the first line of defence, but this gate is defence in depth: it
    fails closed when a verification document is missing, empty, malformed, or
    proves no signature / no CycloneDX attestation - so a cosign run that somehow
    exited zero with no usable output still blocks the publish.

    At least one of -SignatureVerificationPath / -AttestationVerificationPath
    must be given; when both are given both must verify. Pass -ReportOnly to print
    the same verdict without ever failing (kept for symmetry with the other
    supply-chain gates; CI runs this blocking in both the publish and the
    publish-dry-run jobs).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/assert-image-attestation.ps1 -SignatureVerificationPath api.sig.json -AttestationVerificationPath api.att.json

.EXAMPLE
    pwsh -File scripts/assert-image-attestation.ps1 -AttestationVerificationPath api.att.json -PredicateType cyclonedx
#>

[CmdletBinding()]
param(
    # `cosign verify -o json` output for the image under test.
    [string]$SignatureVerificationPath,

    # `cosign verify-attestation -o json` output for the same image.
    [string]$AttestationVerificationPath,

    # The attestation predicate type that must be present. Default: cyclonedx.
    [string]$PredicateType = 'cyclonedx',

    # Print the verdict without ever failing (symmetry with the other gates).
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreImageAttestation.psm1') -Force

function Write-LiveCoreAttestationOutcome {
    param([bool]$Failed, [string]$Message, [bool]$IsReportOnly)
    if (-not $Failed) {
        Write-Host "GATE PASSED: $Message" -ForegroundColor Green
        exit 0
    }
    if ($IsReportOnly) {
        Write-Host "GATE WOULD FAIL (report-only, not blocking): $Message" -ForegroundColor Yellow
        exit 0
    }
    Write-Host "GATE FAILED: $Message" -ForegroundColor Red
    exit 1
}

if (-not $SignatureVerificationPath -and -not $AttestationVerificationPath) {
    Write-LiveCoreAttestationOutcome -Failed $true -Message 'no verification document supplied (need a signature and/or an attestation document)' -IsReportOnly $ReportOnly
}

$reasons = New-Object System.Collections.Generic.List[string]

# Signature verification. Fail-closed: a missing or malformed document blocks.
if ($SignatureVerificationPath) {
    try {
        $model = Get-LiveCoreVerificationModel -Path $SignatureVerificationPath
        $signature = Test-LiveCoreImageSignature -Model $model
        Write-Host "Signature verification: $SignatureVerificationPath" -ForegroundColor Cyan
        Write-Host "  verified entries: $($model.EntryCount)"
        if (-not $signature.IsValid) {
            foreach ($message in $signature.Findings) { Write-Host "  $message" -ForegroundColor Red }
            $reasons.Add(($signature.Findings -join ' '))
        }
    }
    catch {
        $message = "could not read the signature verification '$SignatureVerificationPath': $($_.Exception.Message)"
        Write-Host "  $message" -ForegroundColor Red
        $reasons.Add($message)
    }
}

# SBOM attestation verification. Fail-closed: missing/malformed/wrong-type blocks.
if ($AttestationVerificationPath) {
    try {
        $model = Get-LiveCoreVerificationModel -Path $AttestationVerificationPath
        $attestation = Test-LiveCoreImageAttestation -Model $model -PredicateType $PredicateType
        Write-Host "Attestation verification: $AttestationVerificationPath" -ForegroundColor Cyan
        Write-Host "  predicate type required: $PredicateType; verified entries: $($model.EntryCount)"
        if (-not $attestation.IsValid) {
            foreach ($message in $attestation.Findings) { Write-Host "  $message" -ForegroundColor Red }
            $reasons.Add(($attestation.Findings -join ' '))
        }
    }
    catch {
        $message = "could not read the attestation verification '$AttestationVerificationPath': $($_.Exception.Message)"
        Write-Host "  $message" -ForegroundColor Red
        $reasons.Add($message)
    }
}

if ($reasons.Count -gt 0) {
    Write-LiveCoreAttestationOutcome -Failed $true -Message ($reasons -join '; ') -IsReportOnly $ReportOnly
}

$parts = New-Object System.Collections.Generic.List[string]
if ($SignatureVerificationPath) { $parts.Add('a verified signature') }
if ($AttestationVerificationPath) { $parts.Add("a verified $PredicateType SBOM attestation") }
Write-LiveCoreAttestationOutcome -Failed $false -Message ($parts -join ' and ') -IsReportOnly $ReportOnly
