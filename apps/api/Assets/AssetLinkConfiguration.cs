// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Assets;

/// <summary>
/// Relational mapping of <see cref="AssetLink"/> to the <c>asset_links</c> table (CORE-AST-005;
/// csv/database_tables.csv: module Assets, scope <c>workspace</c>, "Asset-to-content/entity links"). A
/// link attaches one <see cref="Asset"/> to one host-prepared resource (a content block or entity), so it
/// carries an <c>asset_id</c> column that is a foreign key into <c>assets(id)</c> and a
/// (<c>target_type</c>, <c>target_id</c>) pair naming the linked resource. It is workspace-scoped, so it
/// carries a <c>workspace_id</c> column that is a foreign key into <c>workspaces(id)</c>, and
/// tenant-scoped, so it carries an <c>organization_id</c> column that is a foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principles: tenant-scoped tables include
/// <c>organization_id</c>, workspace-scoped tables include <c>workspace_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already pins the organization, but storing the
/// tenant on the row lets isolation be enforced at the row level and the organization boundary be checked
/// before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles).
///
/// <c>target_id</c> is intentionally NOT a foreign key: the reference is polymorphic across the
/// <c>content_blocks</c> and <c>entities</c> tables, and a single column cannot foreign-key into two
/// principals. The link is the polymorphic owner; the same-workspace coupling between a link and its
/// target is enforced by the application flow that creates links (<see cref="AssetLinkService"/>),
/// mirroring the <c>visibility_rules.resource_id</c>, <c>content_blocks.scene_id</c> and
/// <c>entities.entity_type_id</c> precedent. The <c>target_type</c> enum is persisted by its stable NAME
/// (not its numeric value), exactly like <c>visibility_rules.resource_type</c> and <c>assets.status</c>.
///
/// The optional <c>created_by</c> column is a NULLABLE foreign key into <c>users(id)</c>: a link is always
/// created by an authenticated host, but the link sets null when that user is deleted so the link survives
/// and is anonymized rather than cascade-deleted (mirrors <c>assets.created_by</c> /
/// <c>participants.user_id</c>).
///
/// Indexes back the documented access patterns and the uniqueness invariant (docs/10_DATABASE_SCHEMA.md
/// documents the critical index <c>asset_links(workspace_id, asset_id)</c>):
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>asset_id</c>) is the documented critical
///   index: it backs the list-by-asset access path (<see cref="AssetLinkRepository.ListByAssetAsync"/>,
///   used by the download authorization) and the <c>asset_id</c> foreign-key check, leading with the
///   workspace column.
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant
///   boundary check (checked before the workspace boundary) and the organization foreign-key check
///   efficient and makes tenant-scoped reads lead with <c>organization_id</c> (threat T5).
///   </item>
///   <item>
///   The UNIQUE composite index on (<c>workspace_id</c>, <c>asset_id</c>, <c>target_type</c>,
///   <c>target_id</c>) is the per-workspace natural-key guarantee: the same asset cannot be linked to the
///   same target twice, so a second writer can never create a duplicate link. It is workspace-scoped, so
///   the same link may legitimately exist in two different workspaces (threat T5).
///   </item>
/// </list>
///
/// The tenant, workspace and asset foreign keys cascade on delete (a link has no meaning without its
/// tenant, its workspace or its asset — mirrors how <c>assets</c> cascade with their tenant/workspace);
/// the <c>created_by</c> user foreign key sets null on delete (the link's history is retained when its
/// creator is removed). The linked target (content block / entity) is referenced only by id, so removing
/// the target leaves a dangling link that resolves to nothing — exactly the polymorphic-reference
/// behavior of <c>visibility_rules.resource_id</c>; the download authorization re-resolves visibility
/// through the Visibility engine, so a dangling link simply grants no audience access.
///
/// Deployment requirement: equality on the id/enum columns is binary, so no collation concern applies.
/// Pinning any column collation, if ever needed, is planned with the first story that runs migrations
/// against real PostgreSQL, exactly as noted for the <c>assets</c> and other workspace-scoped tables.
/// </summary>
internal sealed class AssetLinkConfiguration : IEntityTypeConfiguration<AssetLink>
{
    public void Configure(EntityTypeBuilder<AssetLink> builder)
    {
        builder.ToTable("asset_links");

        builder.HasKey(link => link.Id);

        builder.Property(link => link.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(link => link.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(link => link.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(link => link.AssetId)
            .HasColumnName("asset_id")
            .IsRequired();

        builder.Property(link => link.TargetType)
            .HasColumnName("target_type")
            // Persist the target kind as its stable name (ContentBlock/Entity), not its numeric value,
            // mirrors resource_type on visibility_rules.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(link => link.TargetId)
            .HasColumnName("target_id")
            .IsRequired();

        builder.Property(link => link.CreatedByUserProfileId)
            .HasColumnName("created_by")
            .IsRequired(false);

        builder.Property(link => link.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Documented critical index asset_links(workspace_id, asset_id): the list-by-asset access path
        // (the download authorization) leads with the workspace column (docs/10_DATABASE_SCHEMA.md).
        builder.HasIndex(link => new { link.WorkspaceId, link.AssetId })
            .HasDatabaseName("ix_asset_links_workspace_id_asset_id");

        // Tenant-scoped composite index leading with organization_id: keeps the organization boundary
        // check (checked before the workspace boundary) and tenant-scoped reads efficient
        // (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(link => new { link.OrganizationId, link.WorkspaceId })
            .HasDatabaseName("ix_asset_links_organization_id_workspace_id");

        // Per-workspace unique natural key: the same asset cannot be linked to the same target twice.
        // Enforced at the database level with a unique index so concurrent writers can never create a
        // duplicate link. Workspace-scoped, so the same link may legitimately exist in two different
        // workspaces (threat T5).
        builder.HasIndex(link => new
        {
            link.WorkspaceId,
            link.AssetId,
            link.TargetType,
            link.TargetId,
        })
            .IsUnique()
            .HasDatabaseName("ix_asset_links_workspace_id_asset_id_target_type_target_id");

        // Tenant foreign key: every link hangs off exactly one organization (the owner of its workspace).
        // Cascade delete removes links with their tenant (a link has no meaning without its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(link => link.OrganizationId)
            .HasConstraintName("fk_asset_links_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every link hangs off exactly one workspace. Cascade delete removes links
        // with their workspace (a link has no meaning without its workspace).
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(link => link.WorkspaceId)
            .HasConstraintName("fk_asset_links_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Asset foreign key: every link attaches exactly one asset. Cascade delete removes the link when
        // its asset is deleted (a link has no meaning without its asset).
        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(link => link.AssetId)
            .HasConstraintName("fk_asset_links_assets_asset_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Optional creator foreign key: a link may reference at most one user profile as its creator. Set
        // null on delete so removing the user anonymizes the link record rather than deleting the link
        // (mirrors assets.created_by / participants.user_id).
        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(link => link.CreatedByUserProfileId)
            .HasConstraintName("fk_asset_links_users_created_by")
            .OnDelete(DeleteBehavior.SetNull);

        // NOTE: target_id is deliberately NOT mapped as a foreign key — the reference is polymorphic
        // across content_blocks/entities; the same-workspace coupling is enforced by the application flow
        // that creates links (AssetLinkService).
    }
}
