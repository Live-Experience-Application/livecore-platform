// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Observability;
using LiveCore.Api.Retention;

namespace LiveCore.Worker;

/// <summary>
/// Schedules the data-retention sweep (CORE-PRIV-003/CORE-PRIV-006): on a fixed, configurable interval it runs
/// one sweep of <see cref="DataRetentionSweepService"/>, which expires and PURGES terminal/old
/// personal-data-bearing records (and the unbounded idempotency-key store) across five independently-gated
/// families — completed/expired sessions (and their cascade-removed session events, recaps and visibility rules),
/// generated recaps, completed export artifacts (the row and any object-storage blob), closed/expired/revoked
/// invitations (their plaintext email) and idempotency-key rows past the retry horizon (a count-only bulk purge
/// by age) — auditing each tenant-scoped purge by id (GDPR Art.5(1)(e) "storage limitation"). The worker owns the
/// platform's async jobs
/// (docs/02_ARCHITECTURE.md: the worker owns "background jobs, exports, cleanup, async processing"); the purge
/// LOGIC lives in the Retention module, so this host service only handles timing, scoping and resilience —
/// exactly the split, and the structure, of <see cref="AssetCleanupBackgroundService"/> and
/// <see cref="ExportProcessingBackgroundService"/>.
///
/// <para>
/// Each sweep runs inside its own dependency-injection scope (the sweep service, its repositories, the audit log
/// and the unit of work are scoped), created from <see cref="IServiceScopeFactory"/>. The loop is RESILIENT: a
/// sweep that throws is logged and the loop continues to the next tick, so one transient database or storage
/// failure never tears the worker down. Cancellation (host shutdown) ends the loop cleanly. This service is only
/// registered when persistence is configured (see <see cref="WorkerHostFactory"/>), so it always has a database
/// to sweep. The sweep is idempotent and concurrency-safe at the data layer (each purge re-loads its record
/// inside a transaction and audits-then-deletes atomically), so a sweep retried after a crash, or overlapping
/// with another worker, never double-purges.
/// </para>
///
/// <para>
/// Liveness heartbeat (CORE-OPS-005, per-loop under CORE-DR-003): like the other worker loops, this loop writes
/// its OWN <see cref="WorkerHeartbeat"/> on startup and after every sweep tick, so orchestration can detect a
/// WEDGED loop (this loop's heartbeat file goes stale when its sweep hangs, even while the other loops keep
/// beating their own files, so a single hung loop is detectable rather than masked).
/// </para>
/// </summary>
internal sealed class DataRetentionSweepBackgroundService : BackgroundService
{
    private const string _jobName = WorkerJobNames.DataRetention;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DataRetentionOptions _options;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly LiveCoreMetrics _metrics;
    private readonly ILogger<DataRetentionSweepBackgroundService> _logger;

    public DataRetentionSweepBackgroundService(
        IServiceScopeFactory scopeFactory,
        DataRetentionOptions options,
        WorkerJobHeartbeats heartbeats,
        LiveCoreMetrics metrics,
        ILogger<DataRetentionSweepBackgroundService> logger)
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
            "Data-retention sweep started; sweeping every {Interval} (batch size {BatchSize}). "
                + "Enabled windows — sessions: {Sessions}, recaps: {Recaps}, exports: {Exports}, "
                + "invitations: {Invitations}, idempotency keys: {IdempotencyKeys}.",
            _options.SweepInterval,
            _options.BatchSize,
            _options.Sessions.Enabled,
            _options.Recaps.Enabled,
            _options.Exports.Enabled,
            _options.Invitations.Enabled,
            _options.IdempotencyKeys.Enabled);

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

        _logger.LogInformation("Data-retention sweep stopped.");
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sweep = scope.ServiceProvider.GetRequiredService<DataRetentionSweepService>();

            var result = await sweep.SweepAsync(stoppingToken).ConfigureAwait(false);

            if (result.TotalExamined > 0)
            {
                _logger.LogInformation(
                    "Data-retention sweep complete: examined {Examined}, purged {Purged}, failed {Failed} "
                        + "(sessions {SessionsPurged}, recaps {RecapsPurged}, exports {ExportsPurged}, "
                        + "invitations {InvitationsPurged}, idempotency keys {IdempotencyKeysPurged}).",
                    result.TotalExamined,
                    result.TotalPurged,
                    result.TotalFailed,
                    result.Sessions.Purged,
                    result.Recaps.Purged,
                    result.Exports.Purged,
                    result.Invitations.Purged,
                    result.IdempotencyKeys.Purged);
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
            _logger.LogError(exception, "Data-retention sweep failed; it will be retried on the next interval.");
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
