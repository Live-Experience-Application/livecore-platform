#requires -Version 5.1
<#
.SYNOPSIS
    Spec consistency check for the LiveCore Core Platform (CORE-DOC-001, extended
    by CORE-SPEC-001).

.DESCRIPTION
    Verifies that the route, table, event and epic specifications in docs/ and
    csv/ agree with each other, with the single source of truth per concern
    (docs/24_SPEC_CONSISTENCY.md) AND with the implementation. The check logic
    lives in scripts/LiveCoreSpecConsistency.psm1 so it is shared with the
    seeded-drift tests (scripts/test-spec-consistency.ps1).

    Name-set invariants (CORE-DOC-001):
      1. every route in the docs/08 representative block is a row in
         csv/api_routes.csv;
      2. the docs/10 table list equals the table set in csv/database_tables.csv;
      3. every non-deferred table in csv/entitlement_database_tables.csv exists
         in csv/database_tables.csv;
      4. the docs/09 event table equals the event set in csv/event_catalog.csv;
      5. the docs/18 epic list equals the union of the epic columns of
         csv/core_epics_stories.csv and csv/core_phase2_epics_stories.csv.

    Semantic invariants (CORE-SPEC-001) - validated against the actual
    implementation, not just names/counts:
      6. csv/api_routes.csv equals the routes the minimal-API registrations
         mount, in BOTH directions (undocumented endpoint or phantom route fails);
      7. route roles/auth: roles cells use only known roles/descriptors, and the
         provider-callback routes documented there are exactly the AllowAnonymous
         endpoints in code;
      8. csv/entitlement_event_catalog.csv is well-formed and internally
         consistent (an event with no audit action fails);
      9. csv/mobile_store_api_routes.csv mirrors real /api/v1 routes (path/method/
         auth_required) and the in-process MobileApiGateway route table;
     10. csv/database_tables.csv equals the EF model snapshot's tables (both
         directions) and the promised UNIQUE indexes are declared in the snapshot.

    Exits 0 when every invariant holds, 1 when drift is found, and 2 on a
    configuration error (a spec file that cannot be found or parsed).

    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh).

.EXAMPLE
    pwsh -NoProfile -File scripts/spec-consistency.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/spec-consistency.ps1
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $scriptDir
}

Import-Module (Join-Path -Path $scriptDir -ChildPath 'LiveCoreSpecConsistency.psm1') -Force

try {
    $result = Invoke-LiveCoreSpecConsistency -RepoRoot $RepoRoot
}
catch {
    Write-Host "Spec consistency ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

$findings = $result.Findings
$checkCount = $result.CheckCount

if ($findings.Count -gt 0) {
    Write-Host "Spec consistency FAILED: $($findings.Count) drift finding(s) across $checkCount check(s)." -ForegroundColor Red
    foreach ($finding in $findings) {
        Write-Host "  - $finding"
    }
    Write-Host 'Reconcile to the single source of truth per concern (see docs/24_SPEC_CONSISTENCY.md).'
    exit 1
}

Write-Host "Spec consistency passed: $checkCount check(s), route/table/event/epic specs agree with each other and the implementation." -ForegroundColor Green
exit 0
