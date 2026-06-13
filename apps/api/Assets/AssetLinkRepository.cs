using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Assets;

/// <summary>
/// EF Core implementation of <see cref="IAssetLinkRepository"/> (CORE-AST-005), backed by the
/// <c>asset_links</c> table mapped in <see cref="AssetLinkConfiguration"/>.
/// </summary>
internal sealed class AssetLinkRepository : IAssetLinkRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public AssetLinkRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<AssetLink?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored link (ids are generated non-empty), so the lookup fails
        // fast instead of returning an arbitrary row.
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
            throw new ArgumentException("Asset link id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the tenant column.
        // The lookup is exactly tenant- and workspace-scoped, so a link under another organization or
        // workspace is never returned even when the surrogate id matches (threat T5/T1).
        return await _dbContext.AssetLinks
            .FirstOrDefaultAsync(
                link => link.OrganizationId == organizationId
                    && link.WorkspaceId == workspaceId
                    && link.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetLink>> ListByAssetAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored asset's links, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Asset id must not be empty.", nameof(assetId));
        }

        // The predicate leads with the tenant column, then matches the workspace and the asset, so the
        // list is exactly tenant-, workspace- and asset-scoped: another tenant's or another workspace's
        // links are never returned even when their ids would otherwise be addressable (threat T5/T1; the
        // organization boundary is checked before the workspace boundary). The ordering is
        // deterministic — sorted by the time-ordered surrogate id.
        return await _dbContext.AssetLinks
            .Where(link => link.OrganizationId == organizationId
                && link.WorkspaceId == workspaceId
                && link.AssetId == assetId)
            .OrderBy(link => link.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AssetLinkAddResult> AddAsync(AssetLink assetLink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assetLink);

        _dbContext.AssetLinks.Add(assetLink);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AssetLinkAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on
            // the same scope.
            _dbContext.Entry(assetLink).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if the same asset is already linked to the same
            // target in the same workspace, the unique (workspace_id, asset_id, target_type, target_id)
            // index rejected the insert as a duplicate (typically a lost create race). Any other failure
            // (for example a foreign-key violation for a non-existent asset, workspace, tenant or creating
            // user) is rethrown unchanged.
            var duplicateExists = await _dbContext.AssetLinks
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.WorkspaceId == assetLink.WorkspaceId
                        && existing.AssetId == assetLink.AssetId
                        && existing.TargetType == assetLink.TargetType
                        && existing.TargetId == assetLink.TargetId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return AssetLinkAddResult.Duplicate;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> RemoveByTargetAsync(
        Guid organizationId,
        Guid workspaceId,
        AssetLinkTargetType targetType,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored target's links, so the deletion fails fast instead of
        // matching an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (targetId == Guid.Empty)
        {
            throw new ArgumentException("Target id must not be empty.", nameof(targetId));
        }

        // The predicate leads with the tenant column, then matches the workspace, the target type and the
        // target id, so it removes ALL links attaching any asset to the target while staying exactly
        // tenant-, workspace- and target-scoped: another tenant's or workspace's links are never removed
        // even when the target id would otherwise be addressable (threat T5/T1). Only the LINK rows are
        // removed; the linked assets are untouched. Load-then-RemoveRange (the codebase's add/remove +
        // SaveChanges convention) so the removed rows participate in any ambient transaction the caller
        // controls.
        var links = await _dbContext.AssetLinks
            .Where(link => link.OrganizationId == organizationId
                && link.WorkspaceId == workspaceId
                && link.TargetType == targetType
                && link.TargetId == targetId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (links.Count == 0)
        {
            return 0;
        }

        _dbContext.AssetLinks.RemoveRange(links);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return links.Count;
    }
}
