// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
    public async Task<IReadOnlyList<Session>> ListPageByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's sessions, so the lookup
        // fails fast instead of returning an arbitrary set of rows (mirrors the
        // unbounded list and the audit page).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least one.");
        }

        // The same tenant- AND workspace-scoped, deterministically ordered read as
        // ListByWorkspaceAsync (predicate leads with organization_id then workspace_id, threat T5/T1; ordered by
        // the time-ordered UUIDv7 id, which is provider-independent — SQLite cannot ORDER BY a DateTimeOffset),
        // but bounded by Skip/Take so an unbounded list is never materialized (threat T9). Skip/Take translate to
        // the provider's OFFSET/LIMIT.
        return await _dbContext.Sessions
            .Where(session => session.OrganizationId == organizationId
                && session.WorkspaceId == workspaceId)
            .OrderBy(session => session.Id)
            .Skip(skip)
            .Take(take)
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<Session>> ListTerminalForRetentionAsync(
        DateTimeOffset createdBefore,
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "The batch size must be positive.");
        }

        // SYSTEM retention sweep (CORE-PRIV-003): the predicate filters on STATUS, so a Live or Prepared session
        // is never returned even if it is old — only terminal (Ended/Cancelled) sessions are purge candidates. It
        // deliberately spans all tenants/workspaces because the sweep is a system job, not a tenant actor; it
        // returns rows the sweep then re-loads and purges under its own transaction (it grants no access here).
        //
        // The order is the time-ordered surrogate id (UUIDv7 derived from CreatedAt, chronological and
        // provider-independent — SQLite cannot ORDER BY or compare a DateTimeOffset, so the threshold is applied
        // AFTER materialization exactly as the asset cleanup does), oldest first, so the OLDEST terminal sessions
        // sort first. The bounded batch (Take(maxCount)) therefore surfaces the most-aged sessions, and the
        // creation-age threshold filters out any still-recent terminal session that slips in. Because id order
        // tracks creation order and the window is measured from creation, every past-window terminal session is
        // covered over repeated sweeps without starvation.
        var oldestTerminal = await _dbContext.Sessions
            .AsNoTracking()
            .Where(session => session.Status == SessionStatus.Ended || session.Status == SessionStatus.Cancelled)
            .OrderBy(session => session.Id)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return oldestTerminal
            .Where(session => session.CreatedAt < createdBefore)
            .ToList();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Session session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Remove the terminal session row. The session_events, session_event_sequences, recaps and
        // session-scoped visibility_rules foreign keys cascade on delete, so the session's append-only event
        // stream, its recaps and its reveals are removed with it (docs/10); the audit log references it as a
        // recorded fact (not a foreign key) and survives. A concurrent purge of the same row makes this
        // SaveChanges affect zero rows and throw DbUpdateConcurrencyException, which the sweep treats as
        // already-handled (concurrency-safe).
        _dbContext.Sessions.Remove(session);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
