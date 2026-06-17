// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Participants;

/// <summary>
/// Relational mapping of <see cref="Participant"/> to the <c>participants</c>
/// table (CORE-SES-001; csv/database_tables.csv: module Participants, scope
/// <c>workspace</c>, "Session-facing participant"). The participant is
/// workspace-scoped, so it carries a <c>workspace_id</c> column that is a foreign
/// key into <c>workspaces(id)</c>, the documented
/// <c>participants(workspace_id, id)</c> shape (docs/10_DATABASE_SCHEMA.md
/// critical indexes). It is also tenant-scoped, so it carries an
/// <c>organization_id</c> column that is a foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principle: "tenant-scoped
/// tables include <c>organization_id</c>"; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already pins the
/// organization, but storing the tenant on the row lets isolation be enforced at
/// the row level and lets the organization boundary be checked before the
/// workspace boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles).
///
/// The optional <c>user_id</c> column is a NULLABLE foreign key into
/// <c>users(id)</c>: a participant "may be linked to a user"
/// (docs/03_DOMAIN_LANGUAGE.md), and an anonymous/guest participant stores
/// <see langword="null"/> there.
///
/// Three indexes back the documented access patterns and the identity invariant:
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) is the
///   documented critical index <c>participants(workspace_id, id)</c>: it keeps
///   workspace-scoped lookups by id efficient and makes every participant read
///   lead with the workspace column (docs/10_DATABASE_SCHEMA.md critical
///   indexes).
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>,
///   <c>workspace_id</c>) keeps the tenant boundary check (checked before the
///   workspace boundary) and the organization foreign-key check efficient and
///   makes tenant-scoped reads lead with <c>organization_id</c> (threat T5).
///   </item>
///   <item>
///   The FILTERED unique index on (<c>workspace_id</c>, <c>user_id</c>) where
///   <c>user_id IS NOT NULL</c> is the database-level guarantee that a user is at
///   most one participant per workspace, so a second writer can never create a
///   duplicate participant for one user in one workspace. The filter excludes
///   anonymous participants (whose <c>user_id</c> is null), so a workspace may
///   hold many anonymous participants without colliding on the null user link
///   (threat T5).
///   </item>
/// </list>
///
/// All three foreign keys cascade on delete EXCEPT the optional user link, which
/// sets null on delete: removing the organization or the workspace removes its
/// participants (a participant has no meaning without them), but removing the
/// linked user must not delete the participant record (history of who took part
/// is retained) — the link is cleared instead, leaving an anonymized participant.
/// The <c>status</c> column stores the participant lifecycle status name
/// (docs/10_DATABASE_SCHEMA.md soft-delete principle).
///
/// Deployment requirement: equality on the id columns is binary, so no collation
/// concern applies. Pinning any column collation, if ever needed, is planned with
/// the first story that runs migrations against real PostgreSQL, exactly as noted
/// for the <c>workspaces</c> and <c>workspace_members</c> tables.
/// </summary>
internal sealed class ParticipantConfiguration : IEntityTypeConfiguration<Participant>
{
    public void Configure(EntityTypeBuilder<Participant> builder)
    {
        // Defence-in-depth CHECK constraint (CORE-CONC-009): the status column stores the ParticipantStatus
        // enum by its stable NAME, an invariant the aggregate enforces in memory. The database constraint
        // rejects any other value, so a direct DB write, a future raw-SQL path or a mapping regression can
        // never persist a code-impossible status. Additive and behaviour-neutral.
        builder.ToTable(
            "participants",
            table => table.HasCheckConstraint(
                "ck_participants_status",
                "status IN ('Active', 'Removed')"));

        builder.HasKey(participant => participant.Id);

        builder.Property(participant => participant.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(participant => participant.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(participant => participant.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(participant => participant.UserProfileId)
            .HasColumnName("user_id")
            .IsRequired(false);

        builder.Property(participant => participant.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(Participant.MaxDisplayNameLength)
            .IsRequired();

        builder.Property(participant => participant.Status)
            .HasColumnName("status")
            // Persist the status as its stable name (not its numeric value) so
            // the stored lifecycle value stays readable and is not coupled to the
            // declaration-order integers, which exist only as in-memory storage
            // discriminators.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(participant => participant.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(participant => participant.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Documented critical index participants(workspace_id, id): workspace
        // reads lead with the workspace column (docs/10_DATABASE_SCHEMA.md).
        builder.HasIndex(participant => new { participant.WorkspaceId, participant.Id })
            .HasDatabaseName("ix_participants_workspace_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the
        // organization boundary check (checked before the workspace boundary)
        // and tenant-scoped reads efficient (docs/10_DATABASE_SCHEMA.md;
        // threat T5).
        builder.HasIndex(participant => new { participant.OrganizationId, participant.WorkspaceId })
            .HasDatabaseName("ix_participants_organization_id_workspace_id");

        // A user is at most one participant per workspace. Enforced at the
        // database level with a FILTERED unique index so concurrent writers can
        // never create a duplicate participant for one user in one workspace,
        // while anonymous participants (user_id is null) are excluded from the
        // constraint and may coexist freely (threat T5). The filter predicate is
        // standard SQL understood by both PostgreSQL and the SQLite provider the
        // tests use.
        builder.HasIndex(participant => new { participant.WorkspaceId, participant.UserProfileId })
            .IsUnique()
            .HasFilter("\"user_id\" IS NOT NULL")
            .HasDatabaseName("ix_participants_workspace_id_user_id");

        // Tenant foreign key: every participant hangs off exactly one
        // organization (the owner of its workspace). Cascade delete removes
        // participants with their tenant.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(participant => participant.OrganizationId)
            .HasConstraintName("fk_participants_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every participant hangs off exactly one
        // workspace. Cascade delete removes participants with their workspace.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(participant => participant.WorkspaceId)
            .HasConstraintName("fk_participants_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Optional subject foreign key: a participant may reference at most one
        // user profile. Set null on delete so removing the user anonymizes the
        // participant record rather than deleting the history of who took part.
        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(participant => participant.UserProfileId)
            .HasConstraintName("fk_participants_users_user_id")
            .OnDelete(DeleteBehavior.SetNull);
    }
}
