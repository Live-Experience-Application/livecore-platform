// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Entities;

/// <summary>
/// Relational mapping of <see cref="EntityType"/> to the <c>entity_types</c> table
/// (CORE-ENT-001; csv/database_tables.csv: module Entities/Templates, scope
/// <c>workspace/template</c>, "Template-defined types"). The "template" half of the scope is
/// the later CORE-ENT-004 template loading; THIS story is the workspace half, so the entity
/// type carries a <c>workspace_id</c> column that is a foreign key into <c>workspaces(id)</c>
/// and is also tenant-scoped, carrying an <c>organization_id</c> column that is a foreign key
/// into <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principles: tenant-scoped tables
/// include <c>organization_id</c>, workspace-scoped tables include <c>workspace_id</c>; threat
/// T5 in docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already pins the
/// organization, but storing the tenant on the row lets isolation be enforced at the row level
/// and lets the organization boundary be checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles). There is deliberately NO
/// <c>template_id</c> column: there is no templates table yet, and binding a type to a loaded
/// template is the later CORE-ENT-004 story (docs/05_MODULE_CONTRACTS.md: the Templates module
/// owns the template loader).
///
/// Columns: <c>type_key</c> stores the canonical, lower-case natural key (the DATA a template
/// supplies; Core never branches on its value — the template boundary, docs/04); <c>display_name</c>
/// stores the human-readable label; <c>attribute_schema</c> stores the template-defined
/// attribute JSON document. The attribute schema is mapped as a plain text column bounded by
/// <see cref="EntityType.MaxAttributeSchemaLength"/>; a structured (JSONB) column type is
/// deliberately not used, exactly as for the content-block body — docs/10_DATABASE_SCHEMA.md
/// permits JSONB only for flexible attributes and the schema is validated as well-formed JSON
/// text by <see cref="AttributeSchemaValidator"/> without coupling the column to a JSONB shape.
///
/// Three indexes back the documented access patterns and the identity invariant
/// (docs/10_DATABASE_SCHEMA.md documents no critical index for <c>entity_types</c>, so these
/// follow the established Scene/Participant pattern):
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) is the chosen critical
///   index following the documented <c>scenes(workspace_id, id)</c> shape: it keeps
///   workspace-scoped lookups by id efficient and makes every read lead with the workspace
///   column.
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the
///   tenant boundary check (checked before the workspace boundary) and the organization
///   foreign-key check efficient and makes tenant-scoped reads lead with <c>organization_id</c>
///   (threat T5).
///   </item>
///   <item>
///   The UNIQUE composite index on (<c>workspace_id</c>, <c>type_key</c>) is the per-workspace
///   natural-key guarantee: a workspace cannot define the same type twice, so a second writer
///   can never create a duplicate type for one key in one workspace. It is workspace-scoped, so
///   the same key may legitimately exist in two different workspaces (threat T5). It also backs
///   the by-key lookup.
///   </item>
/// </list>
///
/// Both foreign keys cascade on delete: an entity type has no meaning without its workspace or
/// its tenant, so removing the organization or the workspace removes its entity types (mirrors
/// the required cascades on <c>scenes</c>, <c>sessions</c> and <c>participants</c>).
///
/// Deployment requirement: equality on the id columns is binary, so no collation concern
/// applies. Pinning any column collation, if ever needed, is planned with the first story that
/// runs migrations against real PostgreSQL, exactly as noted for the <c>scenes</c>,
/// <c>content_blocks</c>, <c>workspaces</c> and <c>workspace_members</c> tables.
/// </summary>
internal sealed class EntityTypeConfiguration : IEntityTypeConfiguration<EntityType>
{
    public void Configure(EntityTypeBuilder<EntityType> builder)
    {
        builder.ToTable("entity_types");

        builder.HasKey(entityType => entityType.Id);

        builder.Property(entityType => entityType.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(entityType => entityType.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(entityType => entityType.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(entityType => entityType.TypeKey)
            .HasColumnName("type_key")
            .HasMaxLength(EntityType.MaxTypeKeyLength)
            .IsRequired();

        builder.Property(entityType => entityType.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(EntityType.MaxDisplayNameLength)
            .IsRequired();

        builder.Property(entityType => entityType.AttributeSchema)
            .HasColumnName("attribute_schema")
            .HasMaxLength(EntityType.MaxAttributeSchemaLength)
            .IsRequired();

        builder.Property(entityType => entityType.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(entityType => entityType.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Chosen critical index entity_types(workspace_id, id) following the documented
        // scenes(workspace_id, id) shape: workspace reads lead with the workspace column
        // (docs/10_DATABASE_SCHEMA.md — entity_types has no documented critical index, so this
        // follows the established pattern).
        builder.HasIndex(entityType => new { entityType.WorkspaceId, entityType.Id })
            .HasDatabaseName("ix_entity_types_workspace_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the organization
        // boundary check (checked before the workspace boundary) and tenant-scoped reads
        // efficient (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(entityType => new { entityType.OrganizationId, entityType.WorkspaceId })
            .HasDatabaseName("ix_entity_types_organization_id_workspace_id");

        // Per-workspace unique natural key: a workspace cannot define the same type twice.
        // Enforced at the database level with a unique index so concurrent writers can never
        // create a duplicate type for one key in one workspace. It is workspace-scoped, so the
        // same key may legitimately exist in two different workspaces (threat T5). Also backs
        // the by-key lookup.
        builder.HasIndex(entityType => new { entityType.WorkspaceId, entityType.TypeKey })
            .IsUnique()
            .HasDatabaseName("ix_entity_types_workspace_id_type_key");

        // Tenant foreign key: every entity type hangs off exactly one organization (the owner
        // of its workspace). Cascade delete removes entity types with their tenant (a type has
        // no meaning without its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(entityType => entityType.OrganizationId)
            .HasConstraintName("fk_entity_types_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every entity type hangs off exactly one workspace. Cascade
        // delete removes entity types with their workspace (a type has no meaning without its
        // workspace).
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(entityType => entityType.WorkspaceId)
            .HasConstraintName("fk_entity_types_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
