#requires -Version 5.1

<#
.SYNOPSIS
    Supply-chain gate logic for the published container images' SIGNATURE and
    SBOM ATTESTATION verification (CORE-SEC-008): the pass/fail decision over
    cosign `verify` / `verify-attestation` output.

.DESCRIPTION
    On a release tag the CI publish path signs each pushed image (keyless OIDC
    cosign) and attaches its CycloneDX SBOM as an in-toto attestation, then runs
    `cosign verify` and `cosign verify-attestation` against the PUBLISHED digest.
    The publish-dry-run mirrors the same round-trip against a locally-built
    digest with a throwaway key (no push). cosign performs the cryptography and
    its exit code is the first line of defence, but the *decision* that turns its
    verification output into a published-or-blocked verdict is this module's pure
    functions - so "a missing or invalid signature / SBOM attestation fails
    closed" is deterministically testable from seeded fixtures with no registry,
    no network, no Docker and no cosign binary (scripts/test-image-attestation.ps1).

    The gate is fail-closed: an empty, missing, malformed or unexpected
    verification document never counts as a verified signature or attestation, so
    a cosign run that somehow exited zero with no usable output still blocks. A
    signature is verified only when at least one signature claim is present, and
    the SBOM attestation only when at least one in-toto statement of the requested
    predicate type (CycloneDX by default) carries a non-empty predicate.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

# The CycloneDX in-toto predicate type cosign records for `--type cyclonedx`. The
# attestation gate matches a predicate type that CONTAINS this token (case
# insensitive), so both the bare alias and the canonical URI are recognized.
$script:CycloneDxPredicateToken = 'cyclonedx'

function Get-LiveCoreAttestationJsonValue {
    # Safe property read off a ConvertFrom-Json object: returns the value when the
    # named property exists, otherwise $null. Works the same on Windows PowerShell
    # 5.1 and pwsh, where ConvertFrom-Json yields PSCustomObjects.
    [CmdletBinding()]
    [OutputType([object])]
    param(
        [Parameter(Mandatory = $true)][AllowNull()]$InputObject,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function ConvertFrom-LiveCoreCosignOutput {
    <#
    .SYNOPSIS
        Parses cosign verification output into an array of entries, tolerating
        both a single JSON array (`cosign verify -o json`) and the
        newline-delimited JSON objects `cosign verify-attestation` can emit.
    .DESCRIPTION
        Fail-closed: empty input throws, and text that is neither a JSON array,
        a JSON object, nor a sequence of JSON objects throws - so an unreadable
        verification document blocks rather than silently passing.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        throw 'Empty document: expected cosign verification JSON output.'
    }

    # First try the whole document as one JSON value (an array for `verify`, a
    # single object for a one-line `verify-attestation`).
    try {
        $whole = $Text | ConvertFrom-Json
        if ($whole -is [System.Collections.IEnumerable] -and $whole -isnot [string]) {
            return @($whole)
        }
        return @($whole)
    }
    catch {
        # The whole-document parse failed (for example newline-delimited objects);
        # fall through to the line-by-line parsing below.
        Write-Verbose "Whole-document JSON parse failed, trying newline-delimited: $($_.Exception.Message)"
    }

    # Newline-delimited JSON: parse each non-blank line, fail-closed if none parse.
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($line in ($Text -split "`n")) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
        try {
            $entries.Add(($trimmed | ConvertFrom-Json))
        }
        catch {
            throw "Malformed cosign verification document: $($_.Exception.Message)"
        }
    }

    if ($entries.Count -eq 0) {
        throw 'Malformed cosign verification document: no JSON entries found.'
    }
    return $entries.ToArray()
}

