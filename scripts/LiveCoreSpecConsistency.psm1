#requires -Version 5.1
<#
.SYNOPSIS
    Spec consistency check logic for the LiveCore Core Platform
    (CORE-DOC-001, extended by CORE-SPEC-001).

.DESCRIPTION
    The route/table/event/epic consistency invariants, factored into testable
    functions so scripts/spec-consistency.ps1 (the CLI) and
    scripts/test-spec-consistency.ps1 (the seeded-drift tests) share one
    implementation - the same module + test pattern as LiveCoreComposeDeploy.psm1
    and LiveCoreMigrationLint.psm1.

    CORE-DOC-001 enforced five string-SET invariants (route/table/event/epic name
    membership). CORE-SPEC-001 adds SEMANTIC invariants that validate the specs
    against the actual implementation, so a green run no longer hides auth-role
    drift, undocumented endpoints, dead store/entitlement events or schema/index
    drift:

      6.  csv/api_routes.csv equals the routes the minimal-API registrations
          actually mount, in BOTH directions (a documented-but-unimplemented route
          and an undocumented endpoint both fail);
      7.  every roles cell in csv/api_routes.csv uses only known roles/descriptors,
          and the unauthenticated (provider-callback) routes documented there are
          exactly the AllowAnonymous endpoints in code (route roles/auth);
      8.  csv/entitlement_event_catalog.csv is well-formed and internally
          consistent (a row with no audit action, or audited-but-not-persisted,
          fails) - the AuditAction backing itself is CORE-SPEC-002;
      9.  csv/mobile_store_api_routes.csv mirrors real /api/v1 routes (path,
          method and auth_required) and matches the in-process MobileApiGateway
          route table (the mobile store CSV);
      10. csv/database_tables.csv equals the EF model snapshot's tables in both
          directions, and the security/idempotency-critical UNIQUE indexes the
          spec promises are actually declared in the snapshot (table
          columns/indexes).

    CORE-EVT-004 adds one more semantic invariant, binding the session-event
    catalog to the implementation (the session-event analogue of CORE-SPEC-002):

      11. the EMITTED session-event set (the SessionEventTypes constants in
          apps/api/Realtime/SessionEventTypes.cs) equals the NON-deferred
          csv/event_catalog.csv events, in both directions, so the catalog can no
          longer list a session event that no command emits (a DEFERRED-marked
          catalog row is excluded).

    The single source of truth per concern is docs/24_SPEC_CONSISTENCY.md.
    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (pwsh).
#>

$ErrorActionPreference = 'Stop'

# --- Shared helpers (ported verbatim from the original spec-consistency.ps1) ---

function Get-LiveCoreSpecFile {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$RelativePath
    )
    $full = Join-Path -Path $RepoRoot -ChildPath $RelativePath
    if (-not (Test-Path -Path $full)) {
        throw "Spec file not found: $RelativePath"
    }
    return $full
}

function Get-LiveCoreFencedBlock {
    # Returns the non-empty, trimmed lines of the first ```fence block that
    # follows the given exact heading line, or throws on a missing/unterminated
    # block (a configuration error).
    param(
        [string[]]$Line,
        [Parameter(Mandatory)][string]$Heading,
        [Parameter(Mandatory)][string]$Source
    )
    $fence = '```'
    $start = -1
    for ($i = 0; $i -lt $Line.Count; $i++) {
        if ($Line[$i].Trim() -eq $Heading) { $start = $i; break }
    }
    if ($start -lt 0) {
        throw "Heading '$Heading' not found in $Source"
    }
    $open = -1
    for ($i = $start + 1; $i -lt $Line.Count; $i++) {
        if ($Line[$i].TrimStart().StartsWith($fence)) { $open = $i; break }
    }
    if ($open -lt 0) {
        throw "No fenced block found after '$Heading' in $Source"
    }
    $content = New-Object System.Collections.Generic.List[string]
    for ($i = $open + 1; $i -lt $Line.Count; $i++) {
        if ($Line[$i].TrimStart().StartsWith($fence)) {
            return , $content.ToArray()
        }
        $value = $Line[$i].Trim()
        if ($value -ne '') { $content.Add($value) }
    }
    throw "Unterminated fenced block after '$Heading' in $Source"
}

function Get-LiveCoreStringSet {
    param([string[]]$Value)
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    if ($Value) {
        foreach ($v in $Value) { [void]$set.Add($v) }
    }
    return , $set
}

# --- Route normalization ---

function ConvertTo-LiveCoreRouteKey {
    # The semantic identity of a route is its method plus its path SHAPE: two
    # path-parameter names ({sessionId} vs {id}) describe the same route, so they
    # are normalized to a placeholder before comparison.
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path
    )
    $normalized = [regex]::Replace($Path, '\{[^}]*\}', '{}')
    return ('{0} {1}' -f $Method.ToUpperInvariant(), $normalized)
}

function Join-LiveCoreRoutePath {
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [string]$Relative
    )
    $path = $Prefix.TrimEnd('/')
    if ($Relative -and $Relative -ne '/') {
        if (-not $Relative.StartsWith('/')) { $Relative = '/' + $Relative }
        $path += $Relative
    }
    if ($path -eq '') { $path = '/' }
    return $path
}

# --- Code-side extraction (reuse the minimal-API registrations + EF snapshot) ---

