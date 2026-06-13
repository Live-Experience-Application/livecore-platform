using System.Globalization;

namespace LiveCore.Api.Store;

/// <summary>
/// Configuration for the background store-notification reconciliation job (CORE-JOB-003, the store-notification
/// reconciliation story of the "Worker Background Jobs" epic). The job re-derives drifted purchase status from the
/// recorded store-notification ledger on a configurable cadence, idempotently (docs/02_ARCHITECTURE.md: the worker
/// owns "background jobs, exports, cleanup, async processing"; docs/05_MODULE_CONTRACTS.md: the Store module owns
/// purchase verification and entitlement state). It mirrors the recap generation (CORE-JOB-001) and export
/// processing (CORE-JOB-002) job options: the worker host owns the timing, the reconciliation LOGIC lives in the
/// Store module (<see cref="StoreNotificationReconciliationService"/> reusing <see cref="StoreNotificationService"/>),
/// and these options carry only timing/scoping policy and the explicit enablement gate.
///
/// <para>
/// GATED ON BILLING, FAIL-CLOSED. Unlike the other worker jobs, this one is OFF BY DEFAULT
/// (<see cref="Enabled"/> defaults to <see langword="false"/>). Store receipts/billing are out of scope for Core
/// v1 (docs/01_PRODUCT_VISION_AND_SCOPE.md "Out of scope": billing), and a store notification is only ever
/// produced when a deployment has wired the store integration, so the reconciliation job runs ONLY when a
/// deployment explicitly turns it on (<c>Store:Reconciliation:Enabled=true</c>). With it unset the job is never
/// scheduled (<see cref="StoreNotificationReconciliationServiceCollectionExtensions.AddStoreNotificationReconciliation"/>
/// registers nothing) — "only runs when billing is configured" (the story's acceptance criterion), the same
/// fail-closed default as the store verification/notification parser resolvers, which register no adapter until a
/// deployment supplies one.
/// </para>
///
/// <para>
/// The knobs are deployment policy, not per-request input, so — exactly like
/// <see cref="LiveCore.Api.Exports.ExportProcessingOptions"/> — they are read once from configuration (under
/// <see cref="ConfigurationSection"/>) with safe defaults. <see cref="SweepInterval"/> is how often a
/// reconciliation sweep runs and <see cref="BatchSize"/> bounds how many drifted purchases one sweep reconciles so
/// a run can never do unbounded work. No credentials or secrets live here; the values are product-neutral
/// (AGENTS.md).
/// </para>
/// </summary>
public sealed class StoreNotificationReconciliationOptions
{
    /// <summary>Configuration section the reconciliation settings are read from (<c>Store:Reconciliation</c>).</summary>
    public const string ConfigurationSection = "Store:Reconciliation";

    /// <summary>Default interval between reconciliation sweeps (1 hour), matching the other worker jobs' cadence.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(1);

    /// <summary>Default maximum number of drifted purchases reconciled in a single sweep.</summary>
    public const int DefaultBatchSize = 50;

    /// <summary>
    /// Creates reconciliation options, validating that every timing value is sane. A non-positive sweep interval
    /// or a non-positive batch size is rejected, so a misconfiguration can never schedule a degenerate sweep.
    /// </summary>
    /// <param name="enabled">Whether the reconciliation job is enabled (billing configured).</param>
    /// <param name="sweepInterval">How often a reconciliation sweep runs.</param>
    /// <param name="batchSize">The maximum number of drifted purchases reconciled in one sweep.</param>
    /// <exception cref="ArgumentOutOfRangeException">A timing value is not positive.</exception>
    public StoreNotificationReconciliationOptions(bool enabled, TimeSpan sweepInterval, int batchSize)
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

        Enabled = enabled;
        SweepInterval = sweepInterval;
        BatchSize = batchSize;
    }

    /// <summary>
    /// Whether the reconciliation job runs at all. Defaults to <see langword="false"/>: the job runs only when a
    /// deployment has configured store billing and explicitly enabled reconciliation (fail-closed).
    /// </summary>
    public bool Enabled { get; }

    /// <summary>How often the worker runs a reconciliation sweep (the configurable cadence).</summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>The maximum number of drifted purchases a single sweep reconciles.</summary>
    public int BatchSize { get; }

    /// <summary>
    /// Reads the reconciliation options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Store:Reconciliation:Enabled</c> as a boolean, <c>Store:Reconciliation:SweepInterval</c> as a
    /// <see cref="TimeSpan"/> string, <c>Store:Reconciliation:BatchSize</c> as an integer), falling back to the
    /// safe defaults when a value is absent or blank — and crucially <see cref="Enabled"/> defaults to
    /// <see langword="false"/>, so the job stays off until a deployment turns it on. A value that is PRESENT but
    /// cannot be parsed, or is out of range, is rejected at startup rather than silently falling back.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured value is out of range.</exception>
    public static StoreNotificationReconciliationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);

        return new StoreNotificationReconciliationOptions(
            ParseEnabled(section["Enabled"]),
            ParseTimeSpan(section["SweepInterval"], nameof(SweepInterval), DefaultSweepInterval),
            ParseBatchSize(section["BatchSize"]));
    }

    private static bool ParseEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Fail-closed default: the job does not run unless a deployment explicitly enables it.
            return false;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{nameof(Enabled)}' is not a valid boolean.");
        }

        return parsed;
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
