#requires -Version 5.1

<#
.SYNOPSIS
    Changelog-completeness gate logic (CORE-REL-004): a change to a published
    package's typed surface must not ship undocumented.

.DESCRIPTION
    The four @livecore packages document every public-surface change in a
    Keep a Changelog file (docs/23_PACKAGE_VERSIONING.md). The 0.3.0 cut found that
    a package's src/ can change with no changelog entry, so the release-cut story had
    to reconstruct the changelog from the commit range. This module is the pure
    decision that prevents the gap: given the packages whose source changed since the
    last release tag and the relevant changelog texts, it reports which changed
    package (and the root summary) lacks an entry NEWER than that tag.

    "Newer than the last tag" means the changelog's topmost meaningful entry is
    either an `## [Unreleased]` section that carries at least one bullet, or a dated
    `## [X.Y.Z]` entry whose version is greater than the tag (a release already cut
    but not yet tagged). This keeps the gate correct across the whole release window:
    during development the Unreleased section documents the change; after a cut the
    dated entry above the tag documents it.

    Pure PowerShell with no git or file I/O, so it is unit-testable with seeded
    inputs (test-changelog-completeness.ps1). Compatible with Windows PowerShell 5.1
    and PowerShell 7+ (pwsh) on Linux.
#>

function Get-LiveCoreSemVerCore {
    [CmdletBinding()]
    [OutputType([int[]])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $core = (($Version -replace '^[vV]', '') -split '[-+]', 2)[0]
    $parts = @($core -split '\.')
    if ($parts.Count -lt 3) {
        throw "Not a MAJOR.MINOR.PATCH version: '$Version'."
    }

    $numbers = @()
    foreach ($part in $parts[0..2]) {
        $parsed = 0
        if (-not [int]::TryParse($part, [ref]$parsed)) {
            throw "Not a numeric version segment in '$Version'."
        }
        $numbers += $parsed
    }

    return , ([int[]]$numbers)
}

function Compare-LiveCoreSemVer {
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Left,

        [Parameter(Mandatory = $true)]
        [string]$Right
    )

    $leftCore = Get-LiveCoreSemVerCore -Version $Left
    $rightCore = Get-LiveCoreSemVerCore -Version $Right
    for ($i = 0; $i -lt 3; $i++) {
        if ($leftCore[$i] -lt $rightCore[$i]) { return -1 }
        if ($leftCore[$i] -gt $rightCore[$i]) { return 1 }
    }
    return 0
}

function Test-LiveCoreChangelogNewerThanTag {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$ChangelogContent,

        [Parameter(Mandatory = $true)]
        [string]$LastTagVersion
    )

    $lines = $ChangelogContent -split "`r`n|`n|`r"

    # Collect the version-entry headers ("## [X]" / "## [Unreleased]") in order,
    # each with the line index where its body begins.
    $entries = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $match = [regex]::Match($lines[$i], '^##\s*\[(?<label>[^\]]+)\]')
        if ($match.Success) {
            $entries.Add([pscustomobject]@{ Label = $match.Groups['label'].Value.Trim(); Start = $i })
        }
    }
    if ($entries.Count -eq 0) { return $false }

    for ($e = 0; $e -lt $entries.Count; $e++) {
        $label = $entries[$e].Label
        $bodyStart = $entries[$e].Start + 1
        if ($e + 1 -lt $entries.Count) { $bodyEnd = $entries[$e + 1].Start - 1 } else { $bodyEnd = $lines.Count - 1 }

        if ($label -match '^unreleased$') {
            # An Unreleased section documents the change only when it carries a bullet;
            # an empty Unreleased is treated as absent and we fall through to the next
            # (dated) entry.
            for ($b = $bodyStart; $b -le $bodyEnd; $b++) {
                if ($lines[$b] -match '^\s*-\s+\S') { return $true }
            }
            continue
        }

        # The first dated entry is newer iff its version is greater than the last tag.
        $version = (@($label -split '\s'))[0]
        try {
            return ((Compare-LiveCoreSemVer -Left $version -Right $LastTagVersion) -gt 0)
        }
        catch {
            return $false
        }
    }

    return $false
}

function Get-LiveCoreChangelogCompletenessReport {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$LastTagVersion,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ChangedPackages,

        [Parameter(Mandatory = $true)]
        [hashtable]$ChangelogContentByScope
    )

    $violations = New-Object System.Collections.Generic.List[string]

    # The root workspace summary must document the release too, whenever any package
    # surface changed since the tag.
    if ($ChangedPackages.Count -gt 0) {
        $rootContent = [string]$ChangelogContentByScope['root']
        if (-not (Test-LiveCoreChangelogNewerThanTag -ChangelogContent $rootContent -LastTagVersion $LastTagVersion)) {
            $violations.Add("CHANGELOG.md (root) has no entry newer than v$LastTagVersion, but a published package's source changed since that tag.")
        }
    }

    foreach ($package in $ChangedPackages) {
        $content = [string]$ChangelogContentByScope[$package]
        if (-not (Test-LiveCoreChangelogNewerThanTag -ChangelogContent $content -LastTagVersion $LastTagVersion)) {
            $violations.Add("packages/$package/CHANGELOG.md has no entry newer than v$LastTagVersion, but packages/$package/src changed since that tag.")
        }
    }

    return [pscustomobject]@{
        IsClean    = ($violations.Count -eq 0)
        Violations = $violations.ToArray()
    }
}

Export-ModuleMember -Function Get-LiveCoreSemVerCore, Compare-LiveCoreSemVer, Test-LiveCoreChangelogNewerThanTag, Get-LiveCoreChangelogCompletenessReport
