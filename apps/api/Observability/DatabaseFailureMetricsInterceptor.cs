using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LiveCore.Api.Observability;

/// <summary>
/// An EF Core command interceptor that counts failed database commands onto <see cref="LiveCoreMetrics"/>
/// (CORE-OBS-001) — the docs/15_OBSERVABILITY.md "database query failures" signal. Registering it on the
/// <c>DbContext</c> makes every query path emit the metric uniformly, without each repository having to
/// instrument its own calls.
///
/// It records ONLY a count (threat T7 in docs/07_SECURITY_THREAT_MODEL.md): the SQL text, parameters and
/// exception detail belong in structured server logs, never on a metric label, so the interceptor reads
/// nothing from the failing command — it simply increments the counter and returns.
/// </summary>
public sealed class DatabaseFailureMetricsInterceptor : DbCommandInterceptor
{
    private readonly LiveCoreMetrics _metrics;

    public DatabaseFailureMetricsInterceptor(LiveCoreMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    /// <inheritdoc />
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        _metrics.RecordDatabaseFailure();
        base.CommandFailed(command, eventData);
    }

    /// <inheritdoc />
    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _metrics.RecordDatabaseFailure();
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }
}
