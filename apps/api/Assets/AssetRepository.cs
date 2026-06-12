using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Assets;

/// <summary>
/// EF Core implementation of <see cref="IAssetRepository"/> (CORE-AST-001), backed by the <c>assets</c>
/// table mapped in <see cref="AssetConfiguration"/>.
/// </summary>
internal sealed class AssetRepository : IAssetRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public AssetRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<Asset?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored asset (ids are generated non-empty), so the lookup fails
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
            throw new ArgumentException("Asset id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the tenant column.
        // The lookup is exactly tenant- and workspace-scoped, so an asset under another organization or
        // workspace is never returned even when the surrogate id matches (threat T5/T1).
        return await _dbContext.Assets
            .FirstOrDefaultAsync(
                asset => asset.OrganizationId == organizationId
                    && asset.WorkspaceId == workspaceId
                    && asset.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Asset>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's assets, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The predicate leads with the tenant column and then matches the workspace, so the list is
        // exactly tenant- and workspace-scoped: another tenant's or another workspace's assets are never
        // returned even when their ids would otherwise be addressable (threat T5/T1; the organization
        // boundary is checked before the workspace boundary). The ordering is deterministic — sorted by
        // the surrogate id, which is time-ordered (UUIDv7), so the sequence is stable and repeatable.
        return await _dbContext.Assets
            .Where(asset => asset.OrganizationId == organizationId
                && asset.WorkspaceId == workspaceId)
            .OrderBy(asset => asset.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AssetAddResult> AddAsync(Asset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        _dbContext.Assets.Add(asset);

        // An asset has no uniqueness constraint to violate in this story, so there is no duplicate
        // outcome to translate here; a foreign-key violation (a non-existent workspace, tenant or
        // creating user) propagates as a DbUpdateException.
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return AssetAddResult.Added;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Asset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // The asset was loaded and mutated within this scope's change tracker (or is attached here); only
        // the mutable status, size/checksum and update timestamp change. The organization, workspace, id,
        // creator and storage coordinates are immutable on the aggregate, so an update can never move the
        // row to another tenant or workspace, nor make the asset public (threat T4/T5).
        _dbContext.Assets.Update(asset);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
