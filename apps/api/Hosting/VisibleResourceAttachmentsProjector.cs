// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Composition-root ADAPTER fulfilling the Visibility module's
/// <see cref="IVisibleResourceAttachmentsProjector"/> port (CORE-ALC-002) by resolving each visible
/// resource's AUDIENCE-SAFE attachments through the Assets module's existing <c>AssetLink</c> join and asset
/// metadata. The participant-visible feed needs, per item, the list of assets attached to the resource so an
/// audience surface can enumerate the attachments of content the participant has been shown and then request
/// the authorized per-asset download (the vertical adopter gap ARC-GAP-009). But the Visibility module (THE
/// central security module) is, by the enforced module dependency graph (CORE-ARCH-001;
/// docs/05_MODULE_CONTRACTS.md), NOT allowed to reference the Assets module (the Assets module references
/// Visibility, not the reverse). This adapter lives in the shared-kernel composition root
/// (<c>LiveCore.Api.Hosting</c>, which references every module by design), so it can resolve the attachments
/// through the Assets module's repositories without adding a forbidden Visibility -&gt; Assets edge — the
/// same port-and-adapter shape <see cref="VisibleResourceAudienceProjector"/> (CORE-APROJ-002) uses.
///
/// REUSES THE EXISTING ASSET-LINK MODEL — VISIBILITY IS NEVER RE-DERIVED HERE. The attachments are the
/// assets LINKED to the resource (the <c>AssetLink</c> aggregate, CORE-AST-005, asset -&gt;
/// ContentBlock/Entity). This adapter is invoked ONLY for a resource the central
/// <see cref="VisibilityPolicy"/> has ALREADY decided the participant may see (the feed item is built only
/// for a visible resource), so each linked asset INHERITS the resource's audience visibility — a hidden
/// resource's attachments are never enumerated (threat T2 in docs/07_SECURITY_THREAT_MODEL.md). This adapter
/// performs NO visibility computation of its own.
///
/// AUDIENCE-SAFE BY CONSTRUCTION. Each entry carries only the asset's surrogate id, its audience-safe name
/// (null today — Core stores no host-facing asset filename, and the storage coordinates are host-only;
/// threats T4/T7) and its content type (the same non-sensitive metadata the signed download-url response
/// already discloses to an authorized audience caller). The host-only storage coordinates
/// (provider/bucket/object key), checksum, size and creator are NEVER projected. Listing an attachment never
/// grants access to its bytes: the download still goes through the server-side <c>getDownloadUrl</c>
/// authorization check (defence in depth).
///
/// FAIL-SOFT, TENANT- AND WORKSPACE-SCOPED. A <see cref="VisibilityResourceType.Scene"/> is not an
/// asset-link target (only a content block or entity is), and an undefined resource type matches no target,
/// so both resolve to an EMPTY attachments list without a query. The link lookup and each asset lookup are
/// tenant- AND workspace-scoped (the repositories lead their predicates with <c>organization_id</c> then
/// <c>workspace_id</c>), so an asset or link in another tenant or workspace is never read (threats T1/T5).
/// Only an <see cref="AssetStatus.Available"/> asset is listed — a still-<see cref="AssetStatus.Pending"/>
/// asset's upload is not yet confirmed and is not downloadable (<c>getDownloadUrl</c> is 409 for it), so a
/// not-yet-uploaded asset is never advertised as an attachment. A link whose asset no longer resolves (a
/// race with deletion) is skipped, so the list degrades without error.
/// </summary>
internal sealed class VisibleResourceAttachmentsProjector : IVisibleResourceAttachmentsProjector
{
    private readonly IAssetLinkRepository _assetLinks;
    private readonly IAssetRepository _assets;

    public VisibleResourceAttachmentsProjector(IAssetLinkRepository assetLinks, IAssetRepository assets)
    {
        ArgumentNullException.ThrowIfNull(assetLinks);
        ArgumentNullException.ThrowIfNull(assets);
        _assetLinks = assetLinks;
        _assets = assets;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VisibleResourceAttachment>> ListAttachmentsAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        // Map the generic Core resource kind to the asset-link target kind. A Scene is never an asset-link
        // target (only a content block or entity is), and an undefined enum value matches no target, so both
        // resolve to an empty attachments list WITHOUT a query (fail-soft). An empty resource id likewise can
        // never address a stored target's links.
        if (resourceId == Guid.Empty || !TryMapTargetType(resourceType, out var targetType))
        {
            return [];
        }

        // List the links attaching any asset to this (already-visible) resource, tenant- and
        // workspace-scoped. The link inherits the resource's audience visibility (decided upstream by the
        // central VisibilityPolicy), so this lookup never re-derives visibility.
        var links = await _assetLinks
            .ListByTargetAsync(organizationId, workspaceId, targetType, resourceId, cancellationToken)
            .ConfigureAwait(false);

        if (links.Count == 0)
        {
            return [];
        }

        var attachments = new List<VisibleResourceAttachment>(links.Count);
        foreach (var link in links)
        {
            // Resolve each linked asset within the SAME tenant and workspace (the asset and its link are
            // always in the same workspace; the lookup leads with organization_id then workspace_id, so a
            // foreign-tenant/workspace asset is never read — threats T1/T5).
            var asset = await _assets
                .FindByIdAsync(organizationId, workspaceId, link.AssetId, cancellationToken)
                .ConfigureAwait(false);

            // A link whose asset no longer resolves (a race with deletion) is skipped, and a still-pending
            // asset (upload not yet confirmed, not downloadable) is not advertised — only an available,
            // downloadable asset is listed.
            if (asset is null || !asset.IsAvailable)
            {
                continue;
            }

            // Audience-safe metadata only: the assetId (the download handle), the audience-safe name (null
            // today — Core has no host-facing asset name; the storage coordinates are host-only, threats
            // T4/T7) and the content type. No host-only field is ever carried.
            attachments.Add(new VisibleResourceAttachment(asset.Id, Name: null, asset.ContentType));
        }

        return attachments;
    }

    /// <summary>
    /// Maps a generic Core <see cref="VisibilityResourceType"/> to the <see cref="AssetLinkTargetType"/> an
    /// asset may be linked to, or reports that the kind cannot carry attachments. A
    /// <see cref="VisibilityResourceType.ContentBlock"/> and a <see cref="VisibilityResourceType.Entity"/>
    /// are the asset-link targets (CORE-AST-005); a <see cref="VisibilityResourceType.Scene"/> is never a
    /// target, and an undefined value maps to nothing — both yield an empty attachments list. This is the
    /// inverse of <see cref="Assets.AssetLinkTargetType"/>'s <c>ToVisibilityResourceType</c>; it is mapped
    /// here in the composition root (not in either governed module) because it bridges the two.
    /// </summary>
    private static bool TryMapTargetType(VisibilityResourceType resourceType, out AssetLinkTargetType targetType)
    {
        switch (resourceType)
        {
            case VisibilityResourceType.ContentBlock:
                targetType = AssetLinkTargetType.ContentBlock;
                return true;
            case VisibilityResourceType.Entity:
                targetType = AssetLinkTargetType.Entity;
                return true;
            default:
                targetType = default;
                return false;
        }
    }
}
