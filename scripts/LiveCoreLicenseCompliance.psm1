#requires -Version 5.1

<#
.SYNOPSIS
    License-compliance and third-party attribution logic for the LiveCore
    distribution (CORE-LIC-003): generating the third-party NOTICE inventory, the
    NOTICE-coverage check over the shipping dependency set, and the SBOM
    license-policy gate.

.DESCRIPTION
    The Core is AGPL-3.0-or-later and redistributes attribution-requiring
    dependencies (AWSSDK.S3 under Apache-2.0, Npgsql, the OpenTelemetry.* and
    Microsoft.* packages, ...). AGPL section 7 and Apache section 4 require
    preserving their notices on redistribution, and a hosted distribution should
    fail a build that pulls in a disallowed or unknown license. This module holds
    the pure, deterministically-testable logic behind those controls so the CI
    gates and the seeded-fixture tests share one implementation
    (scripts/test-third-party-notices.ps1 / scripts/test-license-compliance.ps1):

      - the attribution source of truth (csv/third_party_notices.csv) is rendered
        into the committed THIRD-PARTY-NOTICES.md inventory deterministically, so
        the file is generated, never hand-maintained, and drift-gated;
      - the NOTICE-coverage check binds the inventory to reality: every direct,
        runtime-shipping NuGet PackageReference must have an attribution row, so a
        newly added attribution-requiring dependency cannot ship without a notice;
      - the SBOM license gate reads the per-image CycloneDX/SPDX SBOM (the same
        SBOM CORE-DEP-003 already produces) and is fail-closed: a denied license
        blocks, and any license not on the allow-list (including an absent or
        NOASSERTION license) is treated as unknown and blocks too.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

# The canonical upstream repository, used in the rendered NOTICE header. A fork
# can override it when rendering its own inventory.
$script:DefaultRepositoryUrl = 'https://github.com/Live-Experience-Application/livecore-platform'

function Get-LiveCoreComplianceJsonValue {
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

# ---------------------------------------------------------------------------
# Third-party NOTICE inventory generation
# ---------------------------------------------------------------------------

function Get-LiveCoreThirdPartyNotices {
    <#
    .SYNOPSIS
        Reads the curated attribution source of truth (csv/third_party_notices.csv)
        into a normalized, sorted list of notice rows.
    .OUTPUTS
        An array of PSCustomObjects with Ecosystem, Component, Version, License,
        Copyright, SourceUrl, Kind and Notes.
    #>
    [CmdletBinding()]
    [OutputType([psobject[]])]
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Third-party attribution file not found: $Path"
    }

    $rows = @(Import-Csv -LiteralPath $Path)
    $required = @('ecosystem', 'component', 'version', 'license', 'copyright', 'source_url', 'kind', 'notes')
    if ($rows.Count -gt 0) {
        $columns = @($rows[0].PSObject.Properties.Name)
        foreach ($column in $required) {
            if ($columns -notcontains $column) {
                throw "Attribution CSV $Path is missing the required '$column' column."
            }
        }
    }

    $notices = New-Object System.Collections.Generic.List[psobject]
    foreach ($row in $rows) {
        $component = [string]$row.component
        if ([string]::IsNullOrWhiteSpace($component)) { continue }
        $notices.Add([pscustomobject]@{
                Ecosystem = ([string]$row.ecosystem).Trim()
                Component = $component.Trim()
                Version   = ([string]$row.version).Trim()
                License   = ([string]$row.license).Trim()
                Copyright = ([string]$row.copyright).Trim()
                SourceUrl = ([string]$row.source_url).Trim()
                Kind      = ([string]$row.kind).Trim()
                Notes     = ([string]$row.notes).Trim()
            })
    }

    # Deterministic order: by ecosystem, then component (case-insensitive), so the
    # rendered NOTICE is stable regardless of CSV row order.
    return @($notices |
            Sort-Object -Property @{ Expression = { $_.Ecosystem } }, @{ Expression = { $_.Component.ToLowerInvariant() } })
}

