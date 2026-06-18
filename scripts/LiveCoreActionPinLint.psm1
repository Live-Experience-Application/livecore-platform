#requires -Version 5.1

<#
.SYNOPSIS
    Flags any GitHub Actions `uses:` reference that is not pinned to an immutable
    commit SHA (CORE-DEP-008).

.DESCRIPTION
    A workflow step that pins a third-party action by a MUTABLE tag
    (actions/checkout@v4) runs whatever commit that tag currently points at, so a
    compromised or retagged action would run inside this pipeline - including the
    publish job that holds the registry's packages: write token. The same
    digest-pinning discipline the Dockerfiles apply to their base images
    (name:tag@sha256:..., apps/api/Dockerfile) extends to the workflows: every
    `uses:` must reference a full 40-char commit SHA, with the readable version
    kept in a trailing comment so a human (and Dependabot) can still see what the
    pin resolves to.

    This module is the pure analysis behind the CI lint. It parses the `uses:`
    references out of a workflow file and classifies each one:

      - Local          a first-party in-repo action ('./...' or '../...'); has no
                       third-party ref to pin, so it is allowed.
      - Pinned         'owner/repo[/path]@<40-hex-sha>' WITH a trailing '# comment';
                       the required, immutable form.
      - MissingComment SHA-pinned but with no trailing comment - the pin is
                       immutable but unreadable, so the readable-version discipline
                       (and Dependabot's comment-rewrite) is lost. A violation.
      - Unpinned       the ref is a tag or branch (e.g. '@v4', '@v4.3.1', '@main')
                       or there is no ref at all - a mutable reference. A violation.
      - DockerDigest   'docker://image@sha256:<64-hex>'; a digest-pinned container
                       action, allowed.
      - DockerUnpinned 'docker://image:tag' with no digest - a mutable reference.
                       A violation.

    The lint FAILS CLOSED on Unpinned, MissingComment and DockerUnpinned, so a new
    floating-tag (or comment-less) `uses:` cannot merge.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

# The classifications that fail the lint.
$script:ActionPinViolationKind = @('Unpinned', 'MissingComment', 'DockerUnpinned')

function Get-LiveCoreActionReference {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        # A single line of a workflow file.
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Line
    )

    # Only a real YAML `uses:` step key counts: optional list marker, optional
    # indentation, then `uses:`. A `uses:` appearing inside a `run:` script body or
    # a comment ('# uses: ...') does not start at the key position, so it is ignored.
    $match = [regex]::Match($Line, '^\s*(?:-\s+)?uses:\s*(?<rest>\S.*?)\s*$')
    if (-not $match.Success) {
        return $null
    }

    $rest = $match.Groups['rest'].Value
    $reference = ''
    $comment = ''

    if ($rest.StartsWith('"') -or $rest.StartsWith("'")) {
        # Quoted scalar: the value is delimited by the matching quote; anything after
        # it (following a '#') is the trailing comment.
        $quote = $rest[0]
        $end = $rest.IndexOf($quote, 1)
        if ($end -lt 0) {
            $reference = $rest.Substring(1)
        }
        else {
            $reference = $rest.Substring(1, $end - 1)
            $after = $rest.Substring($end + 1)
            $commentMatch = [regex]::Match($after, '^\s*#\s*(?<comment>.*?)\s*$')
            if ($commentMatch.Success) {
                $comment = $commentMatch.Groups['comment'].Value
            }
        }
    }
    else {
        # Unquoted scalar: the action reference carries no whitespace, and YAML
        # requires a space before a '#' for it to begin a comment, so the value is
        # the first token and the optional comment follows ' #'.
        $valueMatch = [regex]::Match($rest, '^(?<value>\S+)(?:\s+#\s*(?<comment>.*?))?\s*$')
        if ($valueMatch.Success) {
            $reference = $valueMatch.Groups['value'].Value
            $comment = $valueMatch.Groups['comment'].Value
        }
        else {
            $reference = $rest
        }
    }

    return [pscustomobject]@{
        Reference = $reference
        Comment   = $comment.Trim()
    }
}

