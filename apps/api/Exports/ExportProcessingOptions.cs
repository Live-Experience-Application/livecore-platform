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
/// <see cref="SweepInterval"/> is how often the worker runs a processing sweep (the configurable cadence), and
/// <see cref="BatchSize"/> bounds how many queued jobs a single sweep processes so one run can never do
/// unbounded work. There is no retention window: an export job needs processing as soon as it is queued
/// (eligibility is "workspace-scoped and not terminal", evaluated by <see cref="IQueuedExportJobReader"/>), so
/// — unlike the cleanup job's grace window — no minimum age gates processing.
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
    /// Creates export processing options, validating that every value is sane. A non-positive sweep interval or
    /// a non-positive batch size is rejected, so a misconfiguration can never schedule a degenerate sweep.
    /// </summary>
    /// <param name="sweepInterval">How often an export processing sweep runs.</param>
    /// <param name="batchSize">The maximum number of queued jobs processed in one sweep.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
    public ExportProcessingOptions(TimeSpan sweepInterval, int batchSize)
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

        SweepInterval = sweepInterval;
        BatchSize = batchSize;
    }

    /// <summary>How often the worker runs an export processing sweep (the configurable cadence).</summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>The maximum number of queued jobs a single sweep processes.</summary>
    public int BatchSize { get; }

    /// <summary>
    /// Reads the processing options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Exports:Processing:SweepInterval</c> as a <see cref="TimeSpan"/> string,
    /// <c>Exports:Processing:BatchSize</c> as an integer), falling back to the safe defaults when a value is
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
