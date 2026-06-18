// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api;
using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Exports;
using LiveCore.Api.Hosting;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Observability;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Recaps;
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

// AGPL section 13 source-offer configuration guard (CORE-LIC-005). Build the offer once at startup so a
// deployment whose source-offer configuration would yield a wrong or non-fetchable Corresponding Source is
// REFUSED HERE rather than first surfacing on a /source request: a deployment that declares modified source
// (SourceOffer:Modified=true) but left SourceOffer:RepositoryUrl unset would silently offer the canonical
// UPSTREAM source instead of the modified source actually running, and a malformed RepositoryUrl would offer a
// location no remote user can fetch. This mirrors the OIDC audience startup guard (CORE-OPS-004): a foot-gun
// that would otherwise serve a wrong value is refused at startup, fail-closed. An unmodified deployment leaves
// both unset and resolves to the revision-pinned canonical upstream offer, so it always starts. The resolved
// value is discarded; only its validation side effect matters here. See docs/16_LICENSING.md.
_ = SourceOffer.ForRunningBuild(builder.Configuration);

builder.Services.AddHealthChecks();

// Operational metrics (CORE-OBS-001, extended with the CORE-OBS-007 service-level indicators). LiveCoreMetrics
// owns the eight original signals docs/15_OBSERVABILITY.md requires (request duration/error rate, realtime
// connections, reveal latency, event-delivery failures, asset failures, database failures, background-job
// failures) plus the SLIs (auth-failure rate, rate-limit rejections, and per-loop worker job success/duration/
// backlog) on a single OpenTelemetry-collected meter, and
// this wires the MeterProvider plus the Prometheus exporter that backs the /metrics scrape endpoint
// (mapped below). It is registered UNCONDITIONALLY (no persistence/identity needed), so the surface exists
// even when the host runs without a database, exactly like the health endpoints. The instrumentation seams
// (the request middleware, the realtime hub, the reveal command, the event publisher, the asset storage
// decorator and the EF Core command interceptor) all record onto the same LiveCoreMetrics singleton. The
// scrape endpoint is unauthenticated by convention and carries only low-cardinality aggregates, never content
// or identifiers (threat T7 in docs/07_SECURITY_THREAT_MODEL.md; restricted at the edge per
// docs/13_SELF_HOSTING_REQUIREMENTS.md).
builder.Services.AddLiveCorePrometheusMetrics();

// Distributed tracing (CORE-OBS-003). LiveCoreActivitySource owns the single ActivitySource the key request
// and realtime flows docs/15_OBSERVABILITY.md calls out produce spans through — the HTTP request pipeline
// (RequestTracingMiddleware, added below) and the reveal/publish path (the reveal endpoint + the session-event
// publisher) — and this wires the OpenTelemetry TracerProvider that exports them. It is registered
// UNCONDITIONALLY (it needs no database/identity), so the source exists even when the host runs without
// persistence, exactly like the metrics. The OTLP exporter is attached ONLY when a collector endpoint is
// configured (Tracing:Otlp:Endpoint); with nothing configured spans are still produced (and any in-process
// listener observes them) but shipped nowhere, so an unconfigured host never reaches a non-existent collector
// (the same fail-closed/inert posture as the storage adapter and realtime backplane). The endpoint comes from
// configuration only, and spans carry only low-cardinality, non-sensitive attributes — never content or a
// token (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
builder.Services.AddLiveCoreOpenTelemetryTracing(builder.Configuration);

// Per-request structured log context (CORE-OBS-002). RequestLogContext is the single, request-scoped holder
// of the identifiers docs/15_OBSERVABILITY.md requires on every request/event log line (request_id,
// organization_id, workspace_id, session_id, user_id, event_id). The RequestLogContextMiddleware (added to
// the pipeline below) opens one logging scope around the request with it as the scope state and seeds the
// edge-available keys; the authoritative owners enrich the rest in their own scope — the TenantContextResolver
// sets organization_id from the resolved tenant, the SessionEventPublisher sets event_id from the published
// event — so JSON logging (already wired with IncludeScopes, CORE-FND-004) carries the documented context
// without any module logging sensitive content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). Registered
// scoped and UNCONDITIONALLY (it needs no database), so the request scope exists even when the host runs
// without persistence; the resolver/publisher that enrich it are registered inside the persistence
// conditional and resolve the same scoped instance.
builder.Services.AddScoped<RequestLogContext>();

// CORS allow-list for browser/PWA clients (CORE-OPS-003). One named policy
// (CorsConfiguration.PolicyName) is applied to BOTH the REST API and the SignalR
// hub, read from configuration only (Cors:AllowedOrigins) and FAIL-CLOSED by
// default: with no configured origins no cross-origin browser client is allowed.
// CORS is a browser-enforced boundary layered on top of the OIDC/tenant checks
// every endpoint already applies; it never widens server-side authorization
// (docs/07_SECURITY_THREAT_MODEL.md; docs/13_SELF_HOSTING_REQUIREMENTS.md lists
// "CORS allowed origins" as runtime configuration).
builder.Services.AddLiveCoreCors(builder.Configuration);

// Forwarded-headers posture for running behind a TLS-terminating reverse proxy
// (CORE-OPS-003). UseForwardedHeaders (added to the pipeline below) restores the
// real client scheme/host/IP from the proxy's X-Forwarded-* headers, but only
// when the immediate peer is a trusted proxy: loopback by the framework default,
// plus any proxy/network named in configuration (ForwardedHeaders:KnownProxies /
// :KnownNetworks). Nothing is hardcoded, so an untrusted client can never spoof
// the scheme (threat T7).
builder.Services.AddLiveCoreForwardedHeaders(builder.Configuration);

// App-level HSTS and HTTPS redirection posture (CORE-SEC-005, the "Audit Integrity and Security Hardening"
// epic). The Core API is documented to run behind a TLS-terminating reverse proxy (CORE-OPS-003), where the
// public HTTPS boundary — the http->https redirect and the Strict-Transport-Security header — lives at the
// edge; before this story the host wired NEITHER UseHsts NOR UseHttpsRedirection, so a deployment that runs
// WITHOUT a terminating proxy (terminating TLS in Kestrel directly) had no app-level transport-security
// defense. This registers the framework's HstsOptions/HttpsRedirectionOptions (part of the shared framework, NO
// new dependency) configured from HttpsSecurity:*, and the resolved HttpsSecuritySettings the pipeline reads to
// decide whether to add UseHsts/UseHttpsRedirection below. Both toggles are FAIL-SAFE OFF by default, so the
// documented proxy posture is unchanged and a deployment opts in only when it terminates TLS itself. Because
// UseForwardedHeaders runs first, behind a trusted proxy the app already sees the forwarded https scheme, so an
// enabled redirect does not fire and does not fight the edge (threat T7).
builder.Services.AddLiveCoreHttpsSecurity(builder.Configuration);

// Baseline HTTP security response headers (CORE-SEC-009, the "Audit Integrity and Security Hardening" epic).
// Before this story the pipeline wired NO response-header middleware (only Kestrel AddServerHeader=false above),
// so every JSON/Problem-Details response was served with no anti-sniffing or framing defense. This registers the
// resolved SecurityHeadersSettings the SecurityHeadersMiddleware (added to the pipeline below) reads; the
// middleware adds X-Content-Type-Options: nosniff, Referrer-Policy: no-referrer and a deny-all
// Content-Security-Policy (default-src 'none'; frame-ancestors 'none') to every API, error and SignalR-negotiate
// response. The deny-all CSP is defensible because the API renders no HTML (no script/style sources to allow);
// frame-ancestors subsumes X-Frame-Options. The feature is ON by default and each header is individually
// configurable (SecurityHeaders:*); the values are static directives that leak no tenant/principal detail
// (threat T7). It complements CORE-SEC-005 (HSTS/HTTPS-redirect) and, like CORS/the rate limiter, never widens
// the OIDC/tenant authorization every endpoint already enforces.
builder.Services.AddLiveCoreSecurityHeaders(builder.Configuration);

// HTTP response compression for JSON (CORE-PERF-006, the "Performance and Scalability" epic). Before this story
// the pipeline wired NEITHER AddResponseCompression NOR UseResponseCompression, so the list/feed/replay
// endpoints — which return JSON ARRAYS that can be large for a busy session — were always sent uncompressed,
// inflating bandwidth and latency for large sessions and mobile clients. This registers ASP.NET Core's
// response-compression services (Brotli preferred, gzip fallback; part of the shared framework, NO new
// dependency) configured to compress ONLY application/json and application/problem+json, plus the resolved
// ResponseCompressionSettings the pipeline reads to decide whether to add UseResponseCompression below. The
// middleware is added (below) only for NON-hub paths, so the /hubs SignalR negotiate/transport is never
// double-compressed. It changes only transfer encoding, never response content: a client that sends no
// Accept-Encoding (or one the server cannot satisfy) gets the identical uncompressed body. The feature is ON by
// default and configurable (ResponseCompression:*; docs/13_SELF_HOSTING_REQUIREMENTS.md), and — like CORS, the
// rate limiter and the security headers — it never widens the OIDC/tenant authorization every endpoint enforces.
builder.Services.AddLiveCoreResponseCompression(builder.Configuration);

// Graceful shutdown drain window (CORE-DEP-002, the "Deployment and Supply Chain" epic). On a rolling restart
// the orchestrator sends this instance a termination signal; the host then stops accepting new connections and
// DRAINS its in-flight work before exiting. This applies ONE explicit, tuned, configurable
// HostOptions.ShutdownTimeout (Hosting:ShutdownTimeout) so an in-flight HTTP request or SignalR connection is
// allowed to complete within the window rather than being abruptly cut — before this story the host left the
// window at the implicit framework default, untuned and not coordinated with the deployment's termination grace
// period. The same wiring is applied identically in the worker host (WorkerHostFactory), so both hosts drain
// within a tuned window. The window is deployment policy read from configuration only with a safe default
// (docs/13_SELF_HOSTING_REQUIREMENTS.md "Graceful shutdown drain window"), and it must stay at or below the
// orchestration grace period so the process exits cleanly before it is force-killed. A multi-instance SignalR
// deployment additionally requires sticky sessions / ARR affinity at the proxy for the negotiate/transport
// handshake (documented alongside the Redis backplane in docs/13); affinity is an edge concern, not a host
// setting, so it is configured at the proxy rather than here.
builder.Services.AddLiveCoreGracefulShutdown(builder.Configuration);

