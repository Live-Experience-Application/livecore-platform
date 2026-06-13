using LiveCore.Api;
using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.Entitlements;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Scenes;
using LiveCore.Api.Sessions;
using LiveCore.Api.Store;
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

// Realtime scale-out seam (CORE-RT-006, the Realtime Event Stream epic's final story). The backplane is
// the single transport boundary a computed event delivery crosses on its way to connected clients. The
// default in-process implementation sends a recipient-safe payload to its server-managed group via
// IHubContext<SessionHub> (this instance's connections only); a multi-instance deployment substitutes a
// Valkey/Redis-compatible IRealtimeBackplane (docs/11_REALTIME_SYNC.md "Scale-out") so the SAME delivery
// also reaches connections on other instances. It receives an ALREADY-AUTHORIZED delivery (one payload to
// one group, computed by the per-recipient resolver), so swapping the transport cannot widen the audience
// and realtime delivery never leaks a hidden event (threat T3). It needs only the shared-framework hub
// context (no new dependency, no database), so it is registered unconditionally next to AddSignalR.
builder.Services.AddSingleton<IRealtimeBackplane, InProcessRealtimeBackplane>();

// Asset storage adapter seam (CORE-AST-002, the storage adapter interface story of the "Asset Storage and
// Authorization" epic). IAssetStorage is the single port between Core and the private, S3-compatible
// object storage that holds an asset's binary content (docs/05_MODULE_CONTRACTS.md: the Assets module owns
// the "storage adapter" and "signed URL creation"; docs/12_STORAGE_ASSETS.md; ADR 0006). The concrete,
// provider-specific adapter (and its SDK + object-storage endpoint/credentials) is supplied by the
// deployment (docs/13_SELF_HOSTING_REQUIREMENTS.md), exactly as a Valkey/Redis backplane replaces the
// in-process realtime default (CORE-RT-006). Until one is wired, the default is the FAIL-CLOSED
// UnconfiguredAssetStorage: every asset operation throws AssetStorageNotConfiguredException rather than
// serving bytes some insecure way, so assets stay private by default even when storage is not configured
// (the epic acceptance criterion; threat T4 "Asset leak"). It is stateless and needs no database, so it is
// registered unconditionally next to the realtime backplane. The upload-intent flow (CORE-AST-003) and the
// signed download flow with authorization (CORE-AST-004) both consume this port to mint short-lived signed
// upload/download URLs after their server-side permission checks.
builder.Services.AddSingleton<IAssetStorage, UnconfiguredAssetStorage>();

// Purchase verification provider seam (CORE-STORE-001, the first story of the "Store Purchase Verification"
// epic). IPurchaseVerificationProvider is the single port between Core and a store's own server APIs that
// verify a purchase proof; one adapter serves one provider (Apple/Google) and reduces its raw response to a
// provider-neutral PurchaseVerificationResult, so "Apple/Google provider logic is isolated from Core domain
// logic" (the epic acceptance criterion). The concrete, credential-bearing adapters (the store SDK and keys)
// are supplied by the deployment (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7), exactly as the
// S3-compatible IAssetStorage adapter and the Valkey/Redis IRealtimeBackplane are. The
// PurchaseVerificationProviderResolver is registered here unconditionally (it is stateless and needs no
// database, like the seams above); Core registers NO provider adapter, so the resolver FAILS CLOSED with
// PurchaseProviderNotConfiguredException for every provider until a deployment wires one — Core never trusts a
// client's unverified proof ("Never unlock limits before server verification succeeds", docs/21). The Apple and
// Google verification endpoints (CORE-STORE-003/004) authorize the caller server-side and only then resolve the
// adapter and verify; persistence of the verified transaction (CORE-STORE-002) is a later story.
builder.Services.AddSingleton<PurchaseVerificationProviderResolver>();

