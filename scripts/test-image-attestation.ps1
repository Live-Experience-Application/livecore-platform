#requires -Version 5.1

<#
.SYNOPSIS
    Tests the image signature + SBOM attestation gate (CORE-SEC-008): a verified
    signature and a verified CycloneDX attestation pass, while a missing, empty,
    wrong-type or malformed verification document fails closed.

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreImageAttestation.psm1 and
    assert-image-attestation.ps1 - no external test framework and no
    Docker/registry/cosign, so it runs as a CI gate and locally on both pwsh and
    Windows PowerShell 5.1. Exits 0 when every assertion holds and 1 when any
    fails.

    This is the deterministic proof of the CORE-SEC-008 required behaviour: the
    publish path's `cosign verify` / `cosign verify-attestation` decision "fails
    closed if the signature or SBOM attestation is missing or invalid". The real
    cosign round-trip (keyless on publish; a local key against a locally-built
    digest, no push, on publish-dry-run) is exercised in CI; this test proves the
    GATE LOGIC without committing a real key or contacting a registry.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-image-attestation.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreImageAttestation.psm1') -Force
$assertScript = Join-Path $scriptDir 'assert-image-attestation.ps1'

$failures = New-Object System.Collections.Generic.List[string]

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because") }
}

function AssertThrows {
    param([scriptblock]$Action, [string]$Because)
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    if ($threw) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because (expected a fail-closed error, but it succeeded)") }
}

# --- Seeded fixtures. Built with ConvertTo-Json so the in-toto DSSE payloads are
#     real base64-encoded statements, exactly as cosign emits them. The content is
#     product-neutral on purpose: only generic image/package names. ---

# A `cosign verify -o json` array with one verified signature claim.
$signedReference = 'localhost:5000/livecore-api'
$signatureOutput = @(
    [ordered]@{
        critical = [ordered]@{
            identity = [ordered]@{ 'docker-reference' = $signedReference }
            image    = [ordered]@{ 'docker-manifest-digest' = 'sha256:1111111111111111111111111111111111111111111111111111111111111111' }
            type     = 'cosign container image signature'
        }
        optional = [ordered]@{
            Subject = 'https://github.com/example/livecore-platform/.github/workflows/ci.yml@refs/tags/v0.0.0'
            Issuer  = 'https://token.actions.githubusercontent.com'
        }
    }
) | ConvertTo-Json -Depth 10

# An empty `cosign verify` result (no signature claim) - must fail closed.
$emptyOutput = '[]'

function BuildAttestationOutput {
    param([string]$PredicateType, $Predicate, [switch]$Compress)
    $statement = [ordered]@{
        _type         = 'https://in-toto.io/Statement/v0.1'
        predicateType = $PredicateType
        subject       = @([ordered]@{ name = $signedReference; digest = [ordered]@{ sha256 = '1111111111111111111111111111111111111111111111111111111111111111' } })
        predicate     = $Predicate
    } | ConvertTo-Json -Depth 10
    $payload = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($statement))
    $envelope = [ordered]@{
        payloadType = 'application/vnd.in-toto+json'
        payload     = $payload
    }
    # cosign emits one compact JSON object per line when it verifies several
    # attestations, so the newline-delimited fixture uses the compact form.
    if ($Compress) { return ($envelope | ConvertTo-Json -Depth 10 -Compress) }
    return ($envelope | ConvertTo-Json -Depth 10)
}

# A verified CycloneDX SBOM attestation with a non-empty predicate.
$cycloneDxPredicate = [ordered]@{
    bomFormat    = 'CycloneDX'
    specVersion  = '1.5'
    serialNumber = 'urn:uuid:00000000-0000-0000-0000-000000000001'
    components   = @([ordered]@{ type = 'library'; name = 'openssl'; version = '3.0.1' })
}
$cycloneDxAttestation = BuildAttestationOutput -PredicateType 'https://cyclonedx.org/bom' -Predicate $cycloneDxPredicate

# An attestation of the WRONG predicate type (SPDX) - must fail the CycloneDX gate.
$spdxAttestation = BuildAttestationOutput -PredicateType 'https://spdx.dev/Document' -Predicate $cycloneDxPredicate

# A CycloneDX attestation with an EMPTY predicate - must fail closed.
$emptyPredicateAttestation = BuildAttestationOutput -PredicateType 'https://cyclonedx.org/bom' -Predicate ([ordered]@{})

# A DSSE entry whose payload is not valid base64 - must not count as attested.
$malformedPayloadAttestation = ([ordered]@{ payloadType = 'application/vnd.in-toto+json'; payload = 'not-valid-base64!!' } | ConvertTo-Json -Depth 10)

# --- Signature gate: a verified signature passes; an empty result fails closed. ---
$signatureModel = Get-LiveCoreVerificationModel -Content $signatureOutput
AssertTrue ((Test-LiveCoreImageSignature -Model $signatureModel).IsValid) 'a verified cosign signature passes the signature gate'
AssertTrue ($signatureModel.EntryCount -eq 1) 'the signature document records one verified entry'

$emptyModel = Get-LiveCoreVerificationModel -Content $emptyOutput
AssertTrue (-not (Test-LiveCoreImageSignature -Model $emptyModel).IsValid) 'an empty verification result fails the signature gate (fail-closed)'

