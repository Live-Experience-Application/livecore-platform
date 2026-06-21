// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Exports;
using LiveCore.Api.Hosting;
using LiveCore.Api.Observability;
using LiveCore.Api.Realtime;
using LiveCore.Api.Recaps;
using LiveCore.Api.Retention;
using LiveCore.Api.Store;
using LiveCore.Api.Visibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Worker;

/// <summary>
/// Builds the background worker host.
///
/// The worker owns the platform's asynchronous jobs (docs/02_ARCHITECTURE.md: "background jobs, exports,
/// cleanup, async processing"). It schedules four periodic loops, each gated on a configured database:
/// <list type="bullet">
///   <item>the asset cleanup job (CORE-AST-006), which reclaims abandoned, unconfirmed asset upload intents
///   (their stale metadata rows and any orphaned objects in private storage) — the Assets module owns the
///   cleanup logic (<see cref="ExpiredPendingAssetCleanupService"/>) and this host schedules it through
///   <see cref="AssetCleanupBackgroundService"/>;</item>
///   <item>the recap generation job (CORE-JOB-001), which produces a recap for every ENDED session that has
///   no recap yet, idempotently and tenant-scoped — the Recaps module owns the generation logic
///   (<see cref="RecapGenerationService"/>) and this host schedules it through
///   <see cref="RecapGenerationBackgroundService"/>;</item>
///   <item>the export processing job (CORE-JOB-002), which processes every queued workspace export job into a
///   workspace export manifest, idempotently and tenant-scoped — the Exports module owns the processing logic
///   (<see cref="ExportProcessingService"/>) and this host schedules it through
///   <see cref="ExportProcessingBackgroundService"/>;</item>
///   <item>the store-notification reconciliation job (CORE-JOB-003), which re-derives every drifted purchase's
///   status from the recorded store-notification ledger so missed/out-of-order deliveries converge, idempotently
///   — the Store module owns the reconciliation logic (<see cref="StoreNotificationReconciliationService"/>,
///   reusing <see cref="StoreNotificationService"/>) and this host schedules it through
///   <see cref="StoreNotificationReconciliationBackgroundService"/>. Unlike the others it is GATED ON BILLING:
///   billing/monetization is in scope for Core v1 (docs/01_PRODUCT_VISION_AND_SCOPE.md) but requires
///   deployment-supplied store adapters and credentials, so it runs only when a deployment has enabled it
///   (<c>Store:Reconciliation:Enabled=true</c>).</item>
/// </list>
/// Each job's logic lives in its owning domain module in <c>apps/api</c>; this host only handles timing,
/// scoping and resilience.
///
/// <para>
/// OBSERVABILITY SURFACE (CORE-DR-003). The host doing irreversible work must not be a monitoring blind spot,
/// so the worker exposes a small HTTP surface — built on the ASP.NET Core shared framework the referenced API
/// project already brings — that mirrors the API's observability wiring:
/// <list type="bullet">
///   <item><c>GET /metrics</c> — the Prometheus scrape endpoint serving the OpenTelemetry-collected
///   <see cref="LiveCoreMetrics"/> series, so the <c>livecore_job_failures_total</c> counter each loop records
///   on failure is actually scrapeable (it was recorded onto an unobserved meter before). Wired exactly as the
///   API host wires it (<see cref="MetricsServiceCollectionExtensions.AddLiveCorePrometheusMetrics"/> +
///   <see cref="MetricsEndpointRouteBuilderExtensions.MapLiveCoreMetricsEndpoint"/>); no new dependency.</item>
///   <item><c>GET /health/live</c> — the PER-LOOP liveness endpoint (<see cref="WorkerHealthEndpoints"/>),
///   healthy only when every active loop is beating (<see cref="WorkerJobHeartbeats"/>), so a single hung loop
///   is detectable over one probe instead of being masked by the other healthy loops.</item>
///   <item><c>GET /health/ready</c> — the readiness endpoint DISTINCT from liveness (CORE-OBS-013): in
///   Production the worker reports NOT-READY when it can do no work — persistence is unconfigured or no loop is
///   active (<see cref="WorkerReadiness"/>) — or its configuration is malformed
///   (<see cref="WorkerProductionConfiguration"/>), so the worker is no longer vacuously healthy with zero
///   active loops. Liveness restarts a wedged worker; readiness keeps a worker-that-cannot-work out of rotation
///   without pointlessly restarting it.</item>
/// </list>
/// The listen URL is read from configuration (<c>Worker:Metrics:Url</c>, default port 9464) and the surface is
/// unauthenticated by convention, restricted at the network edge, carrying only low-cardinality aggregates and
/// an overall status — never content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
/// </summary>
public static class WorkerHostFactory
{
    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Structured logging baseline (CORE-FND-004): same shape as the API
        // host - one JSON object per log entry on stdout, UTC timestamps,
        // scopes included. Uses the JSON console formatter built into
        // Microsoft.Extensions.Logging; no external logging dependency.
        //
        // As in the API host, the JSON FORMAT is fixed but the log LEVEL is an
        // operator configuration surface (CORE-OBS-011): ClearProviders()/
        // AddJsonConsole leave the configuration-driven log FILTERING intact, so
        // Logging:LogLevel:Default and per-category Logging:LogLevel:<Category>
        // overrides (Logging__LogLevel__* env vars) tune verbosity with safe
        // defaults from appsettings.json (the worker keeps Microsoft.Hosting.Lifetime
        // at Information). See docs/13_SELF_HOSTING_REQUIREMENTS.md and .env.example.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        });

