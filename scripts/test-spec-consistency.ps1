#requires -Version 5.1

<#
.SYNOPSIS
    Tests the spec-consistency checks (CORE-DOC-001, extended by CORE-SPEC-001).

.DESCRIPTION
    Pure-PowerShell assertions over LiveCoreSpecConsistency.psm1 - no external test
    framework, so it runs as a CI gate and locally on both pwsh and Windows
    PowerShell 5.1. Exits 0 when every assertion holds and 1 when any fails.

    It proves the SEMANTIC checks reject seeded drift over fixtures - a route with
    a changed (unknown) role, an undocumented endpoint, a documented-but-unmounted
    route, an entitlement-catalog event with no audit action, a mobile route whose
    auth_required disagrees with the route, a table/unique-index that the EF model
    snapshot does not back, a non-deferred session event no command emits
    (CORE-EVT-004), and an OpenAPI document that omits a registered route or lists a
    phantom one (CORE-OAS-001) - and then proves the REAL repository tree passes
    every check. This is the seeded-drift / real-tree-passes requirement of
    CORE-SPEC-001.

.EXAMPLE
    pwsh -NoProfile -File scripts/test-spec-consistency.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-spec-consistency.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Import-Module (Join-Path $scriptDir 'LiveCoreSpecConsistency.psm1') -Force
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

function Test-AnyFinding {
    param([string[]]$Finding, [string]$Pattern)
    return [bool](@($Finding) | Where-Object { $_ -match $Pattern }).Count
}

# Fixture builders mirror the shape the extractors produce.
function Get-DocRouteFixture {
    param([string]$Method, [string]$Path, [string]$Roles, [bool]$Anonymous)
    return [pscustomobject]@{
        Method         = $Method.ToUpperInvariant()
        Route          = $Path
        Key            = (ConvertTo-LiveCoreRouteKey -Method $Method -Path $Path)
        Roles          = $Roles
        IsAnonymousDoc = $Anonymous
    }
}

function Get-ImplRouteFixture {
    param([string]$Method, [string]$Path, [bool]$Anonymous, [string]$Source = 'Fixture.cs')
    return [pscustomobject]@{
        Method      = $Method.ToUpperInvariant()
        Path        = $Path
        Key         = (ConvertTo-LiveCoreRouteKey -Method $Method -Path $Path)
        IsAnonymous = $Anonymous
        Source      = $Source
    }
}

function Get-EntlEventFixture {
    param([string]$Name, [string]$Emitter, [string]$Recipients, [string]$Persisted, [string]$Audit)
    return [pscustomobject]@{
        event_name = $Name
        emitter    = $Emitter
        recipients = $Recipients
        persisted  = $Persisted
        audit      = $Audit
        notes      = ''
    }
}

$knownRoles = @('Owner', 'Admin', 'Host', 'CoHost', 'Participant', 'Observer', 'Auditor')
$knownDescriptors = @(Get-LiveCoreKnownRoleDescriptor)

# === Check 6: CSV routes vs implemented endpoints (both directions) ===

# A consistent pair has no findings.
$consistentDoc = @(Get-DocRouteFixture -Method 'GET' -Path '/api/v1/me' -Roles 'authenticated' -Anonymous $false)
$consistentImpl = @(Get-ImplRouteFixture -Method 'GET' -Path '/api/v1/me' -Anonymous $false)
$noFindings = @(Test-LiveCoreRouteImplementation -Documented $consistentDoc -Implemented $consistentImpl)
AssertTrue ($noFindings.Count -eq 0) 'a route documented and implemented identically produces no findings'

