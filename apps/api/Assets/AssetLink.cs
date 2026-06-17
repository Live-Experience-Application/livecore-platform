// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// AssetLink aggregate of the Assets module (CORE-AST-005, the asset-linking story of the "Asset Storage
/// and Authorization" epic).
///
/// An <see cref="AssetLink"/> attaches one <see cref="Asset"/> to one host-prepared Core resource — a
/// <see cref="Content.ContentBlock"/> or an <see cref="Entities.Entity"/>, named generically by a
/// (<see cref="TargetType"/>, <see cref="TargetId"/>) pair — realizing the last step of the asset
/// lifecycle: "asset can be linked to ContentBlock or Entity -&gt; visibility controls whether it can be
/// accessed" (docs/12_STORAGE_ASSETS.md). It carries no vertical product language — both target kinds are
/// generic Core resources (docs/04_PRODUCT_BOUNDARIES.md, csv/forbidden_core_terms.csv).
///
/// WHAT THE LINK DOES — AND DOES NOT — DO. The link is the JOIN that lets an asset INHERIT the audience
/// visibility of the resource it is attached to: an asset becomes audience-accessible only once it is
/// linked to a content block or entity that is VISIBLE to the audience (the consuming
/// <see cref="AssetDownloadPolicy"/>, CORE-AST-004's deferred audience case; threat T4 "Asset leak";
/// threat T2 visibility leak). The link itself NEVER makes an asset public and NEVER decides visibility:
/// whether the linked resource is visible is the central Visibility module's decision (the
/// <c>visibility_rules</c> rows via <c>VisibilityPolicy</c>), not this aggregate
/// (docs/05_MODULE_CONTRACTS.md: "do not duplicate visibility logic elsewhere"). Assets stay PRIVATE BY
/// DEFAULT and reachable only through an authorized, short-lived signed URL (the epic acceptance
/// criterion): a link merely widens WHO may pass the permission check, never how the bytes are served.
///
/// Tenant + workspace boundary: csv/database_tables.csv scopes <c>asset_links</c> to <c>workspace</c>.
/// So a link is workspace-scoped and tenant-scoped, carrying both <see cref="OrganizationId"/> (the
/// tenant; checked before the workspace boundary) and <see cref="WorkspaceId"/>, mirroring
/// <see cref="Asset"/> (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables include <c>organization_id</c>,
/// workspace-scoped tables include <c>workspace_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). A
/// link belongs to exactly one organization and one workspace for its whole lifetime. The surrogate
/// <see cref="Id"/> alone never authorizes access: every lookup is scoped by <see cref="OrganizationId"/>
/// and <see cref="WorkspaceId"/> so one workspace's link can never be reached through another workspace's
/// or another tenant's id (threat T5; T1 broken object-level authorization).
///
/// Asset linkage: <see cref="AssetId"/> is a foreign key into <c>assets(id)</c> (the surrogate id minted
/// in CORE-AST-001). The asset and the link are always in the same workspace (the create flow resolves the
/// asset, then records the link in the asset's own workspace).
///
/// Target reference: <see cref="TargetType"/> + <see cref="TargetId"/> name the linked resource.
/// <see cref="TargetId"/> is a plain surrogate id, NOT a database foreign key: a single column cannot
/// foreign-key into the two different target tables (<c>content_blocks</c>/<c>entities</c>), so the link
/// is the polymorphic owner. Ensuring the target EXISTS and is in the link's OWN workspace is the
/// responsibility of the application flow that creates links (<see cref="AssetLinkService"/>), which
/// resolves the target through a workspace-scoped lookup before creating the link — the same
/// same-workspace-coupling precedent as <c>VisibilityRule.ResourceId</c>, <c>ContentBlock.SceneId</c> and
/// <c>Entity.EntityTypeId</c>.
///
/// Creator: <see cref="CreatedByUserProfileId"/> records the authenticated user that created the link
/// (always a host; creating a link requires a host role, CORE-AST-005). It is a NULLABLE foreign key into
/// <c>users</c> that is set null when the user is deleted (mirrors <see cref="Asset"/>'s
/// <c>created_by</c>): a link survives the later deletion of its creator, anonymized rather than
/// cascade-deleted.
///
/// Uniqueness / immutability: a link is identified by its surrogate <see cref="Id"/> (UUIDv7) and has a
/// per-workspace natural key — (<c>workspace_id</c>, <c>asset_id</c>, <c>target_type</c>,
/// <c>target_id</c>) is unique, so the SAME asset cannot be linked to the SAME target twice (a duplicate
/// insert is rejected via <see cref="AssetLinkAddResult.Duplicate"/>, mirroring
/// <c>EntityRelationship</c>'s per-workspace key). A link is essentially immutable — its asset, its
/// target, its tenant and its workspace never change; redefining any of them would be indistinguishable
/// from creating a different link, so there is deliberately NO mutator (a host that wants a different link
/// removes this one and creates a new one), exactly as <c>EntityRelationship</c> is immutable.
/// </summary>
public sealed class AssetLink
{
    private AssetLink(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        Guid assetId,
        AssetLinkTargetType targetType,
        Guid targetId,
        Guid? createdByUserProfileId,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Asset link id must not be empty.", nameof(id));
        }

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

        if (!IsValidTargetType(targetType))
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

