using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Organizations;

/// <summary>
/// EF Core implementation of <see cref="IOrganizationMemberRepository"/>
/// (CORE-ID-004), backed by the <c>organization_members</c> table mapped in
/// <see cref="OrganizationMemberConfiguration"/>.
/// </summary>
internal sealed class OrganizationMemberRepository : IOrganizationMemberRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public OrganizationMemberRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<OrganizationMember?> FindAsync(
        Guid organizationId,
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

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        // Both predicates translate to parameterized SQL equality on the
        // unique (organization_id, user_id) index: the lookup is exactly
        // tenant-scoped, so a membership in another organization is never
        // returned (threat T5). The unique index guarantees at most one row.
        return await _dbContext.OrganizationMembers
            .FirstOrDefaultAsync(
                member => member.OrganizationId == organizationId && member.UserProfileId == userProfileId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsMemberAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // Reuse the tenant-scoped lookup so the deny-by-default and
        // tenant-isolation semantics live in exactly one place: no membership
        // in this organization (or a membership only in another organization)
        // denies.
        var member = await FindAsync(organizationId, userProfileId, cancellationToken).ConfigureAwait(false);
        return member is not null;
    }

    /// <inheritdoc />
    public async Task<OrganizationMemberAddResult> AddAsync(
        OrganizationMember member,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        _dbContext.OrganizationMembers.Add(member);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return OrganizationMemberAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried
            // by a later SaveChanges on the same scope.
            _dbContext.Entry(member).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row for this
            // (organization, subject) pair exists now, the unique index
            // rejected the insert as a duplicate (typically a lost create
            // race). Any other failure is rethrown unchanged.
            var duplicateExists = await _dbContext.OrganizationMembers
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.OrganizationId == member.OrganizationId
                        && existing.UserProfileId == member.UserProfileId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return OrganizationMemberAddResult.DuplicateMembership;
            }

            throw;
        }
    }
}
