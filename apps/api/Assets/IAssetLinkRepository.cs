// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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

    /// <summary>
    /// HARD-DELETES the given link row, detaching one asset from one target (CORE-LIFE-007, the "Resource
    /// Lifecycle and Deletion" epic). Until now a link could be ADDED (CORE-AST-005) or removed only as a
    /// bulk CASCADE when its asset, target or workspace was deleted (the
    /// <see cref="RemoveByTargetAsync"/> / <see cref="RemoveByAssetAsync"/> cleanups of
    /// CORE-LIFE-003/004/005/006); this is the inverse of <see cref="AddAsync"/> for ONE link — a host
    /// unlinks an asset from a single content block or entity. The caller (the unlink command,
    /// <see cref="AssetLinkService.UnlinkAsync"/>) is responsible for having loaded the link through the
    /// tenant- AND workspace-scoped <see cref="FindByIdAsync"/> first (and confirmed it attaches the
    /// addressed asset), so a foreign tenant's or workspace's link is never reachable to remove even when
    /// its id is known (threat T5/T1); this method removes exactly the row it is handed. Removal touches
    /// only this one link row — the linked asset and the target content block / entity are BOTH untouched
    /// (it is the reverse of the per-link insert, not a cascade from the asset or the target).
    /// </summary>
    Task RemoveAsync(AssetLink assetLink, CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES every link attaching ANY asset to the given target (named by type + id) WITHIN the
    /// given organization and workspace, and returns the number removed (CORE-LIFE-003, the "Resource
    /// Lifecycle and Deletion" epic). This is the cleanup a target resource's deletion requires:
    /// <c>target_id</c> is a POLYMORPHIC reference (no database foreign key — a single column cannot
    /// foreign-key into content_blocks/entities), so the database can NOT cascade a link when its target
    /// resource is deleted; leaving the link behind would be a DANGLING link (and an asset could then
    /// claim audience access through a target that no longer exists; threat T4). So the application
    /// removes the links explicitly when the target is deleted (here, the
    /// <see cref="LiveCore.Api.Entities.EntityDeletionService"/> for an Entity target), documented in
    /// docs/adr/0012-resource-deletion-cascades-dependents.md. This removes only the LINK rows, never the
    /// linked assets (an asset can outlive a target it was linked to). The deletion is tenant-, workspace-
    /// AND target-scoped (the predicate leads with <c>organization_id</c> then <c>workspace_id</c>), so a
    /// foreign tenant's or workspace's links are NEVER removed even when the target id would otherwise be
    /// addressable (threat T5/T1). Removing the links of a target that has none is a no-op that returns 0.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or target id is empty.
    /// </exception>
    Task<int> RemoveByTargetAsync(
        Guid organizationId,
        Guid workspaceId,
        AssetLinkTargetType targetType,
        Guid targetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES every link attaching the given asset to ANY target WITHIN the given organization and
    /// workspace, and returns the number removed (CORE-LIFE-006, the "Resource Lifecycle and Deletion"
    /// epic). This is the cleanup the asset's OWN deletion requires — the inverse of
    /// <see cref="RemoveByTargetAsync"/> (which removes a target's links when the target is deleted). Unlike
    /// the polymorphic <c>target_id</c>, <c>asset_links.asset_id</c> IS a real foreign key into
    /// <c>assets(id)</c> configured <c>ON DELETE CASCADE</c>, so the database would itself remove these links
    /// when the asset row is deleted; the <see cref="AssetDeletionService"/> removes them EXPLICITLY first
    /// anyway so the cascade is deterministic, observable and identical across providers (and the story's
    /// "its links are removed" is a directly testable effect, not an implicit side effect), exactly as the
    /// scene deletion removes its FK-backed child content blocks explicitly
    /// (docs/adr/0012-resource-deletion-cascades-dependents.md step 2). The database cascade then remains as
    /// defence in depth. This removes only the LINK rows, never the linked target content blocks / entities
    /// (a target can outlive an asset it was linked to). The deletion is tenant-, workspace- AND asset-scoped
    /// (the predicate leads with <c>organization_id</c> then <c>workspace_id</c>), so a foreign tenant's or
    /// workspace's links are NEVER removed even when the asset id would otherwise be addressable (threat
    /// T5/T1). Removing the links of an asset that has none is a no-op that returns 0.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or asset id is empty.
    /// </exception>
    Task<int> RemoveByAssetAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid assetId,
        CancellationToken cancellationToken);
}
