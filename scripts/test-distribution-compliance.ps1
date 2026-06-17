#requires -Version 5.1

<#
.SYNOPSIS
    Tests that the LiveCore distribution is legally complete (CORE-LIC-003): the
    third-party NOTICE inventory exists and is generated/included, each published
    package ships the AGPL LICENSE and the NOTICE, the runtime images carry the OCI
    license labels and copy the NOTICE in, and the NOTICE-coverage check binds the
    inventory to the shipping dependency set.

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreLicenseCompliance.psm1,
    generate-third-party-notices.ps1 and the committed distribution files - no
    external test framework and no Docker, so it runs as a CI gate and locally on
    pwsh and Windows PowerShell 5.1. Exits 0 when every assertion holds and 1 when
    any fails.

    Required cases covered:
      - the NOTICE exists at the repo root and in every package, and is in each
        package's files[] so it is included in the tarball;
      - each package tarball ships the AGPL LICENSE (LICENSE present and listed in
        files[], byte-identical to the root LICENSE) - with CORE-PUB-001;
      - the API and worker Dockerfiles carry the OCI image.licenses (+ source,
        revision) labels and COPY the LICENSE/NOTICE into the image;
      - a shipping dependency with no attribution row fails NOTICE coverage
        (seeded), and a build/design-only PrivateAssets=all reference is excluded.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-distribution-compliance.ps1
#>

[CmdletBinding()]
param([string]$RepoRoot)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $scriptDir }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

Import-Module (Join-Path $scriptDir 'LiveCoreLicenseCompliance.psm1') -Force

$failures = New-Object System.Collections.Generic.List[string]
function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) { Write-Host "PASS: $Because" }
    else { $failures.Add("FAIL: $Because") }
}

function Read-Lf { param([string]$Path) if (-not (Test-Path -LiteralPath $Path)) { return $null } return ([System.IO.File]::ReadAllText($Path) -replace "`r`n", "`n") }

