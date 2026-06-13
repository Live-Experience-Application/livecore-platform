using System.Globalization;

namespace LiveCore.Worker;

/// <summary>
/// Writes the worker's liveness heartbeat (CORE-OPS-005): the current UTC timestamp persisted to the
/// configured file (<see cref="WorkerHeartbeatOptions"/>). The asset cleanup loop
/// (<see cref="AssetCleanupBackgroundService"/>) calls <see cref="BeatAsync"/> on startup and after every
/// sweep tick, so the file's timestamp tracks the loop's progress: a wedged sweep stops refreshing it and the
/// file goes stale, which is exactly the signal orchestration uses to detect (and restart) a stalled worker.
///
/// <para>
/// A heartbeat write must never crash the worker: a transient filesystem error is logged and swallowed. If
/// writes fail persistently the file simply goes stale and orchestration restarts the worker, which is the
/// desired fail-safe outcome. The timestamp comes from the injected <see cref="TimeProvider"/> (the same
/// system clock the cleanup service uses), so the behavior is deterministic under test. The written value is a
/// round-trippable ISO-8601 UTC timestamp; <see cref="TryReadLastBeat"/> reads it back for an operator or an
/// in-process freshness probe. No sensitive content is involved (only a file path and a timestamp; threat T7).
/// </para>
/// </summary>
public sealed class WorkerHeartbeat
{
    private readonly WorkerHeartbeatOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkerHeartbeat> _logger;

    public WorkerHeartbeat(
        WorkerHeartbeatOptions options,
        TimeProvider timeProvider,
        ILogger<WorkerHeartbeat> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The path the heartbeat is written to (from <see cref="WorkerHeartbeatOptions.FilePath"/>).</summary>
    public string FilePath => _options.FilePath;

    /// <summary>
    /// Writes the current UTC timestamp to the heartbeat file. Resilient: a write failure is logged and
    /// swallowed so a heartbeat error never tears the worker down.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write on host shutdown.</param>
    public async Task BeatAsync(CancellationToken cancellationToken)
    {
        var timestamp = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        try
        {
            await File.WriteAllTextAsync(_options.FilePath, timestamp, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown during a write is expected; let it end quietly.
        }
        catch (Exception exception)
        {
            // A heartbeat write must never crash the worker. A persistent failure makes the file go stale and
            // orchestration restarts the worker (fail-safe), which is the intended outcome.
            _logger.LogWarning(
                exception, "Failed to write the worker heartbeat to {HeartbeatFile}.", _options.FilePath);
        }
    }

    /// <summary>
    /// Reads the last heartbeat timestamp from a heartbeat file, for an operator or an in-process freshness
    /// probe. Returns <see langword="false"/> (rather than throwing) when the file is absent, unreadable or
    /// does not contain a valid timestamp, so a missing or corrupt heartbeat reads as "no recent beat".
    /// </summary>
    /// <param name="filePath">The heartbeat file path.</param>
    /// <param name="lastBeat">The parsed last-beat timestamp when the read succeeds.</param>
    /// <returns><see langword="true"/> when a valid timestamp was read.</returns>
    public static bool TryReadLastBeat(string filePath, out DateTimeOffset lastBeat)
    {
        lastBeat = default;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(filePath);
            return DateTimeOffset.TryParse(
                content,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out lastBeat);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