function Get-LiveCoreImplementedRoute {
    # Reconstructs the mounted /api/v1 routes from the minimal-API registrations:
    # each endpoint file declares `var g = endpoints.MapGroup("<prefix>")...;`
    # (optionally `.AllowAnonymous()`), then `g.Map{Get|Post|Put|Delete}("<rel>",
    # handler)`. The full route is prefix + rel; only /api/v1 routes are returned.
    param([Parameter(Mandatory)][string]$RepoRoot)

    $apiDir = Join-Path -Path $RepoRoot -ChildPath 'apps/api'
    if (-not (Test-Path -Path $apiDir)) {
        throw "API source directory not found: apps/api"
    }

    $routes = New-Object System.Collections.Generic.List[object]
    $files = Get-ChildItem -Path $apiDir -Recurse -Filter '*.cs' -File

    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ([string]::IsNullOrEmpty($text)) { continue }
        if ($text.IndexOf('MapGroup(') -lt 0) { continue }

        # Group definitions, captured as the whole `var NAME = ... .MapGroup("P") ... ;`
        # statement so a chained .AllowAnonymous() in the same statement is seen.
        $groups = @{}
        $groupPattern = 'var\s+(?<name>\w+)\s*=\s*(?<body>[^;]*?\.MapGroup\(\s*"(?<prefix>[^"]+)"\s*\)[^;]*);'
        foreach ($g in [regex]::Matches($text, $groupPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
            $name = $g.Groups['name'].Value
            $isAnon = $g.Groups['body'].Value -match 'AllowAnonymous\s*\('
            $groups[$name] = [pscustomobject]@{
                Prefix      = $g.Groups['prefix'].Value
                IsAnonymous = [bool]$isAnon
            }
        }
        if ($groups.Count -eq 0) { continue }

        # Verb calls. Whitespace (including newlines) may sit between Map{Verb}(
        # and the route literal, so the multi-line MapDelete(...) form is matched.
        $verbPattern = '(?<recv>\w+)\s*\.\s*Map(?<verb>Get|Post|Put|Delete)\(\s*"(?<rel>[^"]*)"'
        foreach ($v in [regex]::Matches($text, $verbPattern)) {
            $recv = $v.Groups['recv'].Value
            if (-not $groups.ContainsKey($recv)) { continue }
            $group = $groups[$recv]
            $method = $v.Groups['verb'].Value.ToUpperInvariant()
            $full = Join-LiveCoreRoutePath -Prefix $group.Prefix -Relative $v.Groups['rel'].Value
            if (-not $full.StartsWith('/api/v1')) { continue }
            $routes.Add([pscustomobject]@{
                    Method      = $method
                    Path        = $full
                    Key         = (ConvertTo-LiveCoreRouteKey -Method $method -Path $full)
                    IsAnonymous = $group.IsAnonymous
                    Source      = $file.Name
                })
        }
    }
    return $routes.ToArray()
}

function Get-LiveCoreDocumentedRoute {
    # The mounted /api/v1 routes per csv/api_routes.csv (the single source of truth
    # for routes). IsAnonymousDoc marks a provider-callback route (roles "none ...").
    param([Parameter(Mandatory)][string]$RepoRoot)

    $csv = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'csv/api_routes.csv'
    $routes = New-Object System.Collections.Generic.List[object]
    foreach ($row in (Import-Csv -Path $csv)) {
        $method = ([string]$row.method).Trim()
        $route = ([string]$row.route).Trim()
        $roles = ([string]$row.roles).Trim()
        $routes.Add([pscustomobject]@{
                Method         = $method.ToUpperInvariant()
                Route          = $route
                Key            = (ConvertTo-LiveCoreRouteKey -Method $method -Path $route)
                Roles          = $roles
                IsAnonymousDoc = ($roles -match '^\s*none\b')
            })
    }
    return $routes.ToArray()
}

function Get-LiveCoreMembershipRole {
    # The generic role vocabulary, parsed from the MembershipRole enum so a rename
    # in code is reflected without editing this script.
    param([Parameter(Mandatory)][string]$RepoRoot)

    $file = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'apps/api/Organizations/MembershipRole.cs'
    $text = Get-Content -LiteralPath $file -Raw
    $roles = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($text, '(?m)^\s*(?<name>[A-Z][A-Za-z0-9]*)\s*=\s*\d+\s*,')) {
        $roles.Add($m.Groups['name'].Value)
    }
    return $roles.ToArray()
}

function Get-LiveCoreAuditAction {
    # The Core audit-action vocabulary, parsed from the AuditAction enum so a rename/addition in code is reflected
    # without editing this script (mirrors Get-LiveCoreMembershipRole). These are the real AuditAction values the
    # entitlement/store event catalog is bound to (CORE-SPEC-002).
    param([Parameter(Mandatory)][string]$RepoRoot)

    $file = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'apps/api/Audit/AuditAction.cs'
    $text = Get-Content -LiteralPath $file -Raw
    $actions = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($text, '(?m)^\s*(?<name>[A-Z][A-Za-z0-9]*)\s*=\s*\d+\s*,')) {
        $actions.Add($m.Groups['name'].Value)
    }
    return $actions.ToArray()
}