// Request rate limiting (CORE-SEC-001, extended by CORE-SEC-007). This wires ASP.NET Core's built-in rate
// limiting (UseRateLimiter, added to the pipeline below; part of the shared framework, NO new dependency) as
// complementary fixed-window limiters: a STRICT per-IP limit on the anonymous store-notification webhooks (the
// named WebhookPolicyName the webhook route group opts into — which do DB work and run a deployment-supplied
// parser per call, a ledger-amplification/enumeration surface anyone can POST to) and a GLOBAL limiter that is
// per-principal on the authenticated surface (partitioned on the OIDC issuer+subject pair so one caller's burst
// cannot exhaust another's; threats T5/T1) AND per-IP on the anonymous NON-webhook surface — the /hubs/session
// negotiate and any anonymous REST probe — so an unauthenticated flood is bounded too (CORE-SEC-007, threats
// T9/T5). The infrastructure endpoints (/health/*, /metrics) opt out with DisableRateLimiting so probes/scrapes
// stay reachable. Every limit is read from configuration only (RateLimiting:*;
// docs/13_SELF_HOSTING_REQUIREMENTS.md) with safe, generous defaults so normal traffic is unaffected, and the
// feature can be disabled (RateLimiting:Enabled=false) for a deployment that throttles at its edge — but the
// anonymous webhook then keeps a NON-DISABLEABLE request-rate floor (and its body-size cap), so disabling the
// limiter can never fully remove webhook volume protection. A rejected request gets 429 as RFC 7807 Problem
// Details with a Retry-After header and no tenant/principal/resource detail (threat T7). Rate limiting is a
// coarse abuse ceiling layered ON TOP OF the OIDC/tenant authorization every endpoint already enforces; it never
// widens authorization (docs/07_SECURITY_THREAT_MODEL.md), exactly like the CORS allow-list.
builder.Services.AddLiveCoreRateLimiting(builder.Configuration);

// In-process mobile API gateway (CORE-MON-009, the Monetization v1 epic). The store/entitlement routes are
// documented in their mobile-facing shape under a bare /v1 prefix (csv/mobile_store_api_routes.csv; docs/21;
// docs/22), but every Core endpoint is mounted under the /api/v1 prefix docs/08_API_CONTRACTS.md mandates, so
// a mobile client following the documented /v1/... path literally would 404. MobileApiGateway registers an
// IStartupFilter that rewrites a request matching one of the documented mobile routes from its /v1 path to the
// corresponding /api/v1 path BEFORE routing, so it dispatches to the SAME already-implemented endpoint with
// authentication, tenant/subject authorization and server-side resolution unchanged. It is registered ahead of
// routing precisely because the original /v1 path is unmounted; a startup filter runs before the
// WebApplication's automatically-added routing middleware without re-ordering the pipeline below. It is a pure,
// scoped addressing alias (only the EXACT documented mobile routes are rewritten — any other /v1 path still
// 404s, so the rest of the API is never exposed under a second prefix) and adds no endpoint, service, table or
// migration; the rewrite never reads the token, the body or any tenant identifier (threats T1/T5/T7).
builder.Services.AddLiveCoreMobileApiGateway();

// OpenAPI document generation for the v1 API (CORE-OAS-001). OpenApiConfiguration registers the
// Microsoft.AspNetCore.OpenApi generator that builds an OpenAPI 3 document FROM the running minimal-API route
// table (the ApiExplorer metadata of the endpoints mapped below), scoped to the /api/v1 surface, so the
// document can never diverge from the routes the host actually mounts (the committed openapi/livecore-v1.json
// build artifact a CI gate checks for drift). It is registered UNCONDITIONALLY (it needs no database or
// identity, exactly like the metrics/health surfaces), and the route table the document describes is identical
// whether or not persistence is configured because every /api/v1 endpoint below is mapped unconditionally and
// fails closed at runtime. The read-only explorer endpoint is mapped LATER and ONLY outside Production
// (app.MapLiveCoreOpenApi), so a production deployment exposes no schema-discovery surface; the document
// carries only route shapes, generic schema names and the RFC 7807 Problem Details error shape — never a
// secret, tenant identifier or content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
builder.Services.AddLiveCoreOpenApi();

// Realtime SignalR services and the scale-out backplane (CORE-RT-001 SignalR; CORE-RT-006 the
// IRealtimeBackplane seam; CORE-OPS-007 the conditional Redis/Valkey backplane). SignalR is part of the
// ASP.NET Core shared framework (docs/11_REALTIME_SYNC.md mandates it). The IRealtimeBackplane is the single
// transport boundary a computed event delivery crosses on its way to connected clients: the in-process
// implementation sends a recipient-safe payload to its server-managed group via IHubContext<SessionHub>.
// CORE-OPS-007 makes that send cross multiple API instances CONDITIONALLY on the deployment's
// Realtime:Backplane:* configuration: with a backplane connection string configured the SignalR
// HubLifetimeManager becomes Redis/Valkey-backed (Microsoft.AspNetCore.SignalR.StackExchangeRedis), so the
// same group send reaches connections on every instance (the epic acceptance criterion); with nothing
// configured the host keeps the in-process default — correct for a single API instance, the documented
// single-instance constraint. Enabling the backplane changes only the transport beneath IHubContext: the
// per-recipient recipient computation and the one-payload-to-one-group send path are UNCHANGED, so the
// backplane only ever transports an ALREADY-AUTHORIZED delivery and cannot widen the audience or leak a
// hidden event (threat T3). No backplane connection string lives in source (threat T7).
builder.Services.AddLiveCoreRealtime(builder.Configuration);

// Asset storage adapter seam (CORE-AST-002, the storage adapter interface story of the "Asset Storage and
// Authorization" epic). IAssetStorage is the single port between Core and the private, S3-compatible
// object storage that holds an asset's binary content (docs/05_MODULE_CONTRACTS.md: the Assets module owns
// the "storage adapter" and "signed URL creation"; docs/12_STORAGE_ASSETS.md; ADR 0006). The concrete,
// concrete S3-compatible adapter (CORE-OPS-006) is selected CONDITIONALLY on the deployment's
// Assets:Storage:* configuration: with an endpoint and credentials configured, the API mints real SigV4
// pre-signed upload/download URLs against the S3-compatible backend (RustFS self-hosted or any S3-compatible
// provider hosted; docs/12_STORAGE_ASSETS.md; ADR 0006); with nothing (or only a partial) configuration the
// FAIL-CLOSED UnconfiguredAssetStorage stays in place, so every asset operation throws
// AssetStorageNotConfiguredException rather than serving bytes some insecure way and assets stay private by
// default even when storage is not configured (the epic acceptance criterion; threat T4 "Asset leak"). This
// mirrors how the host runs without a database connection string or an OIDC authority. No storage credential
// is read anywhere but configuration (threat T7). The upload-intent flow (CORE-AST-003) and the signed
// download flow with authorization (CORE-AST-004) both consume this port to mint short-lived signed
// upload/download URLs after their server-side permission checks.
builder.Services.AddAssetStorage(builder.Configuration);

// Asset upload/download failure metering (CORE-OBS-001): wraps WHICHEVER IAssetStorage adapter the
// conditional selection above chose (the concrete S3-compatible adapter or the fail-closed default) with the
// transparent MeteredAssetStorage decorator, so a failed upload/download against a CONFIGURED backend
// increments the documented "asset failures" metric (the fail-closed "storage not configured" outcome is
// expected and not counted). It changes no storage behavior and leaves the security-critical selection logic
// untouched (docs/15_OBSERVABILITY.md; threat T4).
builder.Services.AddLiveCoreAssetStorageMetrics();

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

