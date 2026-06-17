namespace LiveCore.Api.Assets;

/// <summary>
/// Persistence contract for the asset metadata aggregate (CORE-AST-001). The Assets module owns the
/// <c>assets</c> table; other modules access asset metadata only through this contract or the module's
/// application services (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Assets module owns "asset metadata").
///
/// Every CALLER-FACING lookup is explicitly scoped by BOTH boundaries: the caller passes the organization
/// id and the workspace id, and an asset is only ever returned when it belongs to exactly that
/// (organization, workspace) pair. The organization boundary is checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so an asset is never returned through a
/// foreign organization's id even when the workspace and ids are correct, and never through a foreign
/// workspace's id even when the organization and ids are correct. There is deliberately no caller-facing
/// lookup of an asset by id alone, no caller-facing lookup that crosses tenants, and NO list-everything
/// read method, so one workspace's asset can never be read through another workspace's id and an asset in
/// one tenant can never be read through another tenant's id (threat T5 in docs/07_SECURITY_THREAT_MODEL.md;
/// threat T1 broken object-level authorization). Returning the metadata row is NOT the same as granting
/// access to the stored object: the object is reached only through an authorized, short-lived signed URL
/// after a permission check (the signed download flow is CORE-AST-004; threat T4 "Asset leak").
///
/// The single exception is the background MAINTENANCE sweep <see cref="ListExpiredPendingAsync"/>
/// (CORE-AST-006): it deliberately spans all tenants because the cleanup job is a SYSTEM job, not a tenant
/// actor on behalf of any caller. It does not implicate threat T5 (a tenant reading another tenant's data
/// through the API) because it is never reached from a request, returns ONLY still-pending — therefore
/// unconfirmed and contentless — assets, and exists only so those abandoned upload intents can be deleted.
/// It serves no bytes and grants no access.
/// </summary>
public interface IAssetRepository
{
    /// <summary>
    /// Finds the asset with exactly the given id WITHIN the given organization and workspace, or
    /// <see langword="null"/> when no such asset exists there. The organization and workspace both scope
    /// the lookup, so an asset that exists under another organization's or workspace's id is never
    /// returned, even when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or asset id is empty. An empty id can never address a stored
    /// asset, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<Asset?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the asset with exactly the given id WITHIN the given organization (tenant) ALONE — without a
    /// workspace boundary — or <see langword="null"/> when no such asset exists in that organization
    /// (CORE-AST-004).
    ///
    /// This lookup exists because the signed-download route
    /// (<c>GET /api/v1/assets/{assetId}/download-url</c>) addresses an asset by id within a query-supplied
    /// organization only: the route path carries no workspace, so the workspace boundary is not known up
    /// front. The asset's own <see cref="Asset.WorkspaceId"/> is DISCOVERED from the returned row AFTER the
    /// tenant boundary has been enforced, and the caller then authorizes against WORKSPACE membership in
    /// exactly that workspace, mirroring
    /// <see cref="Scenes.ISceneRepository.FindByIdInOrganizationAsync"/> and
    /// <see cref="Sessions.ISessionRepository.FindByIdInOrganizationAsync"/>. The lookup is still
    /// tenant-safe: the predicate leads with <c>organization_id</c>, so an asset in another organization is
    /// NEVER returned even when the surrogate id matches, and the surrogate id alone never crosses the
    /// tenant boundary (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
    /// authorization; docs/06_AUTHORIZATION_MATRIX.md: the organization boundary is checked before the
    /// workspace boundary). Returning the metadata row is NOT the same as granting access to the stored
    /// object: the object is reached only through an authorized, short-lived signed URL minted AFTER the
    /// permission check passes (threat T4 "Asset leak").
    ///
    /// The two-boundary <see cref="FindByIdAsync(Guid, Guid, Guid, CancellationToken)"/> REMAINS the
    /// workspace-scoped lookup and is the right choice whenever the workspace is already known from the
    /// request. This org-only lookup is used only by the by-asset-id download route, where the workspace is
    /// not in the path and must be discovered from the row inside the tenant boundary.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or asset id is empty. An empty id can never address a stored asset, so the
    /// lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<Asset?> FindByIdInOrganizationAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every asset of the given workspace (owned by the given organization) in a deterministic
    /// order — sorted by the surrogate id, which is time-ordered (UUIDv7), so the sequence is stable and
    /// repeatable. The list is tenant- AND workspace-scoped: the predicate leads with
    /// <c>organization_id</c> and then matches <c>workspace_id</c>, so a foreign tenant's or a foreign
    /// workspace's assets are NEVER returned even when their ids would otherwise be addressable (threat
    /// T5/T1; the organization boundary is checked before the workspace boundary). An empty list is
    /// returned for a workspace that has no assets; the lookup never crosses the tenant or workspace
    /// boundary to borrow another workspace's assets. This is NOT a list-everything method.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty. An empty id can never address a stored workspace's
    /// assets, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<IReadOnlyList<Asset>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new asset. An asset has no natural key in this story (it is identified only by its
    /// surrogate id), so there is no uniqueness outcome to report; the result is always
    /// <see cref="AssetAddResult.Added"/> on success. Foreign-key violations (a non-existent workspace,
    /// tenant or creating user) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<AssetAddResult> AddAsync(Asset asset, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to an asset previously loaded through this repository — in particular the
    /// confirm transition that marks the asset available and records its size and checksum. The
    /// organization, workspace, id, creator and storage coordinates of an asset are immutable
    /// (<see cref="Asset"/>), so an update only ever changes the status, the size/checksum and the update
    /// timestamp; it can never move the asset to another tenant or workspace, nor make it public (threat
    /// T4/T5). The caller is responsible for having loaded the asset through a tenant-scoped lookup.
    /// </summary>
    Task UpdateAsync(Asset asset, CancellationToken cancellationToken);

