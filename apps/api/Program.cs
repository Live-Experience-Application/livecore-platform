using LiveCore.Api;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Scenes;
using LiveCore.Api.Sessions;
using LiveCore.Api.SystemModule;
using LiveCore.Api.Templates;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Structured logging baseline (CORE-FND-004): one JSON object per log entry
// on stdout, UTC timestamps, scopes included. Uses the JSON console formatter
// built into Microsoft.Extensions.Logging; no external logging dependency.
// Log identifiers and metadata, never sensitive content (threat T7 in
// docs/07_SECURITY_THREAT_MODEL.md).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});

// Do not advertise the server technology in response headers; the
// unauthenticated health endpoints must not aid fingerprinting
// (docs/07_SECURITY_THREAT_MODEL.md).
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddHealthChecks();

// Realtime SignalR services (CORE-RT-001, the Realtime Event Stream epic's first story). SignalR is
// part of the ASP.NET Core shared framework (Microsoft.AspNetCore.App) — no new package dependency.
// Registered unconditionally (the hub needs no database): the session hub it backs is authenticated and
// carries no events yet (groups = CORE-RT-002; event delivery = CORE-RT-003+). docs/11_REALTIME_SYNC.md
// mandates SignalR for realtime communication.
builder.Services.AddSignalR();

// Authentication wiring (CORE-WS-003, the first endpoint story). Adds JWT bearer
// validation for the external OIDC provider per the documented request flow
// (docs/02_ARCHITECTURE.md) and ADR 0005, configured only from configuration
// (Authentication:Oidc:*; no secrets in code). The bearer scheme is registered
// only when an Authority is configured, so the host still starts (and the smoke
// tests still pass) without an identity provider; a fail-closed default scheme is
// registered in its place so authenticated endpoints challenge with 401 rather
// than crashing or allowing anonymous access. MapInboundClaims=false (set in the
// extension) preserves the raw OIDC claim names for OidcPrincipalMapper
// (CORE-ID-001 carry-over requirement).
var oidcConfigured = builder.Services.AddOidcAuthentication(builder.Configuration);

