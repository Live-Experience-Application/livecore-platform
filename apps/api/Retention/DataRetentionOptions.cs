using System.Globalization;

namespace LiveCore.Api.Retention;

/// <summary>
/// Configuration for the background data-retention sweep (CORE-PRIV-003, the "Privacy and Data Lifecycle" epic,
/// GDPR Art.5(1)(e) "storage limitation"). The sweep expires and purges terminal/old personal-data-bearing
/// records across four families, each gated by its own <see cref="RetentionWindowOptions"/>:
/// <list type="bullet">
///   <item><see cref="Sessions"/> — completed/expired sessions (and, by the schema cascade, their append-only
///   session events, recaps and session-scoped visibility rules);</item>
///   <item><see cref="Recaps"/> — generated recaps (the recap body is host content);</item>
///   <item><see cref="Exports"/> — completed export artifacts (the export row and any object-storage blob);</item>
///   <item><see cref="Invitations"/> — closed/expired/revoked invitations (their plaintext invited email).</item>
/// </list>
///
/// The knobs are deployment policy, not per-request input, so — exactly like <see cref="LiveCore.Api.Assets.AssetCleanupOptions"/>
/// — they are read once from configuration (under <see cref="ConfigurationSection"/>) with SAFE DEFAULTS, so the
/// worker host runs without any retention configuration. No credentials or secrets live here (only timings,
/// enable flags and a batch size). The values are product-neutral (AGENTS.md).
///
/// <para>
/// SAFE, DISABLED-BY-DEFAULT-WHERE-SURPRISING DEFAULTS (the acceptance criterion). The three families whose
/// deletion an operator would be surprised to lose — sessions, recaps and completed exports — default to
/// DISABLED, so they never purge until the deployment opts in (with a generous default window present and inert
/// until enabled). The invitation-email purge — terminal rows whose only payload is a now-dead plaintext email,
/// with the invitation's lifecycle audit facts preserved separately — is clear privacy hygiene, so it defaults
/// to ENABLED with a conservative window.
/// </para>
///
/// <para>
/// <see cref="SweepInterval"/> is how often the worker runs a sweep, and <see cref="BatchSize"/> bounds how many
/// records of each family a single sweep examines so one run can never do unbounded work.
/// </para>
/// </summary>
public sealed class DataRetentionOptions
{
    /// <summary>Configuration section the retention settings are read from (<c>Retention</c>).</summary>
    public const string ConfigurationSection = "Retention";

    /// <summary>Default interval between retention sweeps (1 hour).</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(1);

    /// <summary>Default maximum number of records of EACH family processed in a single sweep.</summary>
    public const int DefaultBatchSize = 100;

    /// <summary>Default retention window for completed/expired sessions (365 days). DISABLED by default.</summary>
    public static readonly TimeSpan DefaultSessionRetention = TimeSpan.FromDays(365);

    /// <summary>Default retention window for generated recaps (365 days). DISABLED by default.</summary>
    public static readonly TimeSpan DefaultRecapRetention = TimeSpan.FromDays(365);

    /// <summary>Default retention window for completed export artifacts (90 days). DISABLED by default.</summary>
    public static readonly TimeSpan DefaultExportRetention = TimeSpan.FromDays(90);

    /// <summary>Default retention window for closed/expired/revoked invitations (30 days). ENABLED by default.</summary>
    public static readonly TimeSpan DefaultInvitationRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// Creates retention options, validating that every value is sane. A non-positive sweep interval or a
    /// non-positive batch size is rejected (a degenerate sweep), and each family's window is validated by
    /// <see cref="RetentionWindowOptions"/>.
    /// </summary>
    /// <param name="sweepInterval">How often a retention sweep runs.</param>
    /// <param name="batchSize">The maximum number of records of each family processed in one sweep.</param>
    /// <param name="sessions">The completed/expired session retention policy.</param>
    /// <param name="recaps">The generated recap retention policy.</param>
    /// <param name="exports">The completed export artifact retention policy.</param>
    /// <param name="invitations">The closed/expired/revoked invitation retention policy.</param>
    /// <exception cref="ArgumentNullException">A family policy is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The sweep interval or batch size is not positive.</exception>
    public DataRetentionOptions(
        TimeSpan sweepInterval,
        int batchSize,
        RetentionWindowOptions sessions,
        RetentionWindowOptions recaps,
        RetentionWindowOptions exports,
        RetentionWindowOptions invitations)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(recaps);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(invitations);