# An UNDOCUMENTED endpoint (implemented, not in the CSV) fails.
$docSet = @(Get-DocRouteFixture -Method 'GET' -Path '/api/v1/me' -Roles 'authenticated' -Anonymous $false)
$implWithExtra = @(
    Get-ImplRouteFixture -Method 'GET' -Path '/api/v1/me' -Anonymous $false
    Get-ImplRouteFixture -Method 'GET' -Path '/api/v1/secret-backdoor' -Anonymous $false -Source 'Backdoor.cs'
)
$undocumented = @(Test-LiveCoreRouteImplementation -Documented $docSet -Implemented $implWithExtra)
AssertTrue (Test-AnyFinding -Finding $undocumented -Pattern 'ENDPOINT:.*secret-backdoor.*not documented') `
    'an implemented endpoint missing from csv/api_routes.csv (an undocumented endpoint) fails the check'

# A documented route that nothing mounts fails too.
$docWithPhantom = @(
    Get-DocRouteFixture -Method 'GET' -Path '/api/v1/me' -Roles 'authenticated' -Anonymous $false
    Get-DocRouteFixture -Method 'POST' -Path '/api/v1/ghost' -Roles 'Owner' -Anonymous $false
)
$implOnlyMe = @(Get-ImplRouteFixture -Method 'GET' -Path '/api/v1/me' -Anonymous $false)
$phantom = @(Test-LiveCoreRouteImplementation -Documented $docWithPhantom -Implemented $implOnlyMe)
AssertTrue (Test-AnyFinding -Finding $phantom -Pattern 'ROUTE-IMPL:.*ghost.*no minimal-API endpoint') `
    'a documented route that no endpoint mounts fails the check'

# === Check 7: route roles/auth ===

# A route with a changed (unknown) role fails.
$changedRoleDoc = @(Get-DocRouteFixture -Method 'POST' -Path '/api/v1/workspaces' -Roles 'Owner,Superuser' -Anonymous $false)
$changedRoleImpl = @(Get-ImplRouteFixture -Method 'POST' -Path '/api/v1/workspaces' -Anonymous $false)
$roleDrift = @(Test-LiveCoreRouteRole -Documented $changedRoleDoc -Implemented $changedRoleImpl -KnownRole $knownRoles -KnownDescriptor $knownDescriptors)
AssertTrue (Test-AnyFinding -Finding $roleDrift -Pattern "ROLE:.*unknown role 'Superuser'") `
    'a route with a changed role (a role that is not a MembershipRole) fails the check'

# A well-formed roles cell with only known roles/descriptors passes the vocabulary check.
$goodRoleDoc = @(
    Get-DocRouteFixture -Method 'POST' -Path '/api/v1/workspaces' -Roles 'Owner,Admin' -Anonymous $false
    Get-DocRouteFixture -Method 'GET' -Path '/api/v1/participants/{participantId}/visible-feed' -Roles 'Participant owner or Host' -Anonymous $false
)
$goodRoleImpl = @(
    Get-ImplRouteFixture -Method 'POST' -Path '/api/v1/workspaces' -Anonymous $false
    Get-ImplRouteFixture -Method 'GET' -Path '/api/v1/participants/{participantId}/visible-feed' -Anonymous $false
)
$goodRole = @(Test-LiveCoreRouteRole -Documented $goodRoleDoc -Implemented $goodRoleImpl -KnownRole $knownRoles -KnownDescriptor $knownDescriptors)
AssertTrue ($goodRole.Count -eq 0) 'known roles and authorization descriptors produce no role/auth findings'

# Auth drift: a route documented as a provider callback but implemented behind auth fails.
$authDriftDoc = @(Get-DocRouteFixture -Method 'POST' -Path '/api/v1/store-notifications/apple' -Roles 'none (provider callback)' -Anonymous $true)
$authDriftImpl = @(Get-ImplRouteFixture -Method 'POST' -Path '/api/v1/store-notifications/apple' -Anonymous $false)
$authDrift = @(Test-LiveCoreRouteRole -Documented $authDriftDoc -Implemented $authDriftImpl -KnownRole $knownRoles -KnownDescriptor $knownDescriptors)
AssertTrue (Test-AnyFinding -Finding $authDrift -Pattern 'AUTH:.*provider callback.*not AllowAnonymous') `
    'a route documented as an unauthenticated callback but implemented behind auth fails the check'

# === Check 8: entitlement/store event catalog ===

# A well-formed catalog passes.
$goodCatalog = @(
    Get-EntlEventFixture -Name 'EntitlementGranted' -Emitter 'Core system' -Recipients 'subject' -Persisted 'true' -Audit 'true'
    Get-EntlEventFixture -Name 'AdEligibilityEvaluated' -Emitter 'Core API' -Recipients 'subject' -Persisted 'false' -Audit 'false'
)
$goodCatalogFindings = @(Test-LiveCoreEntitlementEventCatalog -Row $goodCatalog)
AssertTrue ($goodCatalogFindings.Count -eq 0) 'a well-formed entitlement event catalog produces no findings'

