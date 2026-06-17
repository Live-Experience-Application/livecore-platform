using System.Globalization;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Tunable, configurable bounds on a database COMMAND so a single stuck query has a CEILING rather than running
/// indefinitely (CORE-RES-004, the "Reliability and Fault Tolerance" epic). Two complementary timeouts make that
/// ceiling, both read once from configuration (under <see cref="ConfigurationSection"/>) with safe defaults so a
/// host runs without any persistence tuning, exactly like <see cref="LiveCore.Api.Hosting.GracefulShutdownOptions"/>
/// and the worker's heartbeat options:
///
/// <list type="bullet">
///   <item><b><see cref="CommandTimeout"/></b> — the CLIENT-side ceiling (EF Core / Npgsql
///   <c>CommandTimeout</c>): how long the driver waits for a command before it cancels it and surfaces a timeout
///   rather than blocking the calling thread/connection forever. Applied in
///   <see cref="LiveCoreNpgsqlOptions.Configure(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder, System.TimeSpan)"/>.</item>
///   <item><b><see cref="StatementTimeout"/></b> — the SERVER-side ceiling (PostgreSQL <c>statement_timeout</c>):
///   the database aborts the query on its own after this window even if the client never cancels, so the work is
///   bounded at the source. Applied by <see cref="StatementTimeoutConnectionInterceptor"/> on every connection
///   open. <see cref="System.TimeSpan.Zero"/> disables it (no server-side ceiling), an explicit operator choice.</item>
/// </list>
///
/// <para>
/// WHY BOTH, AND WHY THIS PAIRS WITH RETRY. The retrying execution strategy (<see cref="LiveCoreNpgsqlOptions"/>,
/// CORE-CONC-003) can re-issue a failed operation up to <see cref="LiveCoreNpgsqlOptions.MaxRetryCount"/> times.
/// Without a per-command ceiling a single pathological query could run to the Npgsql default and then be reissued
/// several more times, so the worst-case wall-clock — and the pool/thread occupancy it costs — is unbounded. A
/// bounded command timeout makes EACH attempt finite, so retry can only ever amplify a bounded quantity, never an
/// unbounded one. The server-side <c>statement_timeout</c> is the defence-in-depth backstop for the case where the
/// client cancellation is lost (a wedged connection): the database itself stops the work.
/// </para>
///
/// <para>
/// The connection pool's MAXIMUM SIZE is the other half of this story but is intentionally NOT set here: it lives
/// in the connection string (<c>Maximum Pool Size</c>), which the deployment supplies, because the right value
/// depends on the database's <c>max_connections</c> and the number of API/worker replicas sharing it. It is
/// documented as production connection guidance in docs/13_SELF_HOSTING_REQUIREMENTS.md and shown in the bundled
/// compose manifest; Core applies the two timeouts and leaves pool sizing to the connection string.
/// </para>
///
/// <para>
/// No secret lives here (only two timespans) and the values are product-neutral (AGENTS.md). The connection string
/// — the one value that carries a credential — is supplied separately from configuration only (threat T7).
/// </para>
/// </summary>
internal sealed class LiveCorePersistenceOptions
{
    /// <summary>Configuration section the persistence tuning is read from (<c>Persistence</c>).</summary>
    public const string ConfigurationSection = "Persistence";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> holding the client command timeout.</summary>
    public const string CommandTimeoutKey = "CommandTimeout";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> holding the server statement timeout.</summary>
    public const string StatementTimeoutKey = "StatementTimeout";

    /// <summary>
    /// Default client-side command timeout (30 seconds) when none is configured. Stated explicitly so the ceiling
    /// is visible and configurable rather than relying on the implicit Npgsql default.
    /// </summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default server-side <c>statement_timeout</c> (30 seconds) when none is configured. Sized to match the
    /// default command timeout so a stuck query is bounded at both the client and the server.
    /// </summary>
    public static readonly TimeSpan DefaultStatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates persistence-tuning options.</summary>
    /// <param name="commandTimeout">The client-side per-command ceiling. Must be positive (a non-positive Npgsql command timeout means "no timeout", which would defeat the bound).</param>
    /// <param name="statementTimeout">The server-side <c>statement_timeout</c>. <see cref="TimeSpan.Zero"/> disables it; a negative value is rejected.</param>
    /// <exception cref="ArgumentOutOfRangeException">The command timeout is not positive, or the statement timeout is negative.</exception>
    public LiveCorePersistenceOptions(TimeSpan commandTimeout, TimeSpan statementTimeout)
    {
        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout), commandTimeout, "The database command timeout must be positive.");
        }

        if (statementTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statementTimeout), statementTimeout, "The database statement timeout cannot be negative.");
        }

        CommandTimeout = commandTimeout;
        StatementTimeout = statementTimeout;
    }

    /// <summary>The client-side per-command ceiling (EF Core / Npgsql <c>CommandTimeout</c>).</summary>
    public TimeSpan CommandTimeout { get; }

    /// <summary>
    /// The server-side <c>statement_timeout</c>. <see cref="TimeSpan.Zero"/> means no server-side ceiling
    /// (the interceptor is not registered); any positive value bounds the query at the database.
    /// </summary>
    public TimeSpan StatementTimeout { get; }

    /// <summary>The safe defaults, used by the design-time/migrations factory and the offline model-check tests.</summary>
    public static LiveCorePersistenceOptions Default { get; } = new(DefaultCommandTimeout, DefaultStatementTimeout);

    /// <summary>
    /// Reads the persistence-tuning options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Persistence:CommandTimeout</c> / <c>Persistence:StatementTimeout</c>), falling back to the safe defaults
    /// when a value is absent or blank. A value that is PRESENT but cannot be parsed (or is out of range) is
    /// rejected at startup rather than silently falling back — a misconfigured ceiling is a startup error, never a
    /// degenerate (or removed) bound.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present timeout value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured timeout value is out of range.</exception>
    public static LiveCorePersistenceOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        var commandTimeout = ParseTimeout(section[CommandTimeoutKey], CommandTimeoutKey, DefaultCommandTimeout);
        var statementTimeout = ParseTimeout(section[StatementTimeoutKey], StatementTimeoutKey, DefaultStatementTimeout);
        return new LiveCorePersistenceOptions(commandTimeout, statementTimeout);
    }

    private static TimeSpan ParseTimeout(string? value, string key, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{key}' is not a valid TimeSpan.");
        }

        return parsed;
    }
}