// Persistence (CORE-ID-002): PostgreSQL via EF Core per docs/02_ARCHITECTURE.md
// and docs/10_DATABASE_SCHEMA.md. The connection string comes only from
// configuration (ConnectionStrings:Database, e.g. the environment variable
// ConnectionStrings__Database); no credentials live in this repository.
// Without a configured connection string the host runs without persistence
// (no database-backed feature exists yet) and the database readiness check
// is not registered, so local runs and tests need no database server.
var databaseConnectionString = builder.Configuration.GetConnectionString("Database");
if (!string.IsNullOrWhiteSpace(databaseConnectionString))
{
    builder.Services.AddDbContext<LiveCoreDbContext>(options => options.UseNpgsql(databaseConnectionString));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<IUserProfileRepository, UserProfileRepository>();
    builder.Services.AddScoped<UserProfileReferenceService>();
    builder.Services.AddScoped<IOrganizationRepository, OrganizationRepository>();
    builder.Services.AddScoped<IOrganizationMemberRepository, OrganizationMemberRepository>();

    // Workspace persistence (CORE-WS-001): the Workspaces module owns the
    // tenant-scoped workspaces table (docs/05_MODULE_CONTRACTS.md). Registered
    // here, inside the persistence conditional, exactly like the organization
    // repositories above; the repository's lookups are tenant-scoped by
    // organization id (threat T5). HTTP endpoints are a later story (CORE-WS-003).
    builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();

    // Workspace membership persistence (CORE-WS-002): the Workspaces module
    // owns the workspace-scoped, tenant-scoped workspace_members table and the
    // generic workspace-level roles (docs/05_MODULE_CONTRACTS.md). Registered
    // here, inside the persistence conditional, exactly like the workspace and
    // organization repositories above; the repository's lookups are scoped by
    // organization id and workspace id (the organization boundary is checked
    // before the workspace boundary; threat T5). HTTP endpoints and per-action
    // authorization policies are later stories (CORE-WS-003, CORE-WS-005).
    builder.Services.AddScoped<IWorkspaceMemberRepository, WorkspaceMemberRepository>();

    // Workspace invitation persistence (CORE-WS-004): the Workspaces module owns
    // the workspace-scoped, tenant-scoped workspace_invitations table that backs
    // the member invite placeholder with its scoped token model
    // (docs/05_MODULE_CONTRACTS.md; csv/api_routes.csv: POST
    // /api/v1/workspaces/{workspaceId}/members, "Invite/add member"). Registered
    // here, inside the persistence conditional, exactly like the workspace and
    // membership repositories above; the repository's lookups are scoped by
    // organization id and workspace id (threat T5), and it stores only the
    // SHA-256 hash of the scoped token, never the plaintext (threats T6/T7).
    builder.Services.AddScoped<IWorkspaceInvitationRepository, WorkspaceInvitationRepository>();

    // Participant persistence (CORE-SES-001): the Participants module owns the
    // workspace-scoped, tenant-scoped participants table that holds the
    // session-facing participant records (docs/05_MODULE_CONTRACTS.md;
    // csv/database_tables.csv: participants, module Participants, scope
    // workspace). Registered here, inside the persistence conditional, exactly
    // like the workspace and membership repositories above; the repository's
    // lookups are scoped by organization id and workspace id (the organization
    // boundary is checked before the workspace boundary; threat T5). The user
    // link is optional so anonymous participants are supported
    // (docs/03_DOMAIN_LANGUAGE.md). HTTP endpoints (the participant-visible feed
    // CORE-SES-005) and the session-join flow (CORE-SES-003) are later stories.
    builder.Services.AddScoped<IParticipantRepository, ParticipantRepository>();

    // Session persistence (CORE-SES-002): the Sessions module owns the
    // workspace-scoped, tenant-scoped sessions table that holds the live/prepared
    // run records and their lifecycle (docs/05_MODULE_CONTRACTS.md: the Sessions
    // module owns "session lifecycle" and "session status"; csv/database_tables.csv:
    // sessions, module Sessions, scope workspace, "Live/prepared run"). Registered
    // here, inside the persistence conditional, exactly like the participant and
    // workspace repositories above; the repository's lookups are scoped by
    // organization id and workspace id (the organization boundary is checked before
    // the workspace boundary; threat T5). The session lifecycle is a guarded
    // Prepared -> Live -> Ended state machine on the aggregate; the create/start/end
    // HTTP endpoints and the SessionCreated/Started/Ended events (CORE-SES-004) are
    // later stories and are deliberately not wired here.
    builder.Services.AddScoped<ISessionRepository, SessionRepository>();

    // Scene persistence (CORE-SCENE-001): the Scenes module owns the
    // workspace-scoped, tenant-scoped scenes table that holds the workspace-prepared
    // ordered segments (docs/05_MODULE_CONTRACTS.md: the Scenes module owns "scene
    // metadata" and "scene ordering"; csv/database_tables.csv: scenes, module Scenes,
    // scope workspace, "Ordered segments"). Registered here, inside the persistence
    // conditional, exactly like the session and participant repositories above; the
    // repository's lookups are scoped by organization id and workspace id (the
    // organization boundary is checked before the workspace boundary; threat T5), and
    // ListByWorkspaceAsync returns a workspace's scenes in deterministic
    // (scene_order, id) order. A scene carries no session_id: a session activates a
    // scene through its active scene pointer in a later story. The create/reorder HTTP
    // endpoints (POST /api/v1/workspaces/{workspaceId}/scenes and the reorder route),
    // content blocks (CORE-SCENE-002), the scene content APIs (CORE-SCENE-003), the
    // host vs participant DTO separation (CORE-SCENE-004) and content
    // validation/size limits (CORE-SCENE-005) are later stories and are deliberately
    // not wired here.
    builder.Services.AddScoped<ISceneRepository, SceneRepository>();

    // Content block persistence (CORE-SCENE-002): the Content module owns the
    // scene-scoped, workspace-scoped, tenant-scoped content_blocks table that holds
    // the host-prepared Text/media/data units and their revisions
    // (docs/05_MODULE_CONTRACTS.md: the Content module owns "content blocks" and
    // "content block revisions"; csv/database_tables.csv: content_blocks, module
    // Content, scope workspace, "Text/media/data block"; the documented critical index
    // content_blocks(workspace_id, scene_id) scopes a block to its scene). Registered
    // here, inside the persistence conditional, exactly like the scene and session
    // repositories above; the repository's lookups are scoped by organization id then
    // workspace id then scene id (the organization boundary is checked before the
    // workspace boundary; threat T5), there is NO list-everything method, and revisions
    // are an explicit monotonic revision_number on the aggregate (no separate revisions
    // table — csv/database_tables.csv lists only content_blocks). The content block
    // carries NO visibility logic: whether a participant may see it is computed
    // server-side by the Visibility module in a later epic (docs/05_MODULE_CONTRACTS.md:
    // the Content module "may not decide visibility alone"). The scene content HTTP
    // endpoints (CORE-SCENE-003), the host vs participant DTO separation
    // (CORE-SCENE-004) and content validation/size limits (CORE-SCENE-005) are later
    // stories and are deliberately not wired here.
    builder.Services.AddScoped<IContentBlockRepository, ContentBlockRepository>();

    // Entity type persistence (CORE-ENT-001, first story of the Entity System and
    // Templates epic): the Entities module owns the workspace-scoped, tenant-scoped
    // entity_types table that holds the generic, template-defined entity TYPE
    // definitions (docs/05_MODULE_CONTRACTS.md: the Entities module owns "entity types"
    // but may not implement any vertical-specific entity behavior directly;
    // csv/database_tables.csv: entity_types, module Entities/Templates, scope
    // workspace/template, "Template-defined types"). Registered here, inside the
    // persistence conditional, exactly like the scene and content-block repositories
    // above; the repository's lookups are scoped by organization id then workspace id
    // (the organization boundary is checked before the workspace boundary; threat T5),
    // there is NO list-everything method, and ListByWorkspaceAsync returns a workspace's
    // types in deterministic type-key order. The type is fully DATA-DRIVEN: the type key,
    // display name and attribute schema are stored verbatim and the source contains no
    // type-specific logic (THE TEMPLATE BOUNDARY, docs/04_PRODUCT_BOUNDARIES.md). The
    // attribute schema is validated only for JSON well-formedness here; full template
    // schema validation and the template_id linkage are CORE-ENT-004 (no templates table
    // exists yet). Entity instances (CORE-ENT-002), relationships (CORE-ENT-003), search
    // with visibility filtering (CORE-ENT-005) and any HTTP endpoint (csv/api_routes.csv
    // defines no entity-type route) are later stories and are deliberately not wired here.
    builder.Services.AddScoped<IEntityTypeRepository, EntityTypeRepository>();

    // Entity instance persistence (CORE-ENT-002, second story of the Entity System and
    // Templates epic): the Entities module owns the workspace-scoped, tenant-scoped entities
    // table that holds the generic entity INSTANCES — one concrete object of an entity type
    // (docs/05_MODULE_CONTRACTS.md: the Entities module owns "generic entities" but may not
    // implement any vertical-specific entity behavior directly; csv/database_tables.csv:
    // entities, module Entities, scope workspace, "Generic objects"). Registered here, inside the
    // persistence conditional, exactly like the entity-type and content-block repositories above;
    // the repository's lookups are scoped by organization id then workspace id (the organization
    // boundary is checked before the workspace boundary; threat T5), there is NO list-everything
    // method, and ListByWorkspace/ListByType return a workspace's entities in deterministic
    // (time-ordered surrogate id) order. The entity is fully DATA-DRIVEN: its name and attribute
    // values are stored verbatim and the source contains no type-specific logic (THE TEMPLATE
    // BOUNDARY, docs/04_PRODUCT_BOUNDARIES.md). The attribute values are validated only for JSON
    // well-formedness here; validating them against the entity type's attribute schema
    // (schema-conformance) is the template engine / CORE-ENT-004. The entity_type_id foreign key
    // guarantees the referenced type exists but not that it is in the entity's workspace; the
    // same-workspace-type coupling is the responsibility of the future create-entity application
    // flow (mirrors ContentBlock/scene_id). Entity relationships (CORE-ENT-003), template loading
    // (CORE-ENT-004), search with visibility filtering (CORE-ENT-005) and any HTTP endpoint
    // (csv/api_routes.csv defines no entity route) are later stories and are deliberately not
    // wired here.
    builder.Services.AddScoped<IEntityRepository, EntityRepository>();

    // Entity relationship persistence (CORE-ENT-003, third story of the Entity System and
    // Templates epic): the Entities module owns the workspace-scoped, tenant-scoped
    // entity_relationships table that holds the generic GRAPH EDGES between two entity instances
    // (docs/05_MODULE_CONTRACTS.md: the Entities module owns "entity relationships" but may not
    // implement any vertical-specific entity behavior directly; csv/database_tables.csv:
    // entity_relationships, module Entities, scope workspace, "Graph edges"). Registered here,
    // inside the persistence conditional, exactly like the entity and entity-type repositories
    // above; the repository's lookups are scoped by organization id then workspace id (the
    // organization boundary is checked before the workspace boundary; threat T5), there is NO
    // list-everything method, and ListByWorkspace/ListBySource/ListByEntity return a workspace's
    // edges in deterministic (time-ordered surrogate id) order. The edge is DIRECTED (source ->
    // target) and carries a generic relationship_kind: the kind is stored verbatim and the source
    // contains no kind-specific logic (THE TEMPLATE BOUNDARY, docs/04_PRODUCT_BOUNDARIES.md). The
    // source_entity_id and target_entity_id foreign keys guarantee the endpoints exist but not that
    // they are in the edge's workspace; the same-workspace-endpoints coupling is the responsibility
    // of the future create-relationship application flow (mirrors Entity/entity_type_id,
    // ContentBlock/scene_id). The aggregate is immutable, so there is no UpdateAsync. Template
    // loading (CORE-ENT-004), search with visibility filtering / graph traversal (CORE-ENT-005) and
    // any HTTP endpoint (csv/api_routes.csv defines no entity-relationship route) are later stories
    // and are deliberately not wired here.
    builder.Services.AddScoped<IEntityRelationshipRepository, EntityRelationshipRepository>();

    // Template registry persistence (CORE-ENT-004, fourth story of the Entity System and Templates
    // epic): the Templates module — FIRST appearing here — owns the global/organization-scoped
    // templates table that holds the versioned template registry (docs/05_MODULE_CONTRACTS.md: the
    // Templates module owns the "generic template loader, template validation, template versioning"
    // and may not hardcode vertical behavior; csv/database_tables.csv row 19: templates, module
    // Templates, scope global/organization, "Template registry"). Registered here, inside the
    // persistence conditional, exactly like the entity-type and content-block repositories above.
    // A template's scope is a single nullable organization_id (NULL = global / available to all
    // tenants, set = owned by one organization), and the repository's lookups are scope-aware — a
    // global template is never read through an org path and an org-A template is never read through
    // org-B's id (threat T5). The template definition (its entity-type keys/names/schemas) is stored
    // verbatim as DATA and the source contains no key/name branching (THE TEMPLATE BOUNDARY,
    // docs/04_PRODUCT_BOUNDARIES.md); it is validated for JSON well-formedness + minimal structure
    // only. There is NO HTTP endpoint (csv/api_routes.csv defines no template route) and NO template
    // version-transition workflow in this story.
    builder.Services.AddScoped<ITemplateRepository, TemplateRepository>();

    // Visibility rule persistence (CORE-VIS-001, the first story of the Visibility and Reveal Engine
    // epic): the Visibility module — THE central security module (docs/05_MODULE_CONTRACTS.md) — owns
    // the workspace-scoped, tenant-scoped visibility_rules table that holds the generic AUDIENCE
    // RULES binding a Core resource (a scene/content-block/entity, named by resource_type +
    // resource_id) to a base audience visibility state (Hidden/Visible) (csv/database_tables.csv:
    // visibility_rules, module Visibility, scope workspace, "Audience rules"). Registered here, inside
    // the persistence conditional, exactly like the entity and content-block repositories above. The
    // repository's lookups are scoped by organization id then workspace id (organization boundary
    // before workspace boundary; threat T5), there is NO list-everything method, and
    // ListByWorkspace/ListByResource return a workspace's rules in deterministic (time-ordered
    // surrogate id) order. The authorization-relevant fields are REAL COLUMNS, never JSON
    // (docs/10_DATABASE_SCHEMA.md). resource_id is a polymorphic reference (no DB foreign key); the
    // same-workspace coupling is the create-rule application flow's responsibility (mirrors
    // ContentBlock/scene_id, Entity/entity_type_id). The CanViewResource policy (CORE-VIS-002),
    // preview-as-participant (CORE-VIS-003), the reveal command with idempotency + append-only event
    // (CORE-VIS-004), selected-participant reveal (CORE-VIS-005) and any HTTP endpoint (the
    // POST /sessions/{sessionId}/reveal route is CORE-VIS-004) are later stories and are deliberately
    // not wired here.
    builder.Services.AddScoped<IVisibilityRuleRepository, VisibilityRuleRepository>();

    // Visibility access policy (CORE-VIS-002): the Visibility module's CanViewResource decision —
    // "may this viewer see this resource?" — over the CORE-VIS-001 visibility rules. Registered here,
    // inside the persistence conditional, because it depends on the visibility rule repository above
    // (exactly like the entity search service depends on the entity repository). It is a plain,
    // fail-closed decision service: tenant id, workspace id, the caller's role and the resource
    // (type + id) in, an allow/deny decision out. Host-content roles (Owner/Admin/Host/CoHost — "View
    // host-only content" in docs/06_AUTHORIZATION_MATRIX.md) see the resource regardless of rules
    // (short-circuit, no DB read); audience roles (Participant/Observer) see it only when a rule makes
    // it visible (rule lookup leads with organization_id then workspace_id; threat T5); the audit role
    // and any undefined role are denied by default. This is THE central place visibility is decided
    // (docs/05_MODULE_CONTRACTS.md: do not duplicate visibility logic elsewhere) — the Entities
    // module's entity-search role split now delegates to this module's VisibilityRoles. There is NO
    // HTTP endpoint (csv/api_routes.csv defines no CanViewResource route), and preview-as-participant
    // (CORE-VIS-003), the reveal command (CORE-VIS-004), selected-participant reveal (CORE-VIS-005)
    // and audit records (CORE-VIS-006) are later stories not wired here.
    builder.Services.AddScoped<VisibilityPolicy>();

    // Visibility preview-as-participant query (CORE-VIS-003): computes the SET of resources an
    // audience participant may currently see in a workspace (GetVisibleResourcesForParticipant /
    // PreviewVisibilityForHost, docs/05_MODULE_CONTRACTS.md). Registered here, inside the persistence
    // conditional, because it depends on the visibility rule repository and the VisibilityPolicy above.
    // It REUSES VisibilityPolicy.CanViewResourceAsync per candidate resource under the audience
    // viewpoint, so the preview can never diverge from the per-resource access decision and visibility
    // is decided in exactly one place (docs/05: do not duplicate visibility logic elsewhere). The set
    // is audience-wide for now (per-participant subset = CORE-VIS-005). There is NO HTTP endpoint
    // (csv/api_routes.csv defines no preview route); wiring this set into the CORE-SES-005
    // participant-visible-feed response — alongside the Realtime reveal-event projection — is a later
    // step. The reveal command (CORE-VIS-004), selected-participant reveal (CORE-VIS-005) and audit
    // records (CORE-VIS-006) are also later stories not wired here.
    builder.Services.AddScoped<VisibilityPreviewService>();

    // Idempotency key store (CORE-VIS-004): the System module's generic retry-safety store over the
    // idempotency_keys table (csv/database_tables.csv: module System, "Retry safety"). Registered here,
    // inside the persistence conditional, because it depends on the DbContext. The unique
    // idempotency_keys(scope, key) index (docs/10_DATABASE_SCHEMA.md) is the idempotency guarantee; the
    // store is generic infrastructure (any retry-safe write can use it), consumed first by the reveal
    // command below.
    builder.Services.AddScoped<IIdempotencyKeyStore, IdempotencyKeyStore>();

    // Append-only audit log (CORE-VIS-006): the Audit module — FIRST appearing here — owns the
    // tenant-scoped audit_logs table holding the immutable security event records
    // (docs/05_MODULE_CONTRACTS.md: the Audit module owns the "append-only audit log" and "security
    // event records"; csv/database_tables.csv: audit_logs, module Audit, scope organization,
    // "Append-only audit"). Registered here, inside the persistence conditional, because it depends on
    // the DbContext. The repository exposes only append + a tenant-scoped read (no update/delete —
    // audit facts are immutable, docs/10_DATABASE_SCHEMA.md); the documented critical index is
    // audit_logs(organization_id, created_at). It is consumed first by the reveal command below to
    // record visibility changes; the generic append-only audit log (CORE-AUD-001), the audit query
    // endpoint and its "View audit log" authorization (CORE-AUD-005) are later stories not wired here.
    builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();

    // Reveal command service (CORE-VIS-004; CORE-VIS-006 audit): the Visibility module's idempotent
    // reveal — makes a resource VISIBLE to the audience (reusing the CORE-VIS-001
    // VisibilityRule.ChangeVisibility primitive) exactly once per client Idempotency-Key. Registered
    // here because it depends on the visibility rule repository, the idempotency store and the audit log
    // repository above. The reveal is idempotent at the state level (ensure-visible is a no-op when
    // already visible) and the idempotency key short-circuits a retry; when a reveal actually changes
    // visibility it appends an append-only audit record of the change (CORE-VIS-006). The durable
    // ContentRevealed/VisibilityRuleChanged realtime event emission is deferred to the Realtime epic
    // (CORE-RT-003), exactly as the session start/end commands deferred their events.
    builder.Services.AddScoped<RevealService>();

    // Template-loaded entity types loader (CORE-ENT-004, the headline behavior): materializes a
    // workspace's EntityType rows FROM a resolved template's entityTypes definitions, iterating them
    // generically (a foreach, never a switch on type names) and persisting through the Entities
    // module's IEntityTypeRepository.AddAsync — the allowed cross-module contract access
    // (docs/02_ARCHITECTURE.md; docs/05_MODULE_CONTRACTS.md), CONSUMING EntityType.Create without
    // modifying the entity_types table. It enforces TENANT SECURITY before creating anything: an
    // organization-scoped template may be loaded only into a workspace of the SAME organization, a
    // global template into any workspace (threat T5); an org-A template targeted at an org-B
    // workspace is denied and nothing is created. A duplicate key already in the workspace is skipped
    // and reported (partial-load-with-report), not fatal. It depends only on the entity-type
    // repository and TimeProvider, already registered above. No HTTP endpoint, event, visibility or
    // entity-instance schema-conformance is wired here (all out of scope).
    builder.Services.AddScoped<TemplateEntityTypeLoader>();

    // Entity search service (CORE-ENT-005, the last story of the Entity System and Templates epic):
    // searches a workspace's entities for a given workspace role WITH VISIBILITY FILTERING.
    // Registered here, inside the persistence conditional, because it depends on the entity
    // repository above (exactly like the join service depends on the session/participant
    // repositories). It is a plain decision service: tenant id, workspace id, the caller's role and
    // generic criteria in, the role-appropriate entity set out. The host-capable roles
    // (Owner/Admin/Host/CoHost — "View host-only content" in docs/06_AUTHORIZATION_MATRIX.md) get
    // every matching entity through the tenant- and workspace-scoped repository lookups (organization
    // boundary before workspace boundary; threat T5); every other role gets the fail-closed empty
    // audience view, deferring the audience-visible computation to the central Visibility engine
    // (CORE-VIS) rather than duplicating visibility logic here (docs/02_ARCHITECTURE.md,
    // docs/05_MODULE_CONTRACTS.md) — the same skeleton shape as the CORE-SES-005 participant-visible
    // feed. There is NO HTTP endpoint (csv/api_routes.csv defines no entity route) and NO visibility
    // rule engine in this story.
    builder.Services.AddScoped<EntitySearchService>();

    // Tenant context resolver (CORE-ID-005): turns an authenticated principal
    // plus a target organization into a trusted TenantContext or a fail-closed
    // denial. Registered here because it depends on the organization, user
    // profile and membership repositories above, which exist only when a
    // database connection string is configured (docs/02_ARCHITECTURE.md request
    // flow; docs/05_MODULE_CONTRACTS.md: Organizations provides organization
    // context and tenant isolation checks; threat T5).
    builder.Services.AddScoped<TenantContextResolver>();

    // Session participant join service (CORE-SES-003): the fail-closed decision of
    // whether a participant may join a session. Registered here because it depends
    // on the session and participant repositories above, which exist only when a
    // database connection string is configured — exactly like the tenant context
    // resolver. It mirrors that resolver's shape: repositories in, a trusted
    // admission or a typed fail-closed denial out, with every lookup scoped by
    // organization id then workspace id so a session or participant outside the
    // caller's tenant/workspace is hidden as not-found (threats T1/T5). The join
    // HTTP endpoint, the durable ParticipantJoined session event and its SignalR
    // delivery (docs/09_EVENT_CATALOG.md; docs/11_REALTIME_SYNC.md) and the
    // persisted participant connection metadata are later stories (the Realtime
    // epic / Participants-owned work) and are deliberately not wired here.
    builder.Services.AddScoped<SessionParticipantJoinService>();

    // Realtime connection resolver (CORE-RT-002): resolves which SERVER-MANAGED groups a SignalR hub
    // connection joins (or a fail-closed denial). Registered here because it composes the tenant context
    // resolver, the session repository, the workspace member repository and the participant repository
    // above — all registered only when a database connection string is configured. The SessionHub
    // resolves it from the request services on connect and aborts when it is absent (persistence off),
    // exactly as the REST endpoints fail closed with 503. It supersedes the RT-001 authenticated-
    // connection placeholder: the connection supplies only identifiers (organizationSlug, sessionId,
    // optional participantId) and the server computes the group names, so a client can never choose a
    // group or subscribe to another participant's feed (docs/11_REALTIME_SYNC.md; threat T3). Event
    // append/delivery to these groups is CORE-RT-003.
    builder.Services.AddScoped<RealtimeConnectionResolver>();

    // Session event stream (CORE-RT-003): the Realtime module owns the session-scoped, append-only
    // session_events table (csv/database_tables.csv: module Realtime, scope session, "Append-only event
    // stream"; the documented critical index session_events(session_id, created_at, event_id)). The
    // repository is append + tenant/session-scoped read only (no update/delete — events are immutable,
    // docs/10_DATABASE_SCHEMA.md). The publisher composes it with IHubContext<SessionHub> (registered by
    // AddSignalR above) to persist an event and deliver its recipient-safe envelope to the CORE-RT-002
    // server-computed groups ("persist event -> compute recipients -> send to recipient groups",
    // docs/11_REALTIME_SYNC.md). Registered here, inside the persistence conditional, because the
    // repository depends on the DbContext; the reveal endpoint resolves the publisher and fails closed
    // (503) when persistence is off. The reveal command is its first producer (the ContentRevealed
    // event); the SessionStarted/Ended events and recipient-specific projection are later stories
    // (CORE-RT-003 follow-up / CORE-RT-004).
    builder.Services.AddScoped<ISessionEventRepository, SessionEventRepository>();
    builder.Services.AddScoped<ISessionEventPublisher, SessionEventPublisher>();

    // Gate readiness on database connectivity. The health response stays
    // status-only (see HealthEndpoints), so a failing check never leaks
    // connection details to the unauthenticated readiness endpoint.
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<LiveCoreDbContext>("database", tags: [HealthEndpoints.ReadinessTag]);
}

