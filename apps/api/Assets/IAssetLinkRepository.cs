namespace LiveCore.Api.Assets;

/// <summary>
/// Persistence contract for the asset link aggregate (CORE-AST-005). The Assets module owns the
/// <c>asset_links</c> table; other modules access asset links only through this contract or the module's
/// application services (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Assets module owns "upload/download authorization").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the organization id and the
/// workspace id, and a link is only ever returned when it belongs to exactly that (organization,
/// workspace) pair. The organization boundary is checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so a link is never returned through a
/// foreign organization's id even when the workspace and ids are correct, and never through a foreign
/// workspace's id even when the organization and ids are correct. There is deliberately no lookup of a
/// link by id alone, no lookup that crosses tenants, and NO list-everything method, so one workspace's
/// link can never be read through another workspace's id and a link in one tenant can never be read
/// through another tenant's id (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken
/// object-level authorization).
///
/// A link is immutable, so there is no update path. This contract takes explicit ids; resolving the
/// "current" organization or workspace from a request is the tenant context resolver and the consuming
/// endpoint.
/// </summary>
public interface IAssetLinkRepository
{
    /// <summary>
    /// Finds the link with exactly the given id WITHIN the given organization and workspace, or
    /// <see langword="null"/> when no such link exists there. The organization and workspace both scope
    /// the lookup, so a link that exists under another organization's or workspace's id is never returned,
    /// even when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or link id is empty.
    /// </exception>
    Task<AssetLink?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every link attaching the given asset WITHIN the given organization and workspace, in
    /// deterministic (time-ordered surrogate id) order. This is the lookup the download authorization
    /// (<see cref="AssetDownloadPolicy"/>) uses to find an asset's targets and then ask the central
    /// Visibility engine whether any is visible to the audience. The list is tenant-, workspace- AND
    /// asset-scoped: the predicate leads with <c>organization_id</c>, then matches <c>workspace_id</c> and
    /// <c>asset_id</c>, so a foreign tenant's or workspace's links are NEVER returned even when their ids
    /// would otherwise be addressable (threat T5/T1; the organization boundary is checked before the
    /// workspace boundary). An empty list is returned for an asset with no links. This is NOT a
    /// list-everything method.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or asset id is empty.
    /// </exception>
    Task<IReadOnlyList<AssetLink>> ListByAssetAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid assetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new link. The per-workspace natural key (<c>workspace_id</c>, <c>asset_id</c>,
    /// <c>target_type</c>, <c>target_id</c>) is unique, so the same asset cannot be linked to the same
    /// target twice; a duplicate insert is reported as <see cref="AssetLinkAddResult.Duplicate"/>.
    /// Foreign-key violations (a non-existent asset, workspace, tenant or creating user) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<AssetLinkAddResult> AddAsync(AssetLink assetLink, CancellationToken cancellationToken);
}