function Get-LiveCoreMobileRoute {
    # The mobile-facing store/entitlement routes (csv/mobile_store_api_routes.csv),
    # a mirror of the /api/v1 routes under a bare /v1 prefix.
    param([Parameter(Mandatory)][string]$RepoRoot)

    $csv = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'csv/mobile_store_api_routes.csv'
    $routes = New-Object System.Collections.Generic.List[object]
    foreach ($row in (Import-Csv -Path $csv)) {
        $routes.Add([pscustomobject]@{
                Method       = ([string]$row.method).Trim().ToUpperInvariant()
                Path         = ([string]$row.path).Trim()
                Owner        = ([string]$row.owner).Trim()
                AuthRequired = ([string]$row.auth_required).Trim()
            })
    }
    return $routes.ToArray()
}

function Get-LiveCoreGatewayMobileRoute {
    # The in-code mirror of the mobile route shapes: the _mobileRouteTemplates
    # array in MobileApiGateway.cs, reconstructed as normalized /v1 paths.
    param([Parameter(Mandatory)][string]$RepoRoot)

    $file = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'apps/api/Hosting/MobileApiGateway.cs'
    $text = Get-Content -LiteralPath $file -Raw
    $block = [regex]::Match($text, '_mobileRouteTemplates\s*=\s*\[(?<body>.*?)\];', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $block.Success) {
        throw "Could not locate _mobileRouteTemplates in apps/api/Hosting/MobileApiGateway.cs"
    }
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($rowMatch in [regex]::Matches($block.Groups['body'].Value, '\[(?<row>[^\]]*)\]')) {
        $segments = New-Object System.Collections.Generic.List[string]
        foreach ($seg in [regex]::Matches($rowMatch.Groups['row'].Value, '"([^"]+)"')) {
            $segments.Add($seg.Groups[1].Value)
        }
        if ($segments.Count -gt 0) {
            $path = '/' + ($segments -join '/')
            $paths.Add([regex]::Replace($path, '\{[^}]*\}', '{}'))
        }
    }
    return $paths.ToArray()
}

function Get-LiveCoreSnapshotTable {
    # The tables the EF Core model snapshot actually maps (b.ToTable("name")).
    param([Parameter(Mandatory)][string]$RepoRoot)

    $file = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'apps/api/Persistence/Migrations/LiveCoreDbContextModelSnapshot.cs'
    $text = Get-Content -LiteralPath $file -Raw
    $tables = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($text, '\.ToTable\(\s*"(?<t>[^"]+)"')) {
        $tables.Add($m.Groups['t'].Value)
    }
    return $tables.ToArray()
}

function Get-LiveCoreSnapshotText {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $file = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'apps/api/Persistence/Migrations/LiveCoreDbContextModelSnapshot.cs'
    return (Get-Content -LiteralPath $file -Raw)
}

function Get-LiveCoreEntitlementEvent {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $csv = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'csv/entitlement_event_catalog.csv'
    return @(Import-Csv -Path $csv)
}

function Get-LiveCoreSessionEventType {
    # The EMITTED session-event vocabulary (CORE-EVT-004), parsed from the SessionEventTypes catalog so a
    # rename/addition in code is reflected without editing this script (mirrors Get-LiveCoreAuditAction). These
    # are the product-neutral event-type NAMES (the `public const string` values) the Realtime publisher
    # actually emits; the host-only routing set is a `static readonly` field and so is intentionally not matched.
    param([Parameter(Mandatory)][string]$RepoRoot)

    $file = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'apps/api/Realtime/SessionEventTypes.cs'
    $text = Get-Content -LiteralPath $file -Raw
    $types = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($text, 'public\s+const\s+string\s+\w+\s*=\s*"(?<value>[^"]+)"')) {
        $types.Add($m.Groups['value'].Value)
    }
    return $types.ToArray()
}

function Get-LiveCoreCatalogEvent {
    # The session-event catalog rows (csv/event_catalog.csv, the single source of truth for the session-event
    # vocabulary), each with its event name and whether its notes mark it DEFERRED. A DEFERRED row is a
    # documented-but-unemitted event with a named owner (today the workspace-prepared SceneCreated/
    # ContentBlockCreated), excluded from the emitted-set binding exactly as check 3 excludes DEFERRED
    # entitlement tables.
    param([Parameter(Mandatory)][string]$RepoRoot)

    $csv = Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'csv/event_catalog.csv'
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($row in (Import-Csv -Path $csv)) {
        $notes = if ($null -ne $row.notes) { [string]$row.notes } else { '' }
        $rows.Add([pscustomobject]@{
                Event      = ([string]$row.event).Trim()
                IsDeferred = ($notes -match 'DEFERRED')
            })
    }
    return $rows.ToArray()
}

# --- Semantic checks (each returns a string[] of drift findings) ---

function Test-LiveCoreRouteImplementation {
    # CSV routes vs implemented endpoints, BOTH directions.
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Documented,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Implemented
    )
    $findings = New-Object System.Collections.Generic.List[string]

    $docKeys = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($d in $Documented) { [void]$docKeys.Add($d.Key) }

    $implKeys = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($i in $Implemented) {
        [void]$implKeys.Add($i.Key)
        if (-not $docKeys.Contains($i.Key)) {
            $findings.Add("ENDPOINT: '$($i.Key)' is implemented ($($i.Source)) but is not documented in csv/api_routes.csv")
        }
    }
    foreach ($d in $Documented) {
        if (-not $implKeys.Contains($d.Key)) {
            $findings.Add("ROUTE-IMPL: csv/api_routes.csv documents '$($d.Key)' which no minimal-API endpoint mounts")
        }
    }
    return $findings.ToArray()
}

