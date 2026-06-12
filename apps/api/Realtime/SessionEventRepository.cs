using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Realtime;

/// <summary>
/// EF Core implementation of <see cref="ISessionEventRepository"/> (CORE-RT-003), backed by the
/// append-only <c>session_events</c> table mapped in <see cref="SessionEventConfiguration"/>. There is no
/// update or delete path: session events are immutable once appended.
/// </summary>
internal sealed class SessionEventRepository : ISessionEventRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public SessionEventRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        _dbContext.SessionEvents.Add(sessionEvent);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
        Guid organizationId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored session's events, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        // The predicate leads with the tenant column and then matches the session, so the list is
        // exactly tenant- and session-scoped (threat T5/T1). The order is by the time-ordered surrogate
        // id (UUIDv7), which is chronological and provider-independent — SQLite cannot ORDER BY a
        // DateTimeOffset — matching the other repositories' ordering convention.
        return await _dbContext.SessionEvents
            .Where(sessionEvent => sessionEvent.OrganizationId == organizationId
                && sessionEvent.SessionId == sessionId)
            .OrderBy(sessionEvent => sessionEvent.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