        // Metrics/health HTTP surface (CORE-DR-003). Bind the small Kestrel listener to the configured URL
        // (Worker:Metrics:Url, default port 9464) and, like the API host, do not advertise the server header
        // (threat T7). The referenced API project brings the Microsoft.AspNetCore.App shared framework, so the
        // worker can host this surface without any new dependency.
        var metricsOptions = WorkerMetricsOptions.FromConfiguration(builder.Configuration);
        builder.WebHost.UseUrls(metricsOptions.Url);
        builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

        // Operational metrics instrument set + the Prometheus exporter that backs GET /metrics (CORE-OBS-001,
        // surfaced for the worker by CORE-DR-003). The worker records the docs/15_OBSERVABILITY.md "background
        // job failures" signal onto the shared LiveCoreMetrics from each loop; this wires the same MeterProvider
        // + Prometheus exporter the API host uses so those counters are scrapeable. Registered unconditionally
        // (no database/identity needed), exactly like the API's /metrics, so the surface exists even when the
        // worker runs without a database.
        builder.Services.AddLiveCorePrometheusMetrics();

        // Distributed tracing (CORE-OBS-012). Mirror the API host's tracing wiring so the worker — the host
        // doing the IRREVERSIBLE purge/cleanup/export/recap/reconciliation work — is no longer a tracing blind
        // spot when a deployment configures tracing for the API. Each background loop iteration produces one
        // LiveCoreActivitySource.WorkerJobLoopActivityName span tagged with the coarse loop name and outcome
        // (the loops below set it), and the same auto-instrumentations the API uses nest a sweep's database
        // commands under it. Identical wiring to the API, only the resource service.name differs
        // (livecore-worker). INERT BY DEFAULT (parity with the API): the OTLP exporter is attached ONLY when
        // Tracing:Otlp:Endpoint is configured; with nothing configured the source is still registered (so spans
        // are produced and any in-process listener observes them) but shipped nowhere, so an unconfigured worker
        // never reaches a non-existent collector. Registered unconditionally (no database/identity needed), and
        // it also registers the singleton LiveCoreActivitySource the loops inject. The endpoint is read from
        // configuration only; the spans carry only low-cardinality, non-sensitive attributes (threat T7).
        builder.Services.AddLiveCoreWorkerOpenTelemetryTracing(builder.Configuration);

