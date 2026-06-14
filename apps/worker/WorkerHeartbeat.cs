using System.Globalization;

namespace LiveCore.Worker;

/// <summary>
/// Writes ONE job loop's liveness heartbeat (CORE-OPS-005, per-loop under CORE-DR-003): the current UTC
/// timestamp persisted to the loop's own file (<see cref="WorkerHeartbeatOptions.FilePathForJob"/>). Each
/// loop (asset cleanup, recap generation, export processing, store-notification reconciliation) owns its own
/// <see cref="WorkerHeartbeat"/>, obtained from <see cref="WorkerJobHeartbeats"/>, and calls
/// <see cref="BeatAsync"/> on startup and after every sweep tick, so the file's timestamp tracks THAT loop's
/// progress: a wedged sweep stops refreshing it and the file goes stale, while the other loops keep beating
/// their own files — the signal the aggregating liveness check uses to detect a single hung loop.
///
/// <para>
/// A heartbeat write must never crash the worker: a transient filesystem error is logged and swallowed. If
/// writes fail persistently the file simply goes stale and orchestration restarts the worker, which is the
/// desired fail-safe outcome. The timestamp comes from the injected <see cref="TimeProvider"/> (the same
/// system clock the loop uses), so the behavior is deterministic under test. The written value is a
/// round-trippable ISO-8601 UTC timestamp; <see cref="TryReadLastBeat"/> reads it back for an operator or the
/// in-process liveness probe. No sensitive content is involved (only a file path and a timestamp; threat T7).
/// </para>
/// </summary>
public sealed class WorkerHeartbeat
{
    private readonly string _jobName;
    private readonly WorkerHeartbeatOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkerHeartbeat> _logger;

    /// <summary>Creates a heartbeat writer for a single named loop.</summary>
    /// <param name="jobName">The loop's coarse name (see <see cref="WorkerJobNames"/>).</param>
    /// <param name="options">The heartbeat configuration (base path + staleness threshold).</param>
    /// <param name="timeProvider">The clock the heartbeat stamps with.</param>
    /// <param name="logger">The logger a swallowed write failure is reported to.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The job name is null, empty or whitespace.</exception>
    public WorkerHeartbeat(
        string jobName,
        WorkerHeartbeatOptions options,
        TimeProvider timeProvider,
        ILogger<WorkerHeartbeat> logger)
    {
        if (string.IsNullOrWhiteSpace(jobName))
        {
            throw new ArgumentException("The job name must be a non-empty value.", nameof(jobName));
        }

        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _jobName = jobName;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The coarse name of the loop this heartbeat tracks.</summary>
    public string JobName => _jobName;

    /// <summary>The path this loop's heartbeat is written to (<see cref="WorkerHeartbeatOptions.FilePathForJob"/>).</summary>
    public string FilePath => _options.FilePathForJob(_jobName);

    /// <summary>
    /// Writes the current UTC timestamp to this loop's heartbeat file. Resilient: a write failure is logged
    /// and swallowed so a heartbeat error never tears the worker down.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write on host shutdown.</param>
    public async Task BeatAsync(CancellationToken cancellationToken)
    {
        var timestamp = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        try
        {
            await File.WriteAllTextAsync(FilePath, timestamp, cancellationToken).ConfigureAwait(false);
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
                exception,
                "Failed to write the {JobName} worker heartbeat to {HeartbeatFile}.",
                _jobName,
                FilePath);
        }
    }

    /// <summary>
    /// Reads the last heartbeat timestamp from a heartbeat file, for an operator or the in-process liveness
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
