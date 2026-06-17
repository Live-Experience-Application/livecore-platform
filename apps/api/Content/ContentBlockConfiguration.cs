// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Scenes;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Content;

/// <summary>
/// Relational mapping of <see cref="ContentBlock"/> to the <c>content_blocks</c> table
/// (CORE-SCENE-002; csv/database_tables.csv: module Content, scope <c>workspace</c>,
/// "Text/media/data block"). The content block is scene-scoped within a workspace, so
/// it carries a <c>scene_id</c> column that is a foreign key into <c>scenes(id)</c> and
/// a <c>workspace_id</c> column that is a foreign key into <c>workspaces(id)</c>, the
/// documented <c>content_blocks(workspace_id, scene_id)</c> shape
/// (docs/10_DATABASE_SCHEMA.md critical indexes). It is also tenant-scoped, so it
/// carries an <c>organization_id</c> column that is a foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principle: "tenant-scoped
/// tables include <c>organization_id</c>"; threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// The owning scene already pins the workspace and the owning workspace already pins
/// the organization, but storing the workspace and tenant on the row lets isolation be
/// enforced at the row level and lets the organization boundary be checked before the
/// workspace boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles).
///
/// Content columns: <c>content_type</c> stores the generic kind name
/// (Text/Media/Data); <c>body</c> stores the content payload; <c>revision_number</c>
/// stores the monotonic revision counter (the revisions feature). The body is mapped
/// as a plain text column bounded by <see cref="ContentBlock.MaxBodyLength"/> — the
/// overall maximum across content types (the maximum of the per-type limits in
/// <see cref="ContentValidator"/>, CORE-SCENE-005). The aggregate validates the real,
/// narrower per-type size limits and well-formedness; the column keeps the catch-all
/// bound (unchanged from when the table was created, so this story needs no migration).
/// A structured (JSONB) column type is deliberately not used: docs/10_DATABASE_SCHEMA.md
/// permits JSONB only for flexible attributes, never for authorization fields, and a Data
/// body is validated as well-formed JSON text by <see cref="ContentValidator"/> without
/// coupling the column to a JSONB shape.
///
/// Two indexes back the documented access patterns:
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>scene_id</c>) is the
///   documented critical index <c>content_blocks(workspace_id, scene_id)</c>: it keeps
///   the scene-scoped list and lookup efficient and makes every content read lead with
///   the workspace column (docs/10_DATABASE_SCHEMA.md critical indexes).
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>)
///   keeps the tenant boundary check (checked before the workspace boundary) and the
///   organization foreign-key check efficient and makes tenant-scoped reads lead with
///   <c>organization_id</c> (threat T5).
///   </item>
/// </list>
/// There is deliberately NO unique natural-key index: a content block is identified
/// only by its surrogate id, so a scene may hold many content blocks with the same
/// body.
///
/// All three foreign keys cascade on delete: a content block has no meaning without
/// its scene, its workspace or its tenant, so removing the scene, the workspace or the
/// organization removes its content blocks (mirrors the required cascades on
/// <c>scenes</c>, <c>sessions</c> and <c>participants</c>).
///
/// Deployment requirement: equality on the id columns is binary, so no collation
/// concern applies. Pinning any column collation, if ever needed, is planned with the
/// first story that runs migrations against real PostgreSQL, exactly as noted for the
/// <c>scenes</c>, <c>sessions</c>, <c>participants</c>, <c>workspaces</c> and
/// <c>workspace_members</c> tables.
/// </summary>
internal sealed class ContentBlockConfiguration : IEntityTypeConfiguration<ContentBlock>
{
    public void Configure(EntityTypeBuilder<ContentBlock> builder)
    {
        builder.ToTable("content_blocks");

        builder.HasKey(contentBlock => contentBlock.Id);

        builder.Property(contentBlock => contentBlock.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(contentBlock => contentBlock.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(contentBlock => contentBlock.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(contentBlock => contentBlock.SceneId)
            .HasColumnName("scene_id")
            .IsRequired();

        builder.Property(contentBlock => contentBlock.Type)
            .HasColumnName("content_type")
            // Persist the type as its stable name (not its numeric value) so the stored
            // value stays readable and is not coupled to the declaration-order
            // integers, which exist only as in-memory storage discriminators (mirrors
            // SessionStatus / ParticipantStatus).
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(contentBlock => contentBlock.Body)
            .HasColumnName("body")
            .HasMaxLength(ContentBlock.MaxBodyLength)
            .IsRequired();

        builder.Property(contentBlock => contentBlock.RevisionNumber)
            .HasColumnName("revision_number")
            .IsRequired();

        builder.Property(contentBlock => contentBlock.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(contentBlock => contentBlock.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Documented critical index content_blocks(workspace_id, scene_id): scene reads
        // lead with the workspace column (docs/10_DATABASE_SCHEMA.md).
        builder.HasIndex(contentBlock => new { contentBlock.WorkspaceId, contentBlock.SceneId })
            .HasDatabaseName("ix_content_blocks_workspace_id_scene_id");

        // Tenant-scoped composite index leading with organization_id: keeps the
        // organization boundary check (checked before the workspace boundary) and
        // tenant-scoped reads efficient (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(contentBlock => new { contentBlock.OrganizationId, contentBlock.WorkspaceId })
            .HasDatabaseName("ix_content_blocks_organization_id_workspace_id");

        // Tenant foreign key: every content block hangs off exactly one organization
        // (the owner of its workspace). Cascade delete removes content blocks with their
        // tenant (a content block has no meaning without its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(contentBlock => contentBlock.OrganizationId)
            .HasConstraintName("fk_content_blocks_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every content block hangs off exactly one workspace.
        // Cascade delete removes content blocks with their workspace.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(contentBlock => contentBlock.WorkspaceId)
            .HasConstraintName("fk_content_blocks_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Scene foreign key: every content block hangs off exactly one scene. Cascade
        // delete removes content blocks with their scene (a content block has no meaning
        // without its scene).
        builder.HasOne<Scene>()
            .WithMany()
            .HasForeignKey(contentBlock => contentBlock.SceneId)
            .HasConstraintName("fk_content_blocks_scenes_scene_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