$packages = @('contracts', 'sdk-ts', 'design-tokens', 'ui-core')
$rootLicense = Read-Lf -Path (Join-Path $RepoRoot 'LICENSE')
$rootNotice = Read-Lf -Path (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md')

# --- The NOTICE exists at the root and is up to date with the source of truth. ---
AssertTrue ($null -ne $rootNotice) 'THIRD-PARTY-NOTICES.md exists at the repository root'
$notices = Get-LiveCoreThirdPartyNotices -Path (Join-Path $RepoRoot 'csv/third_party_notices.csv')
$rendered = (ConvertTo-LiveCoreThirdPartyNoticeContent -Notices $notices) -replace "`r`n", "`n"
AssertTrue ($rootNotice -eq $rendered) 'the committed THIRD-PARTY-NOTICES.md matches what the generator renders (drift-gated)'
AssertTrue ($rootNotice -match 'AGPL-3.0-or-later') 'the NOTICE records the Core AGPL license'
AssertTrue ($rootNotice -match 'AWSSDK\.S3' -and $rootNotice -match 'Apache-2\.0') 'the NOTICE attributes an Apache-2.0 dependency (AWSSDK.S3)'

# --- Generation is deterministic. ---
$renderedAgain = (ConvertTo-LiveCoreThirdPartyNoticeContent -Notices $notices) -replace "`r`n", "`n"
AssertTrue ($rendered -eq $renderedAgain) 'rendering the inventory twice is byte-identical (deterministic)'

# --- Each package ships the AGPL LICENSE and the NOTICE, and lists both in files[]. ---
foreach ($package in $packages) {
    $dir = Join-Path $RepoRoot "packages/$package"
    $license = Read-Lf -Path (Join-Path $dir 'LICENSE')
    $notice = Read-Lf -Path (Join-Path $dir 'THIRD-PARTY-NOTICES.md')
    AssertTrue ($null -ne $license) "packages/$package ships a LICENSE file"
    AssertTrue ($license -eq $rootLicense) "packages/$package/LICENSE is byte-identical to the root AGPL LICENSE"
    AssertTrue ($null -ne $notice) "packages/$package ships a THIRD-PARTY-NOTICES.md"
    AssertTrue ($notice -eq $rootNotice) "packages/$package/THIRD-PARTY-NOTICES.md matches the root inventory"

    $manifest = Get-Content -LiteralPath (Join-Path $dir 'package.json') -Raw | ConvertFrom-Json
    $files = @($manifest.files)
    AssertTrue ($files -contains 'LICENSE') "packages/$package package.json files[] includes LICENSE (ships in the tarball)"
    AssertTrue ($files -contains 'THIRD-PARTY-NOTICES.md') "packages/$package package.json files[] includes THIRD-PARTY-NOTICES.md"
}

# --- The runtime images carry the OCI license labels and copy the NOTICE/LICENSE in. ---
foreach ($dockerfile in @('apps/api/Dockerfile', 'apps/worker/Dockerfile')) {
    $content = Get-Content -LiteralPath (Join-Path $RepoRoot $dockerfile) -Raw
    AssertTrue ($content -match 'org\.opencontainers\.image\.licenses\s*=\s*"AGPL-3\.0-or-later"') "$dockerfile carries the OCI image.licenses=AGPL-3.0-or-later label"
    AssertTrue ($content -match 'org\.opencontainers\.image\.source\s*=') "$dockerfile carries the OCI image.source label"
    AssertTrue ($content -match 'org\.opencontainers\.image\.revision\s*=') "$dockerfile carries the OCI image.revision label"
    AssertTrue ($content -match 'COPY\s+LICENSE\s+THIRD-PARTY-NOTICES\.md') "$dockerfile copies the LICENSE and NOTICE into the image"
}

# --- .dockerignore re-includes the LICENSE and NOTICE so the COPY can see them. ---
$dockerignore = Get-Content -LiteralPath (Join-Path $RepoRoot '.dockerignore')
AssertTrue ($dockerignore -contains '!LICENSE') '.dockerignore re-includes LICENSE for the image COPY'
AssertTrue ($dockerignore -contains '!THIRD-PARTY-NOTICES.md') '.dockerignore re-includes THIRD-PARTY-NOTICES.md for the image COPY'

# --- NOTICE coverage: every shipping dependency must be attributed; a build/
#     design-only PrivateAssets=all reference is excluded. ---
$apiRefs = Get-LiveCoreShippingPackageReferences -CsprojPath (Join-Path $RepoRoot 'apps/api/LiveCore.Api.csproj')
AssertTrue ($apiRefs -contains 'AWSSDK.S3') 'the API shipping references include AWSSDK.S3'
AssertTrue (-not ($apiRefs -contains 'Microsoft.EntityFrameworkCore.Design')) 'a PrivateAssets=all (design-only) reference is excluded from the shipping set'
$realCoverage = Test-LiveCoreNoticeCoverage -ShippingReferenceName $apiRefs -Notices $notices
AssertTrue ($realCoverage.Covered) 'every real shipping NuGet dependency has an attribution row'

# A seeded shipping dependency with no attribution row fails coverage.
$seededCoverage = Test-LiveCoreNoticeCoverage -ShippingReferenceName @('AWSSDK.S3', 'Totally.Unattributed.Package') -Notices $notices
AssertTrue (-not $seededCoverage.Covered) 'a shipping dependency with no NOTICE row fails coverage (seeded)'
AssertTrue ($seededCoverage.Missing -contains 'Totally.Unattributed.Package') 'the uncovered dependency is reported by name'

# --- The PrivateAssets=all exclusion holds on a throwaway fixture project too. ---
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-dist-" + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $tempDir
try {
    $fixtureCsproj = Join-Path $tempDir 'Fixture.csproj'
    Set-Content -LiteralPath $fixtureCsproj -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Shipping.Package" Version="1.0.0" />
    <PackageReference Include="Design.Only.Package" Version="1.0.0">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
'@
    $fixtureRefs = Get-LiveCoreShippingPackageReferences -CsprojPath $fixtureCsproj
    AssertTrue ($fixtureRefs -contains 'Shipping.Package') 'a normal PackageReference is treated as shipping'
    AssertTrue (-not ($fixtureRefs -contains 'Design.Only.Package')) 'a PrivateAssets=all PackageReference is excluded'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- End to end: the committed-tree drift gate passes. ---
$psExe = (Get-Process -Id $PID).Path
& $psExe -NoProfile -File (Join-Path $scriptDir 'generate-third-party-notices.ps1') -RepoRoot $RepoRoot *> $null
AssertTrue ($LASTEXITCODE -eq 0) 'generate-third-party-notices.ps1 (check mode) reports the committed tree is up to date'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Distribution-compliance tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host $failure }
    exit 1
}

Write-Host ''
Write-Host 'Distribution-compliance tests passed: the NOTICE ships in the images and every package, each package ships the AGPL LICENSE, the images carry the OCI license labels and NOTICE coverage binds the inventory to the shipping dependencies.' -ForegroundColor Green
exit 0
