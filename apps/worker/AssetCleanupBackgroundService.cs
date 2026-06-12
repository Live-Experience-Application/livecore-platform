using LiveCore.Api.Assets;

namespace LiveCore.Worker;

/// <summary>
/// Schedules the asset cleanup job (CORE-AST-006): on a fixed interval it runs one sweep of
/// <see cref="ExpiredPendingAssetCleanupService"/>, which reclaims abandoned, unconfirmed asset upload
/// intents (stale pending metadata rows and any orphaned objects in private storage). The worker owns the
/// platform's async jobs (docs/02_ARCHITECTURE.md); the cleanup LOGIC lives in the Assets module, so this
/// host service only handles timing, scoping and resilience.
///
/// <para>
/// Each sweep runs inside its own dependency-injection scope (the cleanup service and its EF Core
/// repository are scoped), created from <see cref="IServiceScopeFactory"/>. The loop is RESILIENT: a sweep
/// that throws is logged and the loop continues to the next tick, so one transient database or storage
/// failure never tears the worker down. Cancellation (host shutdown) ends the loop cleanly. This service is
/// only registered when persistence is configured (see <see cref="WorkerHostFactory"/>), so it always has a
/// database to sweep.
/// </para>
/// </summary>
internal sealed class AssetCleanupBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AssetCleanupOptions _options;
    private readonly ILogger<AssetCleanupBackgroundService> _logger;

    public AssetCleanupBackgroundService(
        IServiceScopeFactory scopeFactory,
        AssetCleanupOptions options,
        ILogger<AssetCleanupBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Asset cleanup job started; sweeping every {Interval} for pending assets older than {Retention}.",
            _options.SweepInterval,
            _options.PendingRetention);

        // Run a sweep promptly on startup, then once per interval. PeriodicTimer does not drift and is
        // disposed when the loop ends (host shutdown).
        using var timer = new PeriodicTimer(_options.SweepInterval);
        do
        {
            await RunSweepAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false));

        _logger.LogInformation("Asset cleanup job stopped.");
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var cleanup = scope.ServiceProvider.GetRequiredService<ExpiredPendingAssetCleanupService>();

            var result = await cleanup
                .CleanUpExpiredPendingAssetsAsync(stoppingToken)
                .ConfigureAwait(false);

            if (result.StorageUnavailable)
            {
                _logger.LogWarning(
                    "Asset cleanup sweep stopped early: object storage is not configured; nothing was removed.");
            }
            else if (result.Examined > 0)
            {
                _logger.LogInformation(
                    "Asset cleanup sweep complete: examined {Examined}, removed {Removed}, failed {Failed}.",
                    result.Examined,
                    result.Removed,
                    result.Failed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown during a sweep is expected; let the loop end quietly.
        }
        catch (Exception exception)
        {
            // A failed sweep must never crash the worker; log it (no storage coordinates are involved here)
            // and let the next tick try again.
            _logger.LogError(exception, "Asset cleanup sweep failed; it will be retried on the next interval.");
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
