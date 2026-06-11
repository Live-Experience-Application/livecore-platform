using LiveCore.Api.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Relational mapping of <see cref="Workspace"/> to the <c>workspaces</c> table
/// (CORE-WS-001; csv/database_tables.csv: module Workspaces, scope
/// <c>organization</c>, "Generic workspace"). The table is tenant-scoped, so it
/// carries an <c>organization_id</c> column that is a foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables
/// include <c>organization_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). The <c>workspaces(id)</c> primary key is
/// the foreign-key target workspace-scoped tables will reference through their
/// own <c>workspace_id</c> (docs/10_DATABASE_SCHEMA.md critical indexes).
///
/// Two indexes back the documented access patterns and the identity invariant:
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>id</c>) is
///   the documented critical index <c>workspaces(organization_id, id)</c>: it
///   keeps tenant-scoped lookups by id efficient and makes every workspace read
///   lead with the tenant column (docs/10_DATABASE_SCHEMA.md critical indexes;
///   composite indexes lead with <c>organization_id</c>).
///   </item>
///   <item>
///   The unique composite index on (<c>organization_id</c>, <c>slug</c>) is the
///   database-level guarantee that a workspace slug is unique WITHIN one
///   organization but not globally: the same slug may legitimately exist in two
///   different organizations, but a second writer can never create two
///   workspaces with one slug in one tenant. Per-tenant (not global) uniqueness
///   keeps the natural key from blurring the tenant boundary (threat T5).
///   </item>
/// </list>
///
/// The organization foreign key cascades on delete: a workspace has no meaning
/// without its organization, so removing a tenant removes its workspaces.
///
/// Deployment requirement: equality on <c>slug</c> must stay exact and
/// case-sensitive. Slugs are canonicalized to lower case in the domain model
/// (<see cref="Workspace.CanonicalizeSlug"/>), and the target database needs a
/// deterministic default collation (PostgreSQL default "C"/locale collations
/// qualify) so the unique index and lookups stay exact. Pinning the column
/// collation in the model is planned with the first story that runs migrations
/// against real PostgreSQL, exactly as noted for the <c>organizations</c> and
/// <c>users</c> tables.
/// </summary>
internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.ToTable("workspaces");

        builder.HasKey(workspace => workspace.Id);

        builder.Property(workspace => workspace.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(workspace => workspace.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(workspace => workspace.Slug)
            .HasColumnName("slug")
            .HasMaxLength(Workspace.MaxSlugLength)
            .IsRequired();

        builder.Property(workspace => workspace.Name)
            .HasColumnName("name")
            .HasMaxLength(Workspace.MaxNameLength)
            .IsRequired();

        builder.Property(workspace => workspace.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(workspace => workspace.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Documented critical index workspaces(organization_id, id): tenant
        // reads lead with the organization column (docs/10_DATABASE_SCHEMA.md).
        builder.HasIndex(workspace => new { workspace.OrganizationId, workspace.Id })
            .HasDatabaseName("ix_workspaces_organization_id_id");

        // A slug is unique WITHIN one organization (the per-tenant natural key),
        // not globally: enforced at the database level so concurrent writers can
        // never create two workspaces with one slug in one tenant, while the
        // same slug stays available to other tenants (threat T5).
        builder.HasIndex(workspace => new { workspace.OrganizationId, workspace.Slug })
            .IsUnique()
            .HasDatabaseName("ix_workspaces_organization_id_slug");

        // Tenant foreign key: every workspace hangs off exactly one
        // organization. Cascade delete removes workspaces with their tenant.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(workspace => workspace.OrganizationId)
            .HasConstraintName("fk_workspaces_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