function ConvertTo-LiveCoreThirdPartyNoticeContent {
    <#
    .SYNOPSIS
        Renders the third-party attribution inventory into deterministic Markdown.
    .DESCRIPTION
        The output is owned by this generator (it is listed in .prettierignore and
        the boundary scan treats it as license/attribution documentation), so the
        formatting is fixed here and drift-gated against the committed file. The
        line ending is LF, matching .gitattributes.
    .OUTPUTS
        The NOTICE file content as a single LF-joined string.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][psobject[]]$Notices,
        [string]$RepositoryUrl = $script:DefaultRepositoryUrl
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# Third-Party Notices')
    $lines.Add('')
    $lines.Add('The LiveCore Core Platform is licensed AGPL-3.0-or-later (see `LICENSE`). It')
    $lines.Add('redistributes third-party components whose licenses require their copyright and')
    $lines.Add('permission notices to be preserved. This inventory is **generated** from')
    $lines.Add('`csv/third_party_notices.csv` by `scripts/generate-third-party-notices.ps1` and')
    $lines.Add('is drift-gated in CI (CORE-LIC-003); do not edit it by hand. The authoritative,')
    $lines.Add('per-build component list for a published image is its CycloneDX SBOM')
    $lines.Add('(CORE-DEP-003), which the license-compliance gate scans.')
    $lines.Add('')
    $lines.Add("Upstream source: $RepositoryUrl")
    $lines.Add('')

    # Stable, human-readable section titles per ecosystem.
    $ecosystemTitles = [ordered]@{
        nuget = 'NuGet (.NET) runtime dependencies'
        oci   = 'Container base image'
        npm   = 'npm (TypeScript packages) runtime dependencies'
    }

    $byEcosystem = @{}
    foreach ($notice in $Notices) {
        $key = $notice.Ecosystem.ToLowerInvariant()
        if (-not $byEcosystem.ContainsKey($key)) {
            $byEcosystem[$key] = New-Object System.Collections.Generic.List[psobject]
        }
        $byEcosystem[$key].Add($notice)
    }

    # Render known ecosystems first in a fixed order, then any others sorted, so
    # the document order never depends on hashtable enumeration order.
    $orderedKeys = New-Object System.Collections.Generic.List[string]
    foreach ($key in $ecosystemTitles.Keys) {
        if ($byEcosystem.ContainsKey($key)) { $orderedKeys.Add($key) }
    }
    foreach ($key in @($byEcosystem.Keys | Sort-Object)) {
        if (-not $orderedKeys.Contains($key)) { $orderedKeys.Add($key) }
    }

    foreach ($key in $orderedKeys) {
        $title = if ($ecosystemTitles.Contains($key)) { $ecosystemTitles[$key] } else { $key }
        $lines.Add("## $title")
        $lines.Add('')
        foreach ($notice in $byEcosystem[$key]) {
            $heading = $notice.Component
            if (-not [string]::IsNullOrWhiteSpace($notice.Version) -and $notice.Version -ne '-') {
                $heading = "$heading $($notice.Version)"
            }
            $lines.Add("### $heading")
            $lines.Add('')
            $lines.Add("- License: $($notice.License)")
            if (-not [string]::IsNullOrWhiteSpace($notice.Copyright)) {
                $lines.Add("- Copyright: $($notice.Copyright)")
            }
            if (-not [string]::IsNullOrWhiteSpace($notice.SourceUrl)) {
                $lines.Add("- Source: $($notice.SourceUrl)")
            }
            if (-not [string]::IsNullOrWhiteSpace($notice.Notes)) {
                $lines.Add("- Notes: $($notice.Notes)")
            }
            $lines.Add('')
        }
    }

    $lines.Add('Full license texts for the SPDX identifiers above are available from the')
    $lines.Add('respective upstream sources and from https://spdx.org/licenses/.')

    # LF-joined with a trailing newline (matches .gitattributes LF normalization).
    return ($lines -join "`n") + "`n"
}

