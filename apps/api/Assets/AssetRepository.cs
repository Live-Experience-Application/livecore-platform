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
    public async Task<Asset?> FindByIdInOrganizationAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored asset (ids are generated non-empty), so the lookup fails
        // fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Asset id must not be empty.", nameof(id));
        }

        // The predicate LEADS with the tenant column, so the lookup is exactly tenant-scoped: an asset
        // under another organization is never returned even when the surrogate id matches (threat T5/T1).
        // The workspace is not part of the predicate because the by-asset-id download route does not know
        // it up front; it is read off the returned row (Asset.WorkspaceId) so the caller can authorize
        // against workspace membership AFTER the tenant boundary has been enforced (mirrors
        // SceneRepository.FindByIdInOrganizationAsync).
        return await _dbContext.Assets
            .FirstOrDefaultAsync(
                asset => asset.OrganizationId == organizationId
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<Asset>> ListExpiredPendingAsync(
        DateTimeOffset createdBefore,
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "The batch size must be positive.");
        }

        // SYSTEM maintenance sweep (CORE-AST-006): the predicate filters on status, so an Available
        // (confirmed) asset is never returned even if it is old — confirmed content is never reclaimed. It
        // deliberately spans all tenants/workspaces because the cleanup job is a system job, not a tenant
        // actor; it returns only pending (unconfirmed, contentless) rows so it serves no content and grants
        // no access (see the interface remarks; threats T4/T5).
        //
        // The order is the time-ordered surrogate id (UUIDv7, chronological and provider-independent —
        // SQLite cannot ORDER BY or compare a DateTimeOffset, matching the other repositories' ordering
        // convention), so the OLDEST pending assets sort first. The bounded batch (Take(maxCount)) therefore
        // surfaces the most-expired assets first, and the creation-time threshold is applied AFTER
        // materialization: a still-fresh pending asset that slips into the batch is filtered out here and
        // never reclaimed (an upload may still be in flight). The id and creation time are both stamped when
        // the upload intent is registered, so id order tracks creation order; over repeated sweeps every
        // expired asset is covered.
        var oldestPending = await _dbContext.Assets
            .Where(asset => asset.Status == AssetStatus.Pending)
            .OrderBy(asset => asset.Id)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return oldestPending
            .Where(asset => asset.CreatedAt < createdBefore)
            .ToList();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Asset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // Remove the metadata row. The asset_links.asset_id foreign key cascades on delete, so any links to
        // this asset are removed with it and no dangling join remains. The cleanup job has already deleted
        // the storage object before calling this, so the row is never removed while its object remains.
        _dbContext.Assets.Remove(asset);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
