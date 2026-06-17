// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Entities;

/// <summary>
/// Relational mapping of <see cref="Entity"/> to the <c>entities</c> table (CORE-ENT-002;
/// csv/database_tables.csv: module Entities, scope <c>workspace</c>, "Generic objects"). An
/// entity is an INSTANCE of an <see cref="EntityType"/>, so it carries an <c>entity_type_id</c>
/// column that is a foreign key into <c>entity_types(id)</c>. It is workspace-scoped, so it
/// carries a <c>workspace_id</c> column that is a foreign key into <c>workspaces(id)</c>, and
/// tenant-scoped, so it carries an <c>organization_id</c> column that is a foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principles: tenant-scoped tables include
/// <c>organization_id</c>, workspace-scoped tables include <c>workspace_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already pins the organization, but
/// storing the tenant on the row lets isolation be enforced at the row level and lets the
/// organization boundary be checked before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md
/// authorization principles).
///
/// FK design — three SIMPLE single-column foreign keys, mirroring <c>ContentBlock</c> (which has
/// three single-column FKs to organizations, workspaces and scenes, NOT composite FKs). The
/// <c>entity_type_id</c> FK guarantees the referenced type EXISTS but NOT that it is in the SAME
/// workspace — exactly like <c>ContentBlock.scene_id</c> does not DB-enforce that the scene is in
/// the same workspace. That same-workspace-type coupling is the responsibility of the future
/// create-entity application flow (it resolves the type through the workspace-scoped
/// <c>IEntityTypeRepository.FindByIdAsync(org, ws, typeId)</c> before creating the entity); a
/// composite FK or a unique index on <c>entity_types</c> is deliberately NOT added here, because
/// that would diverge from the established ContentBlock/scene_id precedent and touch the
/// CORE-ENT-001 table.
///
/// Columns: <c>name</c> stores the instance's human label; <c>attribute_values</c> stores the
/// instance's actual attribute JSON document. The attribute values are mapped as a plain text
/// column bounded by <see cref="Entity.MaxAttributeValuesLength"/>; a structured (JSONB) column
/// type is deliberately not used, exactly as for the entity-type attribute schema and the
/// content-block body — docs/10_DATABASE_SCHEMA.md permits JSONB only for flexible attributes and
/// the values are validated as well-formed JSON text by <see cref="AttributeValuesValidator"/>
/// without coupling the column to a JSONB shape.
///
/// Three indexes back the documented access patterns (docs/10_DATABASE_SCHEMA.md documents no
/// critical index for <c>entities</c>, so these follow the established Scene/ContentBlock pattern):
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) is the chosen critical
///   index following the documented <c>scenes(workspace_id, id)</c> shape: it keeps
///   workspace-scoped lookups by id efficient and makes every read lead with the workspace column.
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the
///   tenant boundary check (checked before the workspace boundary) and the organization
///   foreign-key check efficient and makes tenant-scoped reads lead with <c>organization_id</c>
///   (threat T5).
///   </item>
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>entity_type_id</c>) backs the
///   list-by-type access path (<see cref="EntityRepository.ListByTypeAsync"/>) and the
///   <c>entity_type_id</c> foreign-key check, leading with the workspace column.
///   </item>
/// </list>
/// There is deliberately NO unique natural-key index: an entity is identified only by its
/// surrogate id, so a workspace may hold many entities with the same name and type.
///
/// All three foreign keys cascade on delete: an entity has no meaning without its tenant, its
/// workspace or its type, so removing the organization, the workspace or the entity type removes
/// its instances (mirrors the established cascades on <c>scenes</c>, <c>content_blocks</c> and
/// <c>entity_types</c> — deleting a type cascades to its instances, consistent with the rest of the
/// schema).
///
/// Deployment requirement: equality on the id columns is binary, so no collation concern applies.
/// Pinning any column collation, if ever needed, is planned with the first story that runs
/// migrations against real PostgreSQL, exactly as noted for the <c>scenes</c>, <c>content_blocks</c>
/// and <c>entity_types</c> tables.
/// </summary>
internal sealed class EntityConfiguration : IEntityTypeConfiguration<Entity>
{
    public void Configure(EntityTypeBuilder<Entity> builder)
    {
        builder.ToTable("entities");

        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(entity => entity.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(entity => entity.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(entity => entity.EntityTypeId)
            .HasColumnName("entity_type_id")
            .IsRequired();

        builder.Property(entity => entity.Name)
            .HasColumnName("name")
            .HasMaxLength(Entity.MaxNameLength)
            .IsRequired();

        builder.Property(entity => entity.AttributeValues)
            .HasColumnName("attribute_values")
            .HasMaxLength(Entity.MaxAttributeValuesLength)
            .IsRequired();

        builder.Property(entity => entity.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(entity => entity.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Chosen critical index entities(workspace_id, id) following the documented
        // scenes(workspace_id, id) shape: workspace reads lead with the workspace column
        // (docs/10_DATABASE_SCHEMA.md — entities has no documented critical index, so this follows
        // the established pattern).
        builder.HasIndex(entity => new { entity.WorkspaceId, entity.Id })
            .HasDatabaseName("ix_entities_workspace_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the organization
        // boundary check (checked before the workspace boundary) and tenant-scoped reads efficient
        // (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId })
            .HasDatabaseName("ix_entities_organization_id_workspace_id");

        // List-by-type composite index entities(workspace_id, entity_type_id): backs the
        // list-by-type access path and the entity_type_id foreign-key check, leading with the
        // workspace column.
        builder.HasIndex(entity => new { entity.WorkspaceId, entity.EntityTypeId })
            .HasDatabaseName("ix_entities_workspace_id_entity_type_id");

        // Tenant foreign key: every entity hangs off exactly one organization (the owner of its
        // workspace). Cascade delete removes entities with their tenant (an entity has no meaning
        // without its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(entity => entity.OrganizationId)
            .HasConstraintName("fk_entities_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every entity hangs off exactly one workspace. Cascade delete
        // removes entities with their workspace (an entity has no meaning without its workspace).
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(entity => entity.WorkspaceId)
            .HasConstraintName("fk_entities_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Type foreign key: every entity is an instance of exactly one entity type. Cascade delete
        // removes an entity type's instances with the type, consistent with the rest of the schema
        // (ContentBlock/Scene all cascade). The simple FK guarantees the type EXISTS but not that
        // it is in the entity's workspace; the same-workspace-type coupling is enforced by the
        // future create-entity application flow (mirrors ContentBlock/scene_id).
        builder.HasOne<EntityType>()
            .WithMany()
            .HasForeignKey(entity => entity.EntityTypeId)
            .HasConstraintName("fk_entities_entity_types_entity_type_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
