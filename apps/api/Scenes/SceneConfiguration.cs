// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Scenes;

/// <summary>
/// Relational mapping of <see cref="Scene"/> to the <c>scenes</c> table
/// (CORE-SCENE-001; csv/database_tables.csv: module Scenes, scope <c>workspace</c>,
/// "Ordered segments"). The scene is workspace-scoped, so it carries a
/// <c>workspace_id</c> column that is a foreign key into <c>workspaces(id)</c>, the
/// documented <c>scenes(workspace_id, id)</c> shape (docs/10_DATABASE_SCHEMA.md
/// critical indexes — note the index carries NO <c>session_id</c>). It is also
/// tenant-scoped, so it carries an <c>organization_id</c> column that is a foreign
/// key into <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principle:
/// "tenant-scoped tables include <c>organization_id</c>"; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already pins the
/// organization, but storing the tenant on the row lets isolation be enforced at the
/// row level and lets the organization boundary be checked before the workspace
/// boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles). There is
/// deliberately no <c>session_id</c> column: a scene is a workspace-prepared
/// segment, and a session activates it through its active scene pointer in a later
/// story (docs/05_MODULE_CONTRACTS.md).
///
/// Ordering column: the position is stored in the <c>scene_order</c> column rather
/// than a bare <c>order</c> column, because <c>order</c> (as in <c>ORDER BY</c>) is a
/// SQL reserved word and a bare column of that name would force quoting on every
/// provider. The column is a plain non-negative integer (the aggregate validates
/// non-negativity).
///
/// Three indexes back the documented access patterns:
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) is the
///   documented critical index <c>scenes(workspace_id, id)</c>: it keeps
///   workspace-scoped lookups by id efficient and makes every scene read lead with
///   the workspace column (docs/10_DATABASE_SCHEMA.md critical indexes).
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>)
///   keeps the tenant boundary check (checked before the workspace boundary) and the
///   organization foreign-key check efficient and makes tenant-scoped reads lead with
///   <c>organization_id</c> (threat T5).
///   </item>
///   <item>
///   The NON-unique composite ordering index on (<c>workspace_id</c>,
///   <c>scene_order</c>) backs the ordered retrieval of a workspace's scenes (the
///   ordering feature). It is deliberately NOT unique: the aggregate takes an explicit
///   order and the repository sorts deterministically on (<c>scene_order</c>,
///   <c>id</c>), so duplicate or non-contiguous orders are allowed and ties are broken
///   by the id; enforcing unique/contiguous orders and shifting siblings is the later
///   reorder endpoint story's concern.
///   </item>
/// </list>
/// There is deliberately NO unique natural-key index: a scene is identified only by
/// its surrogate id, so a workspace may hold many scenes with the same title or the
/// same order.
///
/// Both foreign keys cascade on delete: a scene has no meaning without its workspace
/// or its tenant, so removing the organization or the workspace removes its scenes
/// (mirrors the required cascades on <c>sessions</c> and <c>participants</c>).
///
/// Deployment requirement: equality on the id columns is binary, so no collation
/// concern applies. Pinning any column collation, if ever needed, is planned with the
/// first story that runs migrations against real PostgreSQL, exactly as noted for the
/// <c>workspaces</c>, <c>workspace_members</c>, <c>participants</c> and
/// <c>sessions</c> tables.
/// </summary>
internal sealed class SceneConfiguration : IEntityTypeConfiguration<Scene>
{
    public void Configure(EntityTypeBuilder<Scene> builder)
    {
        builder.ToTable("scenes");

        builder.HasKey(scene => scene.Id);

        builder.Property(scene => scene.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(scene => scene.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(scene => scene.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(scene => scene.Title)
            .HasColumnName("title")
            .HasMaxLength(Scene.MaxTitleLength)
            .IsRequired();

        // The position is stored as scene_order, not a bare reserved-word "order"
        // column. A plain non-negative integer; the aggregate validates the bound.
        builder.Property(scene => scene.Order)
            .HasColumnName("scene_order")
            .IsRequired();

        builder.Property(scene => scene.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(scene => scene.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Documented critical index scenes(workspace_id, id): workspace reads lead
        // with the workspace column (docs/10_DATABASE_SCHEMA.md).
        builder.HasIndex(scene => new { scene.WorkspaceId, scene.Id })
            .HasDatabaseName("ix_scenes_workspace_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the
        // organization boundary check (checked before the workspace boundary) and
        // tenant-scoped reads efficient (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(scene => new { scene.OrganizationId, scene.WorkspaceId })
            .HasDatabaseName("ix_scenes_organization_id_workspace_id");

        // Non-unique ordering index backing deterministic ordered retrieval of a
        // workspace's scenes by (scene_order, id). Not unique on purpose: the
        // aggregate takes an explicit order, duplicate/non-contiguous orders are
        // allowed and ties are broken by the id (the ordering feature). Enforcing
        // unique/contiguous orders is the later reorder endpoint story's concern.
        builder.HasIndex(scene => new { scene.WorkspaceId, scene.Order })
            .HasDatabaseName("ix_scenes_workspace_id_scene_order");

        // Tenant foreign key: every scene hangs off exactly one organization (the
        // owner of its workspace). Cascade delete removes scenes with their tenant (a
        // scene has no meaning without its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(scene => scene.OrganizationId)
            .HasConstraintName("fk_scenes_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every scene hangs off exactly one workspace. Cascade
        // delete removes scenes with their workspace (a scene has no meaning without
        // its workspace).
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(scene => scene.WorkspaceId)
            .HasConstraintName("fk_scenes_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
