using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// EF Core implementation of <see cref="IWorkspaceRepository"/> (CORE-WS-001),
/// backed by the <c>workspaces</c> table mapped in
/// <see cref="WorkspaceConfiguration"/>.
/// </summary>
internal sealed class WorkspaceRepository : IWorkspaceRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public WorkspaceRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<Workspace?> FindByIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace (ids are generated
        // non-empty), so the lookup fails fast instead of returning an
        // arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(id));
        }

        // Both predicates translate to parameterized SQL equality on the
        // (organization_id, id) index: the lookup is exactly tenant-scoped, so a
        // workspace under another organization is never returned even when the
        // surrogate id matches (threat T5/T1).
        return await _dbContext.Workspaces
            .FirstOrDefaultAsync(
                workspace => workspace.OrganizationId == organizationId && workspace.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Workspace?> FindBySlugAsync(
        Guid organizationId,
        string slug,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        // Canonicalize first (and reject malformed input): a stored slug is
        // always canonical, so a malformed value can never address a workspace.
        var canonicalSlug = Workspace.CanonicalizeSlug(slug);

        // The predicate translates to parameterized SQL equality on the unique
        // (organization_id, slug) index, which is exact and case-sensitive under
        // the default binary-style collations of PostgreSQL: a near-match, or
        // the same slug in another organization, never addresses a foreign
        // workspace (threat T5). The unique index guarantees at most one row per
        // (organization, slug).
        return await _dbContext.Workspaces
            .FirstOrDefaultAsync(
                workspace => workspace.OrganizationId == organizationId && workspace.Slug == canonicalSlug,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkspaceAddResult> AddAsync(
        Workspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _dbContext.Workspaces.Add(workspace);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return WorkspaceAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by
            // a later SaveChanges on the same scope.
            _dbContext.Entry(workspace).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row with this slug
            // exists now in the same organization, the unique
            // (organization_id, slug) index rejected the insert as a duplicate
            // (typically a lost create race). Any other failure is rethrown
            // unchanged.
            var duplicateExists = await _dbContext.Workspaces
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.OrganizationId == workspace.OrganizationId
                        && existing.Slug == workspace.Slug,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return WorkspaceAddResult.DuplicateSlug;
            }

            throw;
        }
    }
}
