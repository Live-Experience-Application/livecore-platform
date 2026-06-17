// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// Generic kind of Core resource an <see cref="AssetLink"/> attaches an <see cref="Asset"/> to
/// (CORE-AST-005, the asset-linking story of the "Asset Storage and Authorization" epic). The asset
/// lifecycle ends with "asset can be linked to ContentBlock or Entity -&gt; visibility controls whether
/// it can be accessed" (docs/12_STORAGE_ASSETS.md), so this enum is the closed set of generic,
/// product-neutral Core resources an asset may be linked to.
///
/// The two kinds are exactly the host-prepared resources whose audience visibility then governs the
/// asset's audience access (docs/03_DOMAIN_LANGUAGE.md): a <see cref="Content.ContentBlock"/> is the
/// "Text/media/data unit shown or hidden by visibility rules" and an <see cref="Entities.Entity"/> is a
/// "Generic domain object". A <see cref="Visibility.VisibilityResourceType.Scene"/> is deliberately NOT a
/// link target — the lifecycle names only content blocks and entities — so the asset-link target set is
/// narrower than <see cref="Visibility.VisibilityResourceType"/>;
/// <see cref="AssetLinkTargetTypeExtensions.ToVisibilityResourceType"/> maps each member onto its
/// visibility counterpart so the central Visibility engine decides audience access (it is never re-derived
/// here; docs/05_MODULE_CONTRACTS.md). The kinds carry NO vertical product meaning (AGENTS.md;
/// docs/04_PRODUCT_BOUNDARIES.md; csv/forbidden_core_terms.csv).
///
/// The kind is persisted by its stable NAME (not its numeric value), exactly like
/// <see cref="Visibility.VisibilityResourceType"/>, <see cref="AssetStatus"/> and
/// <c>ContentBlockType</c>, so the integers below are only in-memory storage discriminators and carry no
/// ordering meaning; they must not be compared with &gt;/&lt;. A <c>target_id</c> column references one of
/// these resources by its surrogate id, but it is intentionally NOT a database foreign key (a single
/// column cannot foreign-key into two different tables); the link is the polymorphic owner and the
/// same-workspace coupling between a link and its target is enforced by the application flow that creates
/// links (<see cref="AssetLinkService"/>), mirroring how <c>VisibilityRule.ResourceId</c>,
/// <c>ContentBlock.SceneId</c> and <c>Entity.EntityTypeId</c> are simple references whose same-workspace
/// coupling is enforced above the aggregate.
/// </summary>
public enum AssetLinkTargetType
{
    /// <summary>
    /// A <see cref="Content.ContentBlock"/> — the "Text/media/data unit shown or hidden by visibility
    /// rules" (docs/03_DOMAIN_LANGUAGE.md). The asset becomes audience-accessible once this content block
    /// is visible to the audience.
    /// </summary>
    ContentBlock = 1,

    /// <summary>
    /// An <see cref="Entities.Entity"/> — a "Generic domain object" (docs/03_DOMAIN_LANGUAGE.md). The
    /// asset becomes audience-accessible once this entity is visible to the audience.
    /// </summary>
    Entity = 2,
}