// Store notification parser seam (CORE-STORE-005, the "Store Notifications" epic). IStoreNotificationParser is
// the single port between Core and a store's server-to-server notification format; one adapter serves one
// provider (Apple/Google), VALIDATES the inbound payload's signature/source and reduces it to a provider-neutral
// StoreNotificationParseResult, so provider logic stays isolated from Core domain logic (the verification
// abstraction's seam, applied to notifications). The concrete, credential-bearing validators (signing keys /
// source verification) are supplied by the deployment (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7), exactly
// as the purchase verifier, the S3-compatible IAssetStorage and the Valkey/Redis IRealtimeBackplane are. The
// StoreNotificationParserResolver is registered here unconditionally (it is stateless and needs no database, like
// the seams above); Core registers NO parser adapter, so the resolver FAILS CLOSED with
// StoreNotificationParserNotConfiguredException for every provider until a deployment wires one. Because the
// store notification routes are unauthenticated server-to-server callbacks (csv/mobile_store_api_routes.csv:
// auth_required=false), this fail-closed default is what stops an unvalidated payload from ever changing a
// purchase: with nothing configured an inbound notification is 503 and nothing happens.
builder.Services.AddSingleton<StoreNotificationParserResolver>();

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

    // Visibility preview-as-participant query (CORE-VIS-003, made participant-aware by CORE-API-004):
    // computes the SET of resources a SPECIFIC participant may currently see in a workspace
    // (GetVisibleResourcesForParticipant / PreviewVisibilityForHost, docs/05_MODULE_CONTRACTS.md).
    // Registered here, inside the persistence conditional, because it depends on the visibility rule
    // repository and the VisibilityPolicy above. It REUSES VisibilityPolicy.CanParticipantViewResourceAsync
    // per candidate resource (the same participant-aware primitive EventRecipientVisibility uses), so the
    // preview handles audience-wide AND selected-participant private reveals, fails closed for a reveal
    // scoped to another participant, and can never diverge from the per-resource access decision or the
    // realtime recipient gate — visibility is decided in exactly one place (docs/05: do not duplicate
    // visibility logic elsewhere). There is NO HTTP endpoint (csv/api_routes.csv defines no preview
    // route); this is the single source the participant-visible-feed projection (CORE-API-005) and the
    // entity-search audience filtering (CORE-API-006) consume. The reveal command (CORE-VIS-004),
    // selected-participant reveal (CORE-VIS-005) and audit records (CORE-VIS-006) are separate stories.
    builder.Services.AddScoped<VisibilityPreviewService>();

    // Per-recipient event-visibility decision (CORE-RT-004): the Visibility module's
    // IEventRecipientVisibility — "may this realtime recipient see the resource this event is about?".
    // Registered here, inside the persistence conditional, because it reuses the VisibilityPolicy above
    // (it is a thin, fail-closed adapter that parses the event's subject resource kind and DELEGATES to
    // CanViewResource / CanParticipantViewResource). This keeps the "recipient calculation in Visibility
    // module" (threat T3 in docs/07_SECURITY_THREAT_MODEL.md), so the realtime recipient set never
    // diverges from the REST visibility decision (docs/05_MODULE_CONTRACTS.md: do not duplicate
    // visibility logic elsewhere). It is consumed by the Realtime recipient resolver below.
    builder.Services.AddScoped<IEventRecipientVisibility, EventRecipientVisibility>();

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
    // audit_logs(organization_id, created_at). CORE-AUD-001 makes the log generic: the
    // AuditLogEntry.Create factory records any security-relevant AuditAction through this same append
    // contract (the reveal command below is the first producer, via the ForVisibilityRuleChange
    // specialization). CORE-AUD-005 adds the read-side "View audit log" authorization as the stateless
    // AuditQueryPolicy (Owner/Admin/Auditor, fail-closed) plus the safe AuditLogEntryView read view; like
    // the export/recap projectors it is a static policy, so it needs no DI registration, and there is no
    // audit HTTP route (csv/api_routes.csv defines none).
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

    // Entity search service (CORE-ENT-005, retrofitted by CORE-API-006 to apply the real audience
    // filter): searches a workspace's entities for a given workspace role WITH VISIBILITY FILTERING.
    // Registered here, inside the persistence conditional, because it depends on the entity repository
    // and the central VisibilityPolicy above (exactly like the join service depends on the
    // session/participant repositories and VisibilityPreviewService depends on the policy). It is a
    // plain decision service: tenant id, workspace id, the caller's role, the calling participant and
    // generic criteria in, the role-appropriate entity set out. The host-capable roles
    // (Owner/Admin/Host/CoHost — "View host-only content" in docs/06_AUTHORIZATION_MATRIX.md) get every
    // matching entity through the tenant- and workspace-scoped repository lookups (organization boundary
    // before workspace boundary; threat T5); an audience PARTICIPANT (Participant/Observer with an
    // identified participant) gets exactly the entities revealed to them, decided per candidate by
    // VisibilityPolicy.CanParticipantViewResourceAsync — the SAME participant-aware primitive the
    // participant-visible feed and realtime recipient resolver use, so entity search never diverges and
    // visibility is decided in ONE place (docs/02_ARCHITECTURE.md, docs/05_MODULE_CONTRACTS.md); every
    // other caller (the audit role, any undefined role, an audience role with no participant) fails
    // closed to the empty view before any query. There is NO HTTP endpoint (csv/api_routes.csv defines
    // no entity route) and NO parallel visibility engine in this story.
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
    // docs/10_DATABASE_SCHEMA.md). Registered here, inside the persistence conditional, because the
    // repository depends on the DbContext; the reveal endpoint resolves the publisher and fails closed
    // (503) when persistence is off.
    builder.Services.AddScoped<ISessionEventRepository, SessionEventRepository>();

    // Recipient-specific event projection (CORE-RT-004): the Realtime module's recipient resolver
    // computes the per-recipient deliveries of an event (which server-computed groups receive it and the
    // host vs audience projection each gets), FANNING an audience-wide event out to each active
    // participant's group and gating every recipient through the central Visibility engine
    // (IEventRecipientVisibility above + the participant repository), so realtime delivery never leaks a
    // hidden event (threat T3; docs/11_REALTIME_SYNC.md "Events are never broadcast blindly"). The
    // publisher composes the repository, the recipient resolver and the IRealtimeBackplane (CORE-RT-006,
    // registered above) to persist an event and then forward each computed delivery over the scale-out seam
    // ("persist event -> compute recipients -> project payload -> send to recipient groups",
    // docs/11_REALTIME_SYNC.md). The reveal
    // command is the first producer (the ContentRevealed event, carrying the revealed resource as its
    // visibility subject); the SessionStarted/Ended events and reconnect replay (CORE-RT-005) are later
    // stories.
    builder.Services.AddScoped<ISessionEventRecipientResolver, SessionEventRecipientResolver>();
    builder.Services.AddScoped<ISessionEventPublisher, SessionEventPublisher>();

    // Reconnect replay filter (CORE-RT-005): the Realtime module's reconnect replay (it owns "reconnect
    // replay", docs/05_MODULE_CONTRACTS.md). Registered here, inside the persistence conditional, because
    // it depends on the append-only session event repository and the CORE-RT-004 recipient resolver above.
    // It re-runs the SAME live recipient computation per event and keeps only the deliveries addressed to
    // the reconnecting caller's own groups, so reconnect replay re-filters every event through the central
    // Visibility engine and never leaks a hidden event (threat T3; docs/09_EVENT_CATALOG.md step 5,
    // docs/11_REALTIME_SYNC.md). The GET /api/v1/sessions/{sessionId}/events endpoint resolves it from the
    // request services and fails closed (503) when persistence is off.
    builder.Services.AddScoped<SessionReplayService>();

    // Asset metadata persistence (CORE-AST-001, the first story of the "Asset Storage and Authorization"
    // epic): the Assets module — FIRST appearing here — owns the workspace-scoped, tenant-scoped assets
    // table that holds the generic asset METADATA (docs/05_MODULE_CONTRACTS.md: the Assets module owns
    // "asset metadata", the "storage adapter", "upload/download authorization" and "signed URL creation";
    // csv/database_tables.csv: assets, module Assets, scope workspace, "Metadata only"). Registered here,
    // inside the persistence conditional, exactly like the session and entity repositories above; the
    // repository's lookups are scoped by organization id then workspace id (the organization boundary is
    // checked before the workspace boundary; threat T5), and there is NO list-everything method. The row
    // is METADATA ONLY — the binary content lives in private S3-compatible object storage, never in
    // PostgreSQL (docs/12_STORAGE_ASSETS.md; ADR 0006). The asset is PRIVATE BY DEFAULT: nothing on the
    // aggregate makes it publicly reachable, and the stored object is reached only through an authorized,
    // short-lived signed URL after a permission check (threat T4 "Asset leak"). The repository's org-scoped
    // FindByIdInOrganizationAsync backs the signed download route (CORE-AST-004), which discovers the
    // asset's workspace from the loaded row before authorizing. Linking to content blocks/entities
    // (CORE-AST-005) and the cleanup job (CORE-AST-006) are later stories and are deliberately not wired
    // here; the upload intent flow (CORE-AST-003) and the signed download flow (CORE-AST-004) are wired below.
    builder.Services.AddScoped<IAssetRepository, AssetRepository>();

    // Asset storage naming (CORE-AST-003, the upload intent flow): the deployment's PRIVATE provider/bucket
    // new assets are recorded against (docs/12_STORAGE_ASSETS.md metadata; docs/13_SELF_HOSTING_REQUIREMENTS.md
    // "object storage endpoint and credentials"). Read once from configuration (Assets:Storage:*) with safe,
    // private-by-default fallbacks so the host still runs without storage configuration — exactly as it runs
    // without a database connection string or an OIDC authority. Only the NAMING is read here (no storage
    // credentials, no SDK); the endpoint and credentials stay inside the deployment-supplied IAssetStorage
    // adapter (threat T7). Registered as a singleton next to the asset repository because the upload-intent
    // service consumes it.
    builder.Services.AddSingleton(AssetStorageLocation.FromConfiguration(builder.Configuration));

    // Asset upload-intent command (CORE-AST-003): registers a new PENDING asset with server-minted storage
    // coordinates (reusing the CORE-AST-001 Asset aggregate) and mints the short-lived signed upload URL via
    // the CORE-AST-002 IAssetStorage adapter port. Registered here because it depends on the asset repository,
    // the storage location and the storage adapter above. The asset is private by default and the signed URL
    // is minted before the row is persisted, so an unconfigured storage backend fails closed
    // (AssetStorageNotConfiguredException) leaving no orphan pending asset (the epic acceptance criterion;
    // threat T4 "Asset leak"). The endpoint authorizes the caller (role + tenant + workspace) BEFORE invoking
    // it. The signed download URL flow (CORE-AST-004) needs no extra service: its endpoint reuses the asset
    // repository, the tenant context resolver, the workspace member repository and the IAssetStorage adapter
    // above. Linking (CORE-AST-005) and cleanup (CORE-AST-006) are later stories.
    builder.Services.AddScoped<AssetUploadIntentService>();

    // Asset linking persistence + commands (CORE-AST-005, the asset-linking story of the "Asset Storage and
    // Authorization" epic): the Assets module owns the workspace-scoped, tenant-scoped asset_links table
    // that records that an asset is attached to a host-prepared resource — a content block or entity
    // (csv/database_tables.csv: asset_links, module Assets, scope workspace; the documented critical index
    // is asset_links(workspace_id, asset_id)). Registered here, inside the persistence conditional, exactly
    // like the asset repository above; the repository's lookups are scoped by organization id then
    // workspace id (the organization boundary is checked before the workspace boundary; threat T5), and
    // there is NO list-everything method. The AssetLinkService create command enforces the same-workspace
    // coupling for the polymorphic target reference (it resolves the content block/entity through the
    // workspace-scoped repository before linking, so an asset can never be linked to a foreign-workspace or
    // foreign-tenant resource; threats T5/T1) and is consumed by POST /api/v1/assets/{assetId}/links.
    builder.Services.AddScoped<IAssetLinkRepository, AssetLinkRepository>();
    builder.Services.AddScoped<AssetLinkService>();

    // Asset download authorization (CORE-AST-005): the Assets module's "may this workspace role download
    // this asset?" decision over the asset's links and the central Visibility engine. Registered here
    // because it depends on the asset link repository above and the VisibilityPolicy registered with the
    // Visibility module. It REUSES VisibilityPolicy.CanViewResource per linked target rather than
    // duplicating visibility logic (docs/05_MODULE_CONTRACTS.md), so an asset's audience access can never
    // diverge from the content's visibility: host-content roles always download; an audience role
    // (Participant/Observer) downloads only when the asset is linked to a content block/entity VISIBLE to
    // the audience; the audit role and any undefined role are denied fail-closed (threat T4 "Asset leak";
    // threat T2 visibility leak). The signed download endpoint (CORE-AST-004) now applies this policy
    // before minting a URL.
    builder.Services.AddScoped<AssetDownloadPolicy>();

    // Entitlement and plan definition catalog (CORE-ENTL-001, the first story of the "Entitlements and Quotas"
    // epic): the Entitlements module — FIRST appearing here — owns the GLOBAL entitlement_definitions,
    // plan_definitions and plan_entitlements tables that hold the deployment-wide monetization catalog
    // (docs/05_MODULE_CONTRACTS.md / docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: Core "stores
    // entitlements, enforces quotas"; csv/database_tables.csv: module Entitlements, scope global). Registered
    // here, inside the persistence conditional, exactly like the asset and recap repositories above. The
    // entitlement keys are GENERIC and product-neutral (the epic acceptance criterion "Generic entitlements
    // can be defined without vertical terminology"; AGENTS.md): a vertical maps them to its own paywall copy in
    // its UI. These are GLOBAL catalog tables (no organization_id), so the repositories are NOT tenant-scoped
    // and a list-everything read is the catalog read, not a T5 leak; the per-subject assignment and the
    // user-visible "premium state comes only from server entitlements" view are the later subject-entitlement
    // story (CORE-ENTL-002), the quota definition and quota-status API are CORE-ENTL-003, and quota enforcement
    // on protected commands is CORE-ENTL-004. There is NO entitlement HTTP route in this story
    // (csv/mobile_store_api_routes.csv defines the /v1/me/entitlements read for a later story).
    builder.Services.AddScoped<IEntitlementDefinitionRepository, EntitlementDefinitionRepository>();
    builder.Services.AddScoped<IPlanDefinitionRepository, PlanDefinitionRepository>();

    // Subject entitlement assignment and lookup (CORE-ENTL-002, the subject entitlement story of the
    // "Entitlements and Quotas" epic): the Entitlements module owns the subject_entitlements table that records
    // which generic subject (a user or a workspace) holds which catalog entitlement at which value. Registered
    // here, inside the persistence conditional, exactly like the definition repositories above. The repository
    // reads are scoped by the (subject_type, subject_id) pair, so one subject's premium state is never returned
    // through another subject's id (per-subject isolation). The resolver is the single server-side path that
    // produces a subject's effective entitlements, and the assignment service REUSES the CORE-ENTL-001 plan and
    // entitlement catalog to grant/revoke them — so "User-visible premium state comes only from server
    // entitlements" (the epic acceptance criterion; docs/21 "Never trust client-side premium flags"): a subject
    // with no active server assignment is not entitled (fail-closed default), and a revoked assignment removes
    // the premium state. There is NO entitlement HTTP route in this story; the resolver/assignment primitives
    // are the reusable core the later GET /v1/me/entitlements read (csv/mobile_store_api_routes.csv), the quota
    // status API (CORE-ENTL-003) and quota enforcement (CORE-ENTL-004) sit on.
    builder.Services.AddScoped<ISubjectEntitlementRepository, SubjectEntitlementRepository>();
    builder.Services.AddScoped<SubjectEntitlementResolver>();
    builder.Services.AddScoped<SubjectEntitlementAssignmentService>();

    // Ad eligibility decision (CORE-ADS-001, the Ad Eligibility epic): the entitlement-driven service behind
    // GET /api/v1/me/ad-eligibility. Registered here, inside the persistence conditional, because it composes the
    // CORE-ENTL-002 SubjectEntitlementResolver above; the AdEligibilityPolicy it applies is a pure static function
    // (no DI). It decides whether a subject must see ads ENTIRELY from server entitlements and FAIL-CLOSED (no
    // ad-free grant ⇒ ads required), so "Core returns ad eligibility without knowing ad placements" (the epic
    // acceptance criterion; docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md) — Core never renders, requests, configures
    // or places ads.
    builder.Services.AddScoped<AdEligibilityService>();

    // Quota definition + usage persistence and the server-side quota-status calculation (CORE-ENTL-003, the quota
    // definition and quota status story of the "Entitlements and Quotas" epic): the Entitlements module owns the
    // global quota_definitions catalog (how a numeric quota entitlement is measured — for which subject kind and in
    // which unit) and the per-subject quota_usage table (docs/05_MODULE_CONTRACTS.md;
    // docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions"; csv/database_tables.csv: module
    // Entitlements). Registered here, inside the persistence conditional, exactly like the entitlement repositories
    // above. The quota-definition repository is a global catalog read (no organization_id); the usage repository's
    // reads are scoped by the (subject_type, subject_id) pair, so one subject's usage is never returned through
    // another subject's id (per-subject isolation). The QuotaStatusCalculator combines the active quota definitions
    // for a subject kind, the subject's effective entitlement limits (REUSING the CORE-ENTL-002 resolver) and the
    // recorded usage into the subject's quota status — computed entirely server-side and FAIL-CLOSED (no
    // entitlement ⇒ no allowance), the epic acceptance criterion "Quota status is calculated server-side for
    // subjects and workspaces". The GET /api/v1/me/quota-status and /api/v1/workspaces/{workspaceId}/quota-status
    // endpoints sit on it; enforcing quotas on protected workspace/session commands (incrementing usage and
    // rejecting over-limit) is the next story (CORE-ENTL-004).
    builder.Services.AddScoped<IQuotaDefinitionRepository, QuotaDefinitionRepository>();
    builder.Services.AddScoped<IQuotaUsageRepository, QuotaUsageRepository>();
    builder.Services.AddScoped<QuotaStatusCalculator>();

    // Quota ENFORCEMENT on protected commands (CORE-ENTL-004, the quota enforcement story of the "Entitlements and
    // Quotas" epic): the Entitlements module's server-side gate that rejects a protected workspace/session command
    // when it would exceed a subject's free limit, and increments the recorded usage when it succeeds (releasing it
    // when a counted resource is freed). Registered here, inside the persistence conditional, because it composes the
    // quota-definition catalog read, the CORE-ENTL-002 entitlement resolver, the per-subject usage repository and the
    // TimeProvider above. It REUSES QuotaStatus.Calculate (the single quota math the quota-status read uses), so a
    // command's allow/deny can never diverge from the reported status; it is FAIL-CLOSED (a subject not entitled to a
    // defined quota has no allowance) and computed entirely server-side, so "Free limits cannot be bypassed by
    // clients" (the epic acceptance criterion; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). It is consumed by
    // the workspace-create command (workspace.active.max, the creating user subject) and the session start/end
    // commands (session.active.max, the session's workspace subject); no new HTTP route is added.
    builder.Services.AddScoped<QuotaEnforcementService>();

    // Purchase transaction persistence and audit trail (CORE-STORE-002, the second story of the "Store Purchase
    // Verification" epic): the Store module owns the purchase_transactions table (the persisted verified purchase,
    // keyed idempotently on the provider + provider_transaction_id pair) and the append-only purchase_events table
    // (the audit trail of purchase state changes) — docs/05_MODULE_CONTRACTS.md;
    // docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions"; csv/database_tables.csv: module Store.
    // Registered here, inside the persistence conditional, exactly like the entitlement repositories above, because
    // the repositories depend on the DbContext. CORE-STORE-001 modeled the provider abstraction and deferred
    // "persistence of the verified transaction" to here; the PurchaseTransactionService records a verified purchase
    // IDEMPOTENTLY (the unique purchase_transactions(provider, provider_transaction_id) index makes a client retry,
    // a replayed proof or a duplicate notification a safe no-op — "Store notifications must be idempotent", docs/21)
    // and appends a purchase_events entry for every purchase STATE CHANGE, so "all purchase state changes are
    // persisted and auditable" (the story acceptance criterion). Authorization is upstream of this service: the
    // Apple (CORE-STORE-003) and Google (CORE-STORE-004) verification endpoints authorize the caller server-side and
    // verify the proof BEFORE recording, and the store-notification handler (CORE-STORE-005) drives the status
    // changes and their entitlement effects; this story supplies the generic persistence + audit primitives only,
    // and adds no store HTTP route.
    builder.Services.AddScoped<IPurchaseTransactionRepository, PurchaseTransactionRepository>();
    builder.Services.AddScoped<IPurchaseEventRepository, PurchaseEventRepository>();
    builder.Services.AddScoped<PurchaseTransactionService>();

    // Idempotent store notification handling (CORE-STORE-005, the "Store Notifications" epic): the Store module
    // owns the append-only store_notification_events table (the dedup ledger + audit fact of every handled store
    // notification) — docs/05_MODULE_CONTRACTS.md; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database
    // additions"; csv/database_tables.csv: module Store. Registered here, inside the persistence conditional, like
    // the purchase repositories above, because the repository depends on the DbContext. The StoreNotificationService
    // is handed an already-validated, normalized notification (the deployment parser validates signature/source
    // upstream), deduplicates it by its (provider, provider_notification_id) pair (the unique index makes a
    // re-delivered notification a safe no-op — "Store notifications must be idempotent", docs/21) and drives the
    // affected purchase's lifecycle by REUSING the CORE-STORE-002 PurchaseTransactionService.ChangeStatusAsync, so a
    // renewal/cancellation/refund/grace period updates the server-side purchase status (the source of truth for
    // premium state) safely and auditably (the story acceptance criterion). The two unauthenticated store
    // notification endpoints sit on this service.
    builder.Services.AddScoped<IStoreNotificationEventRepository, StoreNotificationEventRepository>();
    builder.Services.AddScoped<StoreNotificationService>();

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

