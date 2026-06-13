using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Organizations;

/// <summary>
/// EF Core implementation of <see cref="IOrganizationRepository"/>
/// (CORE-ID-003), backed by the <c>organizations</c> table mapped in
/// <see cref="OrganizationConfiguration"/>.
/// </summary>
internal sealed class OrganizationRepository : IOrganizationRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public OrganizationRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // An empty id can never address a stored organization (ids are
        // generated non-empty), so the lookup fails fast instead of
        // returning an arbitrary row.
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(id));
        }

        return await _dbContext.Organizations
            .FirstOrDefaultAsync(organization => organization.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        // Canonicalize first (and reject malformed input): a stored slug is
        // always canonical, so a malformed value can never address a tenant.
        var canonicalSlug = Organization.CanonicalizeSlug(slug);

        // The predicate translates to parameterized SQL equality, which is
        // exact and case-sensitive under the default binary-style collations
        // of PostgreSQL: a near-match never addresses a foreign tenant
        // (threat T5). The unique index guarantees at most one row.
        return await _dbContext.Organizations
            .FirstOrDefaultAsync(organization => organization.Slug == canonicalSlug, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Organization>> ListByMemberAsync(
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        // Return only the organizations the subject has a membership row for.
        // The predicate translates to a parameterized EXISTS against
        // organization_members on the (organization_id, user_id) pair, so an
        // organization the subject is not a member of is never returned
        // (deny-by-default; threat T5). Ordering by the (time-ordered) UUIDv7 id
        // keeps the listing stable. The membership role is not consulted: this
        // is the subject's full set of organizations, and the caller intersects
        // it with the token's organization claims.
        return await _dbContext.Organizations
            .AsNoTracking()
            .Where(organization => _dbContext.OrganizationMembers.Any(member =>
                member.OrganizationId == organization.Id
                && member.UserProfileId == userProfileId))
            .OrderBy(organization => organization.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OrganizationAddResult> AddAsync(
        Organization organization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);

        _dbContext.Organizations.Add(organization);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return OrganizationAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried
            // by a later SaveChanges on the same scope.
            _dbContext.Entry(organization).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row with this slug
            // exists now, the unique index rejected the insert as a duplicate
            // (typically a lost create race). Any other failure is rethrown
            // unchanged.
            var duplicateExists = await _dbContext.Organizations
                .AsNoTracking()
                .AnyAsync(existing => existing.Slug == organization.Slug, cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return OrganizationAddResult.DuplicateSlug;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<OrganizationAddResult> AddWithOwnerAsync(
        Organization organization,
        OrganizationMember owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(owner);

        // Guard the invariant the endpoint relies on: the founding membership
        // must belong to exactly the organization being created. The membership
        // is minted by the caller for this organization's id, so a mismatch is a
        // programming error and fails closed rather than persisting a membership
        // for a foreign tenant (threat T5).
        if (!owner.BelongsToOrganization(organization.Id))
        {
            throw new ArgumentException(
                "The owner membership must belong to the organization being created.",
                nameof(owner));
        }

        // Add BOTH the tenant root and its founding owner membership and save
        // once: EF Core wraps a single SaveChanges in one transaction, so either
        // both rows commit or neither does. The tenant is therefore never left
        // ownerless, and a duplicate slug rolls the membership back too.
        _dbContext.Organizations.Add(organization);
        _dbContext.OrganizationMembers.Add(owner);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return OrganizationAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed inserts must not be retried by
            // a later SaveChanges on the same scope.
            _dbContext.Entry(organization).State = EntityState.Detached;
            _dbContext.Entry(owner).State = EntityState.Detached;

            // Provider-neutral duplicate detection: a row with this slug existing
            // now means the unique slug index rejected the create (a lost create
            // race or a pre-existing tenant). The owner membership was rolled back
            // with it (single transaction), so a duplicate create never adds the
            // caller to an existing tenant — no privilege escalation (threats
            // T5/T1). Any other failure is rethrown unchanged.
            var duplicateExists = await _dbContext.Organizations
                .AsNoTracking()
                .AnyAsync(existing => existing.Slug == organization.Slug, cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return OrganizationAddResult.DuplicateSlug;
            }

            throw;
        }
    }
}