function Get-LiveCoreAttestationPredicate {
    # Resolves an entry's in-toto predicate type and whether it carries a non-empty
    # predicate. cosign's DSSE entries carry a base64 `payload` (the in-toto
    # Statement); some shapes expose `predicateType`/`predicate` directly. Returns
    # a PSCustomObject with PredicateType (string or '') and HasPredicate (bool).
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][AllowNull()]$Entry)

    $predicateType = ''
    $hasPredicate = $false

    $payload = Get-LiveCoreAttestationJsonValue -InputObject $Entry -Name 'payload'
    if ($payload -is [string] -and -not [string]::IsNullOrWhiteSpace($payload)) {
        try {
            $decodedBytes = [System.Convert]::FromBase64String($payload)
            $statementText = [System.Text.Encoding]::UTF8.GetString($decodedBytes)
            $statement = $statementText | ConvertFrom-Json
            $predicateType = [string](Get-LiveCoreAttestationJsonValue -InputObject $statement -Name 'predicateType')
            $predicate = Get-LiveCoreAttestationJsonValue -InputObject $statement -Name 'predicate'
            $hasPredicate = ($null -ne $predicate) -and (@($predicate.PSObject.Properties).Count -gt 0)
        }
        catch {
            # A payload that does not base64-decode to an in-toto Statement is not a
            # usable attestation; leave it as "no predicate" so the gate fails closed.
            return [pscustomobject]@{ PredicateType = ''; HasPredicate = $false }
        }
    }
    else {
        # Already-decoded shape: predicateType/predicate on the entry itself.
        $predicateType = [string](Get-LiveCoreAttestationJsonValue -InputObject $Entry -Name 'predicateType')
        $predicate = Get-LiveCoreAttestationJsonValue -InputObject $Entry -Name 'predicate'
        $hasPredicate = ($null -ne $predicate) -and (@($predicate.PSObject.Properties).Count -gt 0)
    }

    return [pscustomobject]@{
        PredicateType = $predicateType
        HasPredicate  = $hasPredicate
    }
}

function Get-LiveCoreVerificationModel {
    <#
    .SYNOPSIS
        Parses cosign verification output (from a path or literal content) into a
        normalized entry list.
    .OUTPUTS
        A PSCustomObject with EntryCount (int) and Entries (an array of
        PSCustomObjects carrying IsSignature (bool), PredicateType (string) and
        HasPredicate (bool)).
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
        [string]$Path,

        [Parameter(Mandatory = $true, ParameterSetName = 'Content')]
        [AllowEmptyString()]
        [string]$Content
    )

    if ($PSCmdlet.ParameterSetName -eq 'Path') {
        if (-not (Test-Path -LiteralPath $Path)) {
            throw "cosign verification document not found: $Path"
        }
        $text = [System.IO.File]::ReadAllText($Path)
    }
    else {
        $text = $Content
    }

    $rawEntries = ConvertFrom-LiveCoreCosignOutput -Text $text

    $entries = New-Object System.Collections.Generic.List[psobject]
    foreach ($entry in $rawEntries) {
        if ($null -eq $entry) { continue }
        $critical = Get-LiveCoreAttestationJsonValue -InputObject $entry -Name 'critical'
        $predicate = Get-LiveCoreAttestationPredicate -Entry $entry
        $entries.Add([pscustomobject]@{
                IsSignature   = ($null -ne $critical)
                PredicateType = $predicate.PredicateType
                HasPredicate  = $predicate.HasPredicate
            })
    }

    return [pscustomobject]@{
        EntryCount = $entries.Count
        Entries    = $entries.ToArray()
    }
}

function Test-LiveCoreImageSignature {
    <#
    .SYNOPSIS
        Decides whether a parsed `cosign verify` model proves a valid signature.
    .DESCRIPTION
        Valid only when at least one verified signature claim is present, so an
        empty or signature-free document fails closed.
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]).
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][psobject]$Model)

    $findings = New-Object System.Collections.Generic.List[string]
    $signatureCount = @($Model.Entries | Where-Object { $_.IsSignature }).Count

    if ($signatureCount -le 0) {
        $findings.Add('No verified signature claim found, so the image is not provably signed.')
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

function Test-LiveCoreImageAttestation {
    <#
    .SYNOPSIS
        Decides whether a parsed `cosign verify-attestation` model proves a valid
        SBOM attestation of the requested predicate type.
    .DESCRIPTION
        Valid only when at least one in-toto statement matches the requested
        predicate type (CycloneDX by default) AND carries a non-empty predicate,
        so a missing, empty or wrong-type attestation fails closed.
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]).
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true)][psobject]$Model,
        [string]$PredicateType = $script:CycloneDxPredicateToken
    )

    $findings = New-Object System.Collections.Generic.List[string]
    $token = $PredicateType.ToLowerInvariant()

    $matching = @($Model.Entries | Where-Object {
            $_.PredicateType -and ($_.PredicateType.ToLowerInvariant().Contains($token)) -and $_.HasPredicate
        })

    if ($matching.Count -le 0) {
        $findings.Add("No verified '$PredicateType' attestation with a non-empty predicate found, so the SBOM is not provably attested.")
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreVerificationModel, `
    Test-LiveCoreImageSignature, `
    Test-LiveCoreImageAttestation