# An event with NO audit action (a blank audit column) fails.
$noAuditCatalog = @(Get-EntlEventFixture -Name 'EntitlementGranted' -Emitter 'Core system' -Recipients 'subject' -Persisted 'true' -Audit '')
$noAudit = @(Test-LiveCoreEntitlementEventCatalog -Row $noAuditCatalog)
AssertTrue (Test-AnyFinding -Finding $noAudit -Pattern "ENTL-EVENT:.*EntitlementGranted.*no audit action") `
    'an entitlement-catalog event with no audit action fails the check'

# An event marked audited but not persisted fails.
$inconsistentCatalog = @(Get-EntlEventFixture -Name 'QuotaExceeded' -Emitter 'Core API' -Recipients 'subject' -Persisted 'false' -Audit 'true')
$inconsistent = @(Test-LiveCoreEntitlementEventCatalog -Row $inconsistentCatalog)
AssertTrue (Test-AnyFinding -Finding $inconsistent -Pattern 'ENTL-EVENT:.*audited but not persisted') `
    'an entitlement-catalog event marked audited but not persisted fails the check'

# CORE-SPEC-002: the catalog is BOUND to the real AuditAction enum. An audit=true event with no backing
# AuditAction member fails (the catalog is aspirational).
$auditActionsFixture = @('EntitlementGranted', 'QuotaExceeded')
$unbackedCatalog = @(Get-EntlEventFixture -Name 'PhantomAudited' -Emitter 'Core system' -Recipients 'subject' -Persisted 'true' -Audit 'true')
$unbacked = @(Test-LiveCoreEntitlementEventCatalog -Row $unbackedCatalog -AuditAction $auditActionsFixture)
AssertTrue (Test-AnyFinding -Finding $unbacked -Pattern "ENTL-EVENT:.*PhantomAudited.*no AuditAction") `
    'an audit=true catalog event with no backing AuditAction fails the check (CORE-SPEC-002)'

# An AuditAction member exists but its catalog event is still audit=false fails (a backed event must be audited).
$unmarkedCatalog = @(Get-EntlEventFixture -Name 'EntitlementGranted' -Emitter 'Core system' -Recipients 'subject' -Persisted 'false' -Audit 'false')
$unmarked = @(Test-LiveCoreEntitlementEventCatalog -Row $unmarkedCatalog -AuditAction $auditActionsFixture)
AssertTrue (Test-AnyFinding -Finding $unmarked -Pattern "ENTL-EVENT:.*AuditAction.EntitlementGranted exists but.*audit=false") `
    'an AuditAction whose catalog event is marked audit=false fails the binding (CORE-SPEC-002)'

