// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Exports;
using LiveCore.Api.Observability;

namespace LiveCore.Worker;

/// <summary>
/// Schedules the export processing job (CORE-JOB-002): on a fixed, configurable interval it runs one sweep of
/// <see cref="ExportProcessingService"/>, which processes every queued export job — a workspace-scoped job that
/// has not yet finished — into a workspace export manifest, idempotently and tenant-scoped, driving each job
/// through its guarded status transitions. The worker owns the platform's async jobs (docs/02_ARCHITECTURE.md:
/// the worker owns "background jobs, exports, cleanup, async processing"); the processing LOGIC lives in the
/// Exports module, so this host service only handles timing, scoping and resilience — exactly the split, and
/// the structure, of <see cref="RecapGenerationBackgroundService"/> and <see cref="AssetCleanupBackgroundService"/>.
///
/// <para>
/// Each sweep runs inside its own dependency-injection scope (the processing service, its readers and the
/// repositories are scoped), created from <see cref="IServiceScopeFactory"/>. The loop is RESILIENT: a sweep
/// that throws is logged and the loop continues to the next tick, so one transient database failure never tears
/// the worker down. Cancellation (host shutdown) ends the loop cleanly. This service is only registered when
/// persistence is configured (see <see cref="WorkerHostFactory"/>), so it always has a database to sweep. The
/// job is idempotent at the data layer (a terminal job is never queued and a job's manifest is admitted at most
/// once), so a sweep retried after a crash never double-produces a manifest.
/// </para>
///
/// <para>
/// Liveness heartbeat (CORE-OPS-005, per-loop under CORE-DR-003): like the other worker loops, this loop
/// writes its OWN <see cref="WorkerHeartbeat"/> on startup and after every sweep tick, so orchestration can
/// detect a WEDGED loop (this loop's heartbeat file goes stale when its sweep hangs, even while the other
/// loops keep beating their own files, so a single hung loop is detectable rather than masked).
/// </para>
/// </summary>
internal sealed class ExportProcessingBackgroundService : BackgroundService
{
    private const string _jobName = WorkerJobNames.ExportProcessing;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExportProcessingOptions _options;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly LiveCoreMetrics _metrics;
    private readonly ILogger<ExportProcessingBackgroundService> _logger;

    public ExportProcessingBackgroundService(
        IServiceScopeFactory scopeFactory,
        ExportProcessingOptions options,
        WorkerJobHeartbeats heartbeats,
        LiveCoreMetrics metrics,
        ILogger<ExportProcessingBackgroundService> logger)
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
            "Export processing job started; processing queued export jobs every {Interval} (batch size {BatchSize}).",
            _options.SweepInterval,
            _options.BatchSize);

        // Emit an initial heartbeat so the liveness signal exists from the moment the loop starts, before the
        // first sweep runs (CORE-OPS-005).
        await _heartbeat.BeatAsync(stoppingToken).ConfigureAwait(false);

        // Run a sweep promptly on startup, then once per interval. PeriodicTimer does not drift and is disposed
        // when the loop ends (host shutdown).
        using var timer = new PeriodicTimer(_options.SweepInterval);
        do
        {
            await RunSweepAsync(stoppingToken).ConfigureAwait(false);

            // Heartbeat after each completed sweep tick. A wedged sweep never reaches this point, so its
            // heartbeat file goes stale and orchestration can detect the stalled loop (CORE-OPS-005).
            await _heartbeat.BeatAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false));

        _logger.LogInformation("Export processing job stopped.");
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<ExportProcessingService>();

            var result = await processor
                .ProcessQueuedExportsAsync(stoppingToken)
                .ConfigureAwait(false);

            if (result.Examined > 0)
            {
                _logger.LogInformation(
                    "Export processing sweep complete: examined {Examined}, processed {Processed}, failed {Failed}, dead-lettered {DeadLettered}.",
                    result.Examined,
                    result.Processed,
                    result.Failed,
                    result.DeadLettered);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown during a sweep is expected; let the loop end quietly.
        }
        catch (Exception exception)
        {
            // A failed sweep must never crash the worker; record the docs/15_OBSERVABILITY.md "background job
            // failures" signal (CORE-OBS-001), log it (identifiers and counts only) and let the next tick try
            // again. Counting only — the exception detail goes to the structured log, never a metric label
            // (threat T7).
            _metrics.RecordBackgroundJobFailure(_jobName);
            _logger.LogError(exception, "Export processing sweep failed; it will be retried on the next interval.");
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
