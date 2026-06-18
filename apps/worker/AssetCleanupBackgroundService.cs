// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Assets;
using LiveCore.Api.Observability;

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
///
/// <para>
/// Liveness heartbeat (CORE-OPS-005, per-loop under CORE-DR-003): the loop writes its OWN
/// <see cref="WorkerHeartbeat"/> (obtained from <see cref="WorkerJobHeartbeats"/>) on startup and after every
/// sweep tick, so orchestration can detect a WEDGED loop. A resilient sweep survives transient failures, but a
/// sweep that HANGS (a stuck database or storage call that never returns or throws) would otherwise leave the
/// worker process alive yet doing no work — invisible to a process-liveness check. Because the heartbeat is
/// refreshed only by the loop making progress, a hung sweep stops refreshing its file (while the other loops
/// keep beating theirs) and the worker's <c>/health/live</c> endpoint reports not-live, which is the signal
/// orchestration uses to restart the stalled worker.
/// </para>
/// </summary>
internal sealed class AssetCleanupBackgroundService : BackgroundService
{
    private const string _jobName = WorkerJobNames.AssetCleanup;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AssetCleanupOptions _options;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly LiveCoreMetrics _metrics;
    private readonly ILogger<AssetCleanupBackgroundService> _logger;

    public AssetCleanupBackgroundService(
        IServiceScopeFactory scopeFactory,
        AssetCleanupOptions options,
        WorkerJobHeartbeats heartbeats,
        LiveCoreMetrics metrics,
        ILogger<AssetCleanupBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(heartbeats);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _heartbeat = heartbeats.ForJob(_jobName);
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Asset cleanup job started; sweeping every {Interval} for pending assets older than {Retention}.",
            _options.SweepInterval,
            _options.PendingRetention);

        // Emit an initial heartbeat so the liveness signal exists from the moment the loop starts, before the
        // first sweep runs (CORE-OPS-005).
        await _heartbeat.BeatAsync(stoppingToken).ConfigureAwait(false);

        // Run a sweep promptly on startup, then once per interval. PeriodicTimer does not drift and is
        // disposed when the loop ends (host shutdown).
        using var timer = new PeriodicTimer(_options.SweepInterval);
        do
        {
            await RunSweepAsync(stoppingToken).ConfigureAwait(false);

            // Heartbeat after each completed sweep tick. A wedged sweep never reaches this point, so its
            // heartbeat file goes stale and orchestration can detect the stalled loop (CORE-OPS-005).
            await _heartbeat.BeatAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false));

        _logger.LogInformation("Asset cleanup job stopped.");
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var cleanup = scope.ServiceProvider.GetRequiredService<ExpiredPendingAssetCleanupService>();

            var result = await cleanup
                .CleanUpExpiredPendingAssetsAsync(stoppingToken)
                .ConfigureAwait(false);

            // SLI signals (CORE-OBS-007): the sweep completed without throwing (a configured-storage-unavailable
            // early stop is still a clean run that examined nothing), so record one job success, the sweep
            // duration and the observed backlog/queue depth (the count of pending assets it examined). Counts
            // only, tagged by the coarse loop name, never a tenant/principal/storage coordinate (threat T7).
            _metrics.RecordBackgroundJobSuccess(_jobName);
            _metrics.RecordBackgroundJobDuration(_jobName, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            _metrics.RecordBackgroundJobBacklog(_jobName, result.Examined);

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
            // A failed sweep must never crash the worker; record the docs/15_OBSERVABILITY.md "background job
            // failures" signal (CORE-OBS-001) and the sweep duration up to the throw (CORE-OBS-007), log it (no
            // storage coordinates are involved here) and let the next tick try again. Counting only — the
            // exception detail goes to the structured log, never a metric label (threat T7).
            _metrics.RecordBackgroundJobFailure(_jobName);
            _metrics.RecordBackgroundJobDuration(_jobName, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
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