var app = builder.Build();

if (string.IsNullOrWhiteSpace(databaseConnectionString))
{
    app.Logger.LogWarning(
        "No database connection string configured (ConnectionStrings:Database); persistence and the database readiness check are disabled.");
}

if (!oidcConfigured)
{
    app.Logger.LogWarning(
        "No OIDC Authority configured (Authentication:Oidc:Authority); authentication is disabled and authenticated endpoints fail closed.");
}

// Authentication runs before authorization, which runs before the endpoints, per
// the documented request flow (docs/02_ARCHITECTURE.md). The health endpoints
// are mapped after this and stay anonymous because they are not in the
// authenticated workspace route group.
app.UseAuthentication();
app.UseAuthorization();

// Health endpoints (CORE-FND-004): unauthenticated by convention.
app.MapLiveCoreHealthEndpoints();

// Workspace endpoints (CORE-WS-003): the first domain HTTP endpoints. They live
// in an authenticated route group and fail closed (503) when persistence is not
// configured, so mapping them never crashes startup.
app.MapWorkspaceEndpoints();

// Session lifecycle endpoints (CORE-SES-004): the session start/end commands.
// They live in an authenticated route group and fail closed (503) when
// persistence is not configured, exactly like the workspace endpoints. No new DI
// registration is required: the tenant context resolver, the session repository
// and the workspace member repository they consume are already registered above
// inside the persistence conditional. The durable SessionStarted/SessionEnded
// events these commands will eventually emit are deferred to the Realtime epic
// (no event store/SignalR transport exists yet); the persisted status transition
// is the behavior delivered here (docs/09_EVENT_CATALOG.md; csv/database_tables.csv
// assigns session_events to the Realtime module).
app.MapSessionEndpoints();