# --- Attestation gate: a CycloneDX attestation passes; wrong/empty/malformed fail closed. ---
$cdxModel = Get-LiveCoreVerificationModel -Content $cycloneDxAttestation
AssertTrue ((Test-LiveCoreImageAttestation -Model $cdxModel -PredicateType 'cyclonedx').IsValid) 'a verified CycloneDX SBOM attestation passes the attestation gate'

$spdxModel = Get-LiveCoreVerificationModel -Content $spdxAttestation
AssertTrue (-not (Test-LiveCoreImageAttestation -Model $spdxModel -PredicateType 'cyclonedx').IsValid) 'an SPDX attestation does not satisfy the required CycloneDX attestation (fail-closed)'

$emptyPredicateModel = Get-LiveCoreVerificationModel -Content $emptyPredicateAttestation
AssertTrue (-not (Test-LiveCoreImageAttestation -Model $emptyPredicateModel -PredicateType 'cyclonedx').IsValid) 'a CycloneDX attestation with an empty predicate fails closed'

$malformedPayloadModel = Get-LiveCoreVerificationModel -Content $malformedPayloadAttestation
AssertTrue (-not (Test-LiveCoreImageAttestation -Model $malformedPayloadModel -PredicateType 'cyclonedx').IsValid) 'a DSSE entry with an undecodable payload is not a usable attestation (fail-closed)'

# A signature-only result has no attestation, and an attestation has no signature claim.
AssertTrue (-not (Test-LiveCoreImageAttestation -Model $signatureModel -PredicateType 'cyclonedx').IsValid) 'a signature-only result does not satisfy the attestation gate'
AssertTrue (-not (Test-LiveCoreImageSignature -Model $cdxModel).IsValid) 'an attestation-only result does not satisfy the signature gate'

# --- Newline-delimited attestation output (the multi-line shape cosign can emit) parses. ---
$cycloneDxCompact = BuildAttestationOutput -PredicateType 'https://cyclonedx.org/bom' -Predicate $cycloneDxPredicate -Compress
$spdxCompact = BuildAttestationOutput -PredicateType 'https://spdx.dev/Document' -Predicate $cycloneDxPredicate -Compress
$ndjsonAttestation = "$cycloneDxCompact`n$spdxCompact"
$ndjsonModel = Get-LiveCoreVerificationModel -Content $ndjsonAttestation
AssertTrue ($ndjsonModel.EntryCount -eq 2) 'newline-delimited cosign output parses every entry'
AssertTrue ((Test-LiveCoreImageAttestation -Model $ndjsonModel -PredicateType 'cyclonedx').IsValid) 'a CycloneDX entry among several is found'

# --- Fail-closed: a malformed document is rejected, never waved through. ---
AssertThrows { Get-LiveCoreVerificationModel -Content 'not json {' } 'a malformed verification document is rejected (fail-closed)'
AssertThrows { Get-LiveCoreVerificationModel -Content '   ' } 'an empty verification document is rejected (fail-closed)'

# --- End to end: the assert-image-attestation.ps1 CLI enforces the gate. ---
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-attest-" + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $tempDir
$psExe = (Get-Process -Id $PID).Path
try {
    $sigPath = Join-Path $tempDir 'api.sig.json'
    $attPath = Join-Path $tempDir 'api.att.json'
    $emptyPath = Join-Path $tempDir 'empty.json'
    $spdxPath = Join-Path $tempDir 'spdx.att.json'
    [System.IO.File]::WriteAllText($sigPath, $signatureOutput)
    [System.IO.File]::WriteAllText($attPath, $cycloneDxAttestation)
    [System.IO.File]::WriteAllText($emptyPath, $emptyOutput)
    [System.IO.File]::WriteAllText($spdxPath, $spdxAttestation)

    & $psExe -NoProfile -File $assertScript -SignatureVerificationPath $sigPath -AttestationVerificationPath $attPath *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'the CLI exits zero for a verified signature and a verified CycloneDX attestation'

    & $psExe -NoProfile -File $assertScript -SignatureVerificationPath $emptyPath -AttestationVerificationPath $attPath *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'the CLI blocks when the signature is missing (empty verify result, fail-closed)'

    & $psExe -NoProfile -File $assertScript -SignatureVerificationPath $sigPath -AttestationVerificationPath $spdxPath *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'the CLI blocks when the SBOM attestation is the wrong predicate type (fail-closed)'

    & $psExe -NoProfile -File $assertScript -AttestationVerificationPath (Join-Path $tempDir 'does-not-exist.json') *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'a missing verification document blocks the publish (fail-closed)'

    & $psExe -NoProfile -File $assertScript *> $null
    AssertTrue ($LASTEXITCODE -ne 0) 'the CLI blocks when no verification document is supplied (fail-closed)'

    & $psExe -NoProfile -File $assertScript -SignatureVerificationPath $emptyPath -ReportOnly *> $null
    AssertTrue ($LASTEXITCODE -eq 0) 'report-only mode never blocks, even on a missing signature'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Image attestation gate tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host $failure }
    exit 1
}

Write-Host ''
Write-Host 'Image attestation gate tests passed: a verified signature and CycloneDX SBOM attestation pass, and a missing, empty, wrong-type or malformed verification fails closed.' -ForegroundColor Green
exit 0