// Purchase environment policy (CORE-MON-008): the fail-closed Core decision of whether a verified purchase may be
// honored given the environment its proof was verified in. The deployment-supplied adapter reports the
// VerifiedPurchase.Environment (sandbox/test vs live production); this policy decides — BEFORE any record or grant —
// whether to honor it: a PRODUCTION deployment honors only a production purchase and rejects a sandbox receipt, so
// "a sandbox receipt is not honored in production" (docs/21). The deployment posture is derived from the host
// environment exactly as the OIDC audience guard (CORE-OPS-004), the readiness gate (CORE-OPS-005) and the
// production configuration contract (CORE-OPS-008) are (production = ASPNETCORE_ENVIRONMENT=Production, the default
// when unset), so a production host fails closed against sandbox receipts with no extra configuration while a
// local/test deployment keeps the latitude to verify sandbox receipts. It is a stateless, secret-free value (threat
// T7), registered unconditionally like the resolver above.
builder.Services.AddSingleton(PurchaseEnvironmentPolicy.ForDeployment(builder.Environment.IsProduction()));

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
// (CORE-ID-001 carry-over requirement). In a Production environment a configured
// Authority with a blank Audience is a misconfiguration (a blank Audience silently
// disables audience scoping), so the host refuses to start (CORE-OPS-004); the
// environment is passed in to make that guard environment-aware.
var oidcConfigured = builder.Services.AddOidcAuthentication(builder.Configuration, builder.Environment);

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
    // Database query-failure metering (CORE-OBS-001): an EF Core command interceptor counts every failed
    // database command onto LiveCoreMetrics (the documented "database failures" signal), uniformly across all
    // query paths, recording only a count — never the SQL or parameters (threat T7). Registered as a
    // singleton and attached to the DbContext below.
    builder.Services.AddSingleton<DatabaseFailureMetricsInterceptor>();
    // Connection resilience (CORE-CONC-003) + per-command timeouts (CORE-RES-004): UseLiveCoreNpgsql turns on the
    // retrying execution strategy (EnableRetryOnFailure) so a transient PostgreSQL disruption (failover, restart,
    // brief network partition, pool exhaustion) is retried automatically instead of surfacing as a user-facing
    // 5xx, AND bounds each command at a configured client-side CommandTimeout plus a server-side statement_timeout
    // so a stuck query has a ceiling and retry cannot amplify it unbounded. The same policy is applied identically
    // in every worker job context. Retry is safe because every multi-step write runs inside the execution
    // strategy's ExecuteAsync (CORE-CONC-002, TransactionalUnitOfWork) rather than a bare user-initiated
    // BeginTransaction, which a retrying strategy would reject.
    //
    // DbContext POOLING (CORE-PERF-003, the "Performance and Scalability" epic): registered with
    // AddDbContextPool, not AddDbContext, so the host REUSES a small pool of LiveCoreDbContext instances across
    // requests and hub connects instead of constructing (and tearing down) one per request. At a high request
    // rate the per-request context allocation is a real cost; pooling resets and returns a context to the pool
    // on scope dispose, so the steady state is allocation-light. The context is poolable as-is: it has the
    // single DbContextOptions<LiveCoreDbContext> constructor pooling requires, holds no per-request mutable
    // field, and the configured interceptors (the failure-metrics singleton plus the stateless audit-tamper
    // and statement-timeout interceptors) live in the shared, pool-wide options. Pooling changes only
    // allocation/throughput, never query results or the resilience, timeout and audit posture above, and the
    // same AddDbContextPool registration is applied identically in every worker job context.
    var persistenceOptions = LiveCorePersistenceOptions.FromConfiguration(builder.Configuration);
    builder.Services.AddDbContextPool<LiveCoreDbContext>((serviceProvider, options) => options
        .UseLiveCoreNpgsql(databaseConnectionString, persistenceOptions)
        .AddInterceptors(serviceProvider.GetRequiredService<DatabaseFailureMetricsInterceptor>()));

    // Transactional unit of work (CORE-CONC-002): runs a multi-step command handler as ONE database
    // transaction so its rule/state change, audit, event-append and quota change commit together or roll
    // back together (the append-only event stream can never diverge from persisted state). Registered here,
    // inside the persistence conditional, because it begins the transaction on the shared scoped
    // LiveCoreDbContext every repository writes through; the reveal/hide and session start/end/cancel
    // endpoints resolve it to wrap their writes and deliver realtime AFTER commit (commit-then-publish). The
    // transaction is opened inside the EF execution strategy's ExecuteAsync, so it stays correct once
    // CORE-CONC-003 enables retry-on-failure.
    builder.Services.AddScoped<TransactionalUnitOfWork>();
    builder.Services.AddSingleton(TimeProvider.System);

    // Per-request authorization-lookup cache (CORE-PERF-003, the "Performance and Scalability" epic). Every
    // authenticated request runs the tenant context resolver (organization-by-slug, user-profile-by-OIDC,
    // organization-membership) before the endpoint, and many endpoints then re-query the caller's workspace
    // membership/role; at a high request rate the SAME principal re-issues exactly those stable lookups on every
    // request and hub connect. AddMemoryCache wires the framework's in-process IMemoryCache (part of the shared
    // framework, NO new dependency); AuthorizationLookupCache is the singleton that holds the SHORT-TTL, POSITIVE-
    // ONLY cache over it (AuthorizationCacheOptions, read from configuration with safe defaults). It is consumed
    // ONLY by the caching repository decorators below, so it exists only when persistence is configured. The cache
    // never changes an authorization decision: a denial (no organization, unknown subject, no membership) is never
    // cached, and every membership change invalidates the affected subject/organization group — so removal revokes
    // cached access on the next request, fail-closed (docs/07_SECURITY_THREAT_MODEL.md threats T1/T5). It holds only
    // surrogate identifiers and authorization metadata, never content (threat T7).
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton(AuthorizationCacheOptions.FromConfiguration(builder.Configuration));
    builder.Services.AddSingleton<AuthorizationLookupCache>();

    // The IdentityAccess, Organizations and Workspaces repositories are registered as the CONCRETE type plus a
    // TRANSPARENT caching decorator as the interface (CORE-PERF-003), so the resolver and every endpoint get the
    // cached read path with no change to their code (the same decorator pattern as MeteredAssetStorage). The
    // UserProfileReferenceService — the only writer that loads-then-refreshes a profile in place — is wired to the
    // CONCRETE UserProfileRepository so it never reads-modify-writes a shared cached instance; the resolver's
    // read-only path goes through the cached decorator.
    builder.Services.AddScoped<UserProfileRepository>();
    builder.Services.AddScoped<IUserProfileRepository>(serviceProvider => new CachingUserProfileRepository(
        serviceProvider.GetRequiredService<UserProfileRepository>(),
        serviceProvider.GetRequiredService<AuthorizationLookupCache>()));
    builder.Services.AddScoped(serviceProvider => new UserProfileReferenceService(
        serviceProvider.GetRequiredService<UserProfileRepository>(),
        serviceProvider.GetRequiredService<TimeProvider>()));

    builder.Services.AddScoped<OrganizationRepository>();
    builder.Services.AddScoped<IOrganizationRepository>(serviceProvider => new CachingOrganizationRepository(
        serviceProvider.GetRequiredService<OrganizationRepository>(),
        serviceProvider.GetRequiredService<AuthorizationLookupCache>()));

    builder.Services.AddScoped<OrganizationMemberRepository>();
    builder.Services.AddScoped<IOrganizationMemberRepository>(serviceProvider =>
        new CachingOrganizationMemberRepository(
            serviceProvider.GetRequiredService<OrganizationMemberRepository>(),
            serviceProvider.GetRequiredService<AuthorizationLookupCache>()));

    // Data-subject erasure command (CORE-PRIV-001, GDPR Art.17 "right to erasure"): the IdentityAccess module's
    // cross-cutting privacy command. An authorized Owner/Admin (DELETE
    // /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data, wired by MapOrganizationEndpoints)
    // erases a data subject — deleting their user profile, anonymizing their participant display data and
    // invited-email rows, and auditing the erasure by id — atomically through the CORE-CONC-002
    // TransactionalUnitOfWork. The schema's ON DELETE SET NULL / CASCADE foreign keys then anonymize the
    // subject's exports/assets and revoke their memberships, while the PII-free append-only audit chain survives
    // intact. Registered here inside the persistence conditional alongside the repositories it composes
    // (user-profile, participant, invitation, organization-member and audit-log).
    builder.Services.AddScoped<DataSubjectErasureService>();

    // Data-subject access and portability export command (CORE-PRIV-004, GDPR Art.15 access / Art.20
    // portability): the IdentityAccess module's read-side privacy command and the access counterpart of the
    // erasure above. The entitled recipient — the data subject themselves or an Owner/Admin acting on their behalf
    // (GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export, wired by
    // MapOrganizationEndpoints) — obtains a machine-readable export of the subject's personal data gathered
    // TENANT-SCOPED (their identity profile plus organization membership, workspace memberships, participant
    // records and invited-email rows), auditing the access by id. Registered here inside the persistence
    // conditional alongside the repositories it composes (user-profile, organization-member, workspace-member,
    // participant, invitation and audit-log).
    builder.Services.AddScoped<PersonalDataExportService>();

    // Authorized tenant organization deletion command (CORE-PRIV-002, tenant offboarding / data deletion): the
    // Organizations module's destructive teardown command. An authorized Owner (DELETE
    // /api/v1/organizations/{organizationSlug}, wired by MapOrganizationEndpoints) deletes a tenant — appending
    // the platform-level OrganizationDeleted offboarding audit fact and removing the tenant root, whose schema
    // ON DELETE CASCADE foreign keys then tear down its workspaces, sessions, participants, memberships and its
    // own audit log — atomically through the CORE-CONC-002 TransactionalUnitOfWork. Registered here inside the
    // persistence conditional alongside the repositories it composes (organization and audit-log).
    builder.Services.AddScoped<OrganizationDeletionService>();

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
    // Wrapped in the transparent CachingWorkspaceMemberRepository (CORE-PERF-003) so the per-endpoint
    // membership/role re-queries (e.g. the participant-feed Host/CoHost checks) are served from the short-TTL
    // authorization cache, invalidated fail-closed on removal.
    builder.Services.AddScoped<WorkspaceMemberRepository>();
    builder.Services.AddScoped<IWorkspaceMemberRepository>(serviceProvider => new CachingWorkspaceMemberRepository(
        serviceProvider.GetRequiredService<WorkspaceMemberRepository>(),
        serviceProvider.GetRequiredService<AuthorizationLookupCache>()));

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

    // Template create command (CORE-TMPL-001, the "Vertical Authoring and Read API Completeness" epic): the
    // Templates module's "an Owner/Admin can create an organization-scoped template" command. Registered here,
    // inside the persistence conditional, because it composes the template and audit repositories plus the
    // shared DbContext for a single transaction. It resolves any existing per-scope key+version through the
    // tenant-scoped ITemplateRepository.FindByOrganizationAndKeyAsync FIRST (a key+version the organization
    // already holds is a duplicate), then creates the template ALWAYS organization-scoped (never global, so the
    // org route can never mint or mutate a global template; threat T5) with a server-minted id and appends a
    // TemplateCreated audit record, the insert and audit committing together (CORE-CONC-002). Consumed by
    // POST /api/v1/organizations/{organizationSlug}/templates.
    builder.Services.AddScoped<TemplateCreationService>();

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

    // Idempotent resource creator (CORE-DX-004): the generic mechanism the create routes (session, scene,
    // content-block, workspace and asset-link create) share to honor an OPTIONAL client Idempotency-Key, so a
    // client/network retry under the same key returns the original result and creates exactly one resource. It
    // reuses the idempotency store above and the CORE-CONC-002 TransactionalUnitOfWork (both scoped), claiming
    // the key and creating the resource in ONE transaction so the unique idempotency_keys(scope, key) index
    // closes the concurrent-first-request race rather than a parallel mechanism. Registered here, inside the
    // persistence conditional, because it depends on the DbContext-backed store and unit of work.
    builder.Services.AddScoped<IdempotentResourceCreator>();

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

    // Audit-log tamper-evidence verifier (CORE-SEC-003, the "Security Hardening" epic). The append path now
    // seals every audit entry into a per-tenant hash chain (an audit_log_sequences-allocated append sequence
    // plus a previous-hash/entry-hash link), so a DB-level actor or a future regression that alters or deletes a
    // persisted row is DETECTABLE. AuditLogChainVerifier is the read-side verification routine an operator runs
    // to check a tenant's chain is intact; registered here because it depends on the audit log repository above.
    // It is tenant-scoped and content-free (threats T5/T7); there is no HTTP verification route (csv/api_routes.csv
    // defines none). The complementary deployment control — REVOKE UPDATE/DELETE on audit_logs — is documented in
    // docs/13_SELF_HOSTING_REQUIREMENTS.md.
    builder.Services.AddScoped<AuditLogChainVerifier>();

    // Recap repository (CORE-AUD-004): the Recaps module's write-once recap persistence (append + tenant-/
    // workspace-scoped reads only, no update/delete — a recap is the produced output of a session, mirroring
    // the append-only audit log). Registered here, inside the persistence conditional, because it depends on
    // the DbContext. The worker host registers the SAME repository for the background generation job
    // (RecapGenerationServiceCollectionExtensions.AddRecapGeneration); the API host needs it too so the recap
    // read endpoint (CORE-RCP-003, GET /api/v1/sessions/{sessionId}/recap, wired by MapRecapEndpoints) can
    // serve a generated recap. The host-vs-audience RecapProjection is a static policy, so — like the
    // export/audit projectors — it needs no DI registration.
    builder.Services.AddScoped<IRecapRepository, RecapRepository>();

    // Export job + manifest repositories (CORE-AUD-002/003): the Exports module's tenant- and workspace-scoped
    // export job and produced-manifest persistence. The worker host registers the SAME repositories for the
    // background export-processing job (ExportProcessingServiceCollectionExtensions.AddExportProcessing); the API
    // host needs them too so the export read/download endpoint (CORE-EXP-001,
    // GET /api/v1/exports/{exportId}, wired by MapExportEndpoints) can serve a completed export's artifact. The
    // role-based ExportManifestProjection and the ExportAccessPolicy download gate are static policies, so — like
    // the recap/audit projectors — they need no DI registration.
    builder.Services.AddScoped<IExportJobRepository, ExportJobRepository>();
    builder.Services.AddScoped<IExportManifestRepository, ExportManifestRepository>();

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

    // Entity search service (CORE-ENT-005; CORE-API-006 real audience filter; CORE-PERF-008 single-load
    // in-memory gate): searches a workspace's entities for a given workspace role WITH VISIBILITY
    // FILTERING. Registered here, inside the persistence conditional, because it depends on the entity
    // repository and the Visibility module's VisibilityPreviewService above (exactly like the join service
    // depends on the session/participant repositories). It is a plain decision service: tenant id,
    // workspace id, the caller's role, the calling participant and generic criteria in, the
    // role-appropriate entity set out. The host-capable roles (Owner/Admin/Host/CoHost — "View host-only
    // content" in docs/06_AUTHORIZATION_MATRIX.md) get every matching entity through the tenant- and
    // workspace-scoped repository lookups (organization boundary before workspace boundary; threat T5); an
    // audience PARTICIPANT (Participant/Observer with an identified participant and session) gets exactly
    // the entities revealed to them, computed from a SINGLE workspace-rule load gated IN MEMORY by the SAME
    // VisibilityPreviewService path the participant-visible feed uses (the CORE-PERF-004 gate
    // VisibilityPolicy.ComputeVisibleResourcesForParticipant), so the rule-query count is independent of
    // entity volume (one workspace-rule load plus the entity load, never the old 1+M per-candidate
    // fan-out) and entity search can never diverge from the feed, per-resource access or the realtime
    // recipient gate — visibility is decided in ONE place (docs/02_ARCHITECTURE.md,
    // docs/05_MODULE_CONTRACTS.md); every other caller (the audit role, any undefined role, an audience
    // role with no participant or no session) fails closed to the empty view before any query. There is NO
    // HTTP endpoint (csv/api_routes.csv defines no entity route) and NO parallel visibility engine in this
    // story.
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
    // caller's tenant/workspace is hidden as not-found (threats T1/T5). CORE-EVT-002
    // additionally has it emit the durable ParticipantJoined session event on (and
    // only on) an admission, through the reused ISessionEventPublisher + TimeProvider
    // (registered in this same persistence conditional / unconditionally above), so
    // the Realtime module stays the sole owner of delivery. CORE-MON-005 adds the
    // server-side free-tier participant gate: as its LAST check the service atomically
    // check-and-consumes the session's session.participant.max quota through the reused
    // QuotaEnforcementService (registered in this same conditional below), so a join is
    // rejected (quota-exceeded) once the session is at its plan participant limit and
    // concurrent joins can never overrun the cap (CORE-CONC-004) — the documented cap is
    // enforced here, not UI-only. The join HTTP endpoint and the persisted participant
    // connection metadata remain later stories (the Realtime epic / Participants-owned
    // work) and are deliberately not wired here.
    builder.Services.AddScoped<SessionParticipantJoinService>();

    // Session participant leave service (CORE-EVT-002): the symmetric counterpart of the join service. It
    // removes a participant from a session's audience over the participant aggregate's soft-delete
    // (Participant.Remove) and, on (and only on) an actual departure, emits the durable ParticipantLeft session
    // event through the same reused ISessionEventPublisher + TimeProvider. Like the join service it is
    // fail-closed and tenant-isolated (a session or participant outside the caller's tenant/workspace is hidden
    // as not-found and emits nothing, threats T1/T5) and idempotent (removing an already-removed participant is
    // a no-op that emits no second event). On (and only on) an actual departure it also RELEASES the session's
    // session.participant.max slot through the reused QuotaEnforcementService (CORE-MON-005), the symmetric
    // counterpart of the join's atomic consume, so the participant cap reflects the session's current participants.
    // The leave HTTP endpoint is a later story.
    builder.Services.AddScoped<SessionParticipantLeaveService>();

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

    // Recipient-specific event projection (CORE-RT-004; CORE-PERF-001 collapse): the Realtime module's
    // recipient resolver computes the per-recipient deliveries of an event (which server-computed groups
    // receive it and the host vs audience projection each gets), gating every recipient through the central
    // Visibility engine (IEventRecipientVisibility above), so realtime delivery never leaks a hidden event
    // (threat T3; docs/11_REALTIME_SYNC.md "Events are never broadcast blindly"). An AUDIENCE-WIDE event the
    // whole audience may see is delivered to the shared session-audience group with ONE backplane publish,
    // resolved from a SINGLE rule lookup (no per-participant query or publish, CORE-PERF-001); per-participant
    // lookups/groups are reserved for selected-participant events, so it no longer depends on the participant
    // repository. The publisher composes the repository, the recipient resolver and the IRealtimeBackplane
    // (CORE-RT-006, registered above) to persist an event and then forward each computed delivery over the
    // scale-out seam ("persist event -> compute recipients -> project payload -> send to recipient groups",
    // docs/11_REALTIME_SYNC.md). The reveal command is the first producer (the ContentRevealed event,
    // carrying the revealed resource as its visibility subject).
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

    // Asset upload-acceptance policy (CORE-AST-007): the deployment's configurable MIME allowlist and absolute
    // per-object size ceiling the upload-intent command enforces BEFORE minting a signed URL — abuse-surface
    // hardening independent of the workspace storage quota. Read once from configuration (Assets:Upload:*) with
    // safe, already-hardened curated defaults so an unconfigured deployment is not wide open. A singleton next to
    // the storage location because the upload-intent service consumes it; it carries no secret (threat T7).
    builder.Services.AddSingleton(AssetUploadConstraints.FromConfiguration(builder.Configuration));

    // Asset upload-intent command (CORE-AST-003 + the CORE-MON-006 storage-quota gate): registers a new
    // PENDING asset with server-minted storage coordinates (reusing the CORE-AST-001 Asset aggregate),
    // atomically consumes the client-declared object size against the workspace's asset.storage.bytes.max
    // quota (the reused QuotaEnforcementService, CORE-CONC-004) and mints the short-lived signed upload URL via
    // the CORE-AST-002 IAssetStorage adapter port. Registered here because it depends on the asset repository,
    // the storage location and the storage adapter above plus the quota enforcement service and the shared
    // TransactionalUnitOfWork. The consume, URL mint and row persist run in ONE transaction, so an unconfigured
    // storage backend fails closed (AssetStorageNotConfiguredException) leaving no orphan pending asset AND no
    // leaked quota (the epic acceptance criterion; threat T4 "Asset leak"); an upload over the workspace's
    // storage limit is rejected (409) having consumed and persisted nothing ("Free limits cannot be bypassed by
    // clients"). The endpoint authorizes the caller (role + tenant + workspace) BEFORE invoking it. The signed
    // download URL flow (CORE-AST-004) needs no extra service: its endpoint reuses the asset repository, the
    // tenant context resolver, the workspace member repository and the IAssetStorage adapter above. Linking
    // (CORE-AST-005) and cleanup (CORE-AST-006) are later stories.
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

    // Asset deletion command (CORE-LIFE-006, the "Resource Lifecycle and Deletion" epic): the Assets module's
    // "a host can delete an asset" command. Registered here, inside the persistence conditional, because it
    // composes the asset and asset-link repositories, the IAssetStorage adapter and the audit repository (all
    // registered above) plus the shared DbContext for a single transaction. It loads the asset through the
    // tenant- AND workspace-scoped IAssetRepository.FindByIdAsync FIRST (an asset in another workspace/tenant
    // is never reachable; threats T1/T5), then REMOVES its asset_links (the FK-backed link rows, removed
    // explicitly so the cascade is deterministic — ADR 0012 step 2), DELETES the underlying storage object via
    // IAssetStorage BEFORE the metadata row (so a storage failure leaves no dangling row — mirrors the
    // upload-intent ordering), deletes the row, appends an AssetDeleted audit record and RELEASES the asset's
    // reserved asset.storage.bytes.max storage bytes back to the workspace (the reused QuotaEnforcementService,
    // CORE-MON-006: freeing an asset restores headroom), all atomically (cascade, not block;
    // docs/adr/0012-resource-deletion-cascades-dependents.md). With no storage configured the fail-closed
    // UnconfiguredAssetStorage makes the whole transaction roll back having changed nothing and the endpoint
    // returns 503 (private-by-default holds even unconfigured; threat T4). Consumed by
    // DELETE /api/v1/assets/{assetId} (wired by MapAssetEndpoints).
    builder.Services.AddScoped<AssetDeletionService>();

    // Entity deletion command (CORE-LIFE-003, the "Resource Lifecycle and Deletion" epic): the Entities
    // module's "a host can delete an entity" command. Registered here, inside the persistence conditional,
    // because it composes the entity, entity-relationship, visibility-rule, asset-link and audit
    // repositories (all registered above) plus the shared DbContext for a single transaction. It loads the
    // entity through the tenant- AND workspace-scoped IEntityRepository.FindByIdAsync FIRST (an entity in
    // another workspace/tenant is never reachable; threats T1/T5), then CASCADES its dependents — the
    // FK-backed EntityRelationship edges and the POLYMORPHIC (non-FK) visibility_rules and asset_links the
    // database cannot cascade — and appends an EntityDeleted audit record, all atomically (cascade, not
    // block; docs/adr/0012-resource-deletion-cascades-dependents.md). Consumed by
    // DELETE /api/v1/workspaces/{workspaceId}/entities/{entityId}.
    builder.Services.AddScoped<EntityDeletionService>();

    // Entity create command (CORE-ENT-006, the "Vertical Authoring and Read API Completeness" epic): the
    // Entities module's "an authoring role can author a generic entity" command. Registered here, inside the
    // persistence conditional, because it composes the entity-type, entity and audit repositories plus the
    // shared DbContext for a single transaction. It resolves the referenced entity type through the tenant-
    // AND workspace-scoped IEntityTypeRepository.FindByIdAsync FIRST (the same-workspace coupling the database
    // FK cannot enforce; an unknown/foreign type is never borrowed), then creates the entity with a
    // server-minted id and appends an EntityCreated audit record, the insert and audit committing together
    // (CORE-CONC-002). Consumed by POST /api/v1/workspaces/{workspaceId}/entities.
    builder.Services.AddScoped<EntityCreationService>();

    // Entity-type create command (CORE-ENT-007, the "Vertical Authoring and Read API Completeness" epic): the
    // Entities module's "an authoring role can define a generic entity type" command. Registered here, inside
    // the persistence conditional, because it composes the entity-type and audit repositories plus the shared
    // DbContext for a single transaction. It resolves any existing per-workspace key through the tenant- AND
    // workspace-scoped IEntityTypeRepository.FindByKeyAsync FIRST (a key the workspace already defines is a
    // duplicate), then creates the type with a server-minted id and appends an EntityTypeCreated audit record,
    // the insert and audit committing together (CORE-CONC-002). Consumed by
    // POST /api/v1/workspaces/{workspaceId}/entity-types.
    builder.Services.AddScoped<EntityTypeCreationService>();

    // Content block deletion command (CORE-LIFE-004, the "Resource Lifecycle and Deletion" epic): the
    // Content module's "a host can delete a content block from a scene" command. Registered here, inside the
    // persistence conditional, because it composes the content-block, visibility-rule, asset-link and audit
    // repositories (all registered above) plus the shared DbContext for a single transaction. It loads the
    // content block through the tenant-, workspace- AND scene-scoped IContentBlockRepository.FindByIdAsync
    // FIRST (a block in another scene/workspace/tenant is never reachable; threats T1/T5), then CASCADES its
    // dependents — the POLYMORPHIC (non-FK) visibility_rules and asset_links the database cannot cascade
    // (its REVISIONS are the inline revision_number on the row, so they go with the row) — and appends a
    // ContentBlockDeleted audit record, all atomically (cascade, not block; handled consistently with the
    // entity deletion, docs/adr/0012-resource-deletion-cascades-dependents.md). Consumed by
    // DELETE /api/v1/scenes/{sceneId}/content-blocks/{contentBlockId} (wired by MapContentBlockEndpoints).
    builder.Services.AddScoped<ContentBlockDeletionService>();

    // Scene deletion command (CORE-LIFE-005, the "Resource Lifecycle and Deletion" epic): the Scenes module's
    // "a host can delete a scene" command. Registered here, inside the persistence conditional, because it
    // composes the scene, content-block, visibility-rule, asset-link and audit repositories (all registered
    // above) plus the shared DbContext for a single transaction. It loads the scene through the tenant- AND
    // workspace-scoped ISceneRepository.FindByIdAsync FIRST (a scene in another workspace/tenant is never
    // reachable; threats T1/T5), then CASCADES its dependents — its child content blocks (and their inline
    // revisions) plus the POLYMORPHIC (non-FK) visibility_rules and asset_links the database cannot cascade —
    // RE-PACKS the remaining scenes' ordering so there is no gap (reusing the SCENE-001 ordering logic) and
    // appends a SceneDeleted audit record, all atomically (cascade, not block; handled consistently with the
    // entity and content-block deletions, docs/adr/0012-resource-deletion-cascades-dependents.md). Consumed
    // by DELETE /api/v1/workspaces/{workspaceId}/scenes/{sceneId} (wired by MapSceneEndpoints).
    builder.Services.AddScoped<SceneDeletionService>();

    // Scene reorder command (CORE-SCENE-006, the "Authoring Completeness" epic): the Scenes module's "a host
    // can move a scene to a new position" command. Registered here, inside the persistence conditional, because
    // it composes the scene repository (above) plus the shared DbContext for a single transaction. It loads the
    // scene through the tenant- AND workspace-scoped ISceneRepository.FindByIdAsync FIRST (a scene in another
    // workspace/tenant is never reachable; threats T1/T5), then moves it to the requested 0-based position and
    // RE-PACKS the workspace's scene ordering DETERMINISTICALLY (contiguous, gap-free, no duplicates) reusing
    // the SCENE-001 ordering and the delete-repack logic — the order assigned SERVER-SIDE, never a
    // client-trusted absolute order. Each re-pack write is xmin-guarded (CORE-CONC-006), so concurrent reorders
    // surface a 409 instead of corrupting the order. A reorder is a benign metadata move, so it is NOT audited.
    // Consumed by POST /api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder (wired by MapSceneEndpoints).
    builder.Services.AddScoped<SceneReorderService>();

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

    // Purchase-to-entitlement grant chain (CORE-MON-003, the grant-chain story of the "Monetization v1" epic):
    // the missing link that turns a verified, buyer-linked purchase into a granted SubjectEntitlement through the
    // documented product -> plan -> entitlement mapping (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md;
    // docs/24_SPEC_CONSISTENCY.md v1 monetization acceptance). Registered here, inside the persistence conditional,
    // because it composes the CORE-ENTL-001 plan catalog (IPlanDefinitionRepository) and the CORE-ENTL-002
    // SubjectEntitlementAssignmentService above — it adds no new table and no parallel assignment path. It maps the
    // verified purchase's product reference to a generic plan by the plan's stable key and REUSES AssignFromPlanAsync,
    // so the grant is idempotent on (purchase, entitlement) (a retry / replayed proof / duplicate notification
    // converges rather than double-granting) and FAIL-CLOSED (a product reference that maps to no active plan grants
    // nothing). The Apple (CORE-STORE-003) and Google (CORE-STORE-004) verification endpoints invoke it in the SAME
    // transaction as the record + buyer link (reusing the CORE-CONC-002 TransactionalUnitOfWork), and only when the
    // purchase belongs to the resolved buyer, so the grant shows up in the GET /api/v1/me/entitlements read.
    builder.Services.AddScoped<ProductEntitlementGrantService>();

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
    // the workspace-create command (workspace.active.max, the creating user subject), the session start/end
    // commands (session.active.max, the session's workspace subject), the participant-join/leave path
    // (session.participant.max, the session subject; CORE-MON-005) and the asset upload-intent/delete path
    // (asset.storage.bytes.max, the asset's workspace subject; CORE-MON-006); no new HTTP route is added.
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
    // notification endpoints sit on this service. It now takes the TransactionalUnitOfWork (registered above) by
    // constructor injection so the status change AND its dedup-ledger write commit in ONE transaction (CORE-MON-010):
    // a crash between them can never leave a status applied without its first-arrival record, and a re-delivery can
    // never double-append the purchase_events audit trail.
    builder.Services.AddScoped<IStoreNotificationEventRepository, StoreNotificationEventRepository>();
    builder.Services.AddScoped<StoreNotificationService>();

    // Purchase-entitlement REVOCATION (CORE-MON-004, the monotonic-purchase-status story of the "Monetization v1"
    // epic): the inverse of the CORE-MON-003 grant chain. When a store notification (or its reconciliation) drives a
    // purchase into a revoked state (Refunded/Cancelled — a refund/cancellation/chargeback), the
    // StoreNotificationService revokes the granted SubjectEntitlement through this service, so "Refunds and
    // chargebacks must revoke or downgrade entitlements" and a revoked state "stays revoked" (docs/21 / docs/24).
    // Registered here, inside the persistence conditional, because it composes the Store buyer linkage
    // (IBillingAccountLinkRepository) and purchase repository above plus the CORE-MON-003
    // ProductEntitlementGrantService.RevokeForProductAsync — it adds no new table and no parallel revoke path. The
    // monotonic state machine itself (PurchaseTransaction.ChangeStatus) is always on regardless; this wires the
    // entitlement side-effect, so the StoreNotificationService picks it up by constructor injection.
    builder.Services.AddScoped<PurchaseEntitlementRevocationService>();

    // Buyer linkage for verified purchases (CORE-MON-002, the buyer-linkage story of the "Monetization v1" epic):
    // the Store module owns the billing_account_links table that durably links a verified purchase to the
    // authenticated buyer (a subject) — docs/24_SPEC_CONSISTENCY.md; csv/database_tables.csv: module Store.
    // Registered here, inside the persistence conditional, like the purchase repositories above, because the
    // repository depends on the DbContext. CORE-STORE-002 deliberately gave purchase_transactions NO buyer column
    // (a purchase is named globally by its provider + provider_transaction_id pair, so the authenticated buyer was
    // verified then discarded); this is the missing link. The BillingAccountLinkService links a recorded purchase
    // to the server-resolved buyer subject and enforces the one-subject-per-receipt rule (the unique
    // billing_account_links(purchase_transaction_id) index): the same buyer re-submitting is idempotent, a
    // DIFFERENT subject claiming the same receipt is denied fail-closed, so "the same external receipt cannot be
    // claimed by two different subjects" (the story acceptance criterion; threat T5). The Apple (CORE-STORE-003)
    // and Google (CORE-STORE-004) verification endpoints record-then-link in one transaction (reusing the
    // CORE-CONC-002 TransactionalUnitOfWork), so the buyer linkage is durably atomic with the recording. Granting
    // the SubjectEntitlement from the linked buyer (the product -> plan -> entitlement mapping) is the next story
    // (CORE-MON-003); this story records WHICH subject a verified purchase belongs to, the prerequisite that grant
    // chain sits on.
    builder.Services.AddScoped<IBillingAccountLinkRepository, BillingAccountLinkRepository>();
    builder.Services.AddScoped<BillingAccountLinkService>();

    // Gate readiness on database connectivity. The health response stays
    // status-only (see HealthEndpoints), so a failing check never leaks
    // connection details to the unauthenticated readiness endpoint.
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<LiveCoreDbContext>("database", tags: [HealthEndpoints.ReadinessTag]);

    // Gate readiness on the database SCHEMA being up to date (CORE-OBS-010). The check above proves the
    // database ANSWERS; this proves it carries the schema this build expects. The schema is applied by a
    // separate run-to-completion migration step BEFORE the API rolls out (CORE-OPS-001) — the host never
    // migrates implicitly — so if that step is skipped/failed, or the API image is rolled forward ahead of its
    // schema, GetPendingMigrationsAsync reports a missing migration and readiness returns NOT-READY (503,
    // tagged ready), leaving the rotation instead of advertising Ready and then 500-ing on the first domain
    // query; once the schema is current readiness flips back to ready. The check is bounded by the configured
    // database command timeout (CORE-RES-004) and the short readiness wall-clock bound (CORE-RES-005) and fails
    // closed, liveness stays shallow/separate (a stale schema never restarts a live process), and the readiness
    // response stays status-only so which migrations are missing never leaks (threat T7). Registered inside the
    // persistence conditional: there is no schema to gate on without a configured database.
    builder.Services.AddSchemaReadinessCheck();
}