// Current-principal endpoint (CORE-API-002): GET /api/v1/me, the IdentityAccess
// module's read of the authenticated caller's principal context (their user
// profile + organization memberships + roles). It lives in an authenticated route
// group (anonymous callers get 401) and fails closed (503) when persistence is not
// configured, exactly like the organization endpoints. No new DI registration is
// required: the user-profile reference service and the organization repository it
// consumes are already registered above inside the persistence conditional. A
// service-account principal is denied 403 (only a human user holds a profile and
// memberships), and the membership list is intersected with the token's
// organization claims so the principal context never exposes a tenant the token
// does not assert; the response is a safe DTO with no token/secret (threats
// T5/T7).
app.MapMeEndpoints();

// Organization endpoints (CORE-API-001): the tenant create/read API,
// GET/POST /api/v1/organizations. They live in an authenticated route group and
// fail closed (503) when persistence is not configured, exactly like the
// workspace endpoints. No new DI registration is required: the organization
// repository and the user-profile reference service they consume are already
// registered above inside the persistence conditional. The list returns only the
// organizations the caller is a member of AND the token claims; the create makes
// the caller the founding Owner atomically, hides a foreign tenant (an unclaimed
// slug) as 404 and rejects a taken slug as 409 without granting any membership
// (threats T1/T5).
app.MapOrganizationEndpoints();

