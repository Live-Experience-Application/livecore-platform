// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Organizations;

/// <summary>
/// Relational mapping of <see cref="Organization"/> to the
/// <c>organizations</c> table (CORE-ID-003; csv/database_tables.csv: scope
/// <c>global</c>, "Tenant root"). The table is global scope: it is the tenant
/// root itself, so it carries no <c>organization_id</c> column. The
/// <c>organizations(id)</c> primary key is the foreign-key target every
/// tenant-scoped table will reference through its own <c>organization_id</c>
/// (docs/10_DATABASE_SCHEMA.md critical indexes).
///
/// The unique index on <c>slug</c> is the database-level tenant boundary
/// guarantee: one slug maps to at most one organization row, so the natural
/// key used for lookups and for matching token organization claims can never
/// address two tenants (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// Deployment requirement: equality on <c>slug</c> must stay exact and
/// case-sensitive. Slugs are canonicalized to lower case in the domain model
/// (<see cref="Organization.CanonicalizeSlug"/>), and the target database
/// needs a deterministic default collation (PostgreSQL default "C"/locale
/// collations qualify) so the unique index and lookups stay exact. Pinning
/// the column collation in the model is planned with the first story that runs
/// migrations against real PostgreSQL (CORE-ID-006 isolation tests), exactly
/// as noted for the <c>users</c> table.
/// </summary>
internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");

        builder.HasKey(organization => organization.Id);

        builder.Property(organization => organization.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(organization => organization.Slug)
            .HasColumnName("slug")
            .HasMaxLength(Organization.MaxSlugLength)
            .IsRequired();

        builder.Property(organization => organization.Name)
            .HasColumnName("name")
            .HasMaxLength(Organization.MaxNameLength)
            .IsRequired();

        builder.Property(organization => organization.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(organization => organization.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(organization => organization.Slug)
            .IsUnique()
            .HasDatabaseName("ix_organizations_slug");
    }
}
