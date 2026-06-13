#requires -Version 5.1

<#
.SYNOPSIS
    Derives the immutable, versioned container image references the CI publish
    job pushes on a tagged release (CORE-OPS-009).

.DESCRIPTION
    The Core API and worker images are published only from a release tag of the
    form 'refs/tags/v<MAJOR>.<MINOR>.<PATCH>' (an optional SemVer prerelease is
    allowed). Derivation is fail-closed: any other git ref - a branch push, a
    pull request, a moving tag such as 'latest', an incomplete or malformed
    version, or a tag carrying build metadata - is rejected, so a mutable or
    unversioned tag can never be produced and a pull request can never publish.

    The produced reference pins the exact release version as the image tag
    (for example 'ghcr.io/<owner>/livecore-api:1.2.3'); it never emits a moving
    tag such as 'latest'. The owner segment is lowercased because container
    repository paths are lowercase.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

# A release tag: 'v' followed by a SemVer version with no build metadata. Build
# metadata uses '+', which is not a legal container image tag character, so a
# tag carrying it could not map to an immutable, valid image tag and is refused.
# The 'version' group is the SemVer value used verbatim as the image tag.
$script:ReleaseTagPattern = '^v(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?)$'

# A legal, non-moving container image tag (charset and length per the OCI/Docker
# tag grammar). The release version always satisfies this; the check is defence
# in depth so a future regression cannot publish an invalid or moving tag.
$script:ImageTagPattern = '^[0-9A-Za-z][0-9A-Za-z._-]{0,127}$'

function Get-LiveCoreReleaseVersion {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Ref
    )

    $prefix = 'refs/tags/'
    if ([string]::IsNullOrWhiteSpace($Ref) -or -not $Ref.StartsWith($prefix)) {
        throw "Refusing to derive an image tag from ref '$Ref': only a release tag ('refs/tags/v<MAJOR>.<MINOR>.<PATCH>') publishes an image, so a branch or pull-request ref never produces one (CORE-OPS-009)."
    }

    $tag = $Ref.Substring($prefix.Length)
    $match = [regex]::Match($tag, $script:ReleaseTagPattern)
    if (-not $match.Success) {
        throw "Refusing to derive an image tag from tag '$tag': a release tag must be an immutable, versioned 'v<MAJOR>.<MINOR>.<PATCH>' SemVer tag (optionally a prerelease, never build metadata), so a moving or malformed tag never produces a published image (CORE-OPS-009)."
    }

    return $match.Groups['version'].Value
}

function Get-LiveCoreImageReference {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Ref,

        [Parameter(Mandatory = $true)]
        [string]$Registry,

        [Parameter(Mandatory = $true)]
        [string]$Owner,

        [Parameter(Mandatory = $true)]
        [ValidateSet('api', 'worker')]
        [string]$Component
    )

    $version = Get-LiveCoreReleaseVersion -Ref $Ref

    # Defence in depth: the published tag must be the exact release version and
    # must never be a moving tag. These invariants always hold for a value that
    # passed Get-LiveCoreReleaseVersion; the checks document them and guard
    # against a future change silently publishing a mutable or invalid tag.
    if ($version -ceq 'latest' -or $version -notmatch $script:ImageTagPattern) {
        throw "Refusing to publish a non-immutable or invalid image tag '$version' (CORE-OPS-009)."
    }

    $normalizedOwner = $Owner.ToLowerInvariant()
    return "$Registry/$normalizedOwner/livecore-${Component}:$version"
}

Export-ModuleMember -Function Get-LiveCoreReleaseVersion, Get-LiveCoreImageReference