        if (sweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sweepInterval), sweepInterval, "The sweep interval must be positive.");
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "The batch size must be positive.");
        }

        SweepInterval = sweepInterval;
        BatchSize = batchSize;
        Sessions = sessions;
        Recaps = recaps;
        Exports = exports;
        Invitations = invitations;
    }

    /// <summary>How often the worker runs a retention sweep.</summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>The maximum number of records of each family a single sweep examines.</summary>
    public int BatchSize { get; }

    /// <summary>Retention policy for completed/expired sessions (and their cascade-removed events/recaps/rules).</summary>
    public RetentionWindowOptions Sessions { get; }

    /// <summary>Retention policy for generated recaps.</summary>
    public RetentionWindowOptions Recaps { get; }

    /// <summary>Retention policy for completed export artifacts (the row and any object-storage blob).</summary>
    public RetentionWindowOptions Exports { get; }

    /// <summary>Retention policy for closed/expired/revoked invitations (their plaintext invited email).</summary>
    public RetentionWindowOptions Invitations { get; }

    /// <summary>Whether ANY family is enabled — when false a sweep is a complete no-op and registers no useful work.</summary>
    public bool AnyEnabled => Sessions.Enabled || Recaps.Enabled || Exports.Enabled || Invitations.Enabled;

    /// <summary>
    /// Reads the retention options from configuration under <see cref="ConfigurationSection"/>. The sweep cadence
    /// and batch size come from <c>Retention:SweepInterval</c> / <c>Retention:BatchSize</c>; each family's policy
    /// from <c>Retention:&lt;Family&gt;:Enabled</c> (a boolean) and <c>Retention:&lt;Family&gt;:RetentionWindow</c>
    /// (a <see cref="TimeSpan"/>), where <c>&lt;Family&gt;</c> is <c>Sessions</c>, <c>Recaps</c>, <c>Exports</c> or
    /// <c>Invitations</c>. Absent or blank values fall back to the safe defaults (sessions/recaps/exports disabled,
    /// invitations enabled). A value that is PRESENT but cannot be parsed, or is out of range, is rejected at
    /// startup rather than silently falling back — a misconfigured timing or flag is a startup error, never a
    /// degenerate or surprising sweep.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured value is out of range.</exception>
    public static DataRetentionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);

        return new DataRetentionOptions(
            ParseTimeSpan(section["SweepInterval"], "SweepInterval", DefaultSweepInterval),
            ParseBatchSize(section["BatchSize"]),
            ReadFamily(section.GetSection("Sessions"), "Sessions", enabledDefault: false, DefaultSessionRetention),
            ReadFamily(section.GetSection("Recaps"), "Recaps", enabledDefault: false, DefaultRecapRetention),
            ReadFamily(section.GetSection("Exports"), "Exports", enabledDefault: false, DefaultExportRetention),
            ReadFamily(section.GetSection("Invitations"), "Invitations", enabledDefault: true, DefaultInvitationRetention));
    }

    private static RetentionWindowOptions ReadFamily(
        IConfigurationSection familySection,
        string familyName,
        bool enabledDefault,
        TimeSpan windowDefault)
        => new(
            ParseBool(familySection["Enabled"], $"{familyName}:Enabled", enabledDefault),
            ParseTimeSpan(familySection["RetentionWindow"], $"{familyName}:RetentionWindow", windowDefault));

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
                $"Configuration value '{ConfigurationSection}:BatchSize' is not a valid integer.");
        }

        return parsed;
    }

    private static bool ParseBool(string? value, string name, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{name}' is not a valid boolean.");
        }

        return parsed;
    }
}
