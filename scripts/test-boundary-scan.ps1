#requires -Version 5.1

<#
.SYNOPSIS
    Tests the boundary scan (CORE-BND-001).

.DESCRIPTION
    Pure-PowerShell assertions over scripts/boundary-scan.ps1 - no external test
    framework, so it runs as a CI gate and locally on both pwsh and Windows
    PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

    It builds a throwaway git work tree and proves the scanner's coverage rules:
      - a seeded brand/platform term in a TRACKED .cs and in a TRACKED
        Migrations.Dockerfile is flagged, and every forbidden brand term
        (the product/platform brand names plus the generic vertical-business
        and tabletop vocabulary the terms file lists) is detected;
      - a gitignored tooling script (scripts/story-loop.ps1) is NOT flagged,
        because enumeration is tracked-files-only (git ls-files);
      - the documentation that legitimately lists the terms is NOT flagged - the
        docs/ and csv/ trees and the root files README.md, AGENTS.md, LICENSE and
        CHANGELOG.md;
      - neutralizing the seeded source makes the same tree scan clean;
      - a directory that is not a git work tree fails closed (exit 2).
    It then proves the REAL repository tree scans clean (exit 0).

    The seeded brand/platform terms are assembled from fragments at run time so
    this test file - which is itself tracked Core source and therefore scanned -
    never contains a contiguous forbidden term.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-boundary-scan.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-boundary-scan.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
# Invoking the scanner as a child process is expected to return non-zero exit
# codes (1 on violations, 2 on a fail-closed configuration error). Do not let a
# non-zero native exit raise a terminating error on PowerShell 7.4+, where that
# is the default under ErrorActionPreference='Stop'. (Harmless no-op on 5.1.)
$PSNativeCommandUseErrorActionPreference = $false

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$repoRoot = Split-Path -Parent $scriptDir
$scanScript = Join-Path $scriptDir 'boundary-scan.ps1'
$realTermsCsv = Join-Path $repoRoot 'csv/forbidden_core_terms.csv'

# Run the scan under the SAME PowerShell host this test runs in (pwsh on Linux
# CI, Windows PowerShell 5.1 or pwsh locally).
$pwshExe = (Get-Process -Id $PID).Path

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

# Runs boundary-scan.ps1 against a repository root and captures its exit code and
# combined output. The terms file is read from $Root, and enumeration is over
# $Root's tracked files, so each fixture root is fully self-contained.
function Invoke-BoundaryScan {
    param([string]$PwshExe, [string]$ScanScript, [string]$Root)
    # Function-local: the scan is EXPECTED to write to stderr and exit non-zero
    # (the fail-closed cases). On Windows PowerShell 5.1 a 2>&1 capture of a
    # native command's stderr surfaces ErrorRecords that would terminate under
    # 'Stop'; capturing them as plain output here is exactly what we want.
    $ErrorActionPreference = 'Continue'
    $output = & $PwshExe -NoProfile -File $ScanScript -RepoRoot $Root 2>&1 | Out-String
    $code = $LASTEXITCODE
    return [pscustomobject]@{ ExitCode = $code; Output = $output }
}

# Runs a git command, tolerating stderr (hints/warnings) without terminating and
# failing loudly only on a non-zero exit code.
function Invoke-Git {
    param([string[]]$GitArgs)
    $ErrorActionPreference = 'Continue'
    $null = & git @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArgs -join ' ') failed with exit code $LASTEXITCODE"
    }
}

# Writes a fixture file, creating its parent directory. Accepts a string or an
# array of lines.
function WriteFixtureFile {
    param([string]$Root, [string]$RelativePath, [string[]]$Line)
    $full = Join-Path $Root $RelativePath
    $dir = Split-Path -Parent $full
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Set-Content -LiteralPath $full -Value $Line
}

# Brand/platform terms, assembled from fragments so this tracked file does not
# itself contain a contiguous forbidden term (it is scanned too). The fragments
# are individually harmless; only the concatenated VALUE - written into the
# throwaway fixture below - is a forbidden term.
$brandVisual = -join @('Arc', 'anOS')      # the product visual-identity brand
$brandVertical = -join @('Scen', 'arioOS') # the platform/vertical brand name
$termEnt = -join @('enter', 'prise')       # generic vertical-business vocabulary
$termAbbrev = -join @('D', 'n', 'D')        # the tabletop abbreviation
$termPhrase = -join @('Pen', '-and-', 'Paper') # the tabletop phrase
$allBrandTerms = @($brandVisual, $brandVertical, $termEnt, $termAbbrev, $termPhrase)

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-boundary-scan-" + [System.IO.Path]::GetRandomFileName())
$noGitRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("livecore-boundary-nogit-" + [System.IO.Path]::GetRandomFileName())

