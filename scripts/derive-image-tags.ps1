#requires -Version 5.1

<#
.SYNOPSIS
    CLI wrapper the CI publish job uses to derive the immutable, versioned API
    and worker image references for a release tag (CORE-OPS-009).

.DESCRIPTION
    Imports LiveCoreImageTags.psm1 and prints (and, when an output path is given,
    appends) 'api=<reference>' and 'worker=<reference>' lines. In GitHub Actions
    the output path is $GITHUB_OUTPUT, so a later step reads the references as
    step outputs. It fails closed (non-zero) for any non-release ref, so it can
    never emit a reference for a pull request or a moving tag.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -File scripts/derive-image-tags.ps1 -Ref refs/tags/v1.2.3 -Owner my-org
#>

[CmdletBinding()]
param(
    # The git ref to publish from, e.g. 'refs/tags/v1.2.3'.
    [Parameter(Mandatory = $true)]
    [string]$Ref,

    # The container registry host, e.g. 'ghcr.io'.
    [string]$Registry = 'ghcr.io',

    # The registry namespace / repository owner (lowercased by the module).
    [Parameter(Mandatory = $true)]
    [string]$Owner,

    # File the 'api='/'worker=' lines are appended to (defaults to
    # $GITHUB_OUTPUT under GitHub Actions). The lines are always written to
    # stdout regardless.
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreImageTags.psm1') -Force

$apiReference = Get-LiveCoreImageReference -Ref $Ref -Registry $Registry -Owner $Owner -Component 'api'
$workerReference = Get-LiveCoreImageReference -Ref $Ref -Registry $Registry -Owner $Owner -Component 'worker'

$lines = @("api=$apiReference", "worker=$workerReference")

if (-not $OutputPath -and $env:GITHUB_OUTPUT) {
    $OutputPath = $env:GITHUB_OUTPUT
}
if ($OutputPath) {
    Add-Content -Path $OutputPath -Value $lines
}

foreach ($line in $lines) {
    Write-Output $line
}
