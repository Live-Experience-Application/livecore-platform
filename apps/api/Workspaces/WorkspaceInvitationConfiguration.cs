// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Relational mapping of <see cref="WorkspaceInvitation"/> to the
/// <c>workspace_invitations</c> table (CORE-WS-004). The invitation is
/// workspace-scoped, so it carries a <c>workspace_id</c> foreign key into
/// <c>workspaces(id)</c>, and it is tenant-scoped, so it carries an
/// <c>organization_id</c> foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables include
/// <c>organization_id</c>, workspace-scoped tables include <c>workspace_id</c>;
/// threat T5 in docs/07_SECURITY_THREAT_MODEL.md). Both foreign keys cascade on
/// delete: an invitation has no meaning without its organization or its
/// workspace.
///
/// Token-at-rest model (threats T6/T7): the table stores ONLY the SHA-256
/// <c>token_hash</c>, never the plaintext token. The plaintext is returned to
/// the inviter once at creation and never persisted, so a leaked row cannot be
/// replayed as an invite. The unique index on <c>token_hash</c> is the
/// database-level guarantee that one scoped token addresses at most one
/// invitation. The leading-column composite index on
/// (<c>organization_id</c>, <c>workspace_id</c>) keeps tenant-scoped reads
/// efficient and makes every tenant-scoped lookup lead with
/// <c>organization_id</c> (docs/10_DATABASE_SCHEMA.md critical indexes; the
/// organization boundary is checked before the workspace boundary,
/// docs/06_AUTHORIZATION_MATRIX.md).
///
/// The <c>role</c> and <c>status</c> columns store their stable enum NAMES (not
/// numeric ranks) so the persisted authorization/lifecycle values stay readable
/// and are not coupled to the storage discriminators, mirroring
/// <see cref="WorkspaceMemberConfiguration"/>.
///
/// Deployment requirement: equality on the id columns and the base64 hash is
/// binary, so no collation concern applies. Pinning any column collation, if
/// ever needed, is planned with the first story that runs migrations against
/// real PostgreSQL, exactly as noted for the other Workspaces tables.
/// </summary>
internal sealed class WorkspaceInvitationConfiguration : IEntityTypeConfiguration<WorkspaceInvitation>
{
    public void Configure(EntityTypeBuilder<WorkspaceInvitation> builder)
    {
        // Defence-in-depth CHECK constraint (CORE-CONC-009): the status column stores the
        // WorkspaceInvitationStatus enum by its stable NAME, an invariant the aggregate enforces in memory
        // (Pending -> Accepted / Revoked). The database constraint rejects any other value, so a direct DB
        // write, a future raw-SQL path or a mapping regression can never persist a code-impossible status.
        // Additive and behaviour-neutral.
        builder.ToTable(
            "workspace_invitations",
            table => table.HasCheckConstraint(
                "ck_workspace_invitations_status",
                "status IN ('Pending', 'Accepted', 'Revoked')"));

        builder.HasKey(invitation => invitation.Id);

        builder.Property(invitation => invitation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(invitation => invitation.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(invitation => invitation.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(invitation => invitation.InvitedEmail)
            .HasColumnName("invited_email")
            .HasMaxLength(_oidcPrincipalEmailMaxLength)
            .IsRequired();

        builder.Property(invitation => invitation.Role)
            .HasColumnName("role")
            // Persist the role as its stable name, mirroring workspace_members.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(invitation => invitation.TokenHash)
            .HasColumnName("token_hash")
            // A base64-encoded SHA-256 digest is 44 characters; 88 leaves ample
            // headroom while bounding the column.
            .HasMaxLength(88)
            .IsRequired();

        builder.Property(invitation => invitation.Status)
            .HasColumnName("status")
            // Persist the status as its stable name, mirroring the role column.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(invitation => invitation.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        builder.Property(invitation => invitation.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(invitation => invitation.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // One scoped token addresses at most one invitation: the token hash is
        // unique at the database level, so a presented token can never resolve
        // to two invitations (threat T6).
        builder.HasIndex(invitation => invitation.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_workspace_invitations_token_hash");

        // Tenant-scoped composite index leading with organization_id: keeps the
        // organization boundary check (checked before the workspace boundary)
        // and tenant-scoped reads efficient (docs/10_DATABASE_SCHEMA.md;
        // threat T5).
        builder.HasIndex(invitation => new { invitation.OrganizationId, invitation.WorkspaceId })
            .HasDatabaseName("ix_workspace_invitations_organization_id_workspace_id");

        // Tenant foreign key: every invitation hangs off exactly one
        // organization. Cascade delete removes invitations with their tenant.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(invitation => invitation.OrganizationId)
            .HasConstraintName("fk_workspace_invitations_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every invitation hangs off exactly one
        // workspace. Cascade delete removes invitations with their workspace.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(invitation => invitation.WorkspaceId)
            .HasConstraintName("fk_workspace_invitations_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);
    }

    // The invited email column is sized to the same bound the IdentityAccess
    // module validates emails against (OidcPrincipal.MaxEmailLength = 320, the
    // RFC 5321 maximum), so a valid invited email always fits. Referenced as a
    // local constant to avoid coupling the mapping to an internal member while
    // keeping the value documented in one place.
    private const int _oidcPrincipalEmailMaxLength = 320;
}