function Get-LiveCoreShippingPackageReferences {
    <#
    .SYNOPSIS
        Returns the direct, runtime-shipping NuGet PackageReference names from a
        .csproj.
    .DESCRIPTION
        A reference is "shipping" unless it sets <PrivateAssets>all</PrivateAssets>
        (a build/design-time-only dependency such as the EF Core design package,
        which never lands in the published runtime output and so carries no
        redistribution attribution obligation).
    .OUTPUTS
        A sorted, de-duplicated array of PackageReference Include names.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)][string]$CsprojPath)

    if (-not (Test-Path -LiteralPath $CsprojPath)) {
        throw "Project file not found: $CsprojPath"
    }

    [xml]$project = Get-Content -LiteralPath $CsprojPath -Raw
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($reference in $project.SelectNodes('//PackageReference')) {
        $include = [string]$reference.GetAttribute('Include')
        if ([string]::IsNullOrWhiteSpace($include)) { continue }

        $privateAssets = [string]$reference.GetAttribute('PrivateAssets')
        if ([string]::IsNullOrWhiteSpace($privateAssets)) {
            $node = $reference.SelectSingleNode('PrivateAssets')
            if ($node) { $privateAssets = [string]$node.InnerText }
        }
        if ($privateAssets.Trim().ToLowerInvariant() -eq 'all') { continue }

        $names.Add($include.Trim())
    }

    return @($names | Sort-Object -Unique)
}

function Test-LiveCoreNoticeCoverage {
    <#
    .SYNOPSIS
        Decides whether every shipping dependency name is attributed in the NOTICE
        inventory.
    .OUTPUTS
        A PSCustomObject with Covered (bool) and Missing (string[]) - the shipping
        reference names that have no attribution row.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ShippingReferenceName,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][psobject[]]$Notices
    )

    $attributed = @{}
    foreach ($notice in $Notices) {
        $attributed[$notice.Component.ToLowerInvariant()] = $true
    }

    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($name in $ShippingReferenceName) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if (-not $attributed.ContainsKey($name.ToLowerInvariant())) {
            $missing.Add($name)
        }
    }

    return [pscustomobject]@{
        Covered = ($missing.Count -eq 0)
        Missing = @($missing | Sort-Object -Unique)
    }
}

# ---------------------------------------------------------------------------
# SBOM license-policy gate
# ---------------------------------------------------------------------------

# A conservative default allow-list of SPDX license identifiers that are
# compatible with redistributing an AGPL-3.0-or-later work. Permissive licenses,
# the weak-copyleft licenses common in the .NET/Debian closure, and the project's
# own AGPL family. The gate is fail-closed: anything NOT on this list (including a
# missing or NOASSERTION license) is treated as unknown and blocks, so the
# operator must consciously widen the list for a genuinely new license.
$script:DefaultAllowedLicenses = @(
    '0BSD', 'AGPL-3.0-only', 'AGPL-3.0-or-later', 'Apache-2.0',
    'BSD-2-Clause', 'BSD-3-Clause', 'BSL-1.0', 'CC0-1.0', 'CC-BY-4.0',
    'GPL-2.0-only', 'GPL-2.0-or-later', 'GPL-3.0-only', 'GPL-3.0-or-later',
    'ISC', 'LGPL-2.1-only', 'LGPL-2.1-or-later', 'LGPL-3.0-only', 'LGPL-3.0-or-later',
    'MIT', 'MIT-0', 'MPL-2.0', 'MS-PL', 'PostgreSQL', 'Python-2.0',
    'Unicode-DFS-2016', 'Unlicense', 'Zlib'
)

# Licenses explicitly forbidden in the distribution regardless of the allow-list
# (a deny always wins). Empty by default; an operator adds known-incompatible or
# proprietary identifiers here.
$script:DefaultDeniedLicenses = @()

