using LiveCore.Api.Content;
using LiveCore.Api.Entities;

namespace LiveCore.Api.Assets;

/// <summary>
/// The asset-link command of the Assets module (CORE-AST-005) — the "asset can be linked to ContentBlock
/// or Entity" step of the asset lifecycle (docs/12_STORAGE_ASSETS.md). It attaches an already-resolved,
/// tenant- and workspace-scoped <see cref="Asset"/> to a host-prepared resource (a content block or an
/// entity) by creating an <see cref="AssetLink"/>. It is a plain command service over
/// <see cref="IAssetLinkRepository"/>, <see cref="IContentBlockRepository"/> and
/// <see cref="IEntityRepository"/> taking explicit inputs, exactly like
/// <see cref="AssetUploadIntentService"/> and <see cref="LiveCore.Api.Visibility.RevealService"/>: the
/// calling endpoint resolves the tenant, loads the asset, discovers its workspace and authorizes the
/// caller's role BEFORE invoking it. This service performs no authorization itself.
///
/// SAME-WORKSPACE COUPLING ENFORCED HERE. The <c>target_id</c> on a link is a polymorphic reference (not a
/// database foreign key), so this command is the place the same-workspace coupling is enforced (the
/// documented carry-over for <c>visibility_rules.resource_id</c>, <c>ContentBlock.SceneId</c> and
/// <c>Entity.EntityTypeId</c>): it resolves the target through the WORKSPACE-SCOPED repository lookup
/// (<c>FindByIdAsync(org, ws, targetId)</c>) of the asset's OWN organization and workspace BEFORE creating
/// the link. A target that does not exist in the asset's workspace — including a content block or entity
/// in another workspace or tenant — is reported as <see cref="AssetLinkOutcome.TargetNotFound"/> and NO
/// link is created, so an asset can never be linked to a foreign-workspace or foreign-tenant resource
/// (threats T5/T1 in docs/07_SECURITY_THREAT_MODEL.md). The lookup leads with <c>organization_id</c>, so
/// the tenant boundary is enforced before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles).
///
/// PRIVATE BY DEFAULT. Creating a link never makes an asset public: it only records that the asset is
/// attached to a resource whose audience visibility the central Visibility engine then governs (the
/// download authorization, <see cref="AssetDownloadPolicy"/>). The asset stays reachable only through an
/// authorized, short-lived signed URL (the epic acceptance criterion; threat T4 "Asset leak"). The
/// per-workspace unique key makes a repeat link a reported <see cref="AssetLinkOutcome.AlreadyLinked"/>,
/// not a duplicate.
///
/// REMOVAL (CORE-LIFE-007, the "Resource Lifecycle and Deletion" epic). <see cref="UnlinkAsync"/> is the
/// inverse command, living in the same cohesive command service (as the Visibility module's reveal/hide
/// share one service): a host UNLINKS an asset from a single content block or entity. It resolves the link
/// through the same workspace-scoped lookup of the asset's OWN organization and workspace and requires it to
/// attach the addressed asset, so a link in another tenant or workspace — or one attaching a different
/// asset — is never reachable to remove even when its id is known (threats T5/T1). Removal only detaches:
/// the asset and the linked target are BOTH unaffected (the inverse of the per-link insert, never a cascade
/// onto the asset or target). It performs no authorization itself either.
/// </summary>
internal sealed class AssetLinkService
{
    private readonly IAssetLinkRepository _links;
    private readonly IContentBlockRepository _contentBlocks;
    private readonly IEntityRepository _entities;