function Get-LiveCoreActionPinKind {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        # The action reference (the value of a `uses:` step), e.g.
        # 'actions/checkout@<sha>' or './.github/actions/foo'.
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Reference,

        # The trailing comment, if any (the readable version beside the SHA).
        [Parameter(Mandatory = $false)]
        [AllowEmptyString()]
        [string]$Comment = ''
    )

    if ([string]::IsNullOrWhiteSpace($Reference)) {
        return 'Unpinned'
    }

    # A first-party in-repo action has no third-party ref to pin.
    if ($Reference -match '^\.{1,2}/') {
        return 'Local'
    }

    # A container action must be pinned by its image digest, not a mutable tag.
    if ($Reference -match '^docker://') {
        if ($Reference -match '@sha256:[0-9a-fA-F]{64}$') {
            return 'DockerDigest'
        }
        return 'DockerUnpinned'
    }

    # An 'owner/repo[/path]@ref' action (or a reusable workflow): the ref after the
    # last '@' must be a full 40-char commit SHA. The path before it never contains
    # an '@', so the last '@' delimits the ref.
    $at = $Reference.LastIndexOf('@')
    if ($at -lt 0) {
        # No ref at all resolves to the action's default branch - mutable.
        return 'Unpinned'
    }

    $ref = $Reference.Substring($at + 1)
    if ($ref -match '^[0-9a-fA-F]{40}$') {
        if ([string]::IsNullOrWhiteSpace($Comment)) {
            return 'MissingComment'
        }
        return 'Pinned'
    }

    return 'Unpinned'
}

function Get-LiveCoreActionPinFinding {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        # The full text of a workflow file.
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Content
    )

    $findings = New-Object System.Collections.Generic.List[object]
    $lines = $Content -split "`r?`n"
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $parsed = Get-LiveCoreActionReference -Line $lines[$i]
        if ($null -eq $parsed) {
            continue
        }

        $kind = Get-LiveCoreActionPinKind -Reference $parsed.Reference -Comment $parsed.Comment
        $findings.Add([pscustomobject]@{
                LineNumber  = $i + 1
                Reference   = $parsed.Reference
                Comment     = $parsed.Comment
                Kind        = $kind
                IsViolation = ($script:ActionPinViolationKind -contains $kind)
            })
    }

    return , $findings.ToArray()
}

function Get-LiveCoreActionPinReview {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        # Directory holding the GitHub Actions workflow files.
        [Parameter(Mandatory = $true)]
        [string]$WorkflowDirectory
    )

    if (-not (Test-Path -Path $WorkflowDirectory)) {
        throw "Workflows directory not found: $WorkflowDirectory"
    }

    $files = @(
        Get-ChildItem -Path $WorkflowDirectory -File |
            Where-Object { $_.Extension -eq '.yml' -or $_.Extension -eq '.yaml' } |
            Sort-Object -Property Name
    )

    $findings = New-Object System.Collections.Generic.List[object]
    foreach ($file in $files) {
        $content = Get-Content -Path $file.FullName -Raw
        if ($null -eq $content) {
            $content = ''
        }
        foreach ($finding in (Get-LiveCoreActionPinFinding -Content $content)) {
            $findings.Add([pscustomobject]@{
                    File        = $file.Name
                    LineNumber  = $finding.LineNumber
                    Reference   = $finding.Reference
                    Comment     = $finding.Comment
                    Kind        = $finding.Kind
                    IsViolation = $finding.IsViolation
                })
        }
    }

    $violations = @($findings | Where-Object { $_.IsViolation })

    return [pscustomobject]@{
        Findings   = $findings.ToArray()
        Violations = $violations
        Total      = $findings.Count
        IsClean    = ($violations.Count -eq 0)
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreActionReference, `
    Get-LiveCoreActionPinKind, `
    Get-LiveCoreActionPinFinding, `
    Get-LiveCoreActionPinReview