function Get-LiveCoreSbomLicenseModel {
    <#
    .SYNOPSIS
        Parses an SBOM (CycloneDX or SPDX JSON, from a path or literal content)
        into a per-component license model.
    .OUTPUTS
        A PSCustomObject with Format ('CycloneDX'/'SPDX'/'Unknown') and Components,
        an array of { Name, Version, Licenses (string[]) }.
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
        if (-not (Test-Path -LiteralPath $Path)) { throw "SBOM not found: $Path" }
        $text = [System.IO.File]::ReadAllText($Path)
    }
    else {
        $text = $Content
    }

    if ([string]::IsNullOrWhiteSpace($text)) { throw 'Empty document: expected SBOM JSON content.' }
    try { $document = $text | ConvertFrom-Json }
    catch { throw "Malformed SBOM JSON: $($_.Exception.Message)" }

    $components = New-Object System.Collections.Generic.List[psobject]
    $bomFormat = [string](Get-LiveCoreComplianceJsonValue -InputObject $document -Name 'bomFormat')
    $spdxVersion = [string](Get-LiveCoreComplianceJsonValue -InputObject $document -Name 'spdxVersion')

    if ($bomFormat -eq 'CycloneDX') {
        foreach ($component in @(Get-LiveCoreComplianceJsonValue -InputObject $document -Name 'components')) {
            if ($null -eq $component) { continue }
            $licenses = New-Object System.Collections.Generic.List[string]
            foreach ($entry in @(Get-LiveCoreComplianceJsonValue -InputObject $component -Name 'licenses')) {
                if ($null -eq $entry) { continue }
                $expression = [string](Get-LiveCoreComplianceJsonValue -InputObject $entry -Name 'expression')
                if (-not [string]::IsNullOrWhiteSpace($expression)) { $licenses.Add($expression); continue }
                $license = Get-LiveCoreComplianceJsonValue -InputObject $entry -Name 'license'
                if ($null -ne $license) {
                    $id = [string](Get-LiveCoreComplianceJsonValue -InputObject $license -Name 'id')
                    $name = [string](Get-LiveCoreComplianceJsonValue -InputObject $license -Name 'name')
                    if (-not [string]::IsNullOrWhiteSpace($id)) { $licenses.Add($id) }
                    elseif (-not [string]::IsNullOrWhiteSpace($name)) { $licenses.Add($name) }
                }
            }
            $components.Add([pscustomobject]@{
                    Name     = [string](Get-LiveCoreComplianceJsonValue -InputObject $component -Name 'name')
                    Version  = [string](Get-LiveCoreComplianceJsonValue -InputObject $component -Name 'version')
                    Licenses = $licenses.ToArray()
                })
        }
        return [pscustomobject]@{ Format = 'CycloneDX'; Components = $components.ToArray() }
    }

    if (-not [string]::IsNullOrWhiteSpace($spdxVersion)) {
        foreach ($package in @(Get-LiveCoreComplianceJsonValue -InputObject $document -Name 'packages')) {
            if ($null -eq $package) { continue }
            $licenses = New-Object System.Collections.Generic.List[string]
            foreach ($field in @('licenseConcluded', 'licenseDeclared')) {
                $value = [string](Get-LiveCoreComplianceJsonValue -InputObject $package -Name $field)
                if (-not [string]::IsNullOrWhiteSpace($value)) { $licenses.Add($value) }
            }
            $components.Add([pscustomobject]@{
                    Name     = [string](Get-LiveCoreComplianceJsonValue -InputObject $package -Name 'name')
                    Version  = [string](Get-LiveCoreComplianceJsonValue -InputObject $package -Name 'versionInfo')
                    Licenses = $licenses.ToArray()
                })
        }
        return [pscustomobject]@{ Format = 'SPDX'; Components = $components.ToArray() }
    }

    return [pscustomobject]@{ Format = 'Unknown'; Components = @() }
}

function Get-LiveCoreLicenseTokens {
    # Splits an SPDX license expression into its individual license tokens, so a
    # compound expression ('MIT OR Apache-2.0', '(MIT AND BSD-3-Clause)') is
    # evaluated per operand. Operators and parentheses are dropped; the remaining
    # tokens are returned trimmed.
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Expression)

    if ([string]::IsNullOrWhiteSpace($Expression)) { return @() }
    $normalized = $Expression -replace '[()]', ' '
    $tokens = $normalized -split '(?i)\s+(?:OR|AND|WITH)\s+'
    return @($tokens | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
}

