// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Organizations;

/// <summary>
/// Relational mapping of <see cref="OrganizationMember"/> to the
/// <c>organization_members</c> table (CORE-ID-004; csv/database_tables.csv:
/// module Organizations, scope <c>organization</c>, "Tenant membership"). The
/// table is tenant-scoped, so it carries an <c>organization_id</c> column that
/// is a foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables include
/// <c>organization_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The
/// subject is referenced by <c>user_id</c>, a foreign key into
/// <c>users(id)</c>, mirroring the documented
/// <c>workspace_members(workspace_id, user_id)</c> shape.
///
/// The unique index on (<c>organization_id</c>, <c>user_id</c>) is the
/// database-level guarantee that a subject has at most one membership row per
/// organization, so a second writer can never create a conflicting second
/// standing for the same subject in the same tenant. Both foreign keys cascade
/// on delete: a membership has no meaning without its organization or its
/// subject. The leading-column index on <c>organization_id</c> keeps
/// tenant-scoped queries (and the foreign-key check) efficient
/// (docs/10_DATABASE_SCHEMA.md critical indexes: composite indexes lead with
/// <c>organization_id</c>).
///
/// Deployment requirement: equality on the id columns is binary, so no
/// collation concern applies (unlike the slug/identity text keys). Pinning any
/// column collation, if ever needed, is planned with the first story that runs
/// migrations against real PostgreSQL (CORE-ID-006 isolation tests).
/// </summary>
internal sealed class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> builder)
    {
        builder.ToTable("organization_members");

        builder.HasKey(member => member.Id);

        builder.Property(member => member.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(member => member.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(member => member.UserProfileId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(member => member.Role)
            .HasColumnName("role")
            // Persist the role as its stable name (not its numeric rank) so
            // the stored authorization value stays readable and is not coupled
            // to the privilege-rank integers, which exist only for in-memory
            // comparison.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(member => member.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(member => member.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // A subject has at most one membership per organization (the natural
        // key of the aggregate). Enforced at the database level so concurrent
        // writers can never create two standings for one subject in one tenant.
        builder.HasIndex(member => new { member.OrganizationId, member.UserProfileId })
            .IsUnique()
            .HasDatabaseName("ix_organization_members_organization_id_user_id");

        // Tenant foreign key: every membership hangs off exactly one
        // organization. Cascade delete removes memberships with their tenant.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(member => member.OrganizationId)
            .HasConstraintName("fk_organization_members_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Subject foreign key: every membership references exactly one user
        // profile. Cascade delete removes memberships with their subject.
        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(member => member.UserProfileId)
            .HasConstraintName("fk_organization_members_users_user_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
