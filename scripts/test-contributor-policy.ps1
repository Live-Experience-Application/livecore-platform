#requires -Version 5.1

<#
.SYNOPSIS
    Tests the contributor IP policy gates (CORE-LIC-004).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreContributorPolicy.psm1 - no external
    test framework, so it runs as a CI gate and locally on both pwsh and Windows
    PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

    It proves the required behavior from both directions (the CORE-LIC-004
    acceptance test - "CI fails a commit without the required sign-off and a
    shipped source file missing the SPDX header; passes when both are present"):

      SPDX header
        - a file carrying the header passes; one without it fails;
        - a file with a different license (MIT) fails;
        - the inserter produces a header the detector accepts, preserving the body
          and the line ending;
        - the scope predicate excludes generated source (EF migrations, the
          generated OpenAPI types) and non-source files, and includes shipped .cs/.ts.

      DCO sign-off
        - a commit whose Signed-off-by email matches the author passes;
        - a commit with no sign-off, or a sign-off with a different email, fails;
        - merge commits are skipped, and the report reconciles a mixed set.

    Then it guards the real repository: every in-scope source file carries the
    header, so a new headerless shipped file fails this test too.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-contributor-policy.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreContributorPolicy.psm1') -Force
$repoRoot = Split-Path -Parent $scriptDir

$failures = New-Object System.Collections.Generic.List[string]

function AssertTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because")
    }
}

function AssertEqual {
    param([string]$Expected, [string]$Actual, [string]$Because)
    if ($Expected -ceq $Actual) {
        Write-Host "PASS: $Because"
    }
    else {
        $failures.Add("FAIL: $Because`n      expected: '$Expected'`n      actual:   '$Actual'")
    }
}

# --- SPDX header: detection over fixtures. ---
$headeredCs = @"
// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Example;

public sealed class Example;
"@
AssertTrue (Test-LiveCoreLicenseHeaderPresent -Content $headeredCs) `
    'a source file carrying the SPDX header passes detection'

$bareCs = @"
namespace LiveCore.Api.Example;

public sealed class Example;
"@
AssertTrue (-not (Test-LiveCoreLicenseHeaderPresent -Content $bareCs)) `
    'a source file with no header fails detection (a missing header fails the lint)'

$wrongLicense = @"
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Example;
"@
AssertTrue (-not (Test-LiveCoreLicenseHeaderPresent -Content $wrongLicense)) `
    'a source file declaring a different license (MIT) fails detection'

$spdxOnly = @"
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace LiveCore.Api.Example;
"@
AssertTrue (-not (Test-LiveCoreLicenseHeaderPresent -Content $spdxOnly)) `
    'the SPDX line without a copyright line fails detection'

AssertTrue (-not (Test-LiveCoreLicenseHeaderPresent -Content '')) `
    'empty content fails detection'

# --- SPDX header: the inserter round-trips and preserves the body + line ending. ---
$lfBody = "namespace LiveCore.Api.Example;`n`npublic sealed class Example;`n"
$lfFixed = Get-LiveCoreContentWithLicenseHeader -Content $lfBody -Year 2026
AssertTrue (Test-LiveCoreLicenseHeaderPresent -Content $lfFixed) `
    'the inserter produces a header the detector accepts'
AssertTrue ($lfFixed.EndsWith($lfBody)) `
    'the inserter preserves the original body verbatim after the header'
AssertTrue (-not ($lfFixed -match "`r`n")) `
    'the inserter keeps an LF body LF (no CRLF introduced)'

$crlfBody = "namespace LiveCore.Api.Example;`r`n`r`npublic sealed class Example;`r`n"
$crlfFixed = Get-LiveCoreContentWithLicenseHeader -Content $crlfBody -Year 2026
AssertTrue (Test-LiveCoreLicenseHeaderPresent -Content $crlfFixed) `
    'the inserter produces an accepted header for a CRLF file'