        // Graceful shutdown drain window (CORE-DEP-002). The same wiring the API host uses, so BOTH hosts drain
        // in-flight work within ONE explicit, tuned, configurable HostOptions.ShutdownTimeout
        // (Hosting:ShutdownTimeout) on a rolling restart rather than relying on the implicit framework default.
        // The worker's job loops already honor the stopping token (each sweep observes cancellation); this bounds
        // how long the host waits for a loop's in-flight tick to unwind before forcing shutdown, so a rolling
        // restart does not abruptly cut a tick mid-flight. Deployment policy read from configuration only with a
        // safe default, coordinated with the orchestration grace period (docs/13_SELF_HOSTING_REQUIREMENTS.md).
        builder.Services.AddLiveCoreGracefulShutdown(builder.Configuration);

        // Background jobs (CORE-AST-006 asset cleanup, CORE-JOB-001 recap generation, CORE-JOB-002 export
        // processing). Each Add* extension
        // registers its owning module's dependencies (the EF Core DbContext, the module repositories, a
        // TimeProvider, the job policy and the job's application service) and returns whether persistence is
        // configured. Like the API host, each job is GATED on a configured database connection string: with
        // none, no DbContext and no loop are registered, so the worker still starts (it just has no job to run)
        // rather than failing closed. The shared infrastructure (the DbContext and the TimeProvider) is
        // registered with TryAdd/AddDbContext semantics in each extension, so wiring all of them in the same
        // container composes safely. Each scheduling background service is added only when its job is configured.
        var assetCleanupConfigured = builder.Services.AddAssetCleanup(builder.Configuration);
        var recapGenerationConfigured = builder.Services.AddRecapGeneration(builder.Configuration);
        var exportProcessingConfigured = builder.Services.AddExportProcessing(builder.Configuration);

        // CORE-JOB-003: gated on a configured database AND an explicit billing/store-reconciliation opt-in
        // (Store:Reconciliation:Enabled=true). With billing not configured this returns false and registers no
        // loop — "only runs when billing is configured" (fail-closed; monetization is in scope for Core v1 but
        // needs deployment-supplied store adapters/credentials, so reconciliation is opt-in per deployment).
        var storeReconciliationConfigured = builder.Services.AddStoreNotificationReconciliation(builder.Configuration);

        // CORE-PRIV-003: gated on a configured database. The data-retention sweep expires and purges terminal/old
        // personal-data-bearing records (sessions/recaps/exports/invitations) on configurable, per-family windows
        // (each independently enabled; the surprising ones disabled by default), auditing every purge by id. The
        // loop is registered whenever persistence is configured — disabled windows are skipped inside the sweep —
        // so liveness can observe it even when only the default invitation-email window runs.
        var dataRetentionConfigured = builder.Services.AddDataRetention(builder.Configuration);

        // CORE-VSEAL-002: gated on a configured database AND an explicit opt-in (Visibility:ScheduledReveal:Enabled=true).
        // The scheduled-reveal sweep automatically reveals every Hidden visibility rule whose scheduledRevealAt time
        // has arrived, driving the SAME central reveal command as a live host reveal (so the auto-reveal is gated
        // through the Visibility engine and emits the normal session events to exactly the authorized audience). It is
        // OFF BY DEFAULT — server-enforced scheduled reveal is opt-in per deployment — so with it not enabled this
        // returns false and registers no loop (fail-safe).
        var scheduledRevealConfigured = builder.Services.AddScheduledReveal(builder.Configuration);

        // CORE-PUSH-002: gated on a configured database AND an explicit opt-in WITH VAPID configured
        // (WebPush:Delivery:Enabled=true plus the WebPush:Vapid:* signing material). The push dispatch sweep drains
        // the closed-app push outbox the API enqueues for already-authorized, recipient-filtered session events and
        // sends a CONTENT-FREE Web Push to each recipient's subscriptions (deleting any the push service reports gone
        // — the 404/410 cleanup). It introduces an OUTBOUND-HTTP fan-out, so it is strictly OFF BY DEFAULT — with it
        // not enabled (or VAPID unconfigured) this returns false and registers no loop (fail-safe, inert).
        var webPushDeliveryConfigured = builder.Services.AddWebPushDelivery(builder.Configuration);

        // The exact set of loops that are actually scheduled, so the per-loop liveness check (below) expects a
        // beat from precisely those loops — no more (a loop the deployment did not configure must not read as
        // "stalled") and no fewer (a configured loop that hangs must read as stalled).
        var activeJobNames = new List<string>();
        if (assetCleanupConfigured)
        {
            activeJobNames.Add(WorkerJobNames.AssetCleanup);
        }