function Test-LiveCoreLicenseExpression {
    <#
    .SYNOPSIS
        Classifies one SBOM license value (a single id, a name, or an SPDX
        expression) against the allow/deny policy.
    .OUTPUTS
        'allow', 'deny' or 'unknown'. A deny token always wins. An OR-expression
        is allowed when any operand is allowed and none is denied; otherwise every
        operand must be allowed. Anything not on the allow-list is 'unknown'.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Expression,
        [Parameter(Mandatory = $true)][hashtable]$AllowSet,
        [Parameter(Mandatory = $true)][hashtable]$DenySet
    )

    $tokens = Get-LiveCoreLicenseTokens -Expression $Expression
    if ($tokens.Count -eq 0) { return 'unknown' }

    $isOr = ($Expression -match '(?i)\bOR\b')
    $anyAllowed = $false
    $allAllowed = $true
    foreach ($token in $tokens) {
        $key = $token.ToUpperInvariant()
        if ($DenySet.ContainsKey($key)) { return 'deny' }
        if ($AllowSet.ContainsKey($key)) { $anyAllowed = $true } else { $allAllowed = $false }
    }

    if ($isOr) { if ($anyAllowed) { return 'allow' } else { return 'unknown' } }
    if ($allAllowed) { return 'allow' }
    return 'unknown'
}

function Test-LiveCoreLicenseGate {
    <#
    .SYNOPSIS
        Decides whether an SBOM license model passes the distribution license
        policy.
    .DESCRIPTION
        Fail-closed: a component blocks when any of its licenses is denied, or when
        none of its licenses is on the allow-list (an unknown, absent or
        NOASSERTION license). The policy (allow-list/deny-list) is configurable.
    .OUTPUTS
        A PSCustomObject with Passed (bool), Violations (component/license/reason)
        and Counts (a hashtable of verdict -> count).
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory = $true)][psobject]$Model,
        [string[]]$AllowLicense = $script:DefaultAllowedLicenses,
        [string[]]$DenyLicense = $script:DefaultDeniedLicenses
    )

    $allowSet = @{}
    foreach ($license in $AllowLicense) { if ($license) { $allowSet[$license.ToUpperInvariant()] = $true } }
    $denySet = @{}
    foreach ($license in $DenyLicense) { if ($license) { $denySet[$license.ToUpperInvariant()] = $true } }

    $counts = [ordered]@{ allow = 0; deny = 0; unknown = 0 }
    $violations = New-Object System.Collections.Generic.List[psobject]

    foreach ($component in @($Model.Components)) {
        if ($null -eq $component) { continue }
        $licenses = @($component.Licenses | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

        if ($licenses.Count -eq 0) {
            $counts['unknown']++
            $violations.Add([pscustomobject]@{
                    Component = $component.Name
                    Version   = $component.Version
                    License   = '(none)'
                    Reason    = 'unknown'
                })
            continue
        }

        # A component may carry several license records; it passes only if every
        # record is allowed. A denied record blocks, an unknown record blocks.
        $componentVerdict = 'allow'
        $offending = $null
        foreach ($license in $licenses) {
            $verdict = Test-LiveCoreLicenseExpression -Expression $license -AllowSet $allowSet -DenySet $denySet
            if ($verdict -eq 'deny') { $componentVerdict = 'deny'; $offending = $license; break }
            if ($verdict -eq 'unknown' -and $componentVerdict -ne 'deny') { $componentVerdict = 'unknown'; $offending = $license }
        }

        $counts[$componentVerdict]++
        if ($componentVerdict -ne 'allow') {
            $violations.Add([pscustomobject]@{
                    Component = $component.Name
                    Version   = $component.Version
                    License   = $offending
                    Reason    = $componentVerdict
                })
        }
    }

    return [pscustomobject]@{
        Passed     = ($violations.Count -eq 0)
        Violations = $violations.ToArray()
        Counts     = $counts
    }
}

function Get-LiveCoreDefaultAllowedLicenses { return @($script:DefaultAllowedLicenses) }
function Get-LiveCoreDefaultRepositoryUrl { return $script:DefaultRepositoryUrl }

Export-ModuleMember -Function `
    Get-LiveCoreThirdPartyNotices, `
    ConvertTo-LiveCoreThirdPartyNoticeContent, `
    Get-LiveCoreShippingPackageReferences, `
    Test-LiveCoreNoticeCoverage, `
    Get-LiveCoreSbomLicenseModel, `
    Get-LiveCoreLicenseTokens, `
    Test-LiveCoreLicenseExpression, `
    Test-LiveCoreLicenseGate, `
    Get-LiveCoreDefaultAllowedLicenses, `
    Get-LiveCoreDefaultRepositoryUrl
