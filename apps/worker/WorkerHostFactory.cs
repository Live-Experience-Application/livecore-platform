using LiveCore.Api.Assets;
using LiveCore.Api.Exports;
using LiveCore.Api.Observability;
using LiveCore.Api.Recaps;
using LiveCore.Api.Store;
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
///   it runs only when a deployment has enabled it (<c>Store:Reconciliation:Enabled=true</c>), because store
///   receipts/billing are out of scope for Core v1 (docs/01_PRODUCT_VISION_AND_SCOPE.md).</item>
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
        // loop — "only runs when billing is configured" (fail-closed; billing is out of scope for Core v1).
        var storeReconciliationConfigured = builder.Services.AddStoreNotificationReconciliation(builder.Configuration);

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

        var app = builder.Build();

        // GET /metrics — the Prometheus scrape endpoint (same mapping the API host uses), and GET /health/live
        // — the per-loop liveness endpoint. Both unauthenticated by convention and restricted at the edge.
        app.MapLiveCoreMetricsEndpoint();
        app.MapWorkerHealthEndpoints();

        return app;
    }
}
