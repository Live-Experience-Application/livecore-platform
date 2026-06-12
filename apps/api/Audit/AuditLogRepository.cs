using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Audit;

/// <summary>
/// EF Core implementation of <see cref="IAuditLogRepository"/> (CORE-VIS-006), backed by the
/// append-only <c>audit_logs</c> table mapped in <see cref="AuditLogConfiguration"/>. There is no
/// update or delete path: audit facts are immutable once written.
/// </summary>
internal sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public AuditLogRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _dbContext.AuditLogs.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        // An empty id can never address a stored tenant's records, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        // The predicate matches organization_id (the leading column of the documented critical index
        // audit_logs(organization_id, created_at)), so the list is exactly tenant-scoped: another
        // tenant's records are never returned even when their ids would otherwise be addressable
        // (threat T5). The order is by the time-ordered surrogate id (UUIDv7), which is chronological
        // and — unlike ordering by the DateTimeOffset created_at — is supported by every provider
        // (SQLite cannot ORDER BY a DateTimeOffset); this matches the ordering convention of the other
        // repositories. The created_at column still backs future time-range audit queries (CORE-AUD-005).
        return await _dbContext.AuditLogs
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