# A catalog whose audit flags agree with the AuditAction set passes the binding (audit=true IFF a member exists).
$boundGood = @(
    Get-EntlEventFixture -Name 'EntitlementGranted' -Emitter 'Core system' -Recipients 'subject' -Persisted 'true' -Audit 'true'
    Get-EntlEventFixture -Name 'AdEligibilityEvaluated' -Emitter 'Core API' -Recipients 'subject' -Persisted 'false' -Audit 'false'
)
$boundGoodFindings = @(Test-LiveCoreEntitlementEventCatalog -Row $boundGood -AuditAction @('EntitlementGranted'))
AssertTrue ($boundGoodFindings.Count -eq 0) `
    'a catalog whose audit flags match the AuditAction set passes the binding (CORE-SPEC-002)'

# The real AuditAction enum carries the CORE-SPEC-002 entitlement/store actions.
$realAuditActionsCheck = @(Get-LiveCoreAuditAction -RepoRoot $repoRoot)
AssertTrue (($realAuditActionsCheck -contains 'EntitlementGranted') -and ($realAuditActionsCheck -contains 'QuotaExceeded') `
        -and ($realAuditActionsCheck -contains 'StoreNotificationProcessed')) `
    'the AuditAction enum carries the entitlement/store actions (CORE-SPEC-002)'

# === Check 9: mobile store CSV ===

$mobileDoc = @(
    Get-DocRouteFixture -Method 'GET' -Path '/api/v1/me/entitlements' -Roles 'authenticated user' -Anonymous $false
    Get-DocRouteFixture -Method 'POST' -Path '/api/v1/store-notifications/apple' -Roles 'none (provider callback)' -Anonymous $true
)
$gatewayPaths = @('/v1/me/entitlements', '/v1/store-notifications/apple')

$goodMobile = @(
    [pscustomobject]@{ Method = 'GET'; Path = '/v1/me/entitlements'; Owner = 'Core'; AuthRequired = 'true' }
    [pscustomobject]@{ Method = 'POST'; Path = '/v1/store-notifications/apple'; Owner = 'Core'; AuthRequired = 'false' }
)
$goodMobileFindings = @(Test-LiveCoreMobileStoreRoute -Mobile $goodMobile -Documented $mobileDoc -GatewayPath $gatewayPaths)
AssertTrue ($goodMobileFindings.Count -eq 0) 'a mobile store CSV that mirrors the routes and the gateway table produces no findings'

# auth_required disagreeing with the route's documented authentication fails.
$mobileAuthFlip = @(
    [pscustomobject]@{ Method = 'POST'; Path = '/v1/store-notifications/apple'; Owner = 'Core'; AuthRequired = 'true' }
)
$mobileAuthDrift = @(Test-LiveCoreMobileStoreRoute -Mobile $mobileAuthFlip -Documented $mobileDoc -GatewayPath @('/v1/store-notifications/apple'))
AssertTrue (Test-AnyFinding -Finding $mobileAuthDrift -Pattern 'MOBILE:.*auth_required.*disagrees') `
    'a mobile route whose auth_required disagrees with the route fails the check'

# A mobile path that does not map to a documented /api/v1 route fails.
$mobilePhantom = @(
    [pscustomobject]@{ Method = 'GET'; Path = '/v1/me/phantom'; Owner = 'Core'; AuthRequired = 'true' }
)
$mobilePhantomDrift = @(Test-LiveCoreMobileStoreRoute -Mobile $mobilePhantom -Documented $mobileDoc -GatewayPath @('/v1/me/phantom'))
AssertTrue (Test-AnyFinding -Finding $mobilePhantomDrift -Pattern 'MOBILE:.*phantom.*not a documented /api/v1 route') `
    'a mobile route that maps to no documented /api/v1 route fails the check'

# === Check 10: table columns/indexes vs the EF model snapshot ===

$snapshotText = Get-LiveCoreSnapshotText -RepoRoot $repoRoot
$snapshotTables = @(Get-LiveCoreSnapshotTable -RepoRoot $repoRoot)

AssertTrue (Test-LiveCoreUniqueIndex -SnapshotText $snapshotText -Table 'purchase_transactions' -Column @('Provider', 'ProviderTransactionId')) `
    'the snapshot declares the purchase_transactions idempotency UNIQUE index'
AssertTrue (-not (Test-LiveCoreUniqueIndex -SnapshotText $snapshotText -Table 'purchase_transactions' -Column @('Provider', 'NotAColumn'))) `
    'a UNIQUE index the snapshot does not declare is reported missing'

# A csv table the EF model snapshot does not back fails.
$ghostTables = @($snapshotTables + 'ghost_table')
$schemaDrift = @(Test-LiveCoreTableSchema -CsvTable $ghostTables -SnapshotTable $snapshotTables -SnapshotText $snapshotText -RequiredUniqueIndex @())
AssertTrue (Test-AnyFinding -Finding $schemaDrift -Pattern 'SCHEMA:.*ghost_table.*not a table in the EF model snapshot') `
    'a csv/database_tables.csv table missing from the EF model snapshot fails the check'

