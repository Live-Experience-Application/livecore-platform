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
}
