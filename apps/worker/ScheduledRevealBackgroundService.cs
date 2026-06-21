// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Observability;
using LiveCore.Api.Visibility;

namespace LiveCore.Worker;

/// <summary>
/// Schedules the scheduled-reveal sweep job (CORE-VSEAL-002): on a fixed, configurable interval it runs one sweep
/// of <see cref="ScheduledRevealService"/>, which AUTOMATICALLY reveals every Hidden visibility rule whose
/// <c>scheduledRevealAt</c> time has arrived — driving the SAME central reveal command as a live host reveal, so
/// the auto-reveal is gated through the Visibility engine and emits the normal session events to exactly the
/// authorized audience (the server-enforced "control visibility WHEN", docs/01_PRODUCT_VISION_AND_SCOPE.md). The
/// worker owns the platform's async jobs (docs/02_ARCHITECTURE.md); the reveal LOGIC lives in the Visibility
/// module, so this host service only handles timing, scoping and resilience — exactly the split, and the
/// structure, of <see cref="RecapGenerationBackgroundService"/>.
///
/// <para>
/// OFF BY DEFAULT. The loop is registered (see <see cref="WorkerHostFactory"/>) only when persistence is
/// configured AND the deployment opts in (<c>Visibility:ScheduledReveal:Enabled=true</c>), exactly like the
/// billing-gated store-reconciliation loop — so a deployment that does not use scheduled reveals runs no sweep.
/// </para>
///
/// <para>
/// Each sweep runs inside its own dependency-injection scope (the sweep service, the due reader, the reveal
/// command and the repositories are scoped), created from <see cref="IServiceScopeFactory"/>. The loop is
/// RESILIENT: a sweep that throws is logged and the loop continues to the next tick, so one transient database
/// failure never tears the worker down. Cancellation (host shutdown) ends the loop cleanly. The job is idempotent
/// at the data layer: an auto-revealed rule is no longer Hidden, and the auto-reveal uses a deterministic per-rule
/// idempotency key, so a sweep retried after a crash — and overlapping sweeps or multiple worker replicas — never
/// double-reveal a rule, so this loop needs NO single-instance guard.
/// </para>
///
/// <para>
/// Liveness heartbeat (CORE-OPS-005, per-loop under CORE-DR-003): like the other loops, this loop writes its OWN
/// <see cref="WorkerHeartbeat"/> on startup and after every sweep tick, so orchestration can detect a WEDGED loop
/// independently of the others.
/// </para>
/// </summary>
internal sealed class ScheduledRevealBackgroundService : BackgroundService
{
    private const string _jobName = WorkerJobNames.ScheduledReveal;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScheduledRevealOptions _options;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly LiveCoreMetrics _metrics;
    private readonly LiveCoreActivitySource _activitySource;
    private readonly ILogger<ScheduledRevealBackgroundService> _logger;

    public ScheduledRevealBackgroundService(
        IServiceScopeFactory scopeFactory,
        ScheduledRevealOptions options,
        WorkerJobHeartbeats heartbeats,
        LiveCoreMetrics metrics,
        LiveCoreActivitySource activitySource,
        ILogger<ScheduledRevealBackgroundService> logger)
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
            "Scheduled-reveal job started; auto-revealing due rules every {Interval} (batch size {BatchSize}).",
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

        _logger.LogInformation("Scheduled-reveal job stopped.");
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        // Distributed-tracing span for this scheduled-reveal iteration (CORE-OBS-012). Tagged with the coarse
        // loop name and the outcome; any database command the sweep issues nests under it. A no-op (null) when no
        // tracer/exporter is listening, so an unconfigured worker pays nothing (threat T7).
        using var activity = _activitySource.StartWorkerJobLoop(_jobName);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sweep = scope.ServiceProvider.GetRequiredService<ScheduledRevealService>();

            var result = await sweep
                .RevealDueRulesAsync(stoppingToken)
                .ConfigureAwait(false);

            // SLI signals (CORE-OBS-007): the sweep completed without throwing, so record one job success, the
            // sweep duration and the observed backlog (the count of due rules it examined). Counts only, tagged by
            // the coarse loop name, never a tenant/principal/content detail (threat T7).
            _metrics.RecordBackgroundJobSuccess(_jobName);
            _metrics.RecordBackgroundJobDuration(_jobName, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            _metrics.RecordBackgroundJobBacklog(_jobName, result.Examined);
            activity?.SetTag(LiveCoreActivitySource.WorkerJobOutcomeTag, WorkerJobOutcomes.Success);

            if (result.Examined > 0)
            {
                _logger.LogInformation(
                    "Scheduled-reveal sweep complete: examined {Examined}, revealed {Revealed}, already-applied {AlreadyApplied}, failed {Failed}.",
                    result.Examined,
                    result.Revealed,
                    result.AlreadyApplied,
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
            _logger.LogError(exception, "Scheduled-reveal sweep failed; it will be retried on the next interval.");
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