// Production required-dependency readiness gate (CORE-OPS-005). Before this story /health/ready reported
// Healthy whenever no readiness check failed, and the database check above is registered ONLY when a
// connection string is configured — so a host deployed with NO persistence (or no OIDC identity provider)
// advertised READY while every domain route fails closed (503/401). This check reports NOT-READY in a
// Production environment when persistence or OIDC is unconfigured, so a misconfigured production host leaves
// the ready rotation instead. It is registered UNCONDITIONALLY (outside the persistence conditional above)
// precisely so it still runs when persistence is absent; outside Production it is inert, the same
// local-development latitude the OIDC audience guard grants (CORE-OPS-004). The readiness response stays
// status-only, so which dependency is missing never leaks to the unauthenticated endpoint (threat T7).
builder.Services.AddRequiredDependencyReadinessCheck(
    builder.Environment,
    persistenceConfigured: !string.IsNullOrWhiteSpace(databaseConnectionString),
    oidcConfigured: oidcConfigured);

// Deep dependency reachability readiness gate (CORE-OBS-009). The database check above is a live probe and the
// required-dependency gate just above is a startup-captured boolean of config PRESENCE — but OIDC, object storage
// and the realtime backplane were never probed for actual reachability, so a host with a healthy database but a
// DEAD critical dependency reported Ready and kept taking traffic. This adds one LIVE, short-bounded probe per
// CONFIGURED critical dependency (the OIDC discovery endpoint, the object-storage backend, the realtime
// backplane), tagged ready, so a host whose dependency is configured but unreachable reports NOT-READY and leaves
// the rotation. The probes are bounded by a short timeout and fail closed (a timeout/error is not-ready, never a
// false Ready; CORE-RES-005), liveness stays shallow/separate (a dead dependency never restarts a live process),
// and the readiness response stays status-only so which dependency is unreachable never leaks (threat T7). An
// unconfigured dependency is not probed; with none configured the gate is inert (Ready), preserving the existing
// local-development latitude. Registered unconditionally (outside the persistence conditional) so it runs even
// when persistence is absent.
builder.Services.AddDeepDependencyReadinessChecks(builder.Configuration);

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

