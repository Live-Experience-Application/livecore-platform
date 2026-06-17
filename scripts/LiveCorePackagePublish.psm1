#requires -Version 5.1

<#
.SYNOPSIS
    Release-gated npm publish-plan logic for the four @livecore packages
    (CORE-PUB-002): builds the fail-closed publish plan for a release tag and
    decides whether a version is already published (immutability).

.DESCRIPTION
    The four @livecore packages are released together at one shared lockstep
    version (docs/23_PACKAGE_VERSIONING.md). This module is the pure logic the CI
    publish pipeline builds on:

      - Get-LiveCorePackagePublishPlan reuses the existing release-version gate
        (LiveCoreReleaseVersion.psm1, CORE-CMP-003) to assert the release tag's
        version equals the packages' shared version - fail-closed if they drift,
        if the four packages are out of lockstep, or for a non-release ref - and
        returns the ordered list of packages to publish at that version. So a
        mismatched or un-lockstepped version can never produce a publish plan.

      - Test-LiveCorePackageVersionPublished decides immutability: given the
        versions already on the registry for a package (supplied by a caller
        scriptblock so the decision is testable without a registry), it reports
        whether the target version already exists, so the CLI can refuse a
        republish rather than overwrite a shipped version.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.
#>

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
# Reuse the existing release-tag-vs-package-version gate (CORE-CMP-003) so the npm
# publish pipeline and the image publish gate agree on the one shared-version
# invariant and the fail-closed release-tag derivation (CORE-OPS-009).
Import-Module (Join-Path $scriptDir 'LiveCoreReleaseVersion.psm1') -Force

# The four published packages in DEPENDENCY order: a package is published before
# any package that depends on it (@livecore/sdk-ts depends on @livecore/contracts),
# so a consumer installing the dependent always finds its dependency on the
# registry. design-tokens and ui-core have no inter-package dependency.
$script:PublishOrder = @('contracts', 'design-tokens', 'ui-core', 'sdk-ts')

function Get-LiveCorePackagePublishPlan {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        # The git ref being published, e.g. 'refs/tags/v0.1.0'.
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Ref,

        # Repository root containing packages/<name>/package.json.
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    # Fail closed: a non-release ref or packages out of lockstep throw here
    # (reused CORE-CMP-003 / CORE-OPS-009 gate), so neither can produce a plan.
    $check = Test-LiveCoreReleaseTagVersion -Ref $Ref -RepositoryRoot $RepositoryRoot
    if (-not $check.IsMatch) {
        throw "Refusing to publish the packages: release tag version '$($check.ReleaseVersion)' does not match the four packages' shared version '$($check.PackageVersion)'. Cut the release tag from the same version the packages declare (docs/23_PACKAGE_VERSIONING.md, CORE-PUB-002)."
    }

    $version = $check.PackageVersion
    $plan = New-Object System.Collections.Generic.List[pscustomobject]
    foreach ($package in $script:PublishOrder) {
        $plan.Add([pscustomobject]@{
                Name      = "@livecore/$package"
                Directory = "packages/$package"
                Version   = $version
            })
    }
    return $plan.ToArray()
}

function Test-LiveCorePackageVersionPublished {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        # The package name, e.g. '@livecore/contracts'.
        [Parameter(Mandatory = $true)]
        [string]$Name,

        # The target version to be published, e.g. '0.1.0'.
        [Parameter(Mandatory = $true)]
        [string]$Version,

        # Returns the versions already published for $Name (string[]). A package
        # that has never been published returns an empty list. Injected so the
        # immutability decision is testable without a registry; the CLI supplies a
        # scriptblock that queries the registry with `npm view`.
        [Parameter(Mandatory = $true)]
        [scriptblock]$GetPublishedVersions
    )

    $published = @(& $GetPublishedVersions $Name)
    foreach ($existing in $published) {
        if ([string]$existing -ceq $Version) {
            return $true
        }
    }
    return $false
}

Export-ModuleMember -Function Get-LiveCorePackagePublishPlan, Test-LiveCorePackageVersionPublished
