using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Sessions;

/// <summary>
/// EF Core implementation of <see cref="ISessionRepository"/> (CORE-SES-002),
/// backed by the <c>sessions</c> table mapped in
/// <see cref="SessionConfiguration"/>.
/// </summary>
internal sealed class SessionRepository : ISessionRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public SessionRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<Session?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored session (ids are generated
        // non-empty), so the lookup fails fast instead of returning an arbitrary
        // row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading
        // with the tenant column. The lookup is exactly tenant- and
        // workspace-scoped, so a session under another organization or workspace is
        // never returned even when the surrogate id matches (threat T5/T1).
        return await _dbContext.Sessions
            .FirstOrDefaultAsync(
                session => session.OrganizationId == organizationId
                    && session.WorkspaceId == workspaceId
                    && session.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Session?> FindByIdInOrganizationAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored session (ids are generated
        // non-empty), so the lookup fails fast instead of returning an arbitrary
        // row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(id));
        }

        // The predicate LEADS with the tenant column, so the lookup is exactly
        // tenant-scoped: a session under another organization is never returned
        // even when the surrogate id matches (threat T5/T1). The workspace is not
        // part of the predicate because the by-session-id routes do not know it up
        // front; it is read off the returned row (Session.WorkspaceId) so the
        // caller can authorize against workspace membership AFTER the tenant
        // boundary has been enforced.
        return await _dbContext.Sessions
            .FirstOrDefaultAsync(
                session => session.OrganizationId == organizationId
                    && session.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Session>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's sessions, so the lookup
        // fails fast instead of returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The predicate leads with the tenant column and then matches the workspace,
        // so the list is exactly tenant- and workspace-scoped: another tenant's or
        // another workspace's sessions are never returned even when their ids would
        // otherwise be addressable (threat T5/T1; the organization boundary is checked
        // before the workspace boundary, backed by the
        // ix_sessions_organization_id_workspace_id index).
        // The order is by the time-ordered surrogate id (UUIDv7), which is chronological
        // and provider-independent — SQLite cannot ORDER BY a DateTimeOffset — matching
        // the other repositories' ordering convention.
        return await _dbContext.Sessions
            .Where(session => session.OrganizationId == organizationId
                && session.WorkspaceId == workspaceId)
            .OrderBy(session => session.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SessionAddResult> AddAsync(Session session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        _dbContext.Sessions.Add(session);

        // A session has no uniqueness constraint to violate, so there is no
        // duplicate outcome to translate here; a foreign-key violation (a
        // non-existent workspace or tenant) propagates as a DbUpdateException.
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SessionAddResult.Added;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Session session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // The session was loaded and mutated within this scope's change tracker (or
        // is attached here); only the mutable title, status, live-timeline
        // timestamps and update timestamp change. The organization, workspace and
        // id are immutable on the aggregate, so an update can never move the row to
        // another tenant or workspace (threat T5).
        _dbContext.Sessions.Update(session);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
