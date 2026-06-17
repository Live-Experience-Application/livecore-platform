using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Applies the SERVER-side PostgreSQL <c>statement_timeout</c> to every database session (CORE-RES-004) so a stuck
/// query is bounded at the database itself, not only at the client. On each connection open the interceptor issues
/// <c>SET statement_timeout = &lt;milliseconds&gt;</c>, after which PostgreSQL aborts any single statement that runs
/// longer than the configured window — even if the client never cancels it (a wedged connection where the
/// EF Core / Npgsql <see cref="LiveCorePersistenceOptions.CommandTimeout"/> cancellation is lost).
///
/// <para>
/// WHY AN OPEN-TIME <c>SET</c> RATHER THAN A CONNECTION-STRING OPTION. Running the <c>SET</c> on every
/// <see cref="ConnectionOpened"/>/<see cref="ConnectionOpenedAsync"/> guarantees the ceiling is in force regardless
/// of connection pooling: a pooled physical connection that has had its session reset still has the timeout
/// re-applied the next time EF Core opens it, so the bound never silently lapses across reuse. The cost is one
/// lightweight round-trip per open, a worthwhile trade for a reliability ceiling that is the very thing protecting
/// the pool from a pathological query. The <c>SET</c> is issued on a raw ADO command (not the EF command pipeline),
/// so it is invisible to the EF command interceptors and is never miscounted as a query failure (CORE-OBS-001).
/// </para>
///
/// <para>
/// This is registered only on the runtime Npgsql contexts (the API host and the worker jobs, via
/// <see cref="LiveCoreNpgsqlOptions.UseLiveCoreNpgsql"/>) and only when a positive timeout is configured. It is
/// deliberately NOT applied to the design-time/migrations context, so a long, controlled schema migration (a large
/// index build) is not bounded by the runtime query ceiling. The milliseconds value is a server-side integer
/// literal built from the configured timespan — it carries no user input and no secret (threat T7).
/// </para>
/// </summary>
internal sealed class StatementTimeoutConnectionInterceptor : DbConnectionInterceptor
{
    private readonly string _setStatementTimeoutSql;

    /// <summary>Creates the interceptor for a positive server-side <c>statement_timeout</c>.</summary>
    /// <param name="statementTimeout">The server-side ceiling; must be positive (a non-positive value would disable the bound and is rejected — the caller does not register the interceptor in that case).</param>
    /// <exception cref="ArgumentOutOfRangeException">The statement timeout is not positive.</exception>
    public StatementTimeoutConnectionInterceptor(TimeSpan statementTimeout)
    {
        if (statementTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statementTimeout), statementTimeout, "The server-side statement timeout must be positive.");
        }

        StatementTimeout = statementTimeout;

        // statement_timeout is expressed in whole milliseconds; round up so a sub-millisecond fraction never
        // collapses the ceiling to zero ("0" would DISABLE the server timeout in PostgreSQL).
        var milliseconds = (long)Math.Ceiling(statementTimeout.TotalMilliseconds);
        _setStatementTimeoutSql = string.Create(
            CultureInfo.InvariantCulture, $"SET statement_timeout = {milliseconds}");
    }

    /// <summary>The server-side ceiling this interceptor applies on every connection open.</summary>
    public TimeSpan StatementTimeout { get; }

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = _setStatementTimeoutSql;
            command.ExecuteNonQuery();
        }

        base.ConnectionOpened(connection, eventData);
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = _setStatementTimeoutSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }
}
