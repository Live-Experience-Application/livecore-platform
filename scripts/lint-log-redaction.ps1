#requires -Version 5.1

<#
.SYNOPSIS
    CI lint that enforces ID-only structured logging as a guardrail, not a
    convention (CORE-OBS-006, threat T7 in docs/07_SECURITY_THREAT_MODEL.md).

.DESCRIPTION
    Scans the tracked first-party C# source under apps/ (excluding the generated
    EF migrations) for ILogger calls whose message TEMPLATE would put a value into
    the log text rather than a structured identifier, and FAILS the build on one:

      - an interpolated template ($"...{x}...") that embeds a runtime value in the
        message text and cannot be redacted (the CA2254 anti-pattern);
      - a template that concatenates a non-literal expression into the message
        ("user " + name + " ..."); a constant "a" + "b" join is allowed;
      - a constant template naming a content/PII/secret structured property
        ({Email}, {DisplayName}, {AccessToken}, {ContentBody}, ...).

    Existing ID-only templates are unaffected: identifier/metadata placeholders
    ({ExportJobId}, {ItemCount}, {ResourceType}, {OrganizationSlug}) and coarse
    non-PII names ({Provider}, {JobName}, {RequestRoute}) pass. The analysis lives
    in scripts/LiveCoreLogRedaction.psm1 and is unit-tested by
    scripts/test-log-redaction.ps1 (run in CI before this scan).

    Exits 0 when the tree is clean, 1 when a content-bearing log template is
    found, and 2 on a configuration error (no git work tree to enumerate tracked
    source). The git enumeration is fail-closed: if the tracked file set cannot be
    determined the scan errors out rather than passing.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-log-redaction.ps1
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    # $PSScriptRoot is not available in param() defaults on Windows PowerShell 5.1.
    $scriptDir = $PSScriptRoot
    if (-not $scriptDir) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $RepoRoot = Split-Path -Parent $scriptDir
}
else {
    $scriptDir = Join-Path $RepoRoot 'scripts'
}

$RepoRoot = (Resolve-Path -Path $RepoRoot).Path

Import-Module (Join-Path $scriptDir 'LiveCoreLogRedaction.psm1') -Force

$result = $null
try {
    $result = Invoke-LiveCoreLogRedactionScan -RepoRoot $RepoRoot
}
catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 2
}

if ($result.IsClean) {
    Write-Host ("Log-redaction lint passed: {0} ILogger call(s) across {1} file(s) are ID-only; no content/PII/secret log template found." -f $result.LoggerCalls, $result.FilesScanned) -ForegroundColor Green
    exit 0
}

Write-Host ("Log-redaction lint FAILED: {0} content-bearing log template(s) found." -f $result.Findings.Count) -ForegroundColor Red
foreach ($finding in $result.Findings) {
    if ($finding.Kind -eq 'ForbiddenPlaceholder') {
        Write-Host ("  {0}:{1} -> {2} {{{3}}} in: {4}" -f $finding.File, $finding.Line, $finding.Kind, $finding.Placeholder, $finding.Snippet)
    }
    else {
        Write-Host ("  {0}:{1} -> {2} in: {3}" -f $finding.File, $finding.Line, $finding.Kind, $finding.Snippet)
    }
}
Write-Host ''
Write-Host 'Core logs IDs and event types, never sensitive content bodies, PII or secrets (threat T7).'
Write-Host 'Use a constant structured template with identifier-only placeholders, e.g.'
Write-Host '  _logger.LogInformation("Handled {ResourceType} {ResourceId}.", resourceType, resourceId);'
Write-Host 'See docs/07_SECURITY_THREAT_MODEL.md and docs/15_OBSERVABILITY.md.'
exit 1