    /// <summary>
    /// Lists up to <paramref name="maxCount"/> assets that are still <see cref="AssetStatus.Pending"/> and
    /// were created strictly before <paramref name="createdBefore"/>, oldest-first. (The batch is taken in
    /// time-ordered surrogate-id order — a chronological proxy that is provider-portable, since the SQLite
    /// test provider cannot order or compare a <see cref="DateTimeOffset"/> — and the creation-time threshold
    /// is applied to that bounded batch, so the oldest pending assets are surfaced first and a still-fresh
    /// pending asset is never returned.) These are the EXPIRED, UNCONFIRMED upload intents the background
    /// cleanup job reclaims (CORE-AST-006): an upload intent was
    /// registered (the asset went <see cref="AssetStatus.Pending"/>) but its upload was never confirmed
    /// within the deployment's grace window, so the metadata row — and any orphaned object — is abandoned.
    ///
    /// This is a SYSTEM maintenance sweep, NOT a caller-facing read: it spans all tenants and workspaces
    /// because the cleanup job runs on behalf of the deployment, not on behalf of any tenant. It is the one
    /// deliberate exception to this repository's tenant-scoped read rule, and it is safe because it returns
    /// ONLY pending (hence unconfirmed and contentless) assets, exclusively so they can be deleted: it never
    /// returns an <see cref="AssetStatus.Available"/> asset (confirmed content is never reclaimed), serves no
    /// bytes and grants no access (threats T4/T5 are about reaching another tenant's CONTENT through the API,
    /// which this never does). A non-positive <paramref name="maxCount"/> is rejected so a sweep always has
    /// a bounded batch size.
    /// </summary>
    /// <param name="createdBefore">The exclusive upper bound on an asset's creation time (UTC).</param>
    /// <param name="maxCount">The maximum number of assets to return (a positive batch size).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCount"/> is not positive.</exception>
    Task<IReadOnlyList<Asset>> ListExpiredPendingAsync(
        DateTimeOffset createdBefore,
        int maxCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes an asset metadata row previously loaded through this repository — used by the cleanup job
    /// (CORE-AST-006) once the asset's storage object has been deleted. The asset's links cascade away with
    /// it (the <c>asset_links.asset_id</c> foreign key cascades on delete), so the row leaves behind no
    /// dangling join. Deleting the metadata row does not, by itself, delete the stored object; the cleanup
    /// job deletes the object FIRST through <see cref="IAssetStorage.DeleteObjectAsync(Asset, System.Threading.CancellationToken)"/> and only then
    /// removes the row, so a row is never deleted while its object remains (no orphaned object).
    /// </summary>
    Task DeleteAsync(Asset asset, CancellationToken cancellationToken);
}
