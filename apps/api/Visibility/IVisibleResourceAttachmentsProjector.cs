// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The Visibility module's PORT for resolving the AUDIENCE-SAFE attachments of a visible resource
/// (CORE-ALC-002), so the participant-visible feed can carry, per item, the list of assets attached to the
/// resource — letting an audience surface enumerate the attachments of content it has been shown and then
/// request the authorized per-asset download, with no host read (the vertical adopter gap ARC-GAP-009).
///
/// THE CENTRAL SECURITY MODULE DOES NOT REACH INTO THE ASSETS MODULE (CORE-ARCH-001). The Visibility module
/// is a node in the enforced module dependency graph (docs/05_MODULE_CONTRACTS.md) and is NOT allowed to
/// reference the Assets module (the Assets module references Visibility, not the reverse). So this is a port
/// the Visibility module OWNS, declared in terms of its own <see cref="VisibilityResourceType"/> and plain
/// ids only; the adapter that fulfils it lives in the COMPOSITION ROOT (the shared-kernel
/// <c>LiveCore.Api.Hosting</c> namespace, which references every module by design) — the SAME hexagonal
/// port-and-adapter shape <see cref="IVisibleResourceAudienceProjector"/> (CORE-APROJ-002) and
/// <see cref="IVisibilityResourceWorkspaceLocator"/> (CORE-SVIS-005) use.
///
/// THE LINK INHERITS THE RESOURCE'S VISIBILITY — VISIBILITY IS NOT RE-DERIVED HERE. The attachments are
/// resolved ONLY for a resource the central <see cref="VisibilityPolicy"/> has ALREADY decided the
/// participant may see (the feed item is built only for a visible resource). An asset becomes
/// audience-enumerable purely by being LINKED to that visible resource (the existing <c>AssetLink</c> join,
/// CORE-AST-005), so the attachments list inherits the resource's audience visibility and a hidden
/// resource's attachments are NEVER enumerated (threat T2 in docs/07_SECURITY_THREAT_MODEL.md). This port
/// performs NO visibility computation of its own; it only resolves the audience-safe metadata of the assets
/// attached to an already-visible resource.
///
/// AUDIENCE-SAFE BY CONSTRUCTION. Each entry carries only the asset's surrogate id, its audience-safe name
/// and its content type — never a host-only field (the storage provider/bucket/object key and the checksum
/// are host-only and never participant-facing; threats T4 "Asset leak"/T7). Listing an attachment never
/// grants access to its bytes: the download still goes through the server-side
/// <c>getDownloadUrl</c> authorization check (defence in depth) before any signed URL is minted.
/// </summary>
internal interface IVisibleResourceAttachmentsProjector
{
    /// <summary>
    /// Lists the audience-safe attachments of the resource of the given kind and id IN exactly the given
    /// (organization, workspace). The lookup is tenant- AND workspace-scoped (the owning repositories lead
    /// their predicates with <c>organization_id</c> then <c>workspace_id</c>), so an asset or link in
    /// another tenant or workspace is never read (threats T1/T5). Returns an EMPTY list (never throws) when
    /// the resource has no attachments, when the resource kind cannot carry attachments (a
    /// <see cref="VisibilityResourceType.Scene"/> is never an asset-link target), or when the
    /// <paramref name="resourceType"/> is undefined — so a feed item degrades to an empty attachments list
    /// without error. The caller passes a resource the central <see cref="VisibilityPolicy"/> has already
    /// decided is visible; this port never decides visibility.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the resource belongs to.</param>
    /// <param name="resourceType">The kind of the visible resource (Scene/ContentBlock/Entity).</param>
    /// <param name="resourceId">The surrogate id of the visible resource.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<VisibleResourceAttachment>> ListAttachmentsAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken);
}

/// <summary>
/// One AUDIENCE-SAFE attachment of a visible resource (CORE-ALC-002), resolved by
/// <see cref="IVisibleResourceAttachmentsProjector"/> from the asset linked to the resource. Every field is
/// audience-safe: <see cref="AssetId"/> is the surrogate id a consumer passes to <c>getDownloadUrl</c> to
/// request the authorized download; <see cref="ContentType"/> is the asset's MIME content type (the same
/// non-sensitive value the signed download-url response already discloses to an authorized audience caller);
/// and <see cref="Name"/> is the asset's audience-safe display label. Core stores no host-facing asset
/// filename and its storage coordinates (provider/bucket/object key) are host-only and never
/// participant-facing (threats T4/T7), so there is no audience-safe asset name to project yet —
/// <see cref="Name"/> is the forward-compatible audience-safe label slot and is null today, exactly as the
/// feed item's audience body slot is (CORE-APROJ-002). No host-only field (the storage coordinates, the
/// checksum, the size or the creator) is ever carried here.
/// </summary>
/// <param name="AssetId">The surrogate id of the attached asset — the handle for the authorized download.</param>
/// <param name="Name">The asset's audience-safe display label, or <see langword="null"/> when it has none.</param>
/// <param name="ContentType">The asset's MIME content type (audience-safe metadata).</param>
internal readonly record struct VisibleResourceAttachment(Guid AssetId, string? Name, string ContentType);
