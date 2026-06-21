// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Observability;
using LiveCore.Api.Realtime;

namespace LiveCore.Worker;

/// <summary>
/// Schedules the closed-app push DISPATCH sweep job (CORE-PUSH-002): on a fixed, configurable interval it runs one
/// sweep of <see cref="PushNotificationDispatchService"/>, which drains the push OUTBOX and sends a CONTENT-FREE
/// Web Push to each recipient's registered subscriptions (deleting any the push service reports gone — the 404/410
/// cleanup). The worker owns the platform's async jobs and the OUTBOUND send (docs/02_ARCHITECTURE.md); the dispatch
/// LOGIC lives in the Realtime module, so this host service only handles timing, scoping and resilience — exactly
/// the split, and the structure, of <see cref="ScheduledRevealBackgroundService"/>.
///
/// <para>
/// OFF BY DEFAULT. The loop is registered (see <see cref="WorkerHostFactory"/>) only when persistence is configured
/// AND the deployment opts in with VAPID configured (<c>WebPush:Delivery:Enabled=true</c> plus the VAPID keys/subject
/// — <see cref="WebPushDeliveryOptions.IsActive"/>), exactly like the off-by-default scheduled-reveal loop, so a
/// deployment that does not use closed-app push runs no sweep.
/// </para>
///
/// <para>
/// Each sweep runs inside its own dependency-injection scope (the dispatch service, the repositories and the typed
/// HttpClient sender are scoped/transient), created from <see cref="IServiceScopeFactory"/>. The loop is RESILIENT: a
/// sweep that throws is logged and the loop continues to the next tick, so one transient failure never tears the
/// worker down. Cancellation (host shutdown) ends the loop cleanly. Delivery is best-effort (a row is drained whatever
/// the per-subscription outcome), so overlapping sweeps or multiple worker replicas only risk a harmless duplicate
/// content-free signal — no single-instance guard is needed.
/// </para>
///
/// <para>
/// Liveness heartbeat (CORE-OPS-005, per-loop under CORE-DR-003): like the other loops, this loop writes its OWN
/// <see cref="WorkerHeartbeat"/> on startup and after every sweep tick, so orchestration can detect a WEDGED loop
/// independently of the others.
/// </para>
/// </summary>
internal sealed class PushNotificationDispatchBackgroundService : BackgroundService
{
    private const string _jobName = WorkerJobNames.WebPushDispatch;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WebPushDeliveryOptions _options;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly LiveCoreMetrics _metrics;
    private readonly LiveCoreActivitySource _activitySource;
    private readonly ILogger<PushNotificationDispatchBackgroundService> _logger;

    public PushNotificationDispatchBackgroundService(
        IServiceScopeFactory scopeFactory,
        WebPushDeliveryOptions options,
        WorkerJobHeartbeats heartbeats,
        LiveCoreMetrics metrics,
        LiveCoreActivitySource activitySource,
        ILogger<PushNotificationDispatchBackgroundService> logger)
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
            "Closed-app push dispatch job started; draining the push outbox every {Interval} (batch size {BatchSize}).",
            _options.SweepInterval,
            _options.BatchSize);

        // Emit an initial heartbeat so the liveness signal exists from the moment the loop starts (CORE-OPS-005).
        await _heartbeat.BeatAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.SweepInterval);
        do
        {
            await RunSweepAsync(stoppingToken).ConfigureAwait(false);

            // Heartbeat after each completed sweep tick. A wedged sweep never reaches this point, so its heartbeat
            // file goes stale and orchestration can detect the stalled loop (CORE-OPS-005).
            await _heartbeat.BeatAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false));

        _logger.LogInformation("Closed-app push dispatch job stopped.");
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        // Distributed-tracing span for this dispatch iteration (CORE-OBS-012). Tagged with the coarse loop name and
        // the outcome; any database command or outbound send nests under it. A no-op when nothing is listening, so an
        // unconfigured worker pays nothing (threat T7).
        using var activity = _activitySource.StartWorkerJobLoop(_jobName);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dispatch = scope.ServiceProvider.GetRequiredService<PushNotificationDispatchService>();

            var result = await dispatch.DispatchPendingAsync(stoppingToken).ConfigureAwait(false);

            // SLI signals (CORE-OBS-007): the sweep completed without throwing, so record one job success, the sweep
            // duration and the observed backlog (the count of pending rows it examined). Counts only, tagged by the
            // coarse loop name, never a tenant/principal/content detail (threat T7).
            _metrics.RecordBackgroundJobSuccess(_jobName);
            _metrics.RecordBackgroundJobDuration(_jobName, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            _metrics.RecordBackgroundJobBacklog(_jobName, result.Examined);
            activity?.SetTag(LiveCoreActivitySource.WorkerJobOutcomeTag, WorkerJobOutcomes.Success);

            if (result.Examined > 0)
            {
                _logger.LogInformation(
                    "Closed-app push sweep complete: examined {Examined}, delivered {Delivered}, subscriptions removed {Removed}, failed {Failed}.",
                    result.Examined,
                    result.Delivered,
                    result.SubscriptionsRemoved,
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
            // (identifiers and counts only) and let the next tick try again. Counting only — the exception detail
            // goes to the structured log, never a metric label (threat T7).
            _metrics.RecordBackgroundJobFailure(_jobName);
            _metrics.RecordBackgroundJobDuration(_jobName, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(LiveCoreActivitySource.WorkerJobOutcomeTag, WorkerJobOutcomes.Failure);
            _logger.LogError(exception, "Closed-app push sweep failed; it will be retried on the next interval.");
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
