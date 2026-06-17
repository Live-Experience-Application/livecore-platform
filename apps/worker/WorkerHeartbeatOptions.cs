// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;

namespace LiveCore.Worker;

/// <summary>
/// Configuration for the worker liveness heartbeat (CORE-OPS-005, extended by CORE-DR-003). The worker host
/// runs up to four job loops (asset cleanup, recap generation, export processing and the billing-gated
/// store-notification reconciliation); a wedged loop (a sweep that hangs on a stuck database or storage call)
/// would otherwise be invisible to orchestration, which only sees that the process is still alive. The
/// heartbeat is each loop's LIVENESS SIGNAL: every loop writes the current UTC time to its OWN file each tick,
/// so a stalled loop stops refreshing its file and that file goes stale — even while the other loops keep
/// beating. A single shared file would let one healthy loop mask three hung ones; per-loop files
/// (<see cref="FilePathForJob"/>) plus the aggregating <see cref="WorkerJobHeartbeats"/> liveness check make a
/// single hung loop detectable.
///
/// Like <see cref="LiveCore.Api.Assets.AssetCleanupOptions"/> and the asset storage location, the knobs are
/// deployment policy (the base file path and the staleness threshold), read once from configuration (under
/// <see cref="ConfigurationSection"/>) with safe defaults so the worker runs without any heartbeat
/// configuration. No credentials or secrets live here (only a file path and a timespan), and the values are
/// product-neutral (AGENTS.md).
/// </summary>
public sealed class WorkerHeartbeatOptions
{
    /// <summary>Configuration section the heartbeat settings are read from (<c>Worker:Heartbeat</c>).</summary>
    public const string ConfigurationSection = "Worker:Heartbeat";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> holding the heartbeat base file path.</summary>
    public const string FilePathKey = "FilePath";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> holding the staleness threshold.</summary>
    public const string StaleAfterKey = "StaleAfter";

    /// <summary>
    /// Default heartbeat base file path when none is configured: a deterministic, writable location in the
    /// system temp directory. Each loop's own file is derived from it (<see cref="FilePathForJob"/>). A
    /// deployment overrides it (for example to a mounted volume the orchestration probe also reads) through
    /// <c>Worker:Heartbeat:FilePath</c>.
    /// </summary>
    public static readonly string DefaultFilePath =
        Path.Combine(Path.GetTempPath(), "livecore-worker.heartbeat");

    /// <summary>
    /// Default staleness threshold (2 hours): a loop whose heartbeat is older than this is treated as hung.
    /// Comfortably larger than every loop's default 1-hour sweep interval (a few sweep intervals), so a slow
    /// but progressing loop is never flagged. A deployment tunes it through <c>Worker:Heartbeat:StaleAfter</c>.
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(2);

    /// <summary>Creates heartbeat options.</summary>
    /// <param name="filePath">The base path each loop's heartbeat timestamp file is derived from.</param>
    /// <param name="staleAfter">The age beyond which a loop's heartbeat is considered stale (hung).</param>
    /// <exception cref="ArgumentException">The file path is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The staleness threshold is not positive.</exception>
    public WorkerHeartbeatOptions(string filePath, TimeSpan staleAfter)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("The heartbeat file path must be a non-empty path.", nameof(filePath));
        }

        if (staleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(staleAfter), staleAfter, "The heartbeat staleness threshold must be positive.");
        }

        FilePath = filePath;
        StaleAfter = staleAfter;
    }

    /// <summary>The base path each loop's heartbeat file is derived from (<see cref="FilePathForJob"/>).</summary>
    public string FilePath { get; }

    /// <summary>The age beyond which a loop's heartbeat is considered stale (the loop is hung).</summary>
    public TimeSpan StaleAfter { get; }

    /// <summary>
    /// Derives the heartbeat file path for a single named loop by suffixing the base <see cref="FilePath"/>
    /// with the job name (e.g. base <c>/run/livecore-worker.heartbeat</c> and job
    /// <c>asset-cleanup</c> yields <c>/run/livecore-worker.heartbeat.asset-cleanup</c>). Each loop therefore
    /// has its own file in the same directory, so a hung loop's file goes stale independently of the others.
    /// </summary>
    /// <param name="jobName">The loop's coarse name (see <see cref="WorkerJobNames"/>).</param>
    /// <exception cref="ArgumentException">The job name is null, empty or whitespace.</exception>
    public string FilePathForJob(string jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName))
        {
            throw new ArgumentException("The job name must be a non-empty value.", nameof(jobName));
        }

        return FilePath + "." + jobName;
    }

    /// <summary>
    /// Reads the heartbeat options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Worker:Heartbeat:FilePath</c> and <c>Worker:Heartbeat:StaleAfter</c>), falling back to
    /// <see cref="DefaultFilePath"/> / <see cref="DefaultStaleAfter"/> when a value is absent or blank. A
    /// <c>StaleAfter</c> value that is PRESENT but cannot be parsed (or is out of range) is rejected at startup
    /// rather than silently falling back — a misconfigured threshold is a startup error, never a degenerate
    /// liveness window.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present staleness value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured staleness value is out of range.</exception>
    public static WorkerHeartbeatOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);

        var configuredPath = section[FilePathKey];
        var filePath = string.IsNullOrWhiteSpace(configuredPath) ? DefaultFilePath : configuredPath;

        return new WorkerHeartbeatOptions(filePath, ParseStaleAfter(section[StaleAfterKey]));
    }

    private static TimeSpan ParseStaleAfter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultStaleAfter;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{StaleAfterKey}' is not a valid TimeSpan.");
        }

        return parsed;
    }
}