// Workspace endpoints (CORE-WS-003): the first domain HTTP endpoints. They live
// in an authenticated route group and fail closed (503) when persistence is not
// configured, so mapping them never crashes startup.
app.MapWorkspaceEndpoints();

// Session endpoints: the workspace-scoped create/list routes
// (GET/POST /api/v1/workspaces/{workspaceId}/sessions, CORE-API-003) and the
// by-session-id start/end lifecycle commands (CORE-SES-004). They live in
// authenticated route groups and fail closed (503) when persistence is not
// configured, exactly like the workspace endpoints. No new DI registration is
// required: the tenant context resolver, the session repository, the workspace
// member repository and the quota enforcement service they consume are already
// registered above inside the persistence conditional. Create enforces (but does
// not consume) the workspace's session.active.max quota; start consumes it and end
// releases it. The durable SessionCreated/SessionStarted/SessionEnded events are
// deferred to the Session Event Stream epic (CORE-EVT-001; no emission here); the
// persisted status transition is the behavior delivered (docs/09_EVENT_CATALOG.md;
// csv/database_tables.csv assigns session_events to the Realtime module).
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

// Hide (un-reveal) command endpoint (CORE-REV-001, the "Reveal Lifecycle" hide): the inverse of the
// reveal route, POST /api/v1/sessions/{sessionId}/hide. It takes a reveal back so a previously visible
// resource becomes Hidden again (audience and selected participants stop seeing it), idempotently (its
// own Idempotency-Key scope, distinct from reveal) and with the same authz (Owner/Admin/Host/CoHost in
// the session's own workspace). It reuses the SAME RevealService and audit producer, appends a
// VisibilityRuleChanged audit record on a real change, and emits a durable ContentHidden event IFF
// visibility actually changed. No new persistence registration is required (RevealService is already
// registered for the reveal route above).
app.MapHideEndpoints();

