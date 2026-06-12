using LiveCore.Api.Assets;

namespace LiveCore.Worker;

/// <summary>
/// Builds the background worker host.
///
/// The worker owns the platform's asynchronous jobs (docs/02_ARCHITECTURE.md: "background jobs, exports,
/// cleanup, async processing"). Its first job is the asset cleanup job (CORE-AST-006): a periodic sweep
/// that reclaims abandoned, unconfirmed asset upload intents (their stale metadata rows and any orphaned
/// objects in private storage). The Assets module owns the cleanup logic (<see cref="ExpiredPendingAssetCleanupService"/>);
/// this host only schedules it through <see cref="AssetCleanupBackgroundService"/>.
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

        // Asset cleanup job (CORE-AST-006). AddAssetCleanup registers the Assets module's cleanup
        // dependencies (the EF Core DbContext, the asset repository, the fail-closed IAssetStorage default,
        // the cleanup policy and the cleanup service) and returns whether persistence is configured. Like
        // the API host, the job is GATED on a configured database connection string: with none, no DbContext
        // and no cleanup loop are registered, so the worker still starts (it just has no job to run) rather
        // than failing closed. The scheduling background service is only added when the job is configured.
        if (builder.Services.AddAssetCleanup(builder.Configuration))
        {
            builder.Services.AddHostedService<AssetCleanupBackgroundService>();
        }

        return builder;
    }
}
