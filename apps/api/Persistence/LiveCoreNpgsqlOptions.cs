// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace LiveCore.Api.Persistence;

/// <summary>
/// The single, shared Npgsql provider configuration for every <see cref="LiveCoreDbContext"/> the platform
/// builds — the API host, every worker job and the design-time/migrations factory (CORE-CONC-003,
/// CORE-RES-004). It does two jobs in one place so the API and every worker job share one policy:
///
/// <list type="number">
///   <item>turns on a RETRYING execution strategy (<c>EnableRetryOnFailure</c>) so a routine, TRANSIENT
///   PostgreSQL disruption is retried automatically by EF Core / Npgsql instead of surfacing as a user-facing
///   <c>5xx</c> or a worker-job exception;</item>
///   <item>sets a bounded per-command CEILING — the client-side <c>CommandTimeout</c> here, plus, for the
///   runtime contexts wired through <see cref="UseLiveCoreNpgsql"/>, the server-side <c>statement_timeout</c>
///   (<see cref="StatementTimeoutConnectionInterceptor"/>) — so a single stuck query is bounded rather than
///   running to the Npgsql default. Because the whole operation is also the retry unit, a bounded per-command
///   ceiling is what stops retry from amplifying an UNBOUNDED quantity (CORE-RES-004,
///   <see cref="LiveCorePersistenceOptions"/>).</item>
/// </list>
///
/// <para>
/// In the documented topology the API runs behind a proxy and PostgreSQL is a SEPARATE service
/// (docs/02_ARCHITECTURE.md; docs/13_SELF_HOSTING_REQUIREMENTS.md), so the routine disruptions the epic
/// names — a failover/primary promotion, a database restart, a brief network partition, momentary pool
/// exhaustion — are EXPECTED and short-lived. Npgsql's transient-error detection recognises exactly these
/// (connection failures, admin shutdown, serialization failures and the like), and a retrying strategy
/// re-runs the failed operation a few times with exponential back-off; an operation that succeeds on a retry
/// never reaches the caller as an error. A non-transient failure (a constraint violation, a query bug) is
/// NOT retried and still surfaces immediately.
/// </para>
///
/// <para>
/// Retry is SAFE to enable because every multi-step write already runs inside the execution strategy's
/// <c>ExecuteAsync</c> (CORE-CONC-002, <see cref="TransactionalUnitOfWork"/>): a retrying strategy forbids a
/// bare user-initiated <c>BeginTransaction</c> opened outside its delegate — it could not re-run the work
/// after a transient failure — and throws on one, so opening every transaction inside the strategy is a
/// precondition for turning retry on. Because the whole delegate is the retry unit, the effects inside it
/// must be safe to re-run from scratch (the idempotency-key writes and in-memory state-machine guards already
/// are); a retry begins a fresh transaction.
/// </para>
///
/// <para>
/// This changes only RESILIENCE, never posture: it is applied wherever <c>UseNpgsql</c> is called, each of
/// which is already GATED on a configured connection string, so the host still runs without persistence
/// (fail-closed) exactly as before. It holds no secret — the connection string is supplied separately by each
/// caller from configuration only (threat T7 in docs/07_SECURITY_THREAT_MODEL.md), and the timeouts are
/// product-neutral numbers (AGENTS.md).
/// </para>
/// </summary>
internal static class LiveCoreNpgsqlOptions
{
    /// <summary>
    /// The maximum number of retry attempts for a transient failure before the operation is allowed to fail.
    /// This is the EF Core default, stated explicitly so the resilience policy is visible and uniform.
    /// </summary>
    internal const int MaxRetryCount = 6;

    /// <summary>
    /// The ceiling on the exponential back-off delay between retries. The EF Core default, stated explicitly
    /// alongside <see cref="MaxRetryCount"/>.
    /// </summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Configures the Npgsql provider for a <see cref="LiveCoreDbContext"/>: turns on the retrying execution
    /// strategy and applies the DEFAULT client-side command timeout. Pass as the provider-options delegate to
    /// <c>UseNpgsql(connectionString, Configure)</c> at the design-time/migrations factory, which has no
    /// configuration to read; the runtime hosts use <see cref="UseLiveCoreNpgsql"/> instead so they also pick up
    /// the configured command timeout and the server-side <c>statement_timeout</c>.
    /// </summary>
    /// <param name="npgsql">The Npgsql provider options builder supplied by <c>UseNpgsql</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="npgsql"/> is null.</exception>
    public static void Configure(NpgsqlDbContextOptionsBuilder npgsql)
        => Configure(npgsql, LiveCorePersistenceOptions.DefaultCommandTimeout);