function Test-LiveCoreRouteRole {
    # Route roles/auth: roles-vocabulary validity, plus the documented
    # unauthenticated (provider-callback) routes must equal the AllowAnonymous
    # endpoints in code (both directions).
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Documented,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Implemented,
        [Parameter(Mandatory)][string[]]$KnownRole,
        [Parameter(Mandatory)][string[]]$KnownDescriptor
    )
    $findings = New-Object System.Collections.Generic.List[string]

    $roleSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($r in $KnownRole) { [void]$roleSet.Add($r) }
    $descriptorSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($d in $KnownDescriptor) { [void]$descriptorSet.Add($d) }

    foreach ($route in $Documented) {
        if ([string]::IsNullOrWhiteSpace($route.Roles)) {
            $findings.Add("ROLE: route '$($route.Key)' has an empty roles cell in csv/api_routes.csv")
            continue
        }
        foreach ($token in ($route.Roles -split ',')) {
            $t = $token.Trim()
            if ($t -eq '') { continue }
            if ($t -cmatch '^[A-Z][A-Za-z]+$') {
                # Looks like a role name (Capitalized, no spaces): it must be a real MembershipRole.
                if (-not $roleSet.Contains($t)) {
                    $findings.Add("ROLE: route '$($route.Key)' references unknown role '$t' (not a MembershipRole)")
                }
            }
            elseif (-not $descriptorSet.Contains($t)) {
                $findings.Add("ROLE: route '$($route.Key)' has unrecognized authorization descriptor '$t'")
            }
        }
    }

    # Auth posture must match the implementation, both directions.
    $docAnon = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($route in $Documented) {
        if ($route.IsAnonymousDoc) { [void]$docAnon.Add($route.Key) }
    }
    $codeAnon = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($route in $Implemented) {
        if ($route.IsAnonymous) { [void]$codeAnon.Add($route.Key) }
    }
    foreach ($key in $docAnon) {
        if (-not $codeAnon.Contains($key)) {
            $findings.Add("AUTH: '$key' is documented as an unauthenticated provider callback but the implementation is not AllowAnonymous")
        }
    }
    foreach ($key in $codeAnon) {
        if (-not $docAnon.Contains($key)) {
            $findings.Add("AUTH: '$key' is implemented AllowAnonymous but csv/api_routes.csv does not document it as a provider callback")
        }
    }
    return $findings.ToArray()
}

function Test-LiveCoreEntitlementEventCatalog {
    # The store/entitlement domain event catalog must be well-formed and
    # internally consistent. A row with no audit action (a blank/invalid audit
    # column), or one audited-but-not-persisted, is drift.
    #
    # CORE-SPEC-002 BINDS the catalog to the real AuditAction enum, so the catalog
    # is a CONTRACT, not aspirational: for every catalog event, audit=true IFF a
    # matching AuditAction enum member exists. So a catalog event claiming
    # audit=true with NO backing AuditAction fails, AND an AuditAction added for a
    # catalog event whose row is still audit=false fails (the catalog must mark it
    # audited). $AuditAction is the parsed AuditAction vocabulary; pass an empty
    # set to skip the binding (the pre-CORE-SPEC-002 well-formedness-only check).
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Row,
        [AllowEmptyCollection()][string[]]$AuditAction = @()
    )
    $findings = New-Object System.Collections.Generic.List[string]

    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    $bool = @('true', 'false')

    $actionSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($a in $AuditAction) { [void]$actionSet.Add($a) }
    $bindActions = ($AuditAction.Count -gt 0)

    foreach ($r in $Row) {
        $name = if ($null -ne $r.event_name) { ([string]$r.event_name).Trim() } else { '' }
        $persisted = if ($null -ne $r.persisted) { ([string]$r.persisted).Trim().ToLowerInvariant() } else { '' }
        $audit = if ($null -ne $r.audit) { ([string]$r.audit).Trim().ToLowerInvariant() } else { '' }
        $emitter = if ($null -ne $r.emitter) { ([string]$r.emitter).Trim() } else { '' }
        $recipients = if ($null -ne $r.recipients) { ([string]$r.recipients).Trim() } else { '' }

        $label = if ($name -ne '') { $name } else { '(blank)' }

        if ($name -eq '' -or $name -cnotmatch '^[A-Z][A-Za-z]+$') {
            $findings.Add("ENTL-EVENT: '$label' is not a valid generic event name in csv/entitlement_event_catalog.csv")
        }
        elseif (-not $seen.Add($name)) {
            $findings.Add("ENTL-EVENT: event '$name' is listed more than once in csv/entitlement_event_catalog.csv")
        }
        if ($emitter -eq '') {
            $findings.Add("ENTL-EVENT: event '$label' has no emitter")
        }
        if ($recipients -eq '') {
            $findings.Add("ENTL-EVENT: event '$label' has no recipients")
        }
        if ($bool -notcontains $persisted) {
            $findings.Add("ENTL-EVENT: event '$label' has an invalid persisted value '$persisted' (expected true/false)")
        }
        if ($bool -notcontains $audit) {
            $findings.Add("ENTL-EVENT: event '$label' has no audit action (the audit column must be true or false)")
        }
        elseif ($audit -eq 'true' -and $persisted -ne 'true') {
            $findings.Add("ENTL-EVENT: event '$label' is marked audited but not persisted (an audit fact must be persisted)")
        }

        # CORE-SPEC-002: bind the catalog to the real AuditAction enum so it is a contract, not aspirational.
        # For a well-formed event name, audit=true IFF a matching AuditAction member exists.
        if ($bindActions -and $name -ne '' -and $name -cmatch '^[A-Z][A-Za-z]+$' -and ($bool -contains $audit)) {
            $hasAction = $actionSet.Contains($name)
            if ($audit -eq 'true' -and -not $hasAction) {
                $findings.Add("ENTL-EVENT: event '$name' is marked audit=true but no AuditAction.$name backs it (the catalog is aspirational; add the AuditAction or set audit=false) - CORE-SPEC-002")
            }
            elseif ($audit -eq 'false' -and $hasAction) {
                $findings.Add("ENTL-EVENT: AuditAction.$name exists but event '$name' is audit=false (a backed event must be marked audited) - CORE-SPEC-002")
            }
        }
    }
    return $findings.ToArray()
}

