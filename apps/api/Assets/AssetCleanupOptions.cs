using System.Globalization;

namespace LiveCore.Api.Assets;

/// <summary>
/// Configuration for the background asset cleanup job (CORE-AST-006, the cleanup-job story of the "Asset
/// Storage and Authorization" epic). The cleanup job reclaims ABANDONED upload intents: an asset that was
/// registered <see cref="AssetStatus.Pending"/> when its upload intent was created (CORE-AST-003) but whose
/// upload was never confirmed (CORE-AST-004 confirm) within a grace window. Such an asset leaves a stale
/// metadata row and, possibly, an orphaned object in private storage; the job deletes both.
///
/// The knobs are deployment policy, not per-request input, so — exactly like <see cref="AssetStorageLocation"/>
/// — they are read once from configuration (under <see cref="ConfigurationSection"/>) with safe defaults, so
/// the worker host still runs without any cleanup configuration. No credentials or secrets live here (only
/// timings and a batch size); the storage endpoint and credentials stay inside the deployment-supplied
/// <see cref="IAssetStorage"/> adapter (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). The values are product-neutral (AGENTS.md).
///
/// <para>
/// <see cref="PendingRetention"/> is the grace window: an asset is eligible for cleanup only once it has
/// been <see cref="AssetStatus.Pending"/> for longer than this, so an upload still legitimately in flight is
/// never reclaimed. <see cref="SweepInterval"/> is how often the worker runs a sweep, and
/// <see cref="BatchSize"/> bounds how many assets a single sweep processes so one run can never do unbounded
/// work.
/// </para>
/// </summary>
public sealed class AssetCleanupOptions
{
    /// <summary>Configuration section the cleanup settings are read from (<c>Assets:Cleanup</c>).</summary>
    public const string ConfigurationSection = "Assets:Cleanup";

    /// <summary>
    /// Default grace window before an unconfirmed pending asset is eligible for cleanup (24 hours): long
    /// enough that an upload still in progress (a large object, a paused or resumed client) is never
    /// reclaimed, short enough that abandoned intents do not accumulate indefinitely.
    /// </summary>
    public static readonly TimeSpan DefaultPendingRetention = TimeSpan.FromHours(24);

    /// <summary>Default interval between cleanup sweeps (1 hour).</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(1);

    /// <summary>Default maximum number of assets processed in a single sweep.</summary>
    public const int DefaultBatchSize = 100;

    /// <summary>
    /// Creates cleanup options, validating that every value is sane. A non-positive retention, a non-positive
    /// sweep interval or a non-positive batch size is rejected, so a misconfiguration can never disable the
    /// grace window (which would reclaim in-flight uploads) or schedule a degenerate sweep.
    /// </summary>
    /// <param name="pendingRetention">The grace window before an unconfirmed pending asset is eligible for cleanup.</param>
    /// <param name="sweepInterval">How often a cleanup sweep runs.</param>
    /// <param name="batchSize">The maximum number of assets processed in one sweep.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
    public AssetCleanupOptions(TimeSpan pendingRetention, TimeSpan sweepInterval, int batchSize)
    {
        if (pendingRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pendingRetention), pendingRetention, "The pending retention window must be positive.");
        }

        if (sweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sweepInterval), sweepInterval, "The sweep interval must be positive.");
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize), batchSize, "The batch size must be positive.");
        }

        PendingRetention = pendingRetention;
        SweepInterval = sweepInterval;
        BatchSize = batchSize;
    }

    /// <summary>
    /// The grace window an asset must have been <see cref="AssetStatus.Pending"/> for before it is eligible
    /// for cleanup. An asset younger than this is never reclaimed, so an upload still in flight is safe.
    /// </summary>
    public TimeSpan PendingRetention { get; }

    /// <summary>How often the worker runs a cleanup sweep.</summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>The maximum number of assets a single sweep processes.</summary>
    public int BatchSize { get; }

    /// <summary>
    /// Reads the cleanup options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Assets:Cleanup:PendingRetention</c> / <c>Assets:Cleanup:SweepInterval</c> as
    /// <see cref="TimeSpan"/> strings, <c>Assets:Cleanup:BatchSize</c> as an integer), falling back to the
    /// safe defaults when a value is absent or blank. A value that is PRESENT but cannot be parsed, or is
    /// out of range, is rejected at startup rather than silently falling back — a misconfigured timing is a
    /// startup error, never a degenerate sweep.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured value is out of range.</exception>
    public static AssetCleanupOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);

        return new AssetCleanupOptions(
            ParseTimeSpan(section["PendingRetention"], nameof(PendingRetention), DefaultPendingRetention),
            ParseTimeSpan(section["SweepInterval"], nameof(SweepInterval), DefaultSweepInterval),
            ParseBatchSize(section["BatchSize"]));
    }

    private static TimeSpan ParseTimeSpan(string? value, string name, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{name}' is not a valid TimeSpan.");
        }

        return parsed;
    }

    private static int ParseBatchSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultBatchSize;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{nameof(BatchSize)}' is not a valid integer.");
        }

        return parsed;
    }
}