// Production configuration contract (CORE-OPS-008): fail loudly when a required production value is missing.
// Every secret/config value is supplied from configuration only (no secret in source; threat T7), and the full
// names-only contract ships as the repository-root .env.example with its env-var -> secret-store mapping in
// docs/13_SELF_HOSTING_REQUIREMENTS.md. This reuses the existing fail-closed-when-unconfigured posture rather
// than adding a new one: in Production a missing required value does NOT crash an otherwise-live process — the
// host stays up and fails closed (401/503) and reports NOT-READY (CORE-OPS-005), while this logs a loud, NAMED
// startup error so the misconfiguration is unmissable. The one hard fail-to-start case stays the audience
// foot-gun (CORE-OPS-004). Only the missing KEY NAMES are logged, never the configured values, so a secret is
// never written to the log (threat T7). Outside Production the contract is inert (local-development latitude).
var missingRequiredSettings =
    ProductionConfigurationValidator.FindMissingRequiredSettings(builder.Configuration, app.Environment.IsProduction());
if (missingRequiredSettings.Count > 0)
{
    app.Logger.LogCritical(
        "Production configuration is incomplete: the required setting(s) {MissingSettings} are not configured. " +
        "Inject them as environment variables / from your secret store (see .env.example and " +
        "docs/13_SELF_HOSTING_REQUIREMENTS.md). The host stays up and fails closed (401/503) and reports " +
        "not-ready, but it cannot serve production traffic until they are set.",
        string.Join(", ", missingRequiredSettings));
}

