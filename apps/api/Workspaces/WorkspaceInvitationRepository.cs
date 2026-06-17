using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// EF Core implementation of <see cref="IWorkspaceInvitationRepository"/>
/// (CORE-WS-004), backed by the <c>workspace_invitations</c> table mapped in
/// <see cref="WorkspaceInvitationConfiguration"/>.
/// </summary>
internal sealed class WorkspaceInvitationRepository : IWorkspaceInvitationRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public WorkspaceInvitationRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<WorkspaceInvitationAddResult> AddAsync(
        WorkspaceInvitation invitation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        _dbContext.WorkspaceInvitations.Add(invitation);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return WorkspaceInvitationAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by
            // a later SaveChanges on the same scope.
            _dbContext.Entry(invitation).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row with this token
            // hash exists now, the unique token-hash index rejected the insert
            // as a duplicate. Any other failure (for example a foreign-key
            // violation for a non-existent workspace or tenant) is rethrown
            // unchanged. The plaintext token is never involved here — only its
            // hash (threats T6/T7).
            var duplicateExists = await _dbContext.WorkspaceInvitations
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.TokenHash == invitation.TokenHash,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return WorkspaceInvitationAddResult.DuplicateToken;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInvitation?> FindByTokenHashAsync(
        Guid organizationId,
        Guid workspaceId,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        // Empty ids and malformed hashes can never address a stored invitation,
        // so the lookup fails fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (!WorkspaceInvitationToken.IsValidHash(tokenHash))
        {
            throw new ArgumentException("Token hash violates the hash invariants.", nameof(tokenHash));
        }

        // All three predicates translate to parameterized SQL equality. Scoping
        // by the organization (the tenant, checked before the workspace
        // boundary) and the workspace means a matching token hash under another
        // organization or workspace is never returned: the token is only ever
        // honoured inside the scope it was minted for (threats T5/T6). The
        // unique token-hash index guarantees at most one row.
        return await _dbContext.WorkspaceInvitations
            .FirstOrDefaultAsync(
                invitation => invitation.OrganizationId == organizationId
                    && invitation.WorkspaceId == workspaceId
                    && invitation.TokenHash == tokenHash,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkspaceInvitation?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored invitation (ids are generated non-empty), so the lookup fails
        // fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException("Invitation id must not be empty.", nameof(invitationId));
        }

        // All three predicates translate to parameterized SQL equality. The id is the row's own key, but the
        // organization_id and workspace_id predicates additionally pin the tenant and workspace boundaries (the
        // organization boundary checked before the workspace boundary), so an invitation with that id in another
        // workspace, or in the same workspace id under a different organization, is never returned (threats
        // T1/T5). This by-id lookup never touches the token secret — neither the plaintext nor its hash.
        return await _dbContext.WorkspaceInvitations
            .FirstOrDefaultAsync(
                invitation => invitation.Id == invitationId
                    && invitation.OrganizationId == organizationId
                    && invitation.WorkspaceId == workspaceId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceInvitation>> ListPendingByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a tenant or workspace (ids are generated non-empty), so the lookup fails
        // fast instead of scanning the whole table.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // All three predicates translate to parameterized SQL equality. Scoping by the organization (the
        // tenant, checked before the workspace boundary) and the workspace means an invitation under another
        // organization or workspace is never returned (threats T1/T5); the tenant-scoped composite index leads
        // with organization_id (WorkspaceInvitationConfiguration). The status is persisted as its stable name
        // (HasConversion<string>), so EF translates this equality to the stored name; the lifecycle is
        // non-linear, so this is an EXACT Pending match, never an ordering comparison. The read is tracking-free
        // (it never mutates) and ordered oldest-first by the surrogate id, which is a time-ordered UUIDv7, so it
        // yields a deterministic chronological page WITHOUT ordering by a DateTimeOffset column (which the SQLite
        // provider cannot translate), mirroring every other tenant-scoped list query. The token hash rides along
        // on the aggregate but the endpoint projects to a PII-safe DTO that never emits it (threats T6/T7).
        return await _dbContext.WorkspaceInvitations
            .AsNoTracking()
            .Where(invitation => invitation.OrganizationId == organizationId
                && invitation.WorkspaceId == workspaceId
                && invitation.Status == WorkspaceInvitationStatus.Pending)
            .OrderBy(invitation => invitation.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceInvitation>> ListPendingPageByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a tenant or workspace, so the lookup fails fast (mirrors the unbounded
        // list).
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

        // The same tenant- AND workspace-scoped, Pending-only, oldest-first (UUIDv7 id) read as
        // ListPendingByWorkspaceAsync (threats T1/T5), but bounded by Skip/Take so an unbounded list is never
        // materialized (threat T9). Tracking-free (it never mutates); the endpoint projects to a PII-safe DTO
        // that never emits the token hash (threats T6/T7).
        return await _dbContext.WorkspaceInvitations
            .AsNoTracking()
            .Where(invitation => invitation.OrganizationId == organizationId
                && invitation.WorkspaceId == workspaceId
                && invitation.Status == WorkspaceInvitationStatus.Pending)
            .OrderBy(invitation => invitation.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(WorkspaceInvitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        // The invitation was loaded and mutated within this scope's change tracker; only the lifecycle
        // status and the update timestamp change. The tenant, workspace, role, token hash and expiry are
        // immutable on the aggregate, so an update can never move the row to another tenant or workspace
        // (threat T5) nor re-open a consumed token. On PostgreSQL the row carries an xmin optimistic-
        // concurrency token (LiveCoreDbContext), so two concurrent redemptions of one scoped token make the
        // second SaveChanges fail loudly (a DbUpdateConcurrencyException -> 409) instead of silently granting
        // a second membership: the single-use guarantee holds even under a race (threat T6).
        _dbContext.WorkspaceInvitations.Update(invitation);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