// Session event reconnect-replay endpoint (CORE-RT-005): the Realtime module's reconnect replay route,
// GET /api/v1/sessions/{sessionId}/events. It lives in an authenticated route group and fails closed (503)
// when persistence is not configured, exactly like the reveal/session endpoints. No new persistence
// registration beyond SessionReplayService above is required: the tenant context resolver and the realtime
// connection resolver it reuses for authorization are already registered. It authorizes the caller exactly
// as the live hub connection does (the same connection resolver and server-managed groups) and replays
// only the events delivered to the caller's groups, re-filtered through the central Visibility engine, so
// reconnect replay never leaks a hidden event (threat T3; docs/09_EVENT_CATALOG.md "Reconnect replay").
app.MapSessionEventReplayEndpoints();

// Scene content endpoints (CORE-SCENE-003; CORE-API-007 adds the by-scene-id read):
// the Scenes module's HTTP routes, GET/POST /api/v1/workspaces/{workspaceId}/scenes and
// GET /api/v1/scenes/{sceneId}. They live in authenticated route groups and fail closed
// (503) when persistence is not configured, exactly like the workspace endpoints. No new
// DI registration is required: the tenant context resolver, the scene repository and the
// workspace member repository they consume are already registered above inside the
// persistence conditional. The GET list and the GET by-id both PROJECT BY ROLE through the
// same SceneProjection (the host shape to host-capable/metadata roles, the stripped
// participant shape to audience roles — the "Projection by role" route note, CORE-SCENE-004).
// The by-id read resolves the scene within the query-supplied organization, discovers its
// workspace from the loaded row and authorizes the caller's membership there (every denial
// hidden as 404). The POST assigns the scene order server-side as append-to-end (no
// client-supplied order, no reorder route).
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