// Forwarded headers run FIRST, before any middleware that reads the request
// scheme/host/IP (CORS, authentication, URL generation), so the rest of the
// pipeline sees the real client's scheme/host as restored from a TRUSTED proxy's
// X-Forwarded-* headers rather than the internal proxy hop (CORE-OPS-003;
// docs/13_SELF_HOSTING_REQUIREMENTS.md TLS-termination posture). Only loopback
// (framework default) and the configured known proxies/networks are trusted, so
// an untrusted client cannot spoof the scheme (threat T7).
app.UseForwardedHeaders();

// Baseline HTTP security response headers (CORE-SEC-009). Wired here — immediately AFTER UseForwardedHeaders and
// alongside the transport-security headers below — so its OnStarting callback is enrolled for EVERY request
// before any other middleware can short-circuit it (a redirect, a 401 challenge, the rate limiter's 429). It
// adds X-Content-Type-Options: nosniff, Referrer-Policy: no-referrer and a deny-all Content-Security-Policy to
// every API, error and SignalR-negotiate response. It applies the headers from an OnStarting callback (not
// inline) so they survive the Response.Clear() the global exception / concurrency-conflict middleware perform on
// an error path — so the baseline rides on a 500/409 Problem Details too. On by default and individually
// configurable (SecurityHeaders:*); the values are static directives carrying no tenant/principal detail
// (threat T7). When SecurityHeaders:Enabled=false the middleware enrols nothing and passes through.
app.UseMiddleware<SecurityHeadersMiddleware>();

// App-level HSTS and HTTPS redirection (CORE-SEC-005). Wired here — immediately AFTER UseForwardedHeaders so
// the request scheme already reflects the real client (the proxy's forwarded X-Forwarded-Proto when behind a
// trusted terminating proxy), and BEFORE any other middleware — and each guarded by its own configuration
// toggle, both fail-safe OFF by default. With the documented proxy posture (the default) neither runs, so it is
// unchanged. When a deployment terminates TLS in the app itself it enables them: UseHsts emits
// Strict-Transport-Security on a secure response, and UseHttpsRedirection redirects an insecure http request to
// https — but behind a trusted proxy the forwarded https scheme means an enabled redirect never fires, so it
// cannot double-redirect or fight the edge. The header/redirect carry no tenant/principal/resource detail
// (threat T7; docs/13_SELF_HOSTING_REQUIREMENTS.md).
var httpsSecurity = app.Services.GetRequiredService<HttpsSecurityConfiguration.HttpsSecuritySettings>();
var responseCompression =
    app.Services.GetRequiredService<ResponseCompressionConfiguration.ResponseCompressionSettings>();
if (httpsSecurity.HstsEnabled)
{
    app.UseHsts();
}

if (httpsSecurity.HttpsRedirectionEnabled)
{
    app.UseHttpsRedirection();
}

// HTTP response compression for JSON (CORE-PERF-006). Wired here — early, before the middleware that writes the
// response body, as the framework requires — but ONLY for non-hub paths: the branch predicate excludes the
// SignalR hub area (HubBearerToken.PathPrefix, "/hubs", segment-matched so a look-alike like "/hubsignup" is
// unaffected), so the hub negotiate/transport is never double-compressed (SignalR owns its transport framing).
// On the REST surface a client that advertises Accept-Encoding: br/gzip receives the SAME JSON list/feed/replay
// payload compressed (Brotli preferred); a client that sends no Accept-Encoding, or only an encoding the server
// cannot satisfy, gets the identical uncompressed body, so response CONTENT is unchanged. Only application/json
// and application/problem+json are compressed (ResponseCompressionConfiguration.CompressibleJsonMimeTypes), so
// the Prometheus /metrics text, signed-asset redirects and any already-compressed/binary payload pass through
// untouched. The whole feature is skipped when disabled (ResponseCompression:Enabled=false), exactly as
// UseHsts/UseHttpsRedirection above are added only when their toggle is on.
if (responseCompression.Enabled)
{
    app.UseWhen(
        static context => !context.Request.Path.StartsWithSegments(HubBearerToken.PathPrefix),
        static branch => branch.UseResponseCompression());
}

// Request metrics (CORE-OBS-001) run first among the application middleware so they time the WHOLE request
// (including authentication and authorization) and record its duration and server-error count onto
// LiveCoreMetrics — the documented "API request duration" and "API error rate" signals. The middleware tags
// the measurement with the route TEMPLATE (never the concrete path) and skips the /metrics scrape endpoint
// itself (docs/15_OBSERVABILITY.md; threat T7).
app.UseMiddleware<RequestMetricsMiddleware>();

// Request tracing (CORE-OBS-003) runs at the top of the application pipeline so its SERVER span WRAPS the
// whole request — every span produced deeper (the reveal span and its session-event publish spans) nests
// under it. It names/tags the span with the route TEMPLATE (never the concrete path) once routing has run and
// skips the /health and /metrics infrastructure endpoints (docs/15_OBSERVABILITY.md; threat T7). When no
// tracer/exporter is listening, starting the span is a no-op, so this is free when tracing is off.
app.UseMiddleware<RequestTracingMiddleware>();

// Correlation id response headers (CORE-OBS-005). Registered immediately AFTER RequestTracingMiddleware so the
// request span exists and Activity.Current carries the trace context: it writes X-Request-Id (the same value
// RequestLogContextMiddleware stamps as request_id on every log line) and the W3C traceparent (the request
// span's trace context) on EVERY response from an OnStarting callback, so the correlation id survives the
// Response.Clear() the global exception / concurrency-conflict middleware perform and rides on a 500/409
// Problem Details too. It sits INSIDE the request tracing span and OUTSIDE the exception/conflict handlers, so
// a caller can correlate even a failed call with the server's traces and logs. The values are non-sensitive
// identifiers (threat T7) and the CORS policy exposes them to browser SDKs (CORE-DX-005).
app.UseMiddleware<CorrelationHeaderMiddleware>();

// Global Problem Details exception handler (CORE-RES-001). It wraps the whole endpoint pipeline and turns
// ANY exception that reaches it unhandled into a fail-closed RFC 7807 Problem Details 500 carrying the
// documented internal_error code (CORE-DX-001), instead of the bare framework 500 the host returned before.
// It sits OUTSIDE (before) the concurrency-conflict middleware so that one still owns its 409 for a
// DbUpdateConcurrencyException while every OTHER unhandled exception — including the ones that middleware
// rethrows — funnels through here. It sits INSIDE the request metrics/tracing spans so the resulting 500 is
// observed (a genuine server fault IS counted as a 5xx error), and it reuses the same CoreProblem helper every
// endpoint uses so the body shape and stable code match. The exception is logged server-side for the operator;
// the response body never carries the exception type, message, stack, resource, tenant or any internal state
// (threat T7).
app.UseMiddleware<UnhandledExceptionProblemDetailsMiddleware>();

// Optimistic-concurrency conflict translation (CORE-CONC-001). The mutable
// aggregates carry the PostgreSQL xmin row-version token, so an interleaved
// read-modify-write makes the second SaveChanges throw a
// DbUpdateConcurrencyException (a conflicting write fails loudly rather than
// silently overwriting). This middleware wraps the whole endpoint pipeline and
// turns that exception into a fail-closed 409 Conflict for every endpoint, while
// leaving all other exceptions untouched. It sits inside the request
// metrics/tracing spans (so the 409 is observed) and before CORS/auth so it wraps
// the endpoint handlers where SaveChanges runs.
app.UseMiddleware<ConcurrencyConflictMiddleware>();

// CORS runs before authentication so a browser preflight (an OPTIONS request that
// carries no credentials) is answered by the CORS middleware itself rather than
// being challenged with 401. The single named policy (CorsConfiguration.PolicyName)
// is applied here as the middleware default, covering the REST API; the SignalR
// hub additionally pins it with RequireCors so its cross-origin access is
// unmistakable (CORE-OPS-003). It is fail-closed: an origin not on the configured
// allow-list receives no Access-Control-Allow-Origin header and the browser blocks
// the call.
app.UseCors(CorsConfiguration.PolicyName);

// API deprecation/sunset signaling (CORE-DX-006). For a request whose matched endpoint is flagged with
// WithDeprecation(...) this emits the RFC 8594 Sunset header (the route's retirement date) plus the Deprecation
// header, so a consumer gets advance signal before a contract changes. It runs after routing has selected the
// endpoint (routing is added at the top of the minimal-hosting pipeline) and writes nothing for a current
// (non-deprecated) route, so the signal is strictly opt-in. The headers describe only the route's own retirement
// schedule — no tenant/principal/resource content (threat T7) — and the CORS policy exposes them
// (CorsConfiguration.ExposedResponseHeaders) so a browser SDK can read them. No route is deprecated yet; this
// establishes the convention and the mechanism that honors the documented additive-only evolution policy
// (docs/02_ARCHITECTURE.md, docs/08_API_CONTRACTS.md, docs/23_PACKAGE_VERSIONING.md).
app.UseLiveCoreDeprecationHeaders();

// Authentication runs before authorization, which runs before the endpoints, per
// the documented request flow (docs/02_ARCHITECTURE.md). The health endpoints
// are mapped after this and stay anonymous because they are not in the
// authenticated workspace route group.
app.UseAuthentication();

// Per-request structured log scope (CORE-OBS-002). Placed AFTER authentication so the authenticated principal
// is available to seed user_id, and BEFORE authorization so the scope wraps the authorization step and the
// endpoint — a fail-closed 401/403 is logged with its request_id too. It opens one logging scope carrying the
// documented per-request context (request_id, user_id and the route-derived workspace_id/session_id) and emits
// a request-summary log line; the tenant context resolver and the session event publisher enrich the same
// scope with organization_id and event_id as the request runs (docs/15_OBSERVABILITY.md). It skips the
// unauthenticated /health and /metrics endpoints (no tenant/principal context) and never logs content, tokens
// or PII (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
app.UseMiddleware<RequestLogContextMiddleware>();

// Rate limiting (CORE-SEC-001, CORE-SEC-007) runs AFTER authentication so the global limiter can partition an
// authenticated request on its principal and an anonymous request per-IP, and AFTER the request-log scope so a
// throttled request is still logged with its request context, but BEFORE authorization and the endpoints so an
// excess request is rejected with 429 before any per-call database work, the webhook's external parser or the
// hub negotiate runs. Routing has already matched the endpoint (the WebApplication adds routing at the top of the
// pipeline), so the store-notification webhook group's RequireRateLimiting(WebhookPolicyName) per-IP policy — and
// the /health/* and /metrics DisableRateLimiting opt-outs — are honored here too.
app.UseRateLimiter();

