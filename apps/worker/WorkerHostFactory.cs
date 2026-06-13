using LiveCore.Api.Assets;
using LiveCore.Api.Exports;
using LiveCore.Api.Observability;
using LiveCore.Api.Recaps;

namespace LiveCore.Worker;

/// <summary>
/// Builds the background worker host.
///
/// The worker owns the platform's asynchronous jobs (docs/02_ARCHITECTURE.md: "background jobs, exports,
/// cleanup, async processing"). It schedules two periodic jobs, each gated on a configured database:
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
///   <see cref="ExportProcessingBackgroundService"/>.</item>
/// </list>
/// Each job's logic lives in its owning domain module in <c>apps/api</c>; this host only handles timing,
/// scoping and resilience.
/// </summary>
public static class WorkerHostFactory
{
    public static HostApplicationBuilder Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

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

        // Operational metrics instrument set (CORE-OBS-001). The worker records the docs/15_OBSERVABILITY.md
        // "background job failures" signal onto the shared LiveCoreMetrics when a cleanup sweep throws.
        // Registered unconditionally (recording to an unobserved meter is a cheap no-op); the worker is a
        // non-HTTP background host, so it does not expose its own scrape endpoint — surfacing the worker's
        // metrics over a scrape/OTLP surface is a documented follow-up. The API host owns the /metrics surface.
        builder.Services.AddLiveCoreMetrics();

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

        if (assetCleanupConfigured || recapGenerationConfigured || exportProcessingConfigured)
        {
            // Worker liveness heartbeat (CORE-OPS-005): each job loop writes a heartbeat each tick so a wedged
            // loop is detectable by orchestration (the shared file goes stale). Registered once, alongside the
            // jobs, because the heartbeat IS the worker process's liveness signal; with no database there is no
            // loop to stall, so there is nothing to heartbeat. The file path is read from configuration with a
            // safe default (Worker:Heartbeat:FilePath); the TimeProvider the heartbeat stamps with is registered
            // by the Add* extensions above.
            builder.Services.AddSingleton(WorkerHeartbeatOptions.FromConfiguration(builder.Configuration));
            builder.Services.AddSingleton<WorkerHeartbeat>();
        }

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

        return builder;
    }
}