function Test-LiveCoreMobileStoreRoute {
    # The mobile store CSV must mirror real /api/v1 routes (path, method,
    # auth_required) and match the in-process gateway route table.
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Mobile,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Documented,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$GatewayPath
    )
    $findings = New-Object System.Collections.Generic.List[string]

    # Documented /api/v1 routes indexed by normalized key.
    $docByKey = @{}
    foreach ($d in $Documented) { $docByKey[$d.Key] = $d }

    $mobilePaths = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($m in $Mobile) {
        $normPath = [regex]::Replace($m.Path, '\{[^}]*\}', '{}')
        [void]$mobilePaths.Add($normPath)

        if ($m.Owner -ne 'Core') {
            $findings.Add("MOBILE: '$($m.Path)' has owner '$($m.Owner)' but Core owns the mobile store/entitlement surface")
        }
        $auth = $m.AuthRequired.ToLowerInvariant()
        if (@('true', 'false') -notcontains $auth) {
            $findings.Add("MOBILE: '$($m.Path)' has an invalid auth_required value '$($m.AuthRequired)'")
            continue
        }

        # Map the mobile /v1/... shape to its /api/v1/... route and require it.
        $apiKey = (ConvertTo-LiveCoreRouteKey -Method $m.Method -Path ('/api' + $normPath))
        if (-not $docByKey.ContainsKey($apiKey)) {
            $findings.Add("MOBILE: '$($m.Method) $($m.Path)' maps to '$apiKey' which is not a documented /api/v1 route")
            continue
        }
        $docRoute = $docByKey[$apiKey]
        $docAnon = [bool]$docRoute.IsAnonymousDoc
        $mobileAnon = ($auth -eq 'false')
        if ($docAnon -ne $mobileAnon) {
            $findings.Add("MOBILE: auth_required=$auth for '$($m.Path)' disagrees with the route's documented authentication ('$($docRoute.Roles)')")
        }
    }

    # The gateway route table is the in-code mirror of the mobile CSV; require equality.
    $gatewaySet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($g in $GatewayPath) { [void]$gatewaySet.Add($g) }
    foreach ($p in $mobilePaths) {
        if (-not $gatewaySet.Contains($p)) {
            $findings.Add("MOBILE: '$p' is in csv/mobile_store_api_routes.csv but not in the in-process gateway route table (MobileApiGateway)")
        }
    }
    foreach ($g in $gatewaySet) {
        if (-not $mobilePaths.Contains($g)) {
            $findings.Add("MOBILE: gateway route '$g' is not documented in csv/mobile_store_api_routes.csv")
        }
    }
    return $findings.ToArray()
}

function Test-LiveCoreUniqueIndex {
    # True when the EF model snapshot declares a UNIQUE index on $Table over
    # exactly $Column (EF property names), within that table's entity block.
    param(
        [Parameter(Mandatory)][string]$SnapshotText,
        [Parameter(Mandatory)][string]$Table,
        [Parameter(Mandatory)][string[]]$Column
    )
    # The entity block for a table runs up to its b.ToTable("name"); the unique
    # HasIndex(...) declarations precede it, so scope to that region.
    $markers = [regex]::Matches($SnapshotText, '\.ToTable\(\s*"([^"]+)"')
    $region = $null
    for ($i = 0; $i -lt $markers.Count; $i++) {
        if ($markers[$i].Groups[1].Value -eq $Table) {
            $start = if ($i -eq 0) { 0 } else { $markers[$i - 1].Index + $markers[$i - 1].Length }
            $length = $markers[$i].Index - $start
            $region = $SnapshotText.Substring($start, $length)
            break
        }
    }
    if ($null -eq $region) { return $false }

    $columnPattern = ($Column | ForEach-Object { '"' + [regex]::Escape($_) + '"' }) -join '\s*,\s*'
    $pattern = 'HasIndex\(\s*' + $columnPattern + '\s*\)\s*\.IsUnique\(\)'
    return [bool]([regex]::IsMatch($region, $pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline))
}