// Entity relationship removal endpoint (CORE-LIFE-002, the "Resource Lifecycle and Deletion" epic):
// the Entities module's relationship REMOVAL route,
// DELETE /api/v1/workspaces/{workspaceId}/entity-relationships/{relationshipId}. It lives in an
// authenticated route group and fails closed (503) when persistence is not configured, exactly like
// the scene/content-block endpoints. No new DI registration is required: the tenant context resolver,
// the entity-relationship repository (extended with RemoveAsync) and the workspace member repository
// it consumes are already registered above inside the persistence conditional. The edge model
// previously only ever grew (an edge could be added but never removed); this adds the inverse. The
// parent workspace is resolved FIRST (the route pins {workspaceId}, the tenant comes from the required
// ?organizationSlug=), the caller is authorized by their role in that workspace (Owner/Admin/Host/CoHost),
// and the edge is then loaded through the tenant- AND workspace-scoped FindByIdAsync — so an edge in
// another workspace or tenant is never reachable to remove even when its id is known (the endpoint FKs
// do not DB-enforce same-workspace). Every denial is hidden as 404 (an insufficient role as 403), and
// removing a non-existent edge is a safe 404 (threats T1/T5). It adds no event and no audit record,
// faithful to the CORE-ENT-003 add-edge precedent.
app.MapEntityRelationshipEndpoints();