        // The creator link is a recorded user reference; when present it must be a real, non-empty
        // surrogate id. A null value means the creating user was later deleted and the record was
        // anonymized (the column sets null on user deletion), so null is accepted on materialization but
        // an explicit empty id is neither a real user nor a deliberate anonymization and is rejected
        // (mirrors Asset.created_by).
        if (createdByUserProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A created-by user profile id must not be empty; pass null only for an anonymized record.",
                nameof(createdByUserProfileId));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        AssetId = assetId;
        TargetType = targetType;
        TargetId = targetId;
        CreatedByUserProfileId = createdByUserProfileId;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private AssetLink()
    {
    }

    /// <summary>
    /// Surrogate key of the asset link row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).
    /// On its own it never authorizes access: lookups are always scoped by <see cref="OrganizationId"/>
    /// and <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the link: the id of the organization that owns the workspace this link belongs
    /// to (the <c>organization_id</c> foreign key to the <c>organizations</c> table). Immutable; a link
    /// never crosses to another organization. The organization boundary is checked before the workspace
    /// boundary, so this column lets isolation be enforced at the row level (threat T5;
    /// docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the link: the id of the workspace this link belongs to (the
    /// <c>workspace_id</c> foreign key to the <c>workspaces</c> table). Immutable; a link never moves to
    /// another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// The linked asset: the id of the <see cref="Asset"/> this link attaches (the <c>asset_id</c>
    /// foreign key to the <c>assets</c> table). Immutable. The asset and the link are always in the same
    /// workspace (the create flow records the link in the asset's own workspace).
    /// </summary>
    public Guid AssetId { get; }

    /// <summary>
    /// Generic kind of the linked resource — ContentBlock or Entity (<see cref="AssetLinkTargetType"/>).
    /// Immutable; a link attaches one resource for its whole lifetime. Part of the per-workspace natural
    /// key.
    /// </summary>
    public AssetLinkTargetType TargetType { get; }

    /// <summary>
    /// Surrogate id of the linked resource (a <c>content_blocks</c>/<c>entities</c> row, per
    /// <see cref="TargetType"/>). Immutable. Intentionally NOT a database foreign key — the reference is
    /// polymorphic across two tables; the same-workspace coupling is enforced by the application flow that
    /// creates links (<see cref="AssetLinkService"/>). Part of the per-workspace natural key.
    /// </summary>
    public Guid TargetId { get; }

    /// <summary>
    /// The authenticated user that created the link (a host). Required at creation but a NULLABLE column:
    /// it is set null when the user is deleted, so the link survives and is anonymized rather than
    /// cascade-deleted (mirrors <see cref="Asset.CreatedByUserProfileId"/>). Immutable on the aggregate.
    /// </summary>
    public Guid? CreatedByUserProfileId { get; }

    /// <summary>When this link was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Creates a new link attaching the given asset to the given target resource in the given workspace
    /// (owned by the given organization), created by the given authenticated user. The caller
    /// (<see cref="AssetLinkService"/>) supplies the already-resolved tenant, workspace, asset and creator
    /// and is responsible for having resolved the target through a workspace-scoped lookup so the target
    /// is in the link's own workspace; the aggregate enforces only the structural invariants (non-empty
    /// ids, a defined target type).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, asset id, target id or creator id is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The target type is not defined.</exception>
    public static AssetLink Create(
        Guid organizationId,
        Guid workspaceId,
        Guid assetId,
        AssetLinkTargetType targetType,
        Guid targetId,
        Guid createdByUserProfileId,
        DateTimeOffset createdAt)
    {
        if (createdByUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Created-by user profile id must not be empty.", nameof(createdByUserProfileId));
        }

        return new AssetLink(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            assetId,
            targetType,
            targetId,
            createdByUserProfileId,
            createdAt);
    }

    /// <summary>
    /// Whether this link belongs to exactly the given organization. Empty ids match nothing. This is the
    /// tenant-boundary check, checked before the workspace boundary: a link belongs only to the
    /// organization it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this link belongs to exactly the given workspace. Empty ids match nothing. This is the
    /// workspace-boundary check: a link belongs only to the workspace it names, never to any other
    /// (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this link attaches exactly the given asset. Empty ids match nothing.
    /// </summary>
    public bool LinksAsset(Guid assetId)
        => assetId != Guid.Empty && AssetId == assetId;

    /// <summary>
    /// Whether this link attaches exactly the given target (kind AND id). An empty id matches nothing.
    /// </summary>
    public bool LinksTarget(AssetLinkTargetType targetType, Guid targetId)
        => targetId != Guid.Empty && TargetType == targetType && TargetId == targetId;

    /// <summary>
    /// Whether the given value is a defined <see cref="AssetLinkTargetType"/>. Used to reject undefined
    /// enum values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidTargetType(AssetLinkTargetType targetType) => Enum.IsDefined(targetType);

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: link id, organization id,
    /// workspace id, asset id, target (kind + id) and creator (or <c>anonymized</c>). Every field is an
    /// identifier/enum, never free-form content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md: logs
    /// carry identifiers, not sensitive content).
    /// </summary>
    public override string ToString()
        => $"AssetLink {Id} org={OrganizationId} ws={WorkspaceId} asset={AssetId} "
            + $"target={TargetType}:{TargetId} "
            + $"createdBy={(CreatedByUserProfileId is { } creator ? creator.ToString() : "anonymized")}";
}
