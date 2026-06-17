// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;

namespace LiveCore.Api.Exports;

/// <summary>
/// Configuration for the background export processing job (CORE-JOB-002, the export-processing story of the
/// "Worker Background Jobs" epic). The job processes queued export jobs asynchronously into workspace export
/// manifests on a configurable cadence, idempotently and tenant-scoped (docs/02_ARCHITECTURE.md: the worker
/// owns "background jobs, exports, cleanup, async processing"; docs/05_MODULE_CONTRACTS.md: the Exports module
/// owns "export jobs" and "export manifests"). The job mirrors the recap generation job (CORE-JOB-001) and the
/// asset cleanup job (CORE-AST-006): the worker host owns the platform's async jobs, the processing LOGIC lives
/// in the Exports module (<see cref="ExportProcessingService"/>), and these options carry only timing/scoping
/// policy.
///
/// The knobs are deployment policy, not per-request input, so — exactly like
/// <see cref="LiveCore.Api.Recaps.RecapGenerationOptions"/> — they are read once from configuration (under
/// <see cref="ConfigurationSection"/>) with safe defaults, so the worker host still runs without any export
/// configuration. No credentials or secrets live here (only a timing and a batch size); the values are
/// product-neutral (AGENTS.md).
///
/// <para>
/// <see cref="SweepInterval"/> is how often the worker runs a processing sweep (the configurable cadence),
/// <see cref="BatchSize"/> bounds how many queued jobs a single sweep processes so one run can never do
/// unbounded work, and <see cref="MaxAttempts"/> bounds how many times a single job is retried before it is
/// dead-lettered (CORE-RES-002) so a permanently-failing job cannot be retried forever. There is no retention
/// window: an export job needs processing as soon as it is queued (eligibility is "workspace-scoped and not
/// terminal", evaluated by <see cref="IQueuedExportJobReader"/>), so — unlike the cleanup job's grace window —
/// no minimum age gates processing.
/// </para>
/// </summary>
public sealed class ExportProcessingOptions
{
    /// <summary>Configuration section the export processing settings are read from (<c>Exports:Processing</c>).</summary>
    public const string ConfigurationSection = "Exports:Processing";

    /// <summary>Default interval between export processing sweeps (1 hour), matching the other worker jobs' cadence.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(1);

    /// <summary>Default maximum number of queued jobs processed in a single sweep.</summary>
    public const int DefaultBatchSize = 50;

    /// <summary>
    /// Default maximum number of failed processing attempts before a job is dead-lettered (CORE-RES-002).
    /// Generous enough to ride out a few transient failures, low enough that a permanently-broken (poison) job
    /// is taken out of rotation quickly so it stops re-consuming a batch slot.
    /// </summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>
    /// Default duration a worker replica holds a job's processing LEASE before it is reclaimable (CORE-RES-003).
    /// Comfortably longer than processing one export (a workspace inventory count plus one manifest insert) so a
    /// healthy replica never has its own live lease stolen, yet short enough that a replica that crashes mid-job
    /// has its lease reclaimed promptly by the next sweep. A deployment that runs longer sweeps or slower exports
    /// can raise it (it should stay above the time to process one job and is best kept at or above the sweep
    /// interval so a crashed lease is reclaimed on the following sweep).
    /// </summary>
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates export processing options, validating that every value is sane. A non-positive sweep interval, a
    /// non-positive batch size, or a non-positive max-attempt bound is rejected, so a misconfiguration can never
    /// schedule a degenerate sweep nor disable the dead-letter bound.
    /// </summary>
    /// <param name="sweepInterval">How often an export processing sweep runs.</param>
    /// <param name="batchSize">The maximum number of queued jobs processed in one sweep.</param>
    /// <param name="maxAttempts">The maximum number of failed attempts before a job is dead-lettered.</param>
    /// <param name="leaseDuration">How long a replica holds a job's lease before it is reclaimable.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
    public ExportProcessingOptions(
        TimeSpan sweepInterval,
        int batchSize,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? leaseDuration = null)
    {
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

        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts), maxAttempts, "The maximum number of attempts must be positive.");
        }

        var resolvedLeaseDuration = leaseDuration ?? DefaultLeaseDuration;
        if (resolvedLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration), resolvedLeaseDuration, "The lease duration must be positive.");
        }

        SweepInterval = sweepInterval;
        BatchSize = batchSize;
        MaxAttempts = maxAttempts;
        LeaseDuration = resolvedLeaseDuration;
    }

    /// <summary>How often the worker runs an export processing sweep (the configurable cadence).</summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>The maximum number of queued jobs a single sweep processes.</summary>
    public int BatchSize { get; }

    /// <summary>
    /// The maximum number of failed processing attempts a job may accumulate before the worker DEAD-LETTERS it
    /// — moves it to the terminal <see cref="ExportJobStatus.Failed"/> state instead of retrying it forever
    /// (CORE-RES-002). On the attempt that reaches this bound the job is failed, so a permanently-failing
    /// (poison) job stops re-consuming a batch slot and newer work progresses past it.
    /// </summary>
    public int MaxAttempts { get; }

    /// <summary>
    /// How long a worker replica holds a job's processing LEASE before another replica may reclaim it
    /// (CORE-RES-003). While the lease is unexpired the holding replica owns the job, so no other replica
    /// processes it; once it has lapsed (a crashed replica) the job becomes reclaimable, so work is never
    /// stranded. The worker stamps each claimed job's expiry at <c>now + LeaseDuration</c>.
    /// </summary>
    public TimeSpan LeaseDuration { get; }

    /// <summary>
    /// Reads the processing options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Exports:Processing:SweepInterval</c> and <c>Exports:Processing:LeaseDuration</c> as
    /// <see cref="TimeSpan"/> strings, <c>Exports:Processing:BatchSize</c> and
    /// <c>Exports:Processing:MaxAttempts</c> as integers), falling back to the safe defaults when a value is
    /// absent or blank. A value that is PRESENT but cannot be parsed, or is out of range, is rejected at
    /// startup rather than silently falling back — a misconfigured timing is a startup error, never a
    /// degenerate sweep.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured value is out of range.</exception>
    public static ExportProcessingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);

        return new ExportProcessingOptions(
            ParseTimeSpan(section["SweepInterval"], nameof(SweepInterval), DefaultSweepInterval),
            ParseBatchSize(section["BatchSize"]),
            ParsePositiveInt(section["MaxAttempts"], nameof(MaxAttempts), DefaultMaxAttempts),
            ParseTimeSpan(section["LeaseDuration"], nameof(LeaseDuration), DefaultLeaseDuration));
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
        => ParsePositiveInt(value, nameof(BatchSize), DefaultBatchSize);

    private static int ParsePositiveInt(string? value, string name, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{name}' is not a valid integer.");
        }

        return parsed;
    }
}