// Asset endpoints (CORE-AST-003 upload intent, CORE-AST-004 signed download, CORE-AST-005
// linking): the Assets module's HTTP routes, POST /api/v1/assets/upload-intent,
// GET /api/v1/assets/{assetId}/download-url and POST /api/v1/assets/{assetId}/links. They live
// in an authenticated route group and fail closed (503) when persistence is not configured,
// exactly like the reveal/scene endpoints. No new DI registration beyond the upload-intent
// service, the asset link service, the download policy and the storage location above is
// required: the tenant context resolver, the asset/asset-link repositories, the workspace member
// repository and the IAssetStorage adapter they reuse are already registered. Upload-intent
// authorizes the caller by their role in the target workspace (Owner/Admin/Host/CoHost), mints
// server-side storage coordinates, registers a PENDING asset and returns a short-lived signed
// upload URL. The signed download flow resolves the asset within the query-supplied organization,
// discovers its workspace from the loaded row, authorizes the caller through the central Assets
// download policy (host-content roles always; an audience role only when the asset is linked to a
// VISIBLE content block/entity — CORE-AST-005; every denial hidden as 404, an insufficient role as
// 403, a still-pending asset as 409) and returns a short-lived signed download URL. The linking
// flow resolves the asset within the body-supplied organization, authorizes a host-content role,
// verifies the target content block/entity is in the asset's own workspace (the same-workspace
// coupling for the polymorphic target; threats T5/T1) and records the link (201; a repeat is 409, a
// missing target hidden as 404). The asset is private by default and an unconfigured storage
// backend fails closed with 503 in both signed-URL flows (threat T4 "Asset leak"). The cleanup job
// (CORE-AST-006) is a later story.
app.MapAssetEndpoints();

// Quota-status endpoints (CORE-ENTL-003): the Entitlements module's quota-status reads,
// GET /api/v1/me/quota-status and GET /api/v1/workspaces/{workspaceId}/quota-status. They live in an
// authenticated route group and fail closed (503) when persistence is not configured, exactly like the
// workspace/asset endpoints. No new DI registration beyond the quota repositories and the QuotaStatusCalculator
// above is required: the user profile service, the tenant context resolver and the workspace member repository
// they reuse are already registered. /me/quota-status resolves the current user (a service account is 403) and
// calculates the USER subject's status; the workspace route resolves the tenant from the query-supplied
// organization, requires the caller to be a host-capable member of the workspace (every denial hidden as 404, an
// insufficient role as 403) and calculates the WORKSPACE subject's status. Both compute the status entirely
// server-side and fail-closed (the epic acceptance criterion). Quota ENFORCEMENT on protected commands is
// CORE-ENTL-004.
app.MapQuotaStatusEndpoints();