// Participant-visible feed endpoint (CORE-SES-005): the Visibility module's first
// route, GET /api/v1/participants/{participantId}/visible-feed. It lives in an
// authenticated route group and fails closed (503) when persistence is not
// configured, exactly like the session/workspace endpoints. No new DI registration
// is required: the tenant context resolver, the participant repository and the
// workspace member repository it consumes are already registered above inside the
// persistence conditional. This is a SKELETON: it establishes the route + the
// fail-closed object-level authorization (own-feed ownership, or Host/CoHost
// preview, every denial hidden as 404) and returns a participant-safe EMPTY feed.
// The actual visible content (filtered reveal events / content blocks / the
// server-side visibility-rule engine) belongs to the later Visibility + Reveal +
// Realtime epics and is deliberately not built here (docs/05_MODULE_CONTRACTS.md:
// the Visibility module owns visibility rules / audience calculations /
// preview-as-participant).
app.MapVisibilityEndpoints();

// Reveal command endpoint (CORE-VIS-004): the Visibility module's first COMMAND route,
// POST /api/v1/sessions/{sessionId}/reveal. It makes a resource visible to the audience
// idempotently (a required Idempotency-Key header; reuses the VisibilityRule aggregate), authorized
// to the reveal roles (Owner/Admin/Host/CoHost) in the session's own workspace exactly like the
// session start/end commands. The durable reveal event emission is deferred to the Realtime epic.
app.MapRevealEndpoints();

