namespace LiveCore.Worker;

/// <summary>
/// Configuration for the worker liveness heartbeat (CORE-OPS-005, the "Production Operations Readiness"
/// epic). The worker host runs the asset cleanup loop (<see cref="AssetCleanupBackgroundService"/>); a wedged
/// loop (a sweep that hangs on a stuck database or storage call) would otherwise be invisible to
/// orchestration, which only sees that the process is still alive. The heartbeat is the loop's LIVENESS
/// SIGNAL: the loop writes the current UTC time to a file each tick, so a stalled loop stops refreshing it
/// and the file goes stale — a Kubernetes liveness probe (or a Compose healthcheck) that checks the file's
/// age can then restart the wedged worker.
///
/// Like <see cref="LiveCore.Api.Assets.AssetCleanupOptions"/> and the asset storage location, the only knob
/// is deployment policy (the file path), read once from configuration (under <see cref="ConfigurationSection"/>)
/// with a safe default so the worker runs without any heartbeat configuration. No credentials or secrets live
/// here (only a file path), and the value is product-neutral (AGENTS.md).
/// </summary>
public sealed class WorkerHeartbeatOptions
{
    /// <summary>Configuration section the heartbeat settings are read from (<c>Worker:Heartbeat</c>).</summary>
    public const string ConfigurationSection = "Worker:Heartbeat";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> holding the heartbeat file path.</summary>
    public const string FilePathKey = "FilePath";

    /// <summary>
    /// Default heartbeat file path when none is configured: a deterministic, writable location in the system
    /// temp directory. A deployment overrides it (for example to a mounted volume the orchestration probe also
    /// reads) through <c>Worker:Heartbeat:FilePath</c>.
    /// </summary>
    public static readonly string DefaultFilePath =
        Path.Combine(Path.GetTempPath(), "livecore-worker.heartbeat");

    /// <summary>Creates heartbeat options.</summary>
    /// <param name="filePath">The path the heartbeat timestamp is written to.</param>
    /// <exception cref="ArgumentException">The file path is null, empty or whitespace.</exception>
    public WorkerHeartbeatOptions(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("The heartbeat file path must be a non-empty path.", nameof(filePath));
        }

        FilePath = filePath;
    }

    /// <summary>The path the worker writes its heartbeat timestamp to each loop tick.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Reads the heartbeat options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Worker:Heartbeat:FilePath</c>), falling back to <see cref="DefaultFilePath"/> when the value is
    /// absent or blank.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static WorkerHeartbeatOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration.GetSection(ConfigurationSection)[FilePathKey];
        return new WorkerHeartbeatOptions(
            string.IsNullOrWhiteSpace(configured) ? DefaultFilePath : configured);
    }
}