# A promised UNIQUE index the snapshot does not declare fails.
$badIndex = @([pscustomobject]@{ Table = 'purchase_transactions'; Column = @('Provider', 'NotAColumn'); Why = 'seeded drift' })
$indexDrift = @(Test-LiveCoreTableSchema -CsvTable $snapshotTables -SnapshotTable $snapshotTables -SnapshotText $snapshotText -RequiredUniqueIndex $badIndex)
AssertTrue (Test-AnyFinding -Finding $indexDrift -Pattern 'INDEX:.*purchase_transactions') `
    'a promised UNIQUE index missing from the EF model snapshot fails the check'

# === Check 11: emitted session events vs the non-deferred catalog (CORE-EVT-004) ===

function Get-CatalogEventFixture {
    param([string]$EventName, [bool]$Deferred)
    return [pscustomobject]@{ Event = $EventName; IsDeferred = $Deferred }
}

# A catalog whose non-deferred events exactly match the emitted set produces no findings; a DEFERRED row is
# excluded (it need not be emitted), exactly as the real SceneCreated/ContentBlockCreated are.
$consistentCatalog = @(
    Get-CatalogEventFixture -EventName 'SessionCreated' -Deferred $false
    Get-CatalogEventFixture -EventName 'SceneCreated' -Deferred $true
)
$consistentEmission = @(Test-LiveCoreSessionEventEmission -CatalogRow $consistentCatalog -EmittedType @('SessionCreated'))
AssertTrue ($consistentEmission.Count -eq 0) `
    'a catalog whose non-deferred events equal the emitted set (DEFERRED rows excluded) produces no findings (CORE-EVT-004)'

# A non-deferred catalog event that nothing emits fails (the catalog is aspirational).
$aspirationalCatalog = @(
    Get-CatalogEventFixture -EventName 'SessionCreated' -Deferred $false
    Get-CatalogEventFixture -EventName 'PhantomEvent' -Deferred $false
)
$aspirational = @(Test-LiveCoreSessionEventEmission -CatalogRow $aspirationalCatalog -EmittedType @('SessionCreated'))
AssertTrue (Test-AnyFinding -Finding $aspirational -Pattern 'EVENT-EMIT:.*PhantomEvent.*no Core command emits') `
    'a non-deferred catalog event that no command emits fails the check (CORE-EVT-004)'

# An emitted type that is not a non-deferred catalog row fails (emitting an undocumented or deferred event).
$ghostEmit = @(Test-LiveCoreSessionEventEmission `
        -CatalogRow @(Get-CatalogEventFixture -EventName 'SessionCreated' -Deferred $false) `
        -EmittedType @('SessionCreated', 'GhostEmitted'))
AssertTrue (Test-AnyFinding -Finding $ghostEmit -Pattern 'EVENT-EMIT:.*GhostEmitted.*not a non-deferred event') `
    'a SessionEventTypes constant that is not a non-deferred catalog event fails the check (CORE-EVT-004)'

# The real SessionEventTypes carries the CORE-EVT-004 events and binds cleanly to the real catalog.
$realEmittedTypes = @(Get-LiveCoreSessionEventType -RepoRoot $repoRoot)
AssertTrue (($realEmittedTypes -contains 'SessionCreated') -and ($realEmittedTypes -contains 'RecapGenerated')) `
    'SessionEventTypes carries the CORE-EVT-004 events (SessionCreated, RecapGenerated)'
$realCatalogEvents = @(Get-LiveCoreCatalogEvent -RepoRoot $repoRoot)
$realEmission = @(Test-LiveCoreSessionEventEmission -CatalogRow $realCatalogEvents -EmittedType $realEmittedTypes)
AssertTrue ($realEmission.Count -eq 0) `
    'the real SessionEventTypes set equals the real non-deferred event catalog (CORE-EVT-004)'

# === Check 12: committed OpenAPI document vs the implemented /api/v1 endpoints (CORE-OAS-001) ===

function Get-OpenApiRouteFixture {
    param([string]$Method, [string]$Path)
    return [pscustomobject]@{
        Method = $Method.ToUpperInvariant()
        Path   = $Path
        Key    = (ConvertTo-LiveCoreRouteKey -Method $Method -Path $Path)
    }
}

