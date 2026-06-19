// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Observability;
using LiveCore.Api.Recaps;

namespace LiveCore.Worker;

/// <summary>
/// Schedules the recap generation job (CORE-JOB-001): on a fixed, configurable interval it runs one sweep of
/// <see cref="RecapGenerationService"/>, which produces a recap for every session that NEEDS one — a session
/// that has ENDED but has no recap yet — idempotently and tenant-scoped. The worker owns the platform's async
/// jobs (docs/02_ARCHITECTURE.md); the generation LOGIC lives in the Recaps module, so this host service only
/// handles timing, scoping and resilience — exactly the split, and the structure, of
/// <see cref="AssetCleanupBackgroundService"/>.
///
/// <para>
/// Each sweep runs inside its own dependency-injection scope (the generation service, its eligibility reader
/// and the recap repository are scoped), created from <see cref="IServiceScopeFactory"/>. The loop is
/// RESILIENT: a sweep that throws is logged and the loop continues to the next tick, so one transient database
/// failure never tears the worker down. Cancellation (host shutdown) ends the loop cleanly. This service is
/// only registered when persistence is configured (see <see cref="WorkerHostFactory"/>), so it always has a
/// database to sweep. The job is idempotent at the data layer: a session that already has a recap is never
/// eligible, so a sweep retried after a crash never double-produces a recap, and the partial unique index
/// <c>recaps(session_id) WHERE generated_by IS NULL</c> (CORE-RCP-001) makes overlapping sweeps and multiple
/// worker replicas safe — a losing race converges onto the existing recap rather than producing a duplicate,
/// so this loop needs NO single-instance guard.
/// </para>
///
/// <para>
/// Liveness heartbeat (CORE-OPS-005, per-loop under CORE-DR-003): like the asset cleanup loop, this loop
/// writes its OWN <see cref="WorkerHeartbeat"/> on startup and after every sweep tick, so orchestration can
/// detect a WEDGED loop (this loop's heartbeat file goes stale when its sweep hangs, even while the other
/// loops keep beating their own files, so a single hung loop is detectable rather than masked).
/// </para>
/// </summary>
internal sealed class RecapGenerationBackgroundService : BackgroundService
{
    private const string _jobName = WorkerJobNames.RecapGeneration;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecapGenerationOptions _options;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly LiveCoreMetrics _metrics;
    private readonly LiveCoreActivitySource _activitySource;
    private readonly ILogger<RecapGenerationBackgroundService> _logger;

    public RecapGenerationBackgroundService(
        IServiceScopeFactory scopeFactory,
        RecapGenerationOptions options,
        WorkerJobHeartbeats heartbeats,
        LiveCoreMetrics metrics,
        LiveCoreActivitySource activitySource,
        ILogger<RecapGenerationBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(heartbeats);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(activitySource);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _heartbeat = heartbeats.ForJob(_jobName);
        _metrics = metrics;
        _activitySource = activitySource;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Recap generation job started; generating recaps for eligible sessions every {Interval} (batch size {BatchSize}).",
            _options.SweepInterval,
            _options.BatchSize);

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

        _logger.LogInformation("Recap generation job stopped.");
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        // Distributed-tracing span for this recap-generation iteration (CORE-OBS-012). Tagged with the coarse
        // loop name and, below, the outcome; any database command the sweep issues nests under it. A no-op
        // (null) when no tracer/exporter is listening, so an unconfigured worker pays nothing (threat T7).
        using var activity = _activitySource.StartWorkerJobLoop(_jobName);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var generator = scope.ServiceProvider.GetRequiredService<RecapGenerationService>();

            var result = await generator
                .GenerateDueRecapsAsync(stoppingToken)
                .ConfigureAwait(false);

            // SLI signals (CORE-OBS-007): the sweep completed without throwing, so record one job success, the
            // sweep duration and the observed backlog/queue depth (the count of eligible sessions it examined) —
            // so a loop falling behind is alertable. Counts only, tagged by the coarse loop name, never a
            // tenant/principal/content detail (threat T7).
            _metrics.RecordBackgroundJobSuccess(_jobName);
            _metrics.RecordBackgroundJobDuration(_jobName, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            _metrics.RecordBackgroundJobBacklog(_jobName, result.Examined);
            activity?.SetTag(LiveCoreActivitySource.WorkerJobOutcomeTag, WorkerJobOutcomes.Success);

            if (result.Examined > 0)
            {
                _logger.LogInformation(
                    "Recap generation sweep complete: examined {Examined}, generated {Generated}, deduplicated {Deduplicated}, failed {Failed}.",
                    result.Examined,
                    result.Generated,
                    result.Deduplicated,
                    result.Failed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown during a sweep is expected; let the loop end quietly.
            activity?.SetTag(LiveCoreActivitySource.WorkerJobOutcomeTag, WorkerJobOutcomes.Canceled);
        }
        catch (Exception exception)
        {
            // A failed sweep must never crash the worker; record the docs/15_OBSERVABILITY.md "background job
            // failures" signal (CORE-OBS-001) and the sweep duration up to the throw (CORE-OBS-007), log it
            // (identifiers and counts only) and let the next tick try again. Counting only — the exception
            // detail goes to the structured log, never a metric label (threat T7).
            _metrics.RecordBackgroundJobFailure(_jobName);
            _metrics.RecordBackgroundJobDuration(_jobName, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(LiveCoreActivitySource.WorkerJobOutcomeTag, WorkerJobOutcomes.Failure);
            _logger.LogError(exception, "Recap generation sweep failed; it will be retried on the next interval.");
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