function Test-LiveCoreTableSchema {
    # Table columns/indexes: csv/database_tables.csv must equal the EF model
    # snapshot's tables (both directions), and the promised UNIQUE indexes must be
    # declared in the snapshot.
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$CsvTable,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$SnapshotTable,
        [Parameter(Mandatory)][string]$SnapshotText,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$RequiredUniqueIndex
    )
    $findings = New-Object System.Collections.Generic.List[string]

    $csvSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($t in $CsvTable) { [void]$csvSet.Add($t) }
    $snapSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($t in $SnapshotTable) { [void]$snapSet.Add($t) }

    foreach ($t in $csvSet) {
        if (-not $snapSet.Contains($t)) {
            $findings.Add("SCHEMA: csv/database_tables.csv lists '$t' which is not a table in the EF model snapshot")
        }
    }
    foreach ($t in $snapSet) {
        if (-not $csvSet.Contains($t)) {
            $findings.Add("SCHEMA: the EF model snapshot maps table '$t' which is missing from csv/database_tables.csv")
        }
    }

    foreach ($index in $RequiredUniqueIndex) {
        if (-not (Test-LiveCoreUniqueIndex -SnapshotText $SnapshotText -Table $index.Table -Column $index.Column)) {
            $cols = $index.Column -join ', '
            $findings.Add("INDEX: the EF model snapshot does not declare a UNIQUE index on $($index.Table)($cols) ($($index.Why))")
        }
    }
    return $findings.ToArray()
}

function Test-LiveCoreSessionEventEmission {
    # CORE-EVT-004: the catalog is a CONTRACT, not aspirational. The EMITTED session-event set (the
    # SessionEventTypes constants) must EQUAL the NON-deferred catalog (csv/event_catalog.csv), in BOTH
    # directions: a non-deferred catalog event that no command emits (no SessionEventTypes member), and a
    # SessionEventTypes constant that is not a non-deferred catalog row, both fail. A DEFERRED-marked catalog
    # row is a documented-but-unemitted event with a named owner and is excluded from the comparison (the
    # session-event analogue of CORE-SPEC-002's AuditAction binding, and of check 3's DEFERRED-table handling).
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$CatalogRow,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$EmittedType
    )
    $findings = New-Object System.Collections.Generic.List[string]

    $emitted = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($t in $EmittedType) { [void]$emitted.Add($t) }

    $nonDeferred = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($r in $CatalogRow) {
        if (-not $r.IsDeferred) { [void]$nonDeferred.Add($r.Event) }
    }

    foreach ($event in $nonDeferred) {
        if (-not $emitted.Contains($event)) {
            $findings.Add("EVENT-EMIT: csv/event_catalog.csv lists non-deferred event '$event' which no Core command emits (no SessionEventTypes member); emit it, remove it, or mark it DEFERRED - CORE-EVT-004")
        }
    }
    foreach ($type in $emitted) {
        if (-not $nonDeferred.Contains($type)) {
            $findings.Add("EVENT-EMIT: SessionEventTypes emits '$type' but it is not a non-deferred event in csv/event_catalog.csv - CORE-EVT-004")
        }
    }
    return $findings.ToArray()
}

# --- Shared constants for the new checks ---

function Get-LiveCoreKnownRoleDescriptor {
    # Authorization descriptors used in csv/api_routes.csv that are NOT single
    # MembershipRole names (audience phrases / the provider-callback sentinel).
    return @(
        'authenticated',
        'authenticated user',
        'authorized viewers',
        'workspace members',
        'session audience',
        'Participant owner or Host',
        'none (provider callback)'
    )
}

function Get-LiveCoreRequiredUniqueIndex {
    # The security / idempotency / tenant-isolation UNIQUE indexes the spec
    # promises (docs/24_SPEC_CONSISTENCY.md, csv/database_tables.csv notes),
    # expressed in EF property names as declared in the model snapshot.
    return @(
        [pscustomobject]@{ Table = 'purchase_transactions'; Column = @('Provider', 'ProviderTransactionId'); Why = 'idempotent purchase recording' }
        [pscustomobject]@{ Table = 'store_notification_events'; Column = @('Provider', 'ProviderNotificationId'); Why = 'idempotent store-notification ledger' }
        [pscustomobject]@{ Table = 'billing_account_links'; Column = @('PurchaseTransactionId'); Why = 'one buyer subject per receipt' }
        [pscustomobject]@{ Table = 'subject_entitlements'; Column = @('SubjectType', 'SubjectId', 'EntitlementDefinitionId'); Why = 'one assignment per subject+entitlement' }
        [pscustomobject]@{ Table = 'quota_usage'; Column = @('SubjectType', 'SubjectId', 'QuotaDefinitionId'); Why = 'one usage row per subject+quota' }
        [pscustomobject]@{ Table = 'users'; Column = @('Issuer', 'SubjectId'); Why = 'one user per OIDC issuer+subject' }
        [pscustomobject]@{ Table = 'organizations'; Column = @('Slug'); Why = 'unique tenant slug' }
        [pscustomobject]@{ Table = 'workspaces'; Column = @('OrganizationId', 'Slug'); Why = 'unique workspace slug per tenant' }
        [pscustomobject]@{ Table = 'organization_members'; Column = @('OrganizationId', 'UserProfileId'); Why = 'one membership per tenant+user' }
        [pscustomobject]@{ Table = 'workspace_members'; Column = @('WorkspaceId', 'UserProfileId'); Why = 'one membership per workspace+user' }
        [pscustomobject]@{ Table = 'audit_logs'; Column = @('OrganizationId', 'Sequence'); Why = 'gap-free per-tenant audit hash chain' }
        [pscustomobject]@{ Table = 'session_events'; Column = @('SessionId', 'Sequence'); Why = 'gap-free per-session event sequence' }
    )
}

