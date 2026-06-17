// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Entities;

/// <summary>
/// Relational mapping of <see cref="EntityRelationship"/> to the <c>entity_relationships</c> table
/// (CORE-ENT-003; csv/database_tables.csv: module Entities, scope <c>workspace</c>, "Graph edges").
/// A relationship is a DIRECTED edge between two <see cref="Entity"/> instances, so it carries a
/// <c>source_entity_id</c> and a <c>target_entity_id</c> column, each a foreign key into
/// <c>entities(id)</c>. It is workspace-scoped, so it carries a <c>workspace_id</c> column that is a
/// foreign key into <c>workspaces(id)</c>, and tenant-scoped, so it carries an
/// <c>organization_id</c> column that is a foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md principles: tenant-scoped tables include <c>organization_id</c>,
/// workspace-scoped tables include <c>workspace_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already pins the organization, but
/// storing the tenant on the row lets isolation be enforced at the row level and lets the
/// organization boundary be checked before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md
/// authorization principles).
///
/// FK design — FOUR SIMPLE single-column foreign keys, mirroring <see cref="Entity"/>'s simple
/// multi-FK pattern (organization, workspace, plus two endpoints). The two endpoint FKs guarantee
/// both referenced entities EXIST but NOT that they are in the SAME workspace as the edge — exactly
/// like <c>entities.entity_type_id</c> and <c>content_blocks.scene_id</c>. That
/// same-workspace-endpoints coupling is the responsibility of the future create-relationship
/// application flow (it resolves each endpoint through the workspace-scoped
/// <c>IEntityRepository.FindByIdAsync(org, ws, entityId)</c> before creating the edge); a composite
/// FK or a unique index on <c>entities</c> is deliberately NOT added here, because that would
/// diverge from the established Entity/ContentBlock precedent and touch the CORE-ENT-002 table.
///
/// Two cascade FKs to the SAME principal table (<c>entities</c>): both <c>source_entity_id</c> and
/// <c>target_entity_id</c> reference <c>entities(id)</c> with <see cref="DeleteBehavior.Cascade"/>.
/// PostgreSQL and SQLite both permit multiple cascade paths to one principal, and EF Core builds two
/// independent single-column relationships with DISTINCT constraint names
/// (<c>fk_entity_relationships_entities_source_entity_id</c> and
/// <c>..._target_entity_id</c>), so there is no ambiguous-shadow-FK or multiple-cascade-path
/// conflict to resolve. Deleting an entity removes every edge it is an endpoint of (as source or as
/// target), consistent with the cascades on <c>entities</c> / <c>content_blocks</c> / <c>scenes</c>.
///
/// Indexes back the documented access patterns and the uniqueness invariant
/// (docs/10_DATABASE_SCHEMA.md documents NO critical index for <c>entity_relationships</c>, so the
/// chosen critical index follows the established <c>scenes(workspace_id, id)</c> /
/// <c>entities(workspace_id, id)</c> shape):
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) is the chosen critical
///   index: it keeps workspace-scoped lookups by id efficient and makes every read lead with the
///   workspace column.
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the
///   tenant boundary check (checked before the workspace boundary) and the organization foreign-key
///   check efficient and makes tenant-scoped reads lead with <c>organization_id</c> (threat T5).
///   </item>
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>source_entity_id</c>) backs the
///   list-by-source access path (<see cref="EntityRelationshipRepository.ListBySourceEntityAsync"/>)
///   and the <c>source_entity_id</c> foreign-key check, leading with the workspace column.
///   </item>
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>target_entity_id</c>) backs the
///   by-target half of the list-by-entity access path
///   (<see cref="EntityRelationshipRepository.ListByEntityAsync"/>) and the <c>target_entity_id</c>
///   foreign-key check, leading with the workspace column.
///   </item>
///   <item>
///   The UNIQUE composite index on (<c>workspace_id</c>, <c>source_entity_id</c>,
///   <c>target_entity_id</c>, <c>relationship_kind</c>) is the per-workspace natural-key guarantee:
///   the same directed edge of the same kind cannot be created twice, so a second writer can never
///   create a duplicate edge. The same pair connected by a DIFFERENT kind, and the reverse edge
///   (target -&gt; source), are still allowed because they differ in the indexed tuple. It is
///   workspace-scoped, so the same edge may legitimately exist in two different workspaces (threat
///   T5).
///   </item>
/// </list>
///
/// Deployment requirement: equality on the id columns is binary, so no collation concern applies.
/// The relationship kind is a canonical lower-case slug compared ordinally; pinning any column
/// collation, if ever needed, is planned with the first story that runs migrations against real
/// PostgreSQL, exactly as noted for the <c>entities</c>, <c>entity_types</c> and <c>scenes</c>
/// tables.
/// </summary>
internal sealed class EntityRelationshipConfiguration : IEntityTypeConfiguration<EntityRelationship>
{
    public void Configure(EntityTypeBuilder<EntityRelationship> builder)
    {
        builder.ToTable("entity_relationships");

        builder.HasKey(relationship => relationship.Id);

        builder.Property(relationship => relationship.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(relationship => relationship.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(relationship => relationship.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(relationship => relationship.SourceEntityId)
            .HasColumnName("source_entity_id")
            .IsRequired();

        builder.Property(relationship => relationship.TargetEntityId)
            .HasColumnName("target_entity_id")
            .IsRequired();

        builder.Property(relationship => relationship.RelationshipKind)
            .HasColumnName("relationship_kind")
            .HasMaxLength(EntityRelationship.MaxRelationshipKindLength)
            .IsRequired();

        builder.Property(relationship => relationship.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(relationship => relationship.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Chosen critical index entity_relationships(workspace_id, id) following the documented
        // scenes(workspace_id, id) / entities(workspace_id, id) shape: workspace reads lead with the
        // workspace column (docs/10_DATABASE_SCHEMA.md — entity_relationships has no documented
        // critical index, so this follows the established pattern).
        builder.HasIndex(relationship => new { relationship.WorkspaceId, relationship.Id })
            .HasDatabaseName("ix_entity_relationships_workspace_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the organization
        // boundary check (checked before the workspace boundary) and tenant-scoped reads efficient
        // (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(relationship => new { relationship.OrganizationId, relationship.WorkspaceId })
            .HasDatabaseName("ix_entity_relationships_organization_id_workspace_id");

        // List-by-source composite index entity_relationships(workspace_id, source_entity_id): backs
        // the outgoing-edges access path and the source_entity_id foreign-key check, leading with the
        // workspace column.
        builder.HasIndex(relationship => new { relationship.WorkspaceId, relationship.SourceEntityId })
            .HasDatabaseName("ix_entity_relationships_workspace_id_source_entity_id");

        // By-target composite index entity_relationships(workspace_id, target_entity_id): backs the
        // by-target half of the list-by-entity access path and the target_entity_id foreign-key
        // check, leading with the workspace column.
        builder.HasIndex(relationship => new { relationship.WorkspaceId, relationship.TargetEntityId })
            .HasDatabaseName("ix_entity_relationships_workspace_id_target_entity_id");

        // Per-workspace unique natural key: the same directed edge of the same kind cannot be
        // created twice. Enforced at the database level with a unique index so concurrent writers can
        // never create a duplicate edge. The same pair connected by a different kind, and the reverse
        // edge, are still allowed because they differ in the indexed tuple. Workspace-scoped, so the
        // same edge may legitimately exist in two different workspaces (threat T5).
        builder.HasIndex(relationship => new
        {
            relationship.WorkspaceId,
            relationship.SourceEntityId,
            relationship.TargetEntityId,
            relationship.RelationshipKind,
        })
            .IsUnique()
            .HasDatabaseName("ix_entity_relationships_workspace_id_source_target_kind");

        // Tenant foreign key: every relationship hangs off exactly one organization (the owner of
        // its workspace). Cascade delete removes relationships with their tenant (an edge has no
        // meaning without its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(relationship => relationship.OrganizationId)
            .HasConstraintName("fk_entity_relationships_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every relationship hangs off exactly one workspace. Cascade delete
        // removes relationships with their workspace (an edge has no meaning without its workspace).
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(relationship => relationship.WorkspaceId)
            .HasConstraintName("fk_entity_relationships_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Source-endpoint foreign key: the directed edge points FROM this entity. Cascade delete
        // removes the edge when its source entity is deleted. The simple FK guarantees the entity
        // EXISTS but not that it is in the edge's workspace; the same-workspace-endpoints coupling is
        // enforced by the future create-relationship application flow (mirrors Entity/entity_type_id,
        // ContentBlock/scene_id).
        builder.HasOne<Entity>()
            .WithMany()
            .HasForeignKey(relationship => relationship.SourceEntityId)
            .HasConstraintName("fk_entity_relationships_entities_source_entity_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Target-endpoint foreign key: the directed edge points TO this entity. A SECOND cascade FK
        // to the SAME principal table (entities) with a DISTINCT constraint name — PostgreSQL and
        // SQLite both permit multiple cascade paths to one principal, and EF builds two independent
        // single-column relationships, so there is no ambiguity. Cascade delete removes the edge when
        // its target entity is deleted. Same same-workspace-endpoints deferral as the source FK.
        builder.HasOne<Entity>()
            .WithMany()
            .HasForeignKey(relationship => relationship.TargetEntityId)
            .HasConstraintName("fk_entity_relationships_entities_target_entity_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