// RateLimit-Limit/Remaining/Reset on the SUCCESS path (CORE-DX-005). Registered immediately AFTER
// UseRateLimiter so it runs INSIDE the limiter (the request's lease is held), reporting the per-principal
// remaining allowance read from the SAME limiter that enforces it; the limiter's own 429 rejection emits the
// matching headers. The CORS policy exposes them so a browser SDK can read them
// (CorsConfiguration.ExposedResponseHeaders). It only acts on the authenticated per-principal surface and
// leaks no tenant/principal detail (threat T7).
app.UseMiddleware<RateLimitHeaderMiddleware>();

app.UseAuthorization();

// Health endpoints (CORE-FND-004): unauthenticated by convention.
app.MapLiveCoreHealthEndpoints();

// Prometheus metrics scrape endpoint (CORE-OBS-001): GET /metrics, serving the OpenTelemetry-collected
// LiveCoreMetrics series in the Prometheus exposition format. Unauthenticated by convention (a Prometheus
// server scrapes it from inside the deployment network, exactly as orchestration probes /health/*), it
// exposes only low-cardinality aggregates — never content or identifiers (threat T7) — and a deployment
// restricts it at the reverse-proxy/network edge (docs/13_SELF_HOSTING_REQUIREMENTS.md). Mapped
// unconditionally; it needs no database.
app.MapLiveCoreMetricsEndpoint();

// AGPL section 13 source-offer endpoint (CORE-CMP-001): GET /source, the anonymous surface that OFFERS
// remote users access to the Corresponding Source of the running build (its license, build version and the
// repository the source can be obtained from). The platform is AGPL-3.0-or-later (LICENSE; docs/16_LICENSING.md)
// and network-interactive (the SignalR hub + /api/v1), so section 13 obliges a hosted deployment to make this
// offer to every remote user it interacts with; the endpoint discharges that obligation. It is anonymous by
// design (the offer is owed to every remote user) and a top-level infrastructure route, not part of the
// versioned /api/v1 product surface, exactly like /health/* and /metrics. It needs no database, so it is
// mapped unconditionally, and it exposes only the license, a build version and a public repository URL — never
// a token, tenant identifier, configuration value or resource content (threat T7). A deployment that ships
// modified source overrides the offered location with SourceOffer:RepositoryUrl so the offer points at the
// source actually running there.
app.MapLiveCoreSourceOfferEndpoint();

// OpenAPI explorer endpoint (CORE-OAS-001): GET /openapi/v1.json, serving the OpenAPI 3 document generated from
// the /api/v1 route table. Mapped ONLY outside Production (MapLiveCoreOpenApi checks the environment), so a
// production deployment exposes no schema-discovery surface (the story note "keep any explorer non-production
// only"); the committed openapi/livecore-v1.json build artifact remains the canonical document a CI gate checks
// against the registered routes. It is a top-level infrastructure route (not part of the versioned /api/v1
// product surface, so it is excluded from the document it serves and from csv/api_routes.csv, exactly like
// /health/*, /metrics and /source) and needs no database, so it is mapped here unconditionally w.r.t.
// persistence. The document leaks no token, tenant identifier, configuration value or content (threat T7).
app.MapLiveCoreOpenApi();

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

// Audit log read endpoint (CORE-SEC-002, the "Security Hardening" epic): the tenant-scoped, paged
// view-audit-log read, GET /api/v1/audit-logs. The append-only audit log was write-only over HTTP — the
// read-side AuditQueryPolicy (Owner/Admin/Auditor, fail-closed) and the safe AuditLogEntryView projection
// existed and were unit-tested but no route was mapped, so the dedicated Auditor role could not read the
// log. This wires the route ON TOP of those existing parts (no parallel audit engine). It lives in an
// authenticated route group and fails closed (503) when persistence is not configured, exactly like the
// organization endpoints. No new DI registration is required: the tenant context resolver and the audit log
// repository it consumes are already registered above inside the persistence conditional. The tenant comes
// from the required ?organizationSlug= (resolved by the same token-claim-and-membership tenant check the
// other tenant-scoped routes use), so a service account / foreign tenant / non-member is hidden as 404 and a
// known member without the Owner/Admin/Auditor grant is 403; the page is read tenant-scoped (threat T5),
// bounded by ?limit=/?offset=, and projected PII/secret-free through AuditQueryPolicy (threat T7).
app.MapAuditLogEndpoints();

// Template endpoints (the Templates module's organization-scoped template routes). The module had a
// template create + load (CORE-ENT-004) and a DELETE (CORE-LIFE-008,
// DELETE /api/v1/organizations/{organizationSlug}/templates/{templateId}); CORE-TMPL-001 (the "Vertical
// Authoring and Read API Completeness" epic) adds the authoring create/list/read so an Owner/Admin can
// define, list and read its tenant's templates over the API:
//   GET  /api/v1/organizations/{organizationSlug}/templates
//   POST /api/v1/organizations/{organizationSlug}/templates
//   GET  /api/v1/organizations/{organizationSlug}/templates/{templateId}
// They live in the same authenticated route group and fail closed (503) when persistence is not
// configured, exactly like the organization endpoints. The only new DI registration is the
// TemplateCreationService (registered above inside the persistence conditional); the tenant context
// resolver and the template repository were already registered. A template is an ORGANIZATION-level
// registry resource (not workspace content), so every route is org-scoped: the tenant's slug is in the
// path (resolved by the same token-claim-and-membership tenant check the organization member-removal
// route uses), and the caller is authorized as an admin of that tenant (Owner/Admin; a non-privileged
// member is 403). THE GLOBAL/ORGANIZATION BOUNDARY is enforced structurally: the create ALWAYS builds an
// organization-scoped template (never a global one), and the list/read use the org-scoped repository
// methods (ListByOrganizationAsync / FindByOrganizationAndIdAsync), which never return a GLOBAL template —
// so "an org-scoped lookup never returns a global template for mutation" holds at every route and a global
// template addressed by id is an indistinguishable hidden 404 (threats T1/T5). A foreign/unknown template
// is hidden as 404, a duplicate per-scope key+version is 409, and the create is audited as TemplateCreated
// (the insert and audit committing together in one transaction). The pre-existing DELETE is unchanged: it
// loads through the same tenant-scoped FindByOrganizationAndIdAsync (a global template is never deletable
// by an org), removes only the templates row (already-instantiated entity types are unaffected) and emits
// no audit record.
app.MapTemplateEndpoints();

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

// Recap read endpoint (CORE-RCP-003, the "Vertical Authoring and Read API Completeness" epic):
// GET /api/v1/sessions/{sessionId}/recap. A recap was generated and persisted by the worker (CORE-RCP-001/002)
// but had no HTTP surface, so an end user could not retrieve it; this finally gives the recap a read route. It
// lives in an authenticated route group and fails closed (503) when persistence is not configured, exactly
// like the session endpoints. No new DI registration beyond the recap repository above is required: the tenant
// context resolver, the session repository and the workspace member repository it also consumes are already
// registered. The tenant comes from the required ?organizationSlug=; a service account / foreign tenant /
// unknown session / non-member is hidden as 404 (threats T1/T5), and a known member receives the recap
// PROJECTED BY ROLE through the existing RecapProjection — host-content roles get the full body, the audience
// gets the host-only-field-stripped summary (a generated recap is host content until a separate reveal,
// docs/09_EVENT_CATALOG.md; threats T2/T8). The separate participant reveal of a recap body stays deferred
// (docs/24_SPEC_CONSISTENCY.md).
app.MapRecapEndpoints();

// Export read/download endpoint (CORE-EXP-001, the "Vertical Authoring and Read API Completeness" epic):
// GET /api/v1/exports/{exportId}. The export job and its produced manifest (with the role-based projection) were
// modeled, persisted and processed by the worker (CORE-AUD-002/003, CORE-JOB-002) but had no HTTP surface, so an
// authorized host could not retrieve a completed export; this finally gives the export a read/download route. It
// lives in an authenticated route group and fails closed (503) when persistence is not configured, exactly like
// the asset/recap endpoints. No new DI registration beyond the export job + manifest repositories above is
// required: the tenant context resolver and the workspace member repository it also consumes are already
// registered. The tenant comes from the required ?organizationSlug=; a service account / foreign tenant /
// unknown export / non-member is hidden as 404 (threats T1/T5), an authorized downloader is the "Export
// workspace" set {Owner,Admin,Host} (ExportAccessPolicy; a non-authoring role is 403), and an incomplete/failed
// export is 409. The completed export's artifact (its manifest) is returned PROJECTED BY ROLE through the
// existing ExportManifestProjection and delivered as an authorized stream — never a public/static URL (threats
// T4/T8). Authorization runs BEFORE any artifact is produced (the asset signed-URL discipline).
app.MapExportEndpoints();

// Participant presence endpoints (CORE-PRS-001): the join/leave routes
// POST /api/v1/sessions/{sessionId}/participants/{participantId}/join and .../leave. They are the real entry
// point that finally makes the documented ParticipantJoined/ParticipantLeft presence events fire end-to-end:
// CORE-EVT-002 built the SessionParticipantJoinService/SessionParticipantLeaveService that emit them but left
// them with no production caller, and this story wires that caller by REUSING those services (no parallel
// join/leave logic). They live in an authenticated route group and fail closed (503) when persistence is not
// configured, exactly like the session lifecycle endpoints. No new DI registration is required: the tenant
// context resolver, the session repository, the workspace member repository and the join/leave services they
// consume are already registered above inside the persistence conditional. Authorization is host-driven —
// managing presence is a session-control action, so the routes are authorized to the same Owner/Admin/Host/CoHost
// roles (in the session's own workspace) the start/end/cancel commands use, fail-closed and hidden-404 for a
// foreign tenant / non-member (threats T1/T5). The session.participant.max free-tier quota (CORE-MON-005) is now
// enforced on this real join path, and the presence payloads stay identifier-only (no PII; threat T7).
app.MapSessionParticipantPresenceEndpoints();

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