    /// <summary>
    /// Configures the Npgsql provider with an explicit client-side command timeout: turns on the retrying
    /// execution strategy and bounds each command at <paramref name="commandTimeout"/>.
    /// </summary>
    /// <param name="npgsql">The Npgsql provider options builder supplied by <c>UseNpgsql</c>.</param>
    /// <param name="commandTimeout">The client-side per-command ceiling; must be positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="npgsql"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="commandTimeout"/> is not positive.</exception>
    public static void Configure(NpgsqlDbContextOptionsBuilder npgsql, TimeSpan commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(npgsql);
        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout), commandTimeout, "The database command timeout must be positive.");
        }

        // Retry only TRANSIENT failures; Npgsql's default detector recognises the connection-level transients
        // the epic names (failover/restart/partition/pool exhaustion). errorCodesToAdd is null: the default
        // transient set is what we want, and a non-transient error must still fail loudly and immediately.
        npgsql.EnableRetryOnFailure(
            maxRetryCount: MaxRetryCount,
            maxRetryDelay: MaxRetryDelay,
            errorCodesToAdd: null);

        // Client-side per-command ceiling (CORE-RES-004): Npgsql's CommandTimeout is in WHOLE SECONDS, so round
        // up to keep a sub-second configured value from collapsing to 0 ("no timeout"). The driver cancels a
        // command that exceeds this, so a stuck query never blocks its thread/connection indefinitely, and each
        // retry attempt is itself bounded.
        npgsql.CommandTimeout(ToWholeSeconds(commandTimeout));
    }

    /// <summary>
    /// Registers the LiveCore Npgsql provider for a RUNTIME <see cref="LiveCoreDbContext"/> (the API host and the
    /// worker jobs): the retrying execution strategy and the configured client-side command timeout (via
    /// <see cref="Configure(NpgsqlDbContextOptionsBuilder, TimeSpan)"/>), plus the server-side
    /// <c>statement_timeout</c> backstop (<see cref="StatementTimeoutConnectionInterceptor"/>) when a positive
    /// statement timeout is configured. This is the single runtime registration helper every host calls, so the
    /// API and every worker job share one identical persistence policy.
    /// </summary>
    /// <param name="options">The DbContext options builder.</param>
    /// <param name="connectionString">The PostgreSQL connection string (supplied from configuration).</param>
    /// <param name="tuning">The configured per-command timeouts (<see cref="LiveCorePersistenceOptions"/>).</param>
    /// <returns><paramref name="options"/>, so a caller can chain additional interceptors (e.g. the failure metrics interceptor).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="tuning"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or blank.</exception>
    public static DbContextOptionsBuilder UseLiveCoreNpgsql(
        this DbContextOptionsBuilder options,
        string connectionString,
        LiveCorePersistenceOptions tuning)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(tuning);

        options.UseNpgsql(connectionString, npgsql => Configure(npgsql, tuning.CommandTimeout));

        // Server-side statement_timeout (CORE-RES-004): the database aborts a stuck query on its own even if the
        // client cancellation is lost. Zero means the operator has disabled it, so nothing is registered.
        if (tuning.StatementTimeout > TimeSpan.Zero)
        {
            options.AddInterceptors(new StatementTimeoutConnectionInterceptor(tuning.StatementTimeout));
        }

        return options;
    }

    /// <summary>
    /// Converts a timeout to the whole seconds Npgsql's <c>CommandTimeout</c> expects, rounding UP so a positive
    /// sub-second value never becomes <c>0</c> (which Npgsql interprets as "no timeout").
    /// </summary>
    private static int ToWholeSeconds(TimeSpan timeout) => (int)Math.Ceiling(timeout.TotalSeconds);
}
