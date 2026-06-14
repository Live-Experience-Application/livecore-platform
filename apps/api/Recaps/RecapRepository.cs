using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Recaps;

/// <summary>
/// EF Core implementation of <see cref="IRecapRepository"/> (CORE-AUD-004), backed by the <c>recaps</c> table
/// mapped in <see cref="RecapConfiguration"/>. A recap is write-once: there is an append and tenant-scoped
/// reads only, no update or delete path (mirrors the append-only audit log).
/// </summary>
internal sealed class RecapRepository : IRecapRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public RecapRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task AppendAsync(Recap recap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recap);

        _dbContext.Recaps.Add(recap);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RecapAppendResult> TryAppendSystemRecapAsync(Recap recap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recap);

        if (recap.GeneratedByUserProfileId is not null)
        {
            // The at-most-one rule is a SYSTEM-recap invariant (the partial unique index filters on
            // generated_by IS NULL); a host recap is unconstrained and must use the plain AppendAsync. Reject a
            // misuse loudly rather than silently writing it through a path whose dedup would never apply.
            throw new ArgumentException(
                "TryAppendSystemRecapAsync accepts only a system recap (GeneratedByUserProfileId must be null).",
                nameof(recap));
        }

        _dbContext.Recaps.Add(recap);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return RecapAppendResult.Appended;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the same
            // scope (the sweep reuses one DbContext across the batch). Mirrors QuotaUsageRepository.AddAsync.
            _dbContext.Entry(recap).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a system recap already exists for this session, the
            // partial unique index recaps(session_id) WHERE generated_by IS NULL rejected the insert as a
            // duplicate (a lost race against a concurrent sweep or another worker replica) — converge onto the
            // one recap instead of failing. The predicate matches the index filter exactly. Any OTHER failure
            // (for example the session was deleted between the eligibility read and the append, a foreign-key
            // violation) is rethrown unchanged so the sweep counts it as a failure and retries the session.
            var systemRecapExists = await _dbContext.Recaps
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.SessionId == recap.SessionId && existing.GeneratedByUserProfileId == null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (systemRecapExists)
            {
                return RecapAppendResult.AlreadyExists;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Recap?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored recap (ids are generated non-empty), so the lookup fails fast
        // instead of returning an arbitrary row.
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
            throw new ArgumentException("Recap id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the tenant column. The
        // lookup is exactly tenant- and workspace-scoped, so a recap under another organization or workspace
        // is never returned even when the surrogate id matches (threat T5/T1).
        return await _dbContext.Recaps
            .FirstOrDefaultAsync(
                recap => recap.OrganizationId == organizationId
                    && recap.WorkspaceId == workspaceId
                    && recap.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Recap>> ListBySessionAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        // The predicate leads with the tenant column, then the workspace and the session, so the list is
        // exactly tenant- and workspace-scoped: another tenant's or another workspace's recaps are never
        // returned even when the session id matches (threat T5/T1). The order is by the time-ordered surrogate
        // id (UUIDv7), which is chronological and supported by every provider (SQLite cannot ORDER BY a
        // DateTimeOffset); this matches the ordering convention of the other repositories.
        return await _dbContext.Recaps
            .Where(recap => recap.OrganizationId == organizationId
                && recap.WorkspaceId == workspaceId
                && recap.SessionId == sessionId)
            .OrderBy(recap => recap.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