AssertTrue ($crlfFixed.StartsWith("// SPDX-License-Identifier: AGPL-3.0-or-later`r`n")) `
    'the inserter matches the body line ending (CRLF) on the header'

# Inserting twice is not attempted by the lint, but the canonical header lines are stable.
$lines = Get-LiveCoreLicenseHeaderLine -Year 2026
AssertEqual '// SPDX-License-Identifier: AGPL-3.0-or-later' $lines[0] 'the canonical first header line is the SPDX identifier'
AssertEqual '// Copyright (c) 2026 The LiveCore Platform contributors' $lines[1] 'the canonical second header line is the copyright'

# --- SPDX header: the scope predicate. ---
AssertTrue (Test-LiveCoreSourceFileInScope -Path 'apps/api/Program.cs') 'a shipped C# file is in scope'
AssertTrue (Test-LiveCoreSourceFileInScope -Path 'packages/contracts/src/identity.ts') 'a shipped TypeScript file is in scope'
AssertTrue (Test-LiveCoreSourceFileInScope -Path 'tests/LiveCore.SmokeTests/HealthTests.cs') 'a first-party test C# file is in scope'
AssertTrue (-not (Test-LiveCoreSourceFileInScope -Path 'apps/api/Persistence/Migrations/20260101_Init.cs')) 'an EF migration (scaffolded) is out of scope'
AssertTrue (-not (Test-LiveCoreSourceFileInScope -Path 'packages/contracts/src/openapi.ts')) 'the generated OpenAPI types file is out of scope'
AssertTrue (-not (Test-LiveCoreSourceFileInScope -Path 'packages/contracts/dist/index.d.ts')) 'a declaration/build-output file is out of scope'
AssertTrue (-not (Test-LiveCoreSourceFileInScope -Path 'scripts/boundary-scan.ps1')) 'a PowerShell tooling script is out of the shipped-source scope'
AssertTrue (-not (Test-LiveCoreSourceFileInScope -Path 'docs/16_LICENSING.md')) 'documentation is out of scope'
AssertTrue (-not (Test-LiveCoreSourceFileInScope -Path 'apps/api/obj/Debug/Foo.g.cs')) 'generated build output under obj/ is out of scope'

# --- DCO sign-off: detection over fixtures. ---
$author = 'dev@example.com'
$signed = @"
CORE-XXX-001: do a thing

A longer body explaining the change.

Signed-off-by: A Developer <dev@example.com>
"@
AssertTrue (Test-LiveCoreCommitSignOff -AuthorEmail $author -Message $signed) `
    'a commit whose Signed-off-by email matches the author passes'

$unsigned = @"
CORE-XXX-002: do another thing

No trailer here.
"@
AssertTrue (-not (Test-LiveCoreCommitSignOff -AuthorEmail $author -Message $unsigned)) `
    'a commit with no sign-off fails (a commit without the required sign-off fails the lint)'

$mismatched = @"
CORE-XXX-003: do a third thing

Signed-off-by: Someone Else <other@example.com>
"@
AssertTrue (-not (Test-LiveCoreCommitSignOff -AuthorEmail $author -Message $mismatched)) `
    'a sign-off whose email does not match the author fails'

$multi = @"
CORE-XXX-004: co-developed change

Signed-off-by: Someone Else <other@example.com>
Signed-off-by: A Developer <dev@example.com>
"@
AssertTrue (Test-LiveCoreCommitSignOff -AuthorEmail $author -Message $multi) `
    'one matching Signed-off-by among several passes'

AssertTrue (Test-LiveCoreCommitSignOff -AuthorEmail 'DEV@Example.com' -Message $signed) `
    'sign-off matching is case-insensitive on the email'

# --- DCO sign-off: the report reconciles a mixed commit set and skips merges. ---
$commits = @(
    [pscustomobject]@{ Sha = 'aaa'; AuthorEmail = $author; Message = $signed; IsMerge = $false },
    [pscustomobject]@{ Sha = 'bbb'; AuthorEmail = $author; Message = $unsigned; IsMerge = $false },
    [pscustomobject]@{ Sha = 'ccc'; AuthorEmail = $author; Message = $unsigned; IsMerge = $true }
)
$report = Get-LiveCoreSignOffReport -Commits $commits
AssertTrue ($report.Checked -eq 2) 'the report checks only the two non-merge commits (the merge is skipped)'
AssertTrue ($report.Unsigned.Count -eq 1) 'the report flags exactly the one unsigned non-merge commit'
AssertTrue (-not $report.IsClean) 'a set containing an unsigned commit is not clean'

$allSigned = @(
    [pscustomobject]@{ Sha = 'ddd'; AuthorEmail = $author; Message = $signed; IsMerge = $false },
    [pscustomobject]@{ Sha = 'eee'; AuthorEmail = $author; Message = $multi; IsMerge = $false }
)
$cleanReport = Get-LiveCoreSignOffReport -Commits $allSigned
AssertTrue ($cleanReport.IsClean) 'a set whose every non-merge commit is signed off is clean (passes when present)'

# --- Guard the real repository: every in-scope source file carries the header. ---
$real = Get-LiveCoreLicenseHeaderReport -RepoRoot $repoRoot
AssertTrue ($real.Checked -gt 0) 'the scope enumerates real shipped source files'
if (-not $real.IsClean) {
    foreach ($path in $real.Missing) {
        $failures.Add("FAIL: real source file is missing the SPDX header: $path")
    }
}
else {
    Write-Host "PASS: every in-scope source file in the repository carries the SPDX header ($($real.Checked) checked)"
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Contributor-policy tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Contributor-policy tests passed: SPDX header detection/insertion/scope and DCO sign-off behave as documented.' -ForegroundColor Green
exit 0
