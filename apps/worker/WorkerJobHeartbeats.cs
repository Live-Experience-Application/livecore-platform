using System.Collections.Concurrent;

namespace LiveCore.Worker;

/// <summary>
/// The single owner of the worker's PER-LOOP liveness heartbeats (CORE-DR-003). It hands each registered job
/// loop its own <see cref="WorkerHeartbeat"/> (<see cref="ForJob"/>) and aggregates their files into one
/// liveness verdict (<see cref="CheckLiveness"/>) the worker's <c>/health/live</c> endpoint serves.
///
/// <para>
/// WHY PER-LOOP. Before this story the four loops shared one heartbeat file, so a single healthy loop kept the
/// file fresh and masked three hung ones — a monitoring blind spot on the host doing irreversible work. Each
/// loop now beats its OWN file (<see cref="WorkerHeartbeatOptions.FilePathForJob"/>), and liveness is healthy
/// only when EVERY active loop's file is fresh: one hung loop makes its file stale and the worker reports
/// not-live, so orchestration restarts it. The set of active loops is fixed at host build time (only the loops
/// that are actually scheduled — gated on a configured database, and on the billing opt-in for the
/// store-notification loop), so liveness checks exactly the loops that are expected to beat. With no loops
/// (no database) the active set is empty and liveness is vacuously healthy — there is nothing to stall.
/// </para>
///
/// <para>
/// Reading files (rather than an in-memory timestamp) keeps one source of truth: if a loop's heartbeat WRITE
/// fails persistently, its file goes stale and liveness flags it, exactly as a hung loop would — the
/// documented fail-safe. The verdict carries only coarse loop names and timestamps — no tenant identifier,
/// token or resource content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
/// </summary>
public sealed class WorkerJobHeartbeats
{
    private readonly WorkerHeartbeatOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IReadOnlyList<string> _jobNames;
    private readonly ConcurrentDictionary<string, WorkerHeartbeat> _heartbeats =
        new(StringComparer.Ordinal);

    /// <summary>Creates the per-loop heartbeat registry for the worker's active loops.</summary>
    /// <param name="options">The heartbeat configuration (base path + staleness threshold).</param>
    /// <param name="timeProvider">The clock used to stamp beats and to age them for the liveness check.</param>
    /// <param name="loggerFactory">The factory each per-loop heartbeat's logger is created from.</param>
    /// <param name="jobNames">The coarse names of the loops that are actually scheduled (may be empty).</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public WorkerJobHeartbeats(
        WorkerHeartbeatOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IEnumerable<string> jobNames)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(jobNames);

        _options = options;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _jobNames = jobNames.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The coarse names of the active loops this registry tracks.</summary>
    public IReadOnlyList<string> JobNames => _jobNames;

    /// <summary>
    /// Returns the heartbeat writer for one loop, creating it once per name. A loop calls this in its
    /// constructor and beats the returned writer each tick.
    /// </summary>
    /// <param name="jobName">The loop's coarse name (see <see cref="WorkerJobNames"/>).</param>
    /// <exception cref="ArgumentException">The job name is null, empty or whitespace.</exception>
    public WorkerHeartbeat ForJob(string jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName))
        {
            throw new ArgumentException("The job name must be a non-empty value.", nameof(jobName));
        }

        return _heartbeats.GetOrAdd(
            jobName,
            name => new WorkerHeartbeat(
                name, _options, _timeProvider, _loggerFactory.CreateLogger<WorkerHeartbeat>()));
    }

    /// <summary>
    /// Computes the worker's liveness verdict by reading every active loop's heartbeat file and aging it
    /// against the staleness threshold. A loop is LIVE when its file holds a timestamp newer than
    /// <see cref="WorkerHeartbeatOptions.StaleAfter"/>; it is STALE when its file is missing, unreadable or
    /// older than the threshold. The worker is live only when no active loop is stale.
    /// </summary>
    public WorkerLivenessReport CheckLiveness()
    {
        var now = _timeProvider.GetUtcNow();
        var live = new List<string>();
        var stale = new List<string>();

        foreach (var jobName in _jobNames)
        {
            var path = _options.FilePathForJob(jobName);

            if (WorkerHeartbeat.TryReadLastBeat(path, out var lastBeat) && now - lastBeat <= _options.StaleAfter)
            {
                live.Add(jobName);
            }
            else
            {
                stale.Add(jobName);
            }
        }

        return new WorkerLivenessReport(stale.Count == 0, live, stale);
    }
}

/// <summary>
/// The worker's per-loop liveness verdict (CORE-DR-003): whether every active loop is beating, and which
/// loops are fresh versus stalled. Carries only coarse loop names — no sensitive content (threat T7).
/// </summary>
/// <param name="IsLive">True when no active loop is stale (the worker is making progress on every loop).</param>
/// <param name="LiveJobs">The loops whose heartbeat is fresh.</param>
/// <param name="StaleJobs">The loops whose heartbeat is missing or stale (hung).</param>
public sealed record WorkerLivenessReport(
    bool IsLive,
    IReadOnlyList<string> LiveJobs,
    IReadOnlyList<string> StaleJobs);
