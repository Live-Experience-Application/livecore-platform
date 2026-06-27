#requires -Version 5.1

<#
.SYNOPSIS
    Validates the local-consume contract documentation (CORE-DXL-003).

.DESCRIPTION
    A downstream vertical that wants to run an UNRELEASED Core through its own
    end-to-end test harness - before any release is cut and WITHOUT a registry
    publish - pins exactly two coordinates: the three livecore-{api,worker,
    migrations}:local runtime image tags (built by `pnpm run images:local`,
    CORE-DXL-001) and the four dist/*.tgz @livecore package tarballs (packed by
    `pnpm run pack:local`, CORE-DXL-002). The contract carries one rule that keeps
    the consumer's pinned-version lockstep green: keep the package version number
    UNCHANGED in the inner loop and change only code; a real version bump is the
    normal release path (docs/23_PACKAGE_VERSIONING.md).

    This module is the pure docs-lint behind that contract's gate. It has no Docker
    and no build dependency, so it runs anywhere as a CI gate and locally on both
    Windows PowerShell 5.1 and PowerShell 7+ (pwsh). It asserts two things, scoped
    to the documentation section that carries each (so a future edit that drops half
    the contract fails the build rather than passing on text the next section still
    happens to contain):

      1. deploy/compose/README.md carries a 'local-consume contract' section that
         references BOTH the images:local and pack:local coordinates, names the
         three :local image tags and the four dist/*.tgz tarballs, states the
         keep-the-version-unchanged lockstep rule, calls out a real version bump as
         the normal release path, and points to docs/23_PACKAGE_VERSIONING.md
         (Test-LiveCoreLocalConsumeContractDocs); and
      2. docs/23_PACKAGE_VERSIONING.md carries a pointer section that references the
         images:local and pack:local coordinates and the keep-version lockstep rule,
         and links back to deploy/compose/README.md
         (Test-LiveCoreLocalConsumeVersioningPointer).

    Both assertions are documentation-only, so the gate is product-neutral: it never
    introduces a vertical term and the forbidden-core-terms boundary scan stays green.
#>

function Get-LiveCoreMarkdownSection {
    <#
    .SYNOPSIS
        Returns the body of the first level-2 (`## `) Markdown section whose heading
        matches a pattern, through (but excluding) the next level-2 heading.
    .DESCRIPTION
        Scoping a docs-lint to the section that should carry a fact - rather than the
        whole file - means a check proves that section EXISTS and is complete, instead
        of passing on a token an unrelated section happens to contain. Sub-headings
        (`### `) inside the section do not terminate it; only the next `## ` heading
        (or end of file) does.
    .OUTPUTS
        The section text (heading line included), or $null when no matching `## `
        heading is present.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory = $true)][string]$HeadingPattern
    )

    $lines = $Text -split "`r?`n"
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^##\s' -and $lines[$i] -match $HeadingPattern) {
            $start = $i
            break
        }
    }
    if ($start -lt 0) { return $null }

    $body = New-Object System.Collections.Generic.List[string]
    $body.Add($lines[$start])
    for ($j = $start + 1; $j -lt $lines.Count; $j++) {
        if ($lines[$j] -match '^##\s') { break }
        $body.Add($lines[$j])
    }
    return ($body -join "`n")
}

function Test-LiveCoreLocalConsumeContractDocs {
    <#
    .SYNOPSIS
        Asserts deploy/compose/README.md documents the local-consume contract (CORE-DXL-003).
    .DESCRIPTION
        The README must give a vertical the two coordinates it pins to run an
        unreleased Core locally - the images:local runtime image tags and the
        pack:local dist tarballs - plus the keep-the-version-unchanged rule that keeps
        its pinned-version lockstep green, with a real version bump called out as the
        normal release path, and a pointer to the versioning doc.
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]). Findings is
        empty exactly when every required element is present in the section.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$ReadmeText)

    $findings = New-Object System.Collections.Generic.List[string]

    $section = Get-LiveCoreMarkdownSection -Text $ReadmeText -HeadingPattern '(?i)local-consume contract'
    if ($null -eq $section) {
        $findings.Add("SECTION: deploy/compose/README.md must contain a '## ...local-consume contract...' section documenting the two coordinates a vertical pins to run an unreleased Core (CORE-DXL-003)")
        return [pscustomobject]@{ IsValid = $false; Findings = $findings.ToArray() }
    }

    # The two convenience scripts that produce the two coordinates.
    if ($section -notmatch 'images:local') {
        $findings.Add("DOCS: the local-consume contract section must reference the 'images:local' script (CORE-DXL-001 - the runtime image-tag coordinate)")
    }
    if ($section -notmatch 'pack:local') {
        $findings.Add("DOCS: the local-consume contract section must reference the 'pack:local' script (CORE-DXL-002 - the dist tarball coordinate)")
    }

    # Coordinate 1: the three :local runtime image tags.
    foreach ($tag in @('livecore-api:local', 'livecore-worker:local', 'livecore-migrations:local')) {
        if ($section -notmatch [regex]::Escape($tag)) {
            $findings.Add("DOCS: the local-consume contract section must name the runtime image tag '$tag' (the images:local coordinate)")
        }
    }

    # Coordinate 2: the four dist/*.tgz package tarballs.
    if ($section -notmatch [regex]::Escape('dist/')) {
        $findings.Add("DOCS: the local-consume contract section must name the dist/ package tarballs (the pack:local coordinate)")
    }
    if ($section -notmatch [regex]::Escape('.tgz')) {
        $findings.Add("DOCS: the local-consume contract section must name the .tgz package tarballs (the pack:local coordinate)")
    }

    # The keep-the-version-number-unchanged rule that keeps the consumer's lockstep green.
    if ($section -notmatch '(?i)lockstep') {
        $findings.Add("DOCS: the local-consume contract section must state the rule keeps the consumer's version 'lockstep' green (the keep-version rule)")
    }
    if (-not (($section -match '(?i)version') -and ($section -match '(?i)unchanged'))) {
        $findings.Add("DOCS: the local-consume contract section must state the keep-the-version-number-unchanged rule (the package version stays 'unchanged' in the inner loop)")
    }

    # A real version bump is the normal release path.
    if ($section -notmatch '(?i)release') {
        $findings.Add('DOCS: the local-consume contract section must call out a real version bump as the normal release path')
    }

    # The bidirectional pointer to the versioning doc.
    if ($section -notmatch [regex]::Escape('docs/23_PACKAGE_VERSIONING.md')) {
        $findings.Add('DOCS: the local-consume contract section must point to docs/23_PACKAGE_VERSIONING.md')
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

function Test-LiveCoreLocalConsumeVersioningPointer {
    <#
    .SYNOPSIS
        Asserts docs/23_PACKAGE_VERSIONING.md points at the local-consume contract (CORE-DXL-003).
    .DESCRIPTION
        The versioning doc must carry the pointer half of the contract: a section that
        names the images:local and pack:local coordinates, ties the inner loop to the
        keep-the-version-unchanged lockstep rule, and links back to the full contract
        in deploy/compose/README.md.
    .OUTPUTS
        A PSCustomObject with IsValid (bool) and Findings (string[]). Findings is
        empty exactly when every required element is present in the section.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$VersioningText)

    $findings = New-Object System.Collections.Generic.List[string]

    $section = Get-LiveCoreMarkdownSection -Text $VersioningText -HeadingPattern '(?i)unreleased Core locally'
    if ($null -eq $section) {
        $findings.Add("SECTION: docs/23_PACKAGE_VERSIONING.md must contain a '## ...unreleased Core locally...' pointer section for the local-consume contract (CORE-DXL-003)")
        return [pscustomobject]@{ IsValid = $false; Findings = $findings.ToArray() }
    }

    if ($section -notmatch 'images:local') {
        $findings.Add("DOCS: the versioning pointer must reference the 'images:local' local-consume coordinate (CORE-DXL-001)")
    }
    if ($section -notmatch 'pack:local') {
        $findings.Add("DOCS: the versioning pointer must reference the 'pack:local' local-consume coordinate (CORE-DXL-002)")
    }
    if ($section -notmatch '(?i)lockstep') {
        $findings.Add('DOCS: the versioning pointer must tie the local-consume loop to the version lockstep')
    }
    if (-not (($section -match '(?i)version') -and ($section -match '(?i)unchanged'))) {
        $findings.Add("DOCS: the versioning pointer must state the keep-the-version-number-unchanged rule (the package version stays 'unchanged' in the inner loop)")
    }
    if ($section -notmatch [regex]::Escape('deploy/compose/README.md')) {
        $findings.Add('DOCS: the versioning pointer must link back to deploy/compose/README.md (the full local-consume contract)')
    }

    return [pscustomobject]@{
        IsValid  = ($findings.Count -eq 0)
        Findings = $findings.ToArray()
    }
}

Export-ModuleMember -Function `
    Get-LiveCoreMarkdownSection, `
    Test-LiveCoreLocalConsumeContractDocs, `
    Test-LiveCoreLocalConsumeVersioningPointer
