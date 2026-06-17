// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Templates;

/// <summary>
/// Relational mapping of <see cref="Template"/> to the <c>templates</c> table (CORE-ENT-004;
/// csv/database_tables.csv row 19: module Templates, scope <c>global/organization</c>, "Template
/// registry"). The "global/organization" scope is modelled by a single NULLABLE
/// <c>organization_id</c> column: <c>NULL</c> is a GLOBAL template (available to every tenant), a
/// set value is an ORGANIZATION-scoped template owned by one tenant (docs/10_DATABASE_SCHEMA.md
/// principle: tenant-scoped tables include <c>organization_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). This mirrors the optional <c>user_id</c> on
/// <c>participants</c>, which models linked-or-anonymous with one nullable column.
///
/// Columns: <c>template_key</c> stores the canonical dot-separated natural key (the DATA a vertical
/// supplies; Core never branches on its value — the template boundary, docs/04); <c>version</c> is
/// the positive integer template version (docs/05_MODULE_CONTRACTS.md: template versioning);
/// <c>definition</c> stores the template content JSON document. The definition is mapped as a plain
/// text column bounded by <see cref="Template.MaxDefinitionLength"/>; a structured (JSONB) column
/// type is deliberately not used, exactly as for the entity-type attribute schema and the
/// content-block body — docs/10_DATABASE_SCHEMA.md permits JSONB only for flexible attributes and
/// the definition is validated as well-formed, structurally-valid JSON text by
/// <see cref="TemplateDefinitionValidator"/> without coupling the column to a JSONB shape.
///
/// Indexes (docs/10_DATABASE_SCHEMA.md documents no critical index for <c>templates</c>, so these
/// follow the established scope/key pattern):
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>template_key</c>,
///   <c>version</c>) is the key/scope lookup index: scope reads lead with the tenant column and
///   resolve a key+version efficiently. PostgreSQL indexes NULL <c>organization_id</c> values too,
///   so it also serves the global key lookups.
///   </item>
///   <item>
///   The FILTERED unique index on (<c>template_key</c>, <c>version</c>) WHERE
///   <c>organization_id IS NULL</c> is the GLOBAL per-scope natural-key guarantee: the same
///   key+version cannot be registered twice among global templates.
///   </item>
///   <item>
///   The FILTERED unique index on (<c>organization_id</c>, <c>template_key</c>, <c>version</c>)
///   WHERE <c>organization_id IS NOT NULL</c> is the ORGANIZATION per-scope natural-key guarantee:
///   the same key+version cannot be registered twice within one organization, while the same
///   key+version may still exist in a different organization or in the global pool (threat T5).
///   </item>
/// </list>
/// Two FILTERED unique indexes are used (rather than one plain unique index spanning the nullable
/// column) because SQLite — the in-memory provider the tests run against — treats NULL values as
/// DISTINCT in a unique index, so a plain unique index would never reject two global templates that
/// share a key+version. The filter predicates are standard SQL understood by both PostgreSQL and
/// the SQLite provider, exactly like the filtered unique index on <c>participants(workspace_id,
/// user_id)</c>.
///
/// The nullable <c>organization_id</c> foreign key cascades on delete: an organization-scoped
/// template has no meaning without its owning tenant, so removing the organization removes its
/// templates (mirrors the required cascades on <c>workspaces</c>/<c>entity_types</c>). A global
/// template has no organization, so it is unaffected by any tenant deletion.
///
/// Deployment requirement: equality on the id columns is binary, so no collation concern applies.
/// Pinning any column collation, if ever needed, is planned with the first story that runs
/// migrations against real PostgreSQL, exactly as noted for the <c>entity_types</c> and
/// <c>workspaces</c> tables.
/// </summary>
internal sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        builder.ToTable("templates");

        builder.HasKey(template => template.Id);

        builder.Property(template => template.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(template => template.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired(false);

        builder.Property(template => template.TemplateKey)
            .HasColumnName("template_key")
            .HasMaxLength(Template.MaxTemplateKeyLength)
            .IsRequired();

        builder.Property(template => template.Version)
            .HasColumnName("version")
            .IsRequired();

        builder.Property(template => template.Definition)
            .HasColumnName("definition")
            .HasMaxLength(Template.MaxDefinitionLength)
            .IsRequired();

        builder.Property(template => template.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(template => template.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Scope/key lookup index: scope reads lead with the tenant column and resolve key+version
        // efficiently (also serves the global key lookups, where organization_id is NULL).
        builder.HasIndex(template => new { template.OrganizationId, template.TemplateKey, template.Version })
            .HasDatabaseName("ix_templates_organization_id_template_key_version");

        // GLOBAL per-scope uniqueness: key+version unique among global templates (organization_id
        // IS NULL). Filtered so org-scoped rows are excluded; SQLite would otherwise treat the NULL
        // organization_id as distinct and never reject a duplicate global template.
        builder.HasIndex(template => new { template.TemplateKey, template.Version })
            .IsUnique()
            .HasFilter("\"organization_id\" IS NULL")
            .HasDatabaseName("ix_templates_global_template_key_version");

        // ORGANIZATION per-scope uniqueness: key+version unique within one organization
        // (organization_id IS NOT NULL). The same key+version may still exist in another
        // organization or in the global pool (threat T5).
        builder.HasIndex(template => new { template.OrganizationId, template.TemplateKey, template.Version })
            .IsUnique()
            .HasFilter("\"organization_id\" IS NOT NULL")
            .HasDatabaseName("ix_templates_organization_template_key_version");

        // Optional tenant foreign key: an organization-scoped template hangs off exactly one
        // organization; a global template stores NULL. Cascade delete removes an organization's
        // templates with the organization (an org template has no meaning without its tenant); a
        // global template has no organization and is unaffected.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(template => template.OrganizationId)
            .HasConstraintName("fk_templates_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
