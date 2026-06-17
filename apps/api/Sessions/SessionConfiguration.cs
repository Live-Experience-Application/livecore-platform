// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Sessions;

/// <summary>
/// Relational mapping of <see cref="Session"/> to the <c>sessions</c> table
/// (CORE-SES-002; csv/database_tables.csv: module Sessions, scope <c>workspace</c>,
/// "Live/prepared run"). The session is workspace-scoped, so it carries a
/// <c>workspace_id</c> column that is a foreign key into <c>workspaces(id)</c>, the
/// documented <c>sessions(workspace_id, id)</c> shape (docs/10_DATABASE_SCHEMA.md
/// critical indexes). It is also tenant-scoped, so it carries an
/// <c>organization_id</c> column that is a foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md principle: "tenant-scoped tables include
/// <c>organization_id</c>"; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The
/// owning workspace already pins the organization, but storing the tenant on the
/// row lets isolation be enforced at the row level and lets the organization
/// boundary be checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles).
///
/// Lifecycle columns: <c>status</c> stores the lifecycle status name
/// (Prepared/Live/Ended, plus Cancelled for a soft cancel — CORE-LIFE-010), and the
/// NULLABLE <c>started_at</c>/<c>ended_at</c> timestamptz columns capture the
/// live-timeline boundaries — null while the session is still prepared (and they stay
/// null for a cancelled session, which never ran), then stamped by the start and end
/// transitions (docs/09_EVENT_CATALOG.md: <c>SessionStarted</c> "starts live
/// timeline", <c>SessionEnded</c> "ends live timeline"). Cancelled is a new value in
/// the existing string <c>status</c> column (persisted by name), so it needs NO schema
/// migration.
///
/// Two indexes back the documented access patterns:
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) is the
///   documented critical index <c>sessions(workspace_id, id)</c>: it keeps
///   workspace-scoped lookups by id efficient and makes every session read lead
///   with the workspace column (docs/10_DATABASE_SCHEMA.md critical indexes).
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>)
///   keeps the tenant boundary check (checked before the workspace boundary) and
///   the organization foreign-key check efficient and makes tenant-scoped reads
///   lead with <c>organization_id</c> (threat T5).
///   </item>
/// </list>
/// There is deliberately NO unique natural-key index: a session is identified only
/// by its surrogate id, so a workspace may hold many sessions with the same title.
///
/// Both foreign keys cascade on delete: a session has no meaning without its
/// workspace or its tenant, so removing the organization or the workspace removes
/// its sessions (mirrors the required cascades on <c>participants</c>). There is no
/// active-scene foreign key yet: the <c>scenes</c> table does not exist
/// (CORE-SCENE-001), so adding one now would be a forward dependency — it is a
/// follow-up.
///
/// Deployment requirement: equality on the id columns is binary, so no collation
/// concern applies. Pinning any column collation, if ever needed, is planned with
/// the first story that runs migrations against real PostgreSQL, exactly as noted
/// for the <c>workspaces</c>, <c>workspace_members</c> and <c>participants</c>
/// tables.
/// </summary>
internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");

        builder.HasKey(session => session.Id);

        builder.Property(session => session.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(session => session.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(session => session.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(session => session.Title)
            .HasColumnName("title")
            .HasMaxLength(Session.MaxTitleLength)
            .IsRequired();

        builder.Property(session => session.Status)
            .HasColumnName("status")
            // Persist the status as its stable name (not its numeric value) so the
            // stored lifecycle value stays readable and is not coupled to the
            // declaration-order integers, which exist only as in-memory storage
            // discriminators.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(session => session.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(session => session.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // The live-timeline boundaries are null until the session starts/ends, so
        // both columns are nullable timestamptz (docs/09_EVENT_CATALOG.md).
        builder.Property(session => session.StartedAt)
            .HasColumnName("started_at")
            .IsRequired(false);

        builder.Property(session => session.EndedAt)
            .HasColumnName("ended_at")
            .IsRequired(false);

        // Documented critical index sessions(workspace_id, id): workspace reads
        // lead with the workspace column (docs/10_DATABASE_SCHEMA.md).
        builder.HasIndex(session => new { session.WorkspaceId, session.Id })
            .HasDatabaseName("ix_sessions_workspace_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the
        // organization boundary check (checked before the workspace boundary) and
        // tenant-scoped reads efficient (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(session => new { session.OrganizationId, session.WorkspaceId })
            .HasDatabaseName("ix_sessions_organization_id_workspace_id");

        // Tenant foreign key: every session hangs off exactly one organization (the
        // owner of its workspace). Cascade delete removes sessions with their
        // tenant (a session has no meaning without its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(session => session.OrganizationId)
            .HasConstraintName("fk_sessions_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every session hangs off exactly one workspace.
        // Cascade delete removes sessions with their workspace (a session has no
        // meaning without its workspace).
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(session => session.WorkspaceId)
            .HasConstraintName("fk_sessions_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