# A document whose routes exactly match the implemented endpoints produces no findings.
$consistentOpenApi = @(
    Get-OpenApiRouteFixture -Method 'GET' -Path '/api/v1/me'
    Get-OpenApiRouteFixture -Method 'POST' -Path '/api/v1/sessions/{sessionId}/reveal'
)
$consistentImplForOas = @(
    Get-ImplRouteFixture -Method 'GET' -Path '/api/v1/me' -Anonymous $false
    Get-ImplRouteFixture -Method 'POST' -Path '/api/v1/sessions/{sessionId}/reveal' -Anonymous $false
)
$consistentOas = @(Test-LiveCoreOpenApiCoverage -OpenApi $consistentOpenApi -Implemented $consistentImplForOas)
AssertTrue ($consistentOas.Count -eq 0) `
    'an OpenAPI document whose routes match the implemented endpoints produces no findings (CORE-OAS-001)'

# A registered endpoint missing from the committed document fails (the document is stale).
$staleOpenApi = @(Get-OpenApiRouteFixture -Method 'GET' -Path '/api/v1/me')
$staleImpl = @(
    Get-ImplRouteFixture -Method 'GET' -Path '/api/v1/me' -Anonymous $false
    Get-ImplRouteFixture -Method 'POST' -Path '/api/v1/workspaces' -Anonymous $false -Source 'WorkspaceEndpoints.cs'
)
$staleOas = @(Test-LiveCoreOpenApiCoverage -OpenApi $staleOpenApi -Implemented $staleImpl)
AssertTrue (Test-AnyFinding -Finding $staleOas -Pattern 'OPENAPI:.*workspaces.*missing from openapi/livecore-v1.json') `
    'a registered /api/v1 endpoint missing from the committed OpenAPI document fails the check (CORE-OAS-001)'

# A document path that no endpoint mounts fails (a phantom route in the document).
$phantomOpenApi = @(
    Get-OpenApiRouteFixture -Method 'GET' -Path '/api/v1/me'
    Get-OpenApiRouteFixture -Method 'GET' -Path '/api/v1/ghost'
)
$phantomImplForOas = @(Get-ImplRouteFixture -Method 'GET' -Path '/api/v1/me' -Anonymous $false)
$phantomOas = @(Test-LiveCoreOpenApiCoverage -OpenApi $phantomOpenApi -Implemented $phantomImplForOas)
AssertTrue (Test-AnyFinding -Finding $phantomOas -Pattern 'OPENAPI:.*ghost.*no minimal-API endpoint mounts') `
    'an OpenAPI document path that no endpoint mounts fails the check (CORE-OAS-001)'

# The real committed OpenAPI document equals the real implemented /api/v1 routes (both directions).
$realOpenApiRoutes = @(Get-LiveCoreOpenApiRoute -RepoRoot $repoRoot)
AssertTrue ($realOpenApiRoutes.Count -gt 0) 'the committed OpenAPI document declares /api/v1 routes (CORE-OAS-001)'
$realImplementedRoutes = @(Get-LiveCoreImplementedRoute -RepoRoot $repoRoot)
$realOasCoverage = @(Test-LiveCoreOpenApiCoverage -OpenApi $realOpenApiRoutes -Implemented $realImplementedRoutes)
AssertTrue ($realOasCoverage.Count -eq 0) `
    'the real committed OpenAPI document covers exactly the implemented /api/v1 routes (CORE-OAS-001)'

# === The real repository tree passes every check ===

$realEntitlementEvents = @(Get-LiveCoreEntitlementEvent -RepoRoot $repoRoot)
$realAuditActions = @(Get-LiveCoreAuditAction -RepoRoot $repoRoot)
$realCatalog = @(Test-LiveCoreEntitlementEventCatalog -Row $realEntitlementEvents -AuditAction $realAuditActions)
AssertTrue ($realCatalog.Count -eq 0) 'the real csv/entitlement_event_catalog.csv passes the catalog check and the AuditAction binding (CORE-SPEC-002)'

$result = Invoke-LiveCoreSpecConsistency -RepoRoot $repoRoot
AssertTrue ($result.CheckCount -eq 12) 'the orchestrator runs all twelve checks'
if ($result.Findings.Count -gt 0) {
    foreach ($finding in $result.Findings) {
        $failures.Add("FAIL (real tree): $finding")
    }
}
else {
    Write-Host 'PASS: the real repository tree passes every spec-consistency check (zero findings)'
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Spec consistency tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ''
Write-Host 'Spec consistency tests passed: seeded route/role/auth/event/mobile/schema/session-event/openapi drift is rejected and the real tree passes.' -ForegroundColor Green
exit 0