// Scene content endpoints (CORE-SCENE-003): the Scenes module's first HTTP routes,
// GET/POST /api/v1/workspaces/{workspaceId}/scenes. They live in an authenticated
// route group and fail closed (503) when persistence is not configured, exactly like
// the workspace endpoints. No new DI registration is required: the tenant context
// resolver, the scene repository and the workspace member repository they consume are
// already registered above inside the persistence conditional. The GET list returns the
// SAME generic scene DTO to all workspace members; the per-role / host-vs-participant
// projection (the "Projection by role" route note) is the later CORE-SCENE-004 story.
// The POST assigns the scene order server-side as append-to-end (no client-supplied
// order, no reorder route).
app.MapSceneEndpoints();

// Scene content-block endpoint (CORE-SCENE-003): the Content module's first HTTP route,
// POST /api/v1/scenes/{sceneId}/content-blocks. It lives in an authenticated route group
// and fails closed (503) when persistence is not configured, exactly like the session
// endpoints. No new DI registration is required: the tenant context resolver, the scene
// repository (for the org-scoped scene lookup), the content block repository and the
// workspace member repository it consumes are already registered above inside the
// persistence conditional. The scene is resolved within the query-supplied organization,
// its own workspace is discovered from the loaded row after the tenant boundary is
// enforced, and the create is authorized by the caller's role in the scene's own
// workspace (every denial hidden as 404, an insufficient role as 403). The
// host-vs-participant DTO projection (CORE-SCENE-004) and content validation/size limits
// (CORE-SCENE-005) are later stories and are deliberately not built here.
app.MapContentBlockEndpoints();

// Realtime session hub (CORE-RT-001): the Realtime module's SignalR hub at /hubs/session. It requires
// authorization (the hub is [Authorize] and the mapping adds RequireAuthorization()), so an
// unauthenticated client is challenged with 401 at negotiate exactly like the REST endpoints. The hub
// carries no groups or events yet (CORE-RT-002/003+); it is the authenticated front door the later
// realtime delivery connects through. Unlike the persistence-gated REST endpoints it needs no database,
// so it is mapped unconditionally.
app.MapRealtimeHubs();

app.Run();

/// <summary>
/// Marker for in-memory smoke tests (WebApplicationFactory).
/// </summary>
public partial class Program;
