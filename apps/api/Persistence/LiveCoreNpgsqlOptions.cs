using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace LiveCore.Api.Persistence;

/// <summary>
/// The single, shared Npgsql provider configuration for every <see cref="LiveCoreDbContext"/> the platform
/// builds — the API host, every worker job and the design-time/migrations factory (CORE-CONC-003). Its one
/// job is to turn on a RETRYING execution strategy (<c>EnableRetryOnFailure</c>) consistently in one place,
/// so a routine, TRANSIENT PostgreSQL disruption is retried automatically by EF Core / Npgsql instead of
/// surfacing as a user-facing <c>5xx</c> or a worker-job exception.
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
/// This is SAFE to enable because every multi-step write already runs inside the execution strategy's
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
/// (fail-closed) exactly as before. It reads no configuration and holds no secret — the connection string is
/// supplied separately by each caller from configuration only (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
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
    /// strategy. Pass as the provider-options delegate to <c>UseNpgsql(connectionString, Configure)</c> at
    /// every registration site so the API, every worker job and the migrations factory share one resilience
    /// policy.
    /// </summary>
    /// <param name="npgsql">The Npgsql provider options builder supplied by <c>UseNpgsql</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="npgsql"/> is null.</exception>
    public static void Configure(NpgsqlDbContextOptionsBuilder npgsql)
    {
        ArgumentNullException.ThrowIfNull(npgsql);

        // Retry only TRANSIENT failures; Npgsql's default detector recognises the connection-level transients
        // the epic names (failover/restart/partition/pool exhaustion). errorCodesToAdd is null: the default
        // transient set is what we want, and a non-transient error must still fail loudly and immediately.
        npgsql.EnableRetryOnFailure(
            maxRetryCount: MaxRetryCount,
            maxRetryDelay: MaxRetryDelay,
            errorCodesToAdd: null);
    }
}