        if (recapGenerationConfigured)
        {
            activeJobNames.Add(WorkerJobNames.RecapGeneration);
        }

        if (exportProcessingConfigured)
        {
            activeJobNames.Add(WorkerJobNames.ExportProcessing);
        }

        if (storeReconciliationConfigured)
        {
            activeJobNames.Add(WorkerJobNames.StoreNotificationReconciliation);
        }

        if (dataRetentionConfigured)
        {
            activeJobNames.Add(WorkerJobNames.DataRetention);
        }

        if (scheduledRevealConfigured)
        {
            activeJobNames.Add(WorkerJobNames.ScheduledReveal);
        }

        if (webPushDeliveryConfigured)
        {
            activeJobNames.Add(WorkerJobNames.WebPushDispatch);
        }

        // Per-loop liveness heartbeat (CORE-OPS-005, per-loop under CORE-DR-003). Each loop beats its OWN file
        // each tick; a single hung loop's file goes stale while the others keep beating, so one healthy loop no
        // longer masks three hung ones. Registered UNCONDITIONALLY so GET /health/live always responds: with no
        // database the active set is empty and liveness is vacuously healthy (there is nothing to stall). The
        // TimeProvider the heartbeats stamp with is registered by the Add* extensions when a job is configured;
        // TryAdd a system clock for the no-database case so the registry can always resolve one.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(WorkerHeartbeatOptions.FromConfiguration(builder.Configuration));
        builder.Services.AddSingleton(serviceProvider => new WorkerJobHeartbeats(
            serviceProvider.GetRequiredService<WorkerHeartbeatOptions>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILoggerFactory>(),
            activeJobNames));

        // Liveness health check backing GET /health/live: healthy only when every active loop is beating, so a
        // single hung loop makes the worker not-live and orchestration restarts it.
        builder.Services
            .AddHealthChecks()
            .AddCheck<WorkerHeartbeatLivenessHealthCheck>(
                "worker-job-loops", tags: [WorkerHealthEndpoints.LivenessTag]);

        // Worker readiness gate (CORE-OBS-013), the readiness signal DISTINCT from the per-loop liveness above
        // and the worker counterpart of the API's required-dependency readiness gate (CORE-OPS-005). Per-loop
        // liveness is vacuously healthy with zero active loops (a process with nothing to do is genuinely alive),
        // so a worker deployed to Production with no database advertised "live" while able to do no work at all.
        // This backs GET /health/ready: in Production it reports NOT-READY when persistence is unconfigured OR no
        // loop is active (the worker can do no useful work), so a misconfigured production worker leaves the ready
        // rotation instead. The worker requires NO OIDC (it serves no authenticated request), so — unlike the API
        // gate — OIDC is not part of its required set. Registered UNCONDITIONALLY (outside any persistence gate)
        // precisely so it still runs in the no-database case; outside Production it is inert (local-development
        // latitude), and the readiness response stays status-only so WHY it is not ready never leaks (threat T7).
        var persistenceConfigured =
            !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Database"));
        builder.Services.AddWorkerReadinessCheck(
            builder.Environment, persistenceConfigured, activeJobNames.Count);

        // Malformed worker configuration readiness gate (CORE-OBS-013), parity with the API well-formedness gate
        // (CORE-OPS-013). The readiness gate above reports NOT-READY when persistence is UNCONFIGURED, but a
        // connection string that is PRESENT yet malformed passes the non-blank check and (registered lazily with
        // EF Core) would otherwise surface only on the first sweep. This captures the startup well-formedness
        // verdict and reports NOT-READY in Production when the connection string is malformed, so it leaves the
        // ready rotation exactly as an unconfigured dependency does. Registered UNCONDITIONALLY so it runs even
        // when persistence is absent; inert outside Production; status-only so WHICH key is malformed never leaks
        // (threat T7). The matching loud, named Critical startup log is emitted below once the host is built.
        builder.Services.AddMalformedWorkerConfigurationReadinessCheck(builder.Configuration, builder.Environment);

