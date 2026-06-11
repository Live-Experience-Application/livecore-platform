using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Relational mapping of <see cref="WorkspaceMember"/> to the
/// <c>workspace_members</c> table (CORE-WS-002; csv/database_tables.csv: module
/// Workspaces, scope <c>workspace</c>, "Workspace roles"). The membership is
/// workspace-scoped, so it carries a <c>workspace_id</c> column that is a
/// foreign key into <c>workspaces(id)</c>, and its subject is referenced by
/// <c>user_id</c>, a foreign key into <c>users(id)</c>, exactly the documented
/// <c>workspace_members(workspace_id, user_id)</c> shape
/// (docs/10_DATABASE_SCHEMA.md critical indexes).
///
/// The table is additionally tenant-scoped: it carries an <c>organization_id</c>
/// column that is a foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md principle: "tenant-scoped tables include
/// <c>organization_id</c>"; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The
/// owning workspace already pins the organization, but storing the tenant on the
/// row lets isolation be enforced at the row level and lets the organization
/// boundary be checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles).
///
/// The unique index on (<c>workspace_id</c>, <c>user_id</c>) is the
/// database-level guarantee that a subject has at most one membership row per
/// workspace, so a second writer can never create a conflicting second standing
/// for the same subject in the same workspace. The leading-column composite
/// index on (<c>organization_id</c>, <c>workspace_id</c>) keeps tenant-scoped
/// lookups and the organization foreign-key check efficient and makes every
/// tenant-scoped read lead with <c>organization_id</c>
/// (docs/10_DATABASE_SCHEMA.md critical indexes: composite indexes lead with
/// <c>organization_id</c>/<c>workspace_id</c>). All three foreign keys cascade
/// on delete: a membership has no meaning without its organization, its
/// workspace or its subject.
///
/// Deployment requirement: equality on the id columns is binary, so no collation
/// concern applies (unlike the slug/identity text keys). Pinning any column
/// collation, if ever needed, is planned with the first story that runs
/// migrations against real PostgreSQL, exactly as noted for the
/// <c>workspaces</c> and <c>organization_members</c> tables.
/// </summary>
internal sealed class WorkspaceMemberConfiguration : IEntityTypeConfiguration<WorkspaceMember>
{
    public void Configure(EntityTypeBuilder<WorkspaceMember> builder)
    {
        builder.ToTable("workspace_members");

        builder.HasKey(member => member.Id);

        builder.Property(member => member.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(member => member.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(member => member.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(member => member.UserProfileId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(member => member.Role)
            .HasColumnName("role")
            // Persist the role as its stable name (not its numeric rank) so the
            // stored authorization value stays readable and is not coupled to
            // the column-order integers, which exist only as in-memory storage
            // discriminators.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(member => member.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(member => member.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // A subject has at most one membership per workspace (the natural key of
        // the aggregate). Enforced at the database level so concurrent writers
        // can never create two standings for one subject in one workspace. This
        // is the documented critical index workspace_members(workspace_id,
        // user_id) (docs/10_DATABASE_SCHEMA.md).
        builder.HasIndex(member => new { member.WorkspaceId, member.UserProfileId })
            .IsUnique()
            .HasDatabaseName("ix_workspace_members_workspace_id_user_id");

        // Tenant-scoped composite index leading with organization_id: keeps the
        // organization boundary check (checked before the workspace boundary)
        // and tenant-scoped reads efficient (docs/10_DATABASE_SCHEMA.md;
        // threat T5).
        builder.HasIndex(member => new { member.OrganizationId, member.WorkspaceId })
            .HasDatabaseName("ix_workspace_members_organization_id_workspace_id");

        // Tenant foreign key: every membership hangs off exactly one
        // organization (the owner of its workspace). Cascade delete removes
        // memberships with their tenant.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(member => member.OrganizationId)
            .HasConstraintName("fk_workspace_members_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every membership hangs off exactly one
        // workspace. Cascade delete removes memberships with their workspace.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(member => member.WorkspaceId)
            .HasConstraintName("fk_workspace_members_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Subject foreign key: every membership references exactly one user
        // profile. Cascade delete removes memberships with their subject.
        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(member => member.UserProfileId)
            .HasConstraintName("fk_workspace_members_users_user_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
