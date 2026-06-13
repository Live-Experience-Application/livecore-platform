using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Assets;

/// <summary>
/// The host-initiated asset deletion command of the Assets module (CORE-LIFE-006, the "Resource Lifecycle
/// and Deletion" epic). A host can delete an <see cref="Asset"/>; this service removes the asset together
/// with its <c>asset_links</c> and the underlying storage object, and appends an append-only audit record —
/// the story's "its links are removed and the underlying storage object is deleted via IAssetStorage;
/// authorized and tenant-scoped" criteria. Until this story an asset could be created (CORE-AST-003), linked
/// (CORE-AST-005) and downloaded (CORE-AST-004) but never removed by a host: only the background cleanup job
/// (CORE-AST-006) could reclaim still-<see cref="AssetStatus.Pending"/>, never-confirmed intents, so an
/// <see cref="AssetStatus.Available"/> asset could not be deleted at all. This adds the host-initiated path.
///
/// CASCADE, NOT BLOCK (docs/adr/0012-resource-deletion-cascades-dependents.md). The deletion removes the
/// asset together with its dependents rather than refusing while any remain. An asset's dependents are
/// narrower than a scene's or an entity's:
/// <list type="bullet">
///   <item><b>Its <c>asset_links</c></b> reference the asset through <c>asset_links.asset_id</c>, which IS a
///   real foreign key into <c>assets(id)</c> configured <c>ON DELETE CASCADE</c> — so the database would
///   itself cascade them when the asset row is removed. This service removes them EXPLICITLY first anyway
///   (<see cref="IAssetLinkRepository.RemoveByAssetAsync"/>) so the cascade is deterministic, observable and
///   identical across providers, and the story's "its links are removed" is a directly testable effect; the
///   database cascade then remains as defence in depth (ADR 0012 step 2). Only the LINK rows are removed —
///   the linked content blocks / entities are untouched.</item>
///   <item><b>Its underlying storage object</b> is deleted through the <see cref="IAssetStorage"/> adapter
///   port (<see cref="IAssetStorage.DeleteObjectAsync"/>, the same server-side delete the cleanup job uses) —
///   the binary content lives in private object storage, never in PostgreSQL, so the metadata row's removal
///   does not by itself remove the object. Deleting it only ever REMOVES access; no signed URL is produced
///   and no bytes are served, so the private-by-default posture cannot be weakened (threat T4 "Asset leak").</item>
/// </list>
/// An asset is NOT a visibility resource (only <see cref="LiveCore.Api.Visibility.VisibilityResourceType.Scene"/>,
/// <c>ContentBlock</c> and <c>Entity</c> are) and is NEVER an asset-link <i>target</i> (only content blocks and
/// entities are), so — unlike the scene/entity/content-block deletions — there are no <c>visibility_rules</c>
/// governing the asset and no target-side links to clean up.
///
/// ORDER: STORAGE OBJECT BEFORE THE ROW (the story's "delete the storage object then the row ... so a storage
/// failure leaves no dangling row, mirroring the upload-intent ordering"). The links are removed, then the
/// storage object is deleted, then the metadata row is deleted — all inside ONE transaction. Deleting the
/// object before the row means the row is never removed while its object remains, so a deletion never leaves an
/// orphaned object behind (the symmetric guarantee to the upload intent minting its signed URL BEFORE
/// persisting the row, and the cleanup job deleting the object BEFORE the row). The dangerous state — a metadata
/// row deleted while its object survives, which the cleanup job could never reclaim because it only touches
/// pending assets — therefore cannot occur.
///
/// FAIL-CLOSED STORAGE. The storage object delete runs INSIDE the transaction and BEFORE the row delete, so
/// when no object storage is configured the fail-closed <see cref="UnconfiguredAssetStorage"/> throws
/// <see cref="AssetStorageNotConfiguredException"/>, the transaction rolls back having changed nothing (the
/// links are restored, no row is deleted, no audit record is written), and the exception propagates to the
/// endpoint, which maps it to <c>503</c> — exactly as the upload-intent flow fails closed when storage is off
/// (the private-by-default posture holds even unconfigured; threat T4). The object key is never logged or
/// returned (threats T4/T7).
///
/// SCOPE / ISOLATION. The service takes the already-resolved tenant and workspace and an asset id, and loads
/// the asset through the tenant- AND workspace-scoped
/// <see cref="IAssetRepository.FindByIdAsync(Guid, Guid, Guid, CancellationToken)"/> FIRST: an asset in another
/// workspace, or in a workspace owned by another tenant, is never found and so never deleted, even when its
/// surrogate id is known (threats T1/T5). The link removal is likewise tenant- and workspace-scoped, so the
/// cascade can never reach across a workspace or tenant boundary. The endpoint
/// (<see cref="AssetEndpoints"/>) performs the authentication, tenant resolution and role authorization before
/// calling in; this service is the authorized command's effect and performs no authorization itself (exactly
/// like <see cref="AssetUploadIntentService"/>).
///
/// ATOMICITY. The link removal, the storage object delete, the asset-row delete and the audit append run inside
/// a single explicit <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/> over the shared
/// <see cref="LiveCoreDbContext"/> (the same scoped context every injected repository writes through), so either
/// the whole deletion commits together or a failure rolls the lot back leaving everything intact and no audit
/// record written. Auditing is inside the transaction on purpose: a deletion is recorded as a fact or it does
/// not happen.
/// </summary>
internal sealed class AssetDeletionService
{
    private readonly LiveCoreDbContext _dbContext;
    private readonly IAssetRepository _assets;
    private readonly IAssetLinkRepository _assetLinks;
    private readonly IAssetStorage _storage;
    private readonly IAuditLogRepository _audit;