    public AssetLinkService(
        IAssetLinkRepository links,
        IContentBlockRepository contentBlocks,
        IEntityRepository entities)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(contentBlocks);
        ArgumentNullException.ThrowIfNull(entities);
        _links = links;
        _contentBlocks = contentBlocks;
        _entities = entities;
    }

    /// <summary>
    /// Links the given asset to the given target (a content block or entity), after verifying the target
    /// exists in the asset's OWN organization and workspace. Returns
    /// <see cref="AssetLinkOutcome.TargetNotFound"/> when the target is not in the asset's workspace,
    /// <see cref="AssetLinkOutcome.AlreadyLinked"/> when the same link already exists, or
    /// <see cref="AssetLinkOutcome.Linked"/> with the created link on success.
    /// </summary>
    /// <param name="asset">The already-resolved, tenant- and workspace-scoped asset to link.</param>
    /// <param name="targetType">The kind of resource to link the asset to.</param>
    /// <param name="targetId">The surrogate id of the target resource (in the asset's workspace).</param>
    /// <param name="createdByUserProfileId">The authenticated host creating the link (the recorded creator).</param>
    /// <param name="now">The command timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">The asset is null.</exception>
    /// <exception cref="ArgumentException">The target id or creator id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The target type is not defined.</exception>
    public async Task<AssetLinkResult> LinkAsync(
        Asset asset,
        AssetLinkTargetType targetType,
        Guid targetId,
        Guid createdByUserProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (!AssetLink.IsValidTargetType(targetType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetType),
                targetType,
                "Target type is not a defined asset link target type.");
        }

        if (targetId == Guid.Empty)
        {
            throw new ArgumentException("Target id must not be empty.", nameof(targetId));
        }

        if (createdByUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Created-by user profile id must not be empty.", nameof(createdByUserProfileId));
        }

        // Same-workspace coupling: resolve the target through the workspace-scoped lookup of the asset's
        // OWN organization and workspace. A target in another workspace or tenant is never found, so an
        // asset can never be linked to a foreign resource (threats T5/T1). Returning the row only confirms
        // existence and the same-workspace coupling; it grants no access.
        var targetExists = await TargetExistsInWorkspaceAsync(asset, targetType, targetId, cancellationToken)
            .ConfigureAwait(false);
        if (!targetExists)
        {
            return AssetLinkResult.TargetNotFound();
        }

        var link = AssetLink.Create(
            asset.OrganizationId,
            asset.WorkspaceId,
            asset.Id,
            targetType,
            targetId,
            createdByUserProfileId,
            now);

        var added = await _links.AddAsync(link, cancellationToken).ConfigureAwait(false);
        return added == AssetLinkAddResult.Duplicate
            ? AssetLinkResult.AlreadyLinked()
            : AssetLinkResult.Created(link);
    }

    /// <summary>
    /// Removes the link with the given id that attaches the given asset to a target (CORE-LIFE-007), the
    /// inverse of <see cref="LinkAsync"/>: a host unlinks an asset from one content block or entity. The
    /// link is resolved through the WORKSPACE-SCOPED lookup of the asset's OWN organization and workspace
    /// and must attach exactly the addressed asset, so a link in another tenant or workspace, or a link that
    /// attaches a DIFFERENT asset, is never reachable to remove even when its id is known — it is reported
    /// as <see cref="AssetUnlinkResult.NotFound"/> and nothing is changed (threats T5/T1). On success the
    /// one link row is hard-deleted and <see cref="AssetUnlinkResult.Removed"/> is returned; the asset and
    /// the linked target are BOTH untouched (removing a link only detaches; it never deletes the asset or
    /// the target). The calling endpoint resolves the tenant, loads the asset, discovers its workspace and
    /// authorizes the caller's role BEFORE invoking this; the command performs no authorization itself.
    /// </summary>
    /// <param name="asset">The already-resolved, tenant- and workspace-scoped asset whose link to remove.</param>
    /// <param name="linkId">The surrogate id of the link to remove (must attach <paramref name="asset"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">The asset is null.</exception>
    /// <exception cref="ArgumentException">The link id is empty.</exception>
    public async Task<AssetUnlinkResult> UnlinkAsync(
        Asset asset,
        Guid linkId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (linkId == Guid.Empty)
        {
            throw new ArgumentException("Link id must not be empty.", nameof(linkId));
        }

        // Resolve the link through the workspace-scoped lookup of the asset's OWN organization and workspace
        // (the org boundary leads), so a link in another tenant or workspace is never reachable even when
        // its id is known (threats T5/T1).
        var link = await _links
            .FindByIdAsync(asset.OrganizationId, asset.WorkspaceId, linkId, cancellationToken)
            .ConfigureAwait(false);

        // The link must attach exactly the addressed asset. A link id that resolves in the asset's workspace
        // but attaches a DIFFERENT asset is not addressable through this asset's route, so it is hidden as
        // not found — the same workspace-scoped existence rule, applied to the asset/link pairing (threats
        // T1/T5).
        if (link is null || !link.LinksAsset(asset.Id))
        {
            return AssetUnlinkResult.NotFound;
        }

        await _links.RemoveAsync(link, cancellationToken).ConfigureAwait(false);
        return AssetUnlinkResult.Removed;
    }

    /// <summary>
    /// Whether the given target resource exists in the asset's OWN organization and workspace, resolved
    /// through the workspace-scoped repository for its kind. The lookup leads with the organization id,
    /// so a target in another tenant or workspace is never found (threats T5/T1).
    /// </summary>
    private async Task<bool> TargetExistsInWorkspaceAsync(
        Asset asset,
        AssetLinkTargetType targetType,
        Guid targetId,
        CancellationToken cancellationToken)
        => targetType switch
        {
            AssetLinkTargetType.ContentBlock => await _contentBlocks
                .FindByIdAsync(asset.OrganizationId, asset.WorkspaceId, targetId, cancellationToken)
                .ConfigureAwait(false) is not null,
            AssetLinkTargetType.Entity => await _entities
                .FindByIdAsync(asset.OrganizationId, asset.WorkspaceId, targetId, cancellationToken)
                .ConfigureAwait(false) is not null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(targetType),
                targetType,
                "Target type is not a defined asset link target type."),
        };
}