        if (assetCleanupConfigured)
        {
            builder.Services.AddHostedService<AssetCleanupBackgroundService>();
        }

        if (recapGenerationConfigured)
        {
            builder.Services.AddHostedService<RecapGenerationBackgroundService>();
        }

        if (exportProcessingConfigured)
        {
            builder.Services.AddHostedService<ExportProcessingBackgroundService>();
        }

        if (storeReconciliationConfigured)
        {
            builder.Services.AddHostedService<StoreNotificationReconciliationBackgroundService>();
        }

        if (dataRetentionConfigured)
        {
            builder.Services.AddHostedService<DataRetentionSweepBackgroundService>();
        }

        if (scheduledRevealConfigured)
        {
            builder.Services.AddHostedService<ScheduledRevealBackgroundService>();
        }

        if (webPushDeliveryConfigured)
        {
            builder.Services.AddHostedService<PushNotificationDispatchBackgroundService>();
        }

        var app = builder.Build();

        // Worker production configuration contract (CORE-OBS-013), parity with the API's loud startup diagnostics
        // (CORE-OPS-008 / CORE-OPS-013). In Production a missing required value (the database connection string)
        // or a present-but-malformed connection string does NOT crash the worker — it stays up, schedules no loop
        // it cannot back, and reports NOT-READY (the readiness gates registered above) — while these log a loud,
        // NAMED Critical line so the misconfiguration is unmissable. Only the missing/malformed KEY NAMES are
        // logged, never the configured values, so a secret is never written to the log (threat T7). Outside
        // Production both contracts are inert (local-development latitude).
        var missingRequiredSettings = WorkerProductionConfiguration.FindMissingRequiredSettings(
            builder.Configuration, app.Environment.IsProduction());
        if (missingRequiredSettings.Count > 0)
        {
            app.Logger.LogCritical(
                "Worker production configuration is incomplete: the required setting(s) {MissingSettings} are not " +
                "configured. Inject them as environment variables / from your secret store (see .env.example and " +
                "docs/13_SELF_HOSTING_REQUIREMENTS.md). The worker stays up and reports not-ready, but it can run no " +
                "job loop until they are set.",
                string.Join(", ", missingRequiredSettings));
        }

        var malformedCriticalSettings = WorkerProductionConfiguration.FindMalformedCriticalSettings(
            builder.Configuration, app.Environment.IsProduction());
        if (malformedCriticalSettings.Count > 0)
        {
            app.Logger.LogCritical(
                "Worker production configuration is malformed: the setting(s) {MalformedSettings} are present but " +
                "not well-formed (the connection string must parse). Correct them in your environment / secret store " +
                "(see docs/13_SELF_HOSTING_REQUIREMENTS.md). The worker stays up and reports not-ready, but it can " +
                "run no job loop until they are corrected.",
                string.Join(", ", malformedCriticalSettings));
        }

        // GET /metrics — the Prometheus scrape endpoint (same mapping the API host uses), and GET /health/live +
        // GET /health/ready — the per-loop liveness and readiness endpoints. All unauthenticated by convention
        // and restricted at the edge.
        app.MapLiveCoreMetricsEndpoint();
        app.MapWorkerHealthEndpoints();

        // Development-default exposure footgun (CORE-OPS-011), the same loud startup warning the API host emits,
        // reusing the shared DevelopmentExposureWarning helper. The worker binds its metrics/health surface to a
        // non-loopback interface by default (Worker:Metrics:Url, http://0.0.0.0:9464), so a Development worker
        // reachable beyond localhost gets the same warning — the deploy/compose/docker-compose.prod.yml overlay
        // forces Production on the worker too. Emitted from ApplicationStarted because the bound addresses are
        // only resolved once the listener has started; it logs only bind addresses (threat T7) and never changes
        // how the worker runs.
        app.Lifetime.ApplicationStarted.Register(() =>
            DevelopmentExposureWarning.WarnIfExposedDevelopmentBind(
                app.Logger, app.Environment.IsDevelopment(), app.Urls));

        return app;
    }
}