    public AssetDeletionService(
        LiveCoreDbContext dbContext,
        IAssetRepository assets,
        IAssetLinkRepository assetLinks,
        IAssetStorage storage,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(assetLinks);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(audit);
        _dbContext = dbContext;
        _assets = assets;
        _assetLinks = assetLinks;
        _storage = storage;
        _audit = audit;
    }

    /// <summary>
    /// Deletes the asset identified by <paramref name="assetId"/> within the given tenant and workspace,
    /// removing its asset links and its underlying storage object and appending an append-only
    /// <see cref="AuditAction.AssetDeleted"/> record — all atomically. Returns
    /// <see cref="AssetDeletionResult.Deleted"/> when the asset existed and was removed, or
    /// <see cref="AssetDeletionResult.NotFound"/> when no asset with that id exists in the resolved tenant and
    /// workspace (an unknown id, or one belonging to another workspace/tenant — nothing is changed).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the asset belongs to.</param>
    /// <param name="assetId">The surrogate id of the asset to delete.</param>
    /// <param name="actorUserProfileId">
    /// The authenticated host who executed the deletion — the audited actor. Supplied by the endpoint from the
    /// resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="now">The command timestamp (the audit record's time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, asset id or actor id is empty.
    /// </exception>
    /// <exception cref="AssetStorageNotConfiguredException">
    /// No object storage is configured; nothing is deleted (fail-closed) and the transaction rolls back.
    /// </exception>
    public async Task<AssetDeletionResult> DeleteAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid assetId,
        Guid actorUserProfileId,
        DateTimeOffset now,
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

        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Asset id must not be empty.", nameof(assetId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // Load the asset WITHIN the resolved tenant AND workspace. FindByIdAsync leads with organization_id
        // then workspace_id then the asset id, so an asset in another workspace or tenant is never returned
        // even when the surrogate id matches; an unknown id is simply null. Deleting a non-existent asset
        // reveals nothing and changes nothing — a safe NotFound, and no transaction is opened (and no storage
        // call made) for it.
        var asset = await _assets
            .FindByIdAsync(organizationId, workspaceId, assetId, cancellationToken)
            .ConfigureAwait(false);
        if (asset is null)
        {
            return AssetDeletionResult.NotFound;
        }

        // ONE unit of work: the link removal, the storage object delete, the asset-row delete and the audit
        // append commit together or roll back together, so a deletion is applied whole or not at all. Every
        // injected repository writes through this same scoped DbContext, so each repository SaveChanges enrols
        // in this transaction.
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // 1) Remove the asset's links (the story's "its links are removed"). asset_links.asset_id is an
        // ON DELETE CASCADE foreign key, so the row delete below would also remove these; removing them
        // explicitly first makes the cascade deterministic and observable (ADR 0012 step 2). Only the link
        // rows go — the linked content blocks / entities are untouched.
        await _assetLinks
            .RemoveByAssetAsync(organizationId, workspaceId, assetId, cancellationToken)
            .ConfigureAwait(false);

        // 2) Delete the underlying storage object BEFORE the metadata row (the story's ordering, mirroring the
        // upload intent minting its URL before persisting the row). DeleteObjectAsync is idempotent — a pending
        // asset whose client never uploaded has no object, and the adapter treats that as success. With no
        // storage adapter configured the fail-closed UnconfiguredAssetStorage throws
        // AssetStorageNotConfiguredException here, so the `await using` transaction rolls back having changed
        // nothing (the link removal is undone, no row is deleted, no audit written) and the exception
        // propagates to the endpoint, which maps it to 503 (threat T4). The object key is never logged.
        await _storage.DeleteObjectAsync(asset, cancellationToken).ConfigureAwait(false);

        // 3) Delete the metadata row (its object is now gone, so the row never outlives its object). The
        // asset_links FK would also cascade the (already-removed) links, so the explicit removal above leaves
        // the cascade with nothing to do — defence in depth.
        await _assets.DeleteAsync(asset, cancellationToken).ConfigureAwait(false);

        // 4) AUDIT (the story's "authorized" deletion, the consistent application of ADR 0012: "audit the
        // deletion"): a deletion is security-relevant, so append an append-only record capturing the actor
        // (the host who deleted), the deleted asset and the tenant/workspace. The audit references are recorded
        // facts, not foreign keys, so the row outlives the now-deleted asset (threats T1/T5/T7). The cascaded
        // links and the removed storage object are consequences of this one action and are not separately
        // audited. The storage coordinates are never recorded (only the asset id; threats T4/T7).
        var entry = AuditLogEntry.ForAssetDeletion(
            organizationId,
            workspaceId,
            actorUserProfileId,
            nameof(Asset),
            assetId,
            now);
        await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return AssetDeletionResult.Deleted;
    }
}
