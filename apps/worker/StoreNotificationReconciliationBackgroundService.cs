// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Observability;
using LiveCore.Api.Store;

namespace LiveCore.Worker;

/// <summary>
/// Schedules the store-notification reconciliation job (CORE-JOB-003): on a fixed, configurable interval it runs
/// one sweep of <see cref="StoreNotificationReconciliationService"/>, which re-derives every DRIFTED purchase's
/// status from the recorded store-notification ledger and converges it — reconciling the missed or out-of-order
/// deliveries the synchronous webhook (CORE-STORE-005) cannot, idempotently. The worker owns the platform's async
/// jobs (docs/02_ARCHITECTURE.md); the reconciliation LOGIC lives in the Store module (reusing
/// <see cref="StoreNotificationService"/>), so this host service only handles timing, scoping and resilience —
/// exactly the split, and the structure, of <see cref="ExportProcessingBackgroundService"/> and
/// <see cref="RecapGenerationBackgroundService"/>.
///
/// <para>
/// GATED ON BILLING. Unlike the other loops, this one is registered only when a deployment has explicitly enabled
/// reconciliation (<c>Store:Reconciliation:Enabled=true</c>) on top of a configured database
/// (see <see cref="WorkerHostFactory"/>) — billing/monetization is in scope for Core v1
/// (docs/01_PRODUCT_VISION_AND_SCOPE.md) but requires deployment-supplied store adapters and credentials, so the
/// loop never runs unless billing is configured (fail-closed).
/// </para>
///
/// <para>
/// Each sweep runs inside its own dependency-injection scope (the reconciliation service, the reused notification
/// service and the repositories are scoped), created from <see cref="IServiceScopeFactory"/>. The loop is
/// RESILIENT: a sweep that throws is logged and the loop continues to the next tick, so one transient database
/// failure never tears the worker down. Cancellation (host shutdown) ends the loop cleanly. The job is idempotent
/// at the data layer (reconciliation re-derives from immutable ledger facts and a converged purchase drops out of
/// the candidate set), so a sweep retried after a crash never double-applies.
/// </para>
///
/// <para>
/// Liveness heartbeat (CORE-OPS-005, per-loop under CORE-DR-003): like the other worker loops, this loop writes
/// its OWN <see cref="WorkerHeartbeat"/> on startup and after every sweep tick, so orchestration can detect a
/// WEDGED loop (this loop's heartbeat file goes stale when its sweep hangs, even while the other loops keep
/// beating their own files, so a single hung loop is detectable rather than masked).
/// </para>
/// </summary>
internal sealed class StoreNotificationReconciliationBackgroundService : BackgroundService
{
    private const string _jobName = WorkerJobNames.StoreNotificationReconciliation;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StoreNotificationReconciliationOptions _options;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly LiveCoreMetrics _metrics;
    private readonly LiveCoreActivitySource _activitySource;
    private readonly ILogger<StoreNotificationReconciliationBackgroundService> _logger;

    public StoreNotificationReconciliationBackgroundService(
        IServiceScopeFactory scopeFactory,
        StoreNotificationReconciliationOptions options,
        WorkerJobHeartbeats heartbeats,
        LiveCoreMetrics metrics,
        LiveCoreActivitySource activitySource,
        ILogger<StoreNotificationReconciliationBackgroundService> logger)
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
            "Store notification reconciliation job started; reconciling drifted purchases every {Interval} (batch size {BatchSize}).",
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

        _logger.LogInformation("Store notification reconciliation job stopped.");
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        // Distributed-tracing span for this store-reconciliation iteration (CORE-OBS-012). Tagged with the
        // coarse loop name and, below, the outcome; any database command the sweep issues nests under it. A
        // no-op (null) when no tracer/exporter is listening, so an unconfigured worker pays nothing (threat T7).
        using var activity = _activitySource.StartWorkerJobLoop(_jobName);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var reconciler = scope.ServiceProvider.GetRequiredService<StoreNotificationReconciliationService>();

            var result = await reconciler
                .ReconcileDriftedPurchasesAsync(stoppingToken)
                .ConfigureAwait(false);

            // SLI signals (CORE-OBS-007): the sweep completed without throwing, so record one job success, the
            // sweep duration and the observed backlog/queue depth (the count of drifted purchases it examined) —
            // so a loop falling behind is alertable. Counts only, tagged by the coarse loop name, never a
            // tenant/principal/content detail (threat T7).
            _metrics.RecordBackgroundJobSuccess(_jobName);
            _metrics.RecordBackgroundJobDuration(_jobName, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            _metrics.RecordBackgroundJobBacklog(_jobName, result.Examined);
            activity?.SetTag(LiveCoreActivitySource.WorkerJobOutcomeTag, WorkerJobOutcomes.Success);

            if (result.Examined > 0)
            {
                _logger.LogInformation(
                    "Store notification reconciliation sweep complete: examined {Examined}, reconciled {Reconciled}, failed {Failed}.",
                    result.Examined,
                    result.Reconciled,
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
            _logger.LogError(exception, "Store notification reconciliation sweep failed; it will be retried on the next interval.");
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