# --- Orchestrator ---

function Invoke-LiveCoreSpecConsistency {
    <#
    .SYNOPSIS
        Runs every spec-consistency invariant against a repository tree and
        returns @{ Findings = [string[]]; CheckCount = [int] }. Throws on a
        configuration error (a spec file it cannot find or parse).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot)

    $RepoRoot = (Resolve-Path -Path $RepoRoot).Path
    $findings = New-Object System.Collections.Generic.List[string]
    $checkCount = 0

    # --- Check 1: docs/08 representative routes are a subset of api_routes.csv ---
    $checkCount++
    $routeSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($row in (Import-Csv -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'csv/api_routes.csv'))) {
        [void]$routeSet.Add(('{0} {1}' -f $row.method.Trim().ToUpperInvariant(), $row.route.Trim()))
    }
    $apiDocLine = @(Get-Content -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'docs/08_API_CONTRACTS.md'))
    $routeBlock = Get-LiveCoreFencedBlock -Line $apiDocLine -Heading '## Core endpoints' -Source 'docs/08_API_CONTRACTS.md'
    foreach ($entry in $routeBlock) {
        $parts = $entry -split '\s+'
        if ($parts.Count -lt 2) { continue }
        $key = '{0} {1}' -f $parts[0].ToUpperInvariant(), $parts[1]
        if (-not $routeSet.Contains($key)) {
            $findings.Add("ROUTE: docs/08 lists '$entry' which is not a row in csv/api_routes.csv")
        }
    }

    # --- Check 2: docs/10 table list equals database_tables.csv ---
    $checkCount++
    $schemaTableSet = Get-LiveCoreStringSet -Value (@(Import-Csv -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'csv/database_tables.csv')) | ForEach-Object { $_.table.Trim() })
    $schemaDocLine = @(Get-Content -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'docs/10_DATABASE_SCHEMA.md'))
    $docTableList = Get-LiveCoreFencedBlock -Line $schemaDocLine -Heading '## Core tables' -Source 'docs/10_DATABASE_SCHEMA.md'
    $docTableSet = Get-LiveCoreStringSet -Value $docTableList
    foreach ($t in $docTableList) {
        if (-not $schemaTableSet.Contains($t)) {
            $findings.Add("TABLE: docs/10 lists '$t' which is not in csv/database_tables.csv")
        }
    }
    foreach ($t in $schemaTableSet) {
        if (-not $docTableSet.Contains($t)) {
            $findings.Add("TABLE: csv/database_tables.csv has '$t' which is missing from the docs/10 table list")
        }
    }

    # --- Check 3: non-deferred entitlement tables exist in the schema ---
    $checkCount++
    foreach ($row in (Import-Csv -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'csv/entitlement_database_tables.csv'))) {
        $notes = if ($null -ne $row.notes) { [string]$row.notes } else { '' }
        if ($notes -match 'DEFERRED') { continue }
        $t = $row.table.Trim()
        if (-not $schemaTableSet.Contains($t)) {
            $findings.Add("TABLE: csv/entitlement_database_tables.csv lists non-deferred table '$t' which is not in csv/database_tables.csv")
        }
    }

    # --- Check 4: docs/09 event table equals event_catalog.csv ---
    $checkCount++
    $csvEventSet = Get-LiveCoreStringSet -Value (@(Import-Csv -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'csv/event_catalog.csv')) | ForEach-Object { $_.event.Trim() })
    $eventDocLine = @(Get-Content -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'docs/09_EVENT_CATALOG.md'))
    $docEventValue = New-Object System.Collections.Generic.List[string]
    foreach ($line in $eventDocLine) {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith('|')) { continue }
        $firstCell = ($trimmed.Trim('|') -split '\|')[0].Trim()
        if ($firstCell -eq 'Event') { continue }
        if ($firstCell -match '^[A-Z][A-Za-z]+$') { $docEventValue.Add($firstCell) }
    }
    $docEventSet = Get-LiveCoreStringSet -Value $docEventValue.ToArray()
    foreach ($e in $docEventSet) {
        if (-not $csvEventSet.Contains($e)) {
            $findings.Add("EVENT: docs/09 lists '$e' which is not in csv/event_catalog.csv")
        }
    }
    foreach ($e in $csvEventSet) {
        if (-not $docEventSet.Contains($e)) {
            $findings.Add("EVENT: csv/event_catalog.csv has '$e' which is missing from the docs/09 event table")
        }
    }

    # --- Check 5: docs/18 epics equal the union of both epic CSVs ---
    $checkCount++
    $epicValue = New-Object System.Collections.Generic.List[string]
    foreach ($epicCsv in @('csv/core_epics_stories.csv', 'csv/core_phase2_epics_stories.csv')) {
        foreach ($row in (Import-Csv -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath $epicCsv))) {
            $epicValue.Add($row.epic.Trim())
        }
    }
    $csvEpicSet = Get-LiveCoreStringSet -Value $epicValue.ToArray()
    $epicDocLine = @(Get-Content -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'docs/18_EPICS_AND_STORIES.md'))
    $docEpicValue = New-Object System.Collections.Generic.List[string]
    foreach ($line in $epicDocLine) {
        if ($line -match '^\s*\d+\.\s+(.+?)\s*$') { $docEpicValue.Add($Matches[1].Trim()) }
    }
    $docEpicSet = Get-LiveCoreStringSet -Value $docEpicValue.ToArray()
    foreach ($epic in $docEpicSet) {
        if (-not $csvEpicSet.Contains($epic)) {
            $findings.Add("EPIC: docs/18 lists '$epic' which is not an epic in either epic CSV")
        }
    }
    foreach ($epic in $csvEpicSet) {
        if (-not $docEpicSet.Contains($epic)) {
            $findings.Add("EPIC: epic '$epic' is in an epic CSV but missing from docs/18")
        }
    }

    # --- Check 6: csv/api_routes.csv vs the implemented endpoints (both ways) ---
    $checkCount++
    $documented = @(Get-LiveCoreDocumentedRoute -RepoRoot $RepoRoot)
    $implemented = @(Get-LiveCoreImplementedRoute -RepoRoot $RepoRoot)
    foreach ($f in (Test-LiveCoreRouteImplementation -Documented $documented -Implemented $implemented)) { $findings.Add($f) }

    # --- Check 7: route roles/auth ---
    $checkCount++
    $knownRole = @(Get-LiveCoreMembershipRole -RepoRoot $RepoRoot)
    $knownDescriptor = @(Get-LiveCoreKnownRoleDescriptor)
    foreach ($f in (Test-LiveCoreRouteRole -Documented $documented -Implemented $implemented -KnownRole $knownRole -KnownDescriptor $knownDescriptor)) { $findings.Add($f) }

    # --- Check 8: entitlement/store event catalog well-formedness + AuditAction binding (CORE-SPEC-002) ---
    $checkCount++
    $entitlementEvents = @(Get-LiveCoreEntitlementEvent -RepoRoot $RepoRoot)
    $auditActions = @(Get-LiveCoreAuditAction -RepoRoot $RepoRoot)
    foreach ($f in (Test-LiveCoreEntitlementEventCatalog -Row $entitlementEvents -AuditAction $auditActions)) { $findings.Add($f) }

    # --- Check 9: mobile store CSV mirrors the routes + gateway table ---
    $checkCount++
    $mobile = @(Get-LiveCoreMobileRoute -RepoRoot $RepoRoot)
    $gatewayPaths = @(Get-LiveCoreGatewayMobileRoute -RepoRoot $RepoRoot)
    foreach ($f in (Test-LiveCoreMobileStoreRoute -Mobile $mobile -Documented $documented -GatewayPath $gatewayPaths)) { $findings.Add($f) }

    # --- Check 10: table columns/indexes vs the EF model snapshot ---
    $checkCount++
    $csvTables = @(Import-Csv -Path (Get-LiveCoreSpecFile -RepoRoot $RepoRoot -RelativePath 'csv/database_tables.csv') | ForEach-Object { $_.table.Trim() })
    $snapshotTables = @(Get-LiveCoreSnapshotTable -RepoRoot $RepoRoot)
    $snapshotText = Get-LiveCoreSnapshotText -RepoRoot $RepoRoot
    $requiredIndexes = @(Get-LiveCoreRequiredUniqueIndex)
    foreach ($f in (Test-LiveCoreTableSchema -CsvTable $csvTables -SnapshotTable $snapshotTables -SnapshotText $snapshotText -RequiredUniqueIndex $requiredIndexes)) { $findings.Add($f) }

    # --- Check 11: emitted session events vs the non-deferred catalog (CORE-EVT-004) ---
    $checkCount++
    $catalogEvents = @(Get-LiveCoreCatalogEvent -RepoRoot $RepoRoot)
    $emittedTypes = @(Get-LiveCoreSessionEventType -RepoRoot $RepoRoot)
    foreach ($f in (Test-LiveCoreSessionEventEmission -CatalogRow $catalogEvents -EmittedType $emittedTypes)) { $findings.Add($f) }

    return @{
        Findings   = $findings.ToArray()
        CheckCount = $checkCount
    }
}

Export-ModuleMember -Function @(
    'Get-LiveCoreImplementedRoute',
    'Get-LiveCoreDocumentedRoute',
    'Get-LiveCoreMembershipRole',
    'Get-LiveCoreAuditAction',
    'Get-LiveCoreMobileRoute',
    'Get-LiveCoreGatewayMobileRoute',
    'Get-LiveCoreSnapshotTable',
    'Get-LiveCoreSnapshotText',
    'Get-LiveCoreEntitlementEvent',
    'Get-LiveCoreSessionEventType',
    'Get-LiveCoreCatalogEvent',
    'Get-LiveCoreKnownRoleDescriptor',
    'Get-LiveCoreRequiredUniqueIndex',
    'ConvertTo-LiveCoreRouteKey',
    'Test-LiveCoreRouteImplementation',
    'Test-LiveCoreRouteRole',
    'Test-LiveCoreEntitlementEventCatalog',
    'Test-LiveCoreMobileStoreRoute',
    'Test-LiveCoreUniqueIndex',
    'Test-LiveCoreTableSchema',
    'Test-LiveCoreSessionEventEmission',
    'Invoke-LiveCoreSpecConsistency'
)