try {
    # --- Build a throwaway git work tree. ---
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null

    # The terms file is the real one, so the fixture detects exactly the terms the
    # repository forbids (and the test fails if a brand term is later dropped).
    WriteFixtureFile -Root $fixtureRoot -RelativePath 'csv/forbidden_core_terms.csv' -Line (Get-Content -LiteralPath $realTermsCsv)

    # TRACKED Core source that leaks every brand/platform term: must be flagged.
    $leakCs = @('namespace Fixture;')
    foreach ($term in $allBrandTerms) {
        $leakCs += "// $term leaked into a doc comment"
    }
    $leakCs += 'public sealed class Sample { }'
    WriteFixtureFile -Root $fixtureRoot -RelativePath 'apps/api/Entitlements/Sample.cs' -Line $leakCs

    # A TRACKED Migrations.Dockerfile leaking the brand: must be flagged (proves
    # *.Dockerfile is in scope).
    WriteFixtureFile -Root $fixtureRoot -RelativePath 'apps/api/Migrations.Dockerfile' -Line @(
        'FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build'
        "# $brandVisual leaked into the migrations runner image"
        'ENTRYPOINT ["dotnet", "ef", "database", "update"]')

    # A clean tracked file (control): never flagged.
    WriteFixtureFile -Root $fixtureRoot -RelativePath 'packages/contracts/index.ts' -Line 'export const VERSION = "0.1.0";'

    # Scanned (non-excluded) source legitimately using the "third-party" compound -
    # the dependency attribution wiring (CORE-LIC-003) - and its antonym "first-party"
    # - the contributor IP policy / SPDX-header vocabulary (CORE-LIC-004) - must NOT be
    # flagged, because both compounds are allowed even though they contain a forbidden
    # token.
    WriteFixtureFile -Root $fixtureRoot -RelativePath 'apps/api/Attribution.cs' -Line @(
        'namespace Fixture;'
        '// Ships the THIRD-PARTY-NOTICES.md third-party attribution inventory.'
        '// Every first-party source file carries the SPDX header.'
        'public sealed class Attribution { }')

    # Documentation that legitimately names the terms: must NOT be flagged.
    $docLine = "This explains the boundary: $brandVisual and $brandVertical are forbidden in Core."
    foreach ($docPath in @('README.md', 'AGENTS.md', 'LICENSE', 'CHANGELOG.md', 'docs/00_sample.md', 'csv/notes.md')) {
        WriteFixtureFile -Root $fixtureRoot -RelativePath $docPath -Line $docLine
    }

    # License/attribution texts (CORE-LIC-003): the per-package LICENSE copies and the
    # generated THIRD-PARTY-NOTICES.md (root and per-package) reproduce verbatim
    # license/notice text and are excluded by file name at ANY path - so a brand term
    # appearing in them, as it can in a preserved upstream notice, must NOT be flagged
    # even though they are an extensionless file (LICENSE) and a nested .md.
    foreach ($licensePath in @('packages/contracts/LICENSE', 'THIRD-PARTY-NOTICES.md', 'packages/sdk-ts/THIRD-PARTY-NOTICES.md')) {
        WriteFixtureFile -Root $fixtureRoot -RelativePath $licensePath -Line $docLine
    }

    # Gitignored local tooling whose template legitimately names the verticals:
    # present on disk but never tracked, so it must NOT be flagged.
    WriteFixtureFile -Root $fixtureRoot -RelativePath '.gitignore' -Line 'scripts/story-loop.ps1'
    WriteFixtureFile -Root $fixtureRoot -RelativePath 'scripts/story-loop.ps1' -Line "# $brandVisual and $brandVertical in the prompt template"

    Invoke-Git @('-C', $fixtureRoot, 'init', '-q')
    Invoke-Git @('-C', $fixtureRoot, 'add', '-A')

    # --- Scenario 1: the seeded leak is caught, excluded locations are not. ---
    $leak = Invoke-BoundaryScan -PwshExe $pwshExe -ScanScript $scanScript -Root $fixtureRoot

    AssertTrue ($leak.ExitCode -eq 1) 'a tree with a leaked brand term fails the scan (exit 1)'
    AssertTrue ($leak.Output.Contains('apps/api/Entitlements/Sample.cs')) `
        'a leaked brand term in a tracked .cs is flagged'
    AssertTrue ($leak.Output.Contains('apps/api/Migrations.Dockerfile')) `
        'a leaked brand term in a tracked Migrations.Dockerfile is flagged (*.Dockerfile is in scope)'
    foreach ($term in $allBrandTerms) {
        AssertTrue ($leak.Output.Contains($term)) "the scanner detects the forbidden term '$term'"
    }

    AssertTrue (-not $leak.Output.Contains('story-loop.ps1')) `
        'the gitignored scripts/story-loop.ps1 is NOT flagged (enumeration is tracked-files-only)'
    AssertTrue (-not $leak.Output.Contains('README.md')) 'README.md is NOT flagged (allowed documentation)'
    AssertTrue (-not $leak.Output.Contains('AGENTS.md')) 'AGENTS.md is NOT flagged (allowed documentation)'
    AssertTrue (-not $leak.Output.Contains('LICENSE')) 'LICENSE is NOT flagged (allowed documentation)'
    AssertTrue (-not $leak.Output.Contains('CHANGELOG.md')) 'CHANGELOG.md is NOT flagged (allowed documentation)'
    AssertTrue (-not $leak.Output.Contains('docs/00_sample.md')) 'the docs/ tree is NOT flagged (allowed documentation)'
    AssertTrue (-not $leak.Output.Contains('csv/notes.md')) 'the csv/ tree is NOT flagged (allowed documentation)'
    AssertTrue (-not $leak.Output.Contains('packages/contracts/LICENSE')) `
        'a per-package LICENSE copy is NOT flagged (license/attribution text by file name, CORE-LIC-003)'
    AssertTrue (-not $leak.Output.Contains('THIRD-PARTY-NOTICES.md')) `
        'the third-party NOTICE (root and per-package) is NOT flagged (license/attribution text, CORE-LIC-003)'
    AssertTrue (-not $leak.Output.Contains('apps/api/Attribution.cs')) `
        'the "third-party" (CORE-LIC-003) and "first-party" (CORE-LIC-004) compounds in scanned source are NOT flagged (allowed compounds)'
    AssertTrue (-not $leak.Output.Contains('packages/contracts/index.ts')) 'a clean tracked file is not flagged'

    # --- Scenario 2: neutralizing the source makes the same tree scan clean. ---
    WriteFixtureFile -Root $fixtureRoot -RelativePath 'apps/api/Entitlements/Sample.cs' -Line @(
        'namespace Fixture;'
        '// a vertical may display these as ... in its own UI'
        'public sealed class Sample { }')
    WriteFixtureFile -Root $fixtureRoot -RelativePath 'apps/api/Migrations.Dockerfile' -Line @(
        'FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build'
        'ENTRYPOINT ["dotnet", "ef", "database", "update"]')
    Invoke-Git @('-C', $fixtureRoot, 'add', '-A')

    $clean = Invoke-BoundaryScan -PwshExe $pwshExe -ScanScript $scanScript -Root $fixtureRoot
    AssertTrue ($clean.ExitCode -eq 0) 'neutralizing the leaked source makes the tree scan clean (exit 0)'
    AssertTrue ($clean.Output.Contains('Boundary scan passed')) 'a clean tree reports the pass message'

    # --- Scenario 3: fail closed when the tree cannot be enumerated. ---
    New-Item -ItemType Directory -Path $noGitRoot -Force | Out-Null
    WriteFixtureFile -Root $noGitRoot -RelativePath 'csv/forbidden_core_terms.csv' -Line (Get-Content -LiteralPath $realTermsCsv)
    $noGit = Invoke-BoundaryScan -PwshExe $pwshExe -ScanScript $scanScript -Root $noGitRoot
    AssertTrue ($noGit.ExitCode -eq 2) 'a directory that is not a git work tree fails closed (exit 2), never silently passing'

    # --- The real repository tree scans clean. ---
    $real = Invoke-BoundaryScan -PwshExe $pwshExe -ScanScript $scanScript -Root $repoRoot
    AssertTrue ($real.ExitCode -eq 0) 'the real repository tree scans clean (exit 0)'
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $noGitRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Boundary scan tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Boundary scan tests passed: tracked-only enumeration, Dockerfile scope, documentation exclusions and fail-closed behavior all hold.' -ForegroundColor Green
exit 0