// Ad eligibility endpoint (CORE-ADS-001): the Ad Eligibility epic's single read, GET /api/v1/me/ad-eligibility. It
// lives in an authenticated route group and fails closed (503) when persistence is not configured, exactly like the
// quota-status endpoint. No new DI registration beyond the AdEligibilityService above is required: the user profile
// service and the entitlement resolver it reuses are already registered. It resolves the current user (a service
// account is 403) and decides the USER subject's ad eligibility ENTIRELY from server entitlements and fail-closed (no
// ad-free grant ⇒ ads required), returning only the generic decision — never an ad placement, provider/unit id or SDK
// config. So "Core returns ad eligibility without knowing ad placements" (the epic acceptance criterion;
// docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md): Core decides eligibility, the vertical owns all ad rendering.
app.MapAdEligibilityEndpoints();

// Current-user effective-entitlements endpoint (CORE-API-007): the Entitlements module's documented-not-built
// read, GET /api/v1/me/entitlements (csv/mobile_store_api_routes.csv). It lives in an authenticated route group
// and fails closed (503) when persistence is not configured, exactly like the ad-eligibility/quota-status
// endpoints. No new DI registration is required: the CORE-ENTL-002 SubjectEntitlementResolver and the user
// profile service it reuses are already registered above. It resolves the current user (a service account is 403)
// and returns the USER subject's effective entitlements resolved ENTIRELY from server entitlements — the generic
// key + value only, never a subject id, source plan or authorization rationale (threat T7). So "User-visible
// premium state comes only from server entitlements" (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md), reusing
// the same resolver an internal feature guard would so the REST read can never diverge.
app.MapMeEntitlementsEndpoints();

// Apple transaction verification endpoint (CORE-STORE-003): the Store module's first HTTP route,
// POST /api/v1/purchases/apple/transactions. It lives in an authenticated route group and fails closed (503)
// when persistence is not configured, exactly like the asset/quota endpoints. No new DI registration is
// required: the PurchaseVerificationProviderResolver (registered unconditionally above) and the
// PurchaseTransactionService + TimeProvider (registered in the persistence conditional, CORE-STORE-002) it
// reuses are already registered. It authorizes the caller as a user principal (a service account is 403; the
// purchase is global, so there is no tenant boundary — CORE-STORE-002), resolves the deployment-supplied Apple
// verifier and verifies the submitted proof, and ONLY a verified result is recorded as a PurchaseTransaction
// (verify-then-record): a rejected proof is 422 and records nothing, and an unconfigured verifier is 503, so
// "Apple transaction data is verified before entitlements are granted" (the story acceptance criterion;
// docs/21). Granting the SubjectEntitlement from the recorded purchase and the buyer linkage
// (billing_account_links) are later stories; the Google endpoint is CORE-STORE-004.
app.MapApplePurchaseEndpoints();

// Google purchase token verification endpoint (CORE-STORE-004): the Store module's second HTTP route, the
// Google analogue of the Apple endpoint above, POST /api/v1/purchases/google/tokens. It lives in an
// authenticated route group and fails closed (503) when persistence is not configured, exactly like the
// Apple/asset/quota endpoints. No new DI registration is required: the PurchaseVerificationProviderResolver
// (registered unconditionally above) and the PurchaseTransactionService + TimeProvider (registered in the
// persistence conditional, CORE-STORE-002) it reuses are already registered. It authorizes the caller as a
// user principal (a service account is 403; the purchase is global, so there is no tenant boundary —
// CORE-STORE-002), resolves the deployment-supplied Google verifier and verifies the submitted purchase token,
// and ONLY a verified result is recorded as a PurchaseTransaction (verify-then-record): a rejected token is 422
// and records nothing, and an unconfigured verifier is 503, so "Google purchase tokens are verified before
// entitlements are granted" (the story acceptance criterion; docs/21). Granting the SubjectEntitlement from the
// recorded purchase and the buyer linkage (billing_account_links) are later stories; idempotent store
// notifications are CORE-STORE-005.
app.MapGooglePurchaseEndpoints();

// Store notification endpoints (CORE-STORE-005): the Store module's notification-handling routes,
// POST /api/v1/store-notifications/apple and POST /api/v1/store-notifications/google/rtdn. Unlike the
// verification routes these are UNAUTHENTICATED server-to-server callbacks (csv/mobile_store_api_routes.csv:
// auth_required=false), mapped AllowAnonymous; authenticity comes from the deployment-supplied parser adapter
// validating the payload's signature/source, not from an OIDC token. They fail closed (503) when persistence is
// not configured, and (503) when no parser is configured for the provider, so an unauthenticated payload never
// changes a purchase without a real validator behind it. A forged/unparseable payload is 400 and records
// nothing; a validated, normalized notification is deduplicated and drives the affected purchase's lifecycle
// (idempotently and auditably, reusing CORE-STORE-002), so "Renewals, cancellations, refunds and grace periods
// update entitlements safely" (the story acceptance criterion; docs/21). No new DI registration is required: the
// StoreNotificationParserResolver (registered unconditionally above) and the StoreNotificationService +
// TimeProvider (registered in the persistence conditional) it reuses are already registered.
app.MapStoreNotificationEndpoints();

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
