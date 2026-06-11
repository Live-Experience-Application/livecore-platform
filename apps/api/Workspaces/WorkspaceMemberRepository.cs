using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// EF Core implementation of <see cref="IWorkspaceMemberRepository"/>
/// (CORE-WS-002), backed by the <c>workspace_members</c> table mapped in
/// <see cref="WorkspaceMemberConfiguration"/>.
/// </summary>
internal sealed class WorkspaceMemberRepository : IWorkspaceMemberRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public WorkspaceMemberRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<WorkspaceMember?> FindAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored membership (ids are generated
        // non-empty), so the lookup fails fast instead of returning an
        // arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        // All three predicates translate to parameterized SQL equality. The
        // (workspace_id, user_id) pair is the unique natural key; adding the
        // organization_id predicate enforces the tenant boundary at the row
        // level (checked before the workspace boundary), so a membership in
        // another workspace, or in the same workspace id under a different
        // organization, is never returned (threat T5/T1). The unique index
        // guarantees at most one row.
        return await _dbContext.WorkspaceMembers
            .FirstOrDefaultAsync(
                member => member.OrganizationId == organizationId
                    && member.WorkspaceId == workspaceId
                    && member.UserProfileId == userProfileId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsMemberAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // Reuse the scoped lookup so the deny-by-default, tenant-isolation and
        // workspace-isolation semantics live in exactly one place: no membership
        // in this workspace (or a membership only in another workspace, or only
        // under another organization) denies.
        var member = await FindAsync(organizationId, workspaceId, userProfileId, cancellationToken)
            .ConfigureAwait(false);
        return member is not null;
    }

    /// <inheritdoc />
    public async Task<bool> HasRoleAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        MembershipRole role,
        CancellationToken cancellationToken)
    {
        // Reject undefined enum values a cast could smuggle in before touching
        // the database, so the check can never silently pass an invalid role.
        if (!WorkspaceMember.IsValidRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Role is not a defined membership role.");
        }

        // Reuse the scoped lookup, then match the role exactly in memory. The
        // matrix is non-linear, so there is no ordering comparison: a different
        // role (in either direction) denies, and a membership in a foreign
        // workspace or tenant is never consulted (deny-by-default, threat T5;
        // docs/06_AUTHORIZATION_MATRIX.md). The aggregate's HasRole keeps the
        // exact-match rule in one place.
        var member = await FindAsync(organizationId, workspaceId, userProfileId, cancellationToken)
            .ConfigureAwait(false);
        return member is not null && member.HasRole(role);
    }

    /// <inheritdoc />
    public async Task<WorkspaceMemberAddResult> AddAsync(
        WorkspaceMember member,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        _dbContext.WorkspaceMembers.Add(member);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return WorkspaceMemberAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by
            // a later SaveChanges on the same scope.
            _dbContext.Entry(member).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row for this
            // (workspace, subject) pair exists now, the unique index rejected
            // the insert as a duplicate (typically a lost create race). Any
            // other failure (for example a foreign-key violation for a
            // non-existent workspace or tenant) is rethrown unchanged.
            var duplicateExists = await _dbContext.WorkspaceMembers
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.WorkspaceId == member.WorkspaceId
                        && existing.UserProfileId == member.UserProfileId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return WorkspaceMemberAddResult.DuplicateMembership;
            }

            throw;
        }
    }
}