// Scene content endpoints (CORE-SCENE-003; CORE-API-007 adds the by-scene-id read; CORE-LIFE-005 adds the
// DELETE; CORE-SCENE-006 adds the reorder POST): the Scenes module's HTTP routes,
// GET/POST /api/v1/workspaces/{workspaceId}/scenes, GET /api/v1/scenes/{sceneId},
// DELETE /api/v1/workspaces/{workspaceId}/scenes/{sceneId} and
// POST /api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder. They live in authenticated route groups and
// fail closed (503) when persistence is not configured, exactly like the workspace endpoints. No new DI
// registration beyond the SceneDeletionService and SceneReorderService above is required: the tenant context
// resolver, the scene repository and the workspace member repository they consume are already registered above
// inside the persistence conditional. The GET list and the GET by-id both PROJECT BY ROLE through the same
// SceneProjection (the host shape to host-capable/metadata roles, the stripped participant shape to audience
// roles — the "Projection by role" route note, CORE-SCENE-004). The by-id read resolves the scene within the
// query-supplied organization, discovers its workspace from the loaded row and authorizes the caller's
// membership there (every denial hidden as 404). The POST assigns the scene order server-side as append-to-end
// (no client-supplied order). The DELETE resolves the parent workspace first (the route pins {workspaceId}, the
// tenant comes from the required ?organizationSlug=), authorizes the caller's role in that workspace
// (Owner/Admin/Host/CoHost), then loads the scene through the tenant- and workspace-scoped lookup, CASCADES its
// child content blocks and the dependent visibility rules / asset links, RE-PACKS the remaining scenes'
// ordering so there is no gap and appends a SceneDeleted audit record, all atomically (cascade, not block;
// docs/adr/0012-resource-deletion-cascades-dependents.md). The REORDER POST resolves the parent workspace
// first (the tenant from the required organizationSlug body field), authorizes the caller's role
// (Owner/Admin/Host/CoHost), then moves the scene to the client-supplied 0-based position and RE-PACKS the
// ordering DETERMINISTICALLY (contiguous, gap-free, no duplicates) with the order assigned server-side; the
// scene's xmin token (CORE-CONC-006) makes a concurrent reorder a 409 instead of a corrupted order, and the
// re-packed list is returned. A reorder is a benign metadata move, so it is NOT audited. Deleting or
// reordering a non-existent/foreign scene is a safe 404.
app.MapSceneEndpoints();

// Scene content-block endpoints: the Content module's HTTP routes,
// GET  /api/v1/scenes/{sceneId}/content-blocks (list, CORE-CB-001),
// GET  /api/v1/scenes/{sceneId}/content-blocks/{contentBlockId} (read, CORE-CB-001),
// POST /api/v1/scenes/{sceneId}/content-blocks (create, CORE-SCENE-003) and
// DELETE /api/v1/scenes/{sceneId}/content-blocks/{contentBlockId} (delete, CORE-LIFE-004). They live in an
// authenticated route group and fail closed (503) when persistence is not configured, exactly like the
// session endpoints. No new DI registration beyond the ContentBlockDeletionService above is required: the
// tenant context resolver, the scene repository (for the org-scoped scene lookup), the content block
// repository and the workspace member repository they consume are already registered above inside the
// persistence conditional. The scene is resolved within the query-supplied organization, its own workspace
// is discovered from the loaded row after the tenant boundary is enforced, and the request is authorized by
// the caller's role in the scene's own workspace (every denial hidden as 404, an insufficient WRITE role as
// 403). CORE-CB-001 adds the read side: GET list and GET by-id (allowed to any workspace member, PROJECTED BY
// ROLE — a content block IS content, so the host-content roles get the full shape WITH the body and every
// other role the body-stripped audience-safe shape; threat T2), so a client can finally enumerate authored
// content. The create assigns the initial revision (1) server-side; the delete loads the content block through
// the scene-, workspace- and tenant-scoped lookup, CASCADES its dependent visibility rules and asset links
// (its inline revision history goes with the row) and appends a ContentBlockDeleted audit record, all
// atomically (cascade, not block; docs/adr/0012-resource-deletion-cascades-dependents.md). A foreign/unknown
// block is a safe 404. The SDK/contract types for the new read routes are a separate story (CORE-SDK-006).
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

// Entity endpoints (CORE-ENT-006 create/list/read, CORE-LIFE-003 delete): the Entities module's generic
// entity routes under /api/v1/workspaces/{workspaceId}/entities. They live in the same authenticated route
// group and fail closed (503) when persistence is not configured, exactly like the entity-relationship
// removal endpoint. The DI they consume — the tenant context resolver, the entity/entity-type/workspace and
// workspace-member repositories and the EntityCreationService/EntityDeletionService above — is all registered
// inside the persistence conditional. CORE-ENT-006 adds the authoring CRUD: GET list and GET by-id (allowed
// to any workspace member, PROJECTED BY ROLE — an entity IS content, so the host-content roles get the full
// shape and every other role the stripped audience-safe shape), and POST create (Owner/Admin/Host/CoHost),
// which resolves the entity type within the route's workspace, mints the entity's server-side id and audits
// the creation (EntityCreated). The parent workspace is resolved FIRST (the route pins {workspaceId}, the
// tenant comes from the required organizationSlug), the caller is authorized by their role in that workspace,
// and every entity is loaded through the tenant- AND workspace-scoped repository — so an entity in another
// workspace or tenant is never reachable even when its id is known. Every denial is hidden as 404 (an
// insufficient create role as 403), and an unknown entity is a safe 404 (threats T1/T5). The DELETE
// (CORE-LIFE-003) CASCADES the entity's dependent edges, visibility rules and asset links and audits the
// deletion (EntityDeleted), all atomically (docs/adr/0012-resource-deletion-cascades-dependents.md).
app.MapEntityEndpoints();

// Entity-type endpoints (CORE-ENT-007): the Entities module's generic entity-TYPE routes under
// /api/v1/workspaces/{workspaceId}/entity-types. They live in the same authenticated route group and fail
// closed (503) when persistence is not configured, exactly like the entity endpoints. The DI they consume —
// the tenant context resolver, the entity-type/workspace and workspace-member repositories and the
// EntityTypeCreationService above — is all registered inside the persistence conditional. An entity type is the
// DATA-DRIVEN definition of a kind of entity (its template key plus field/type metadata), the template boundary
// through which a vertical maps its domain onto Core (docs/04). The story adds the authoring CRUD: GET list and
// GET by-id, and POST create, which mints the type's server-side id and audits the definition
// (EntityTypeCreated). Unlike the entity list/read, an entity type is an authoring/schema artifact rather than
// audience content, so ALL THREE routes are restricted to the authoring roles (Owner/Admin/Host/CoHost) with no
// host-vs-participant projection ("authorize like entity authoring"). The parent workspace is resolved FIRST
// (the route pins {workspaceId}, the tenant comes from the required organizationSlug), the caller is authorized
// by their role in that workspace, and every type is loaded through the tenant- AND workspace-scoped repository
// — so a type in another workspace or tenant is never reachable even when its id is known. A non-member is
// hidden as 404, an insufficient role is 403, an unknown/foreign type is a safe 404, a duplicate per-workspace
// key or an archived workspace is 409 (threats T1/T5; CORE-LIFE-009).
app.MapEntityTypeEndpoints();

// Asset endpoints (CORE-AST-003 upload intent, CORE-AST-004 signed download, CORE-AST-005
// linking, CORE-LIFE-006 deletion): the Assets module's HTTP routes,
// POST /api/v1/assets/upload-intent, GET /api/v1/assets/{assetId}/download-url,
// POST /api/v1/assets/{assetId}/links and DELETE /api/v1/assets/{assetId}. They live
// in an authenticated route group and fail closed (503) when persistence is not configured,
// exactly like the reveal/scene endpoints. No new DI registration beyond the upload-intent
// service, the asset link service, the download policy, the deletion service and the storage
// location above is required: the tenant context resolver, the asset/asset-link repositories, the
// workspace member repository and the IAssetStorage adapter they reuse are already registered.
// Upload-intent authorizes the caller by their role in the target workspace (Owner/Admin/Host/CoHost),
// mints server-side storage coordinates, registers a PENDING asset and returns a short-lived signed
// upload URL. The signed download flow resolves the asset within the query-supplied organization,
// discovers its workspace from the loaded row, authorizes the caller through the central Assets
// download policy (host-content roles always; an audience role only when the asset is linked to a
// VISIBLE content block/entity — CORE-AST-005; every denial hidden as 404, an insufficient role as
// 403, a still-pending asset as 409) and returns a short-lived signed download URL. The linking
// flow resolves the asset within the body-supplied organization, authorizes a host-content role,
// verifies the target content block/entity is in the asset's own workspace (the same-workspace
// coupling for the polymorphic target; threats T5/T1) and records the link (201; a repeat is 409, a
// missing target hidden as 404). The DELETE flow resolves the asset within the query-supplied
// organization, discovers its workspace from the loaded row, authorizes a host-content role
// (every denial hidden as 404, an insufficient role as 403), then removes the asset's links, deletes
// the storage object BEFORE the row and audits the deletion atomically (cascade, not block;
// docs/adr/0012-resource-deletion-cascades-dependents.md); 204 on success, a non-existent asset a safe
// 404. The asset is private by default and an unconfigured storage backend fails closed with 503 in
// the signed-URL flows AND the deletion flow (threat T4 "Asset leak"). The cleanup job (CORE-AST-006)
// reclaims abandoned pending intents in the worker.
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
// when persistence is not configured, exactly like the asset/quota endpoints. It reuses only registrations made
// above: the PurchaseVerificationProviderResolver and the PurchaseEnvironmentPolicy (both registered
// unconditionally above) and the PurchaseTransactionService + TimeProvider (registered in the persistence
// conditional, CORE-STORE-002). It authorizes the caller as a user principal (a service account is 403; the
// purchase is global, so there is no tenant boundary — CORE-STORE-002), resolves the deployment-supplied Apple
// verifier and verifies the submitted proof, and ONLY a verified result is recorded as a PurchaseTransaction
// (verify-then-record): a rejected proof is 422 and records nothing, an unconfigured verifier is 503, and a
// sandbox receipt is rejected (422, records nothing) on a production deployment (CORE-MON-008), so
// "Apple transaction data is verified before entitlements are granted" (the story acceptance criterion;
// docs/21). CORE-MON-002 additionally LINKS the verified purchase to the authenticated buyer's subject in the
// same transaction (billing_account_links), so a different subject submitting the same external receipt is 409
// and granted nothing; granting the SubjectEntitlement from the linked buyer is the next story (CORE-MON-003).
// The Google endpoint is CORE-STORE-004.
app.MapApplePurchaseEndpoints();

// Google purchase token verification endpoint (CORE-STORE-004): the Store module's second HTTP route, the
// Google analogue of the Apple endpoint above, POST /api/v1/purchases/google/tokens. It lives in an
// authenticated route group and fails closed (503) when persistence is not configured, exactly like the
// Apple/asset/quota endpoints. It reuses only registrations made above: the PurchaseVerificationProviderResolver
// and the PurchaseEnvironmentPolicy (both registered unconditionally above) and the PurchaseTransactionService +
// TimeProvider (registered in the persistence conditional, CORE-STORE-002). It authorizes the caller as a
// user principal (a service account is 403; the purchase is global, so there is no tenant boundary —
// CORE-STORE-002), resolves the deployment-supplied Google verifier and verifies the submitted purchase token,
// and ONLY a verified result is recorded as a PurchaseTransaction (verify-then-record): a rejected token is 422
// and records nothing, an unconfigured verifier is 503, and a sandbox receipt is rejected (422, records nothing)
// on a production deployment (CORE-MON-008), so "Google purchase tokens are verified before
// entitlements are granted" (the story acceptance criterion; docs/21). CORE-MON-002 additionally LINKS the
// verified purchase to the authenticated buyer's subject in the same transaction (billing_account_links), so a
// different subject submitting the same external receipt is 409 and granted nothing; granting the
// SubjectEntitlement from the linked buyer is the next story (CORE-MON-003). Idempotent store notifications are
// CORE-STORE-005.
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
