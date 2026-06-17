// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Recaps;

/// <summary>
/// Relational mapping of <see cref="Recap"/> to the <c>recaps</c> table (CORE-AUD-004; the Recaps module's
/// first table; csv/database_tables.csv: module Recaps, scope <c>session</c>, "Produced session recap";
/// docs/05_MODULE_CONTRACTS.md: the Recaps module owns "session recaps"). A recap row is the produced session
/// summary or structured continuation output: which <c>session</c> it summarizes, the tenant
/// (<c>organization_id</c>) and workspace (<c>workspace_id</c>) it belongs to, the optional producing user
/// (<c>generated_by</c>), the recap <c>recap_version</c>, the generic <c>summary</c> body and the
/// <c>generated_at</c> timestamp.
///
/// The recap is session-scoped, so it carries <c>organization_id</c>, <c>workspace_id</c> and
/// <c>session_id</c> columns (docs/10_DATABASE_SCHEMA.md principles: tenant-scoped tables include
/// <c>organization_id</c>, workspace-scoped include <c>workspace_id</c>, session-scoped include
/// <c>session_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md), mirroring <c>session_events</c>. The
/// owning session already pins the workspace and tenant, but storing all three on the row lets isolation be
/// enforced at the row level and the organization boundary be checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles).
///
/// The optional <c>generated_by</c> column is a NULLABLE foreign key into <c>users(id)</c>: a recap may be
/// produced by a host or by the system (docs/09_EVENT_CATALOG.md <c>RecapGenerated</c> source "System/Host"),
/// and a host-produced recap's link SETS NULL when that user is deleted so the recap survives and is
/// anonymized rather than cascade-deleted (mirrors <c>export_jobs.requested_by</c> and
/// <c>assets.created_by</c>).
///
/// Indexes back the documented access patterns:
/// <list type="bullet">
///   <item>The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) is the documented critical index
///   <c>recaps(workspace_id, id)</c>: it keeps workspace-scoped lookups by id efficient and makes every recap
///   read lead with the workspace column (docs/10_DATABASE_SCHEMA.md).</item>
///   <item>The non-unique composite index on (<c>session_id</c>, <c>id</c>) backs the list-by-session read in
///   produced (time-ordered surrogate id) order — a session may have produced more than one recap.</item>
///   <item>The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant
///   boundary check (checked before the workspace boundary) and the organization foreign-key check efficient
///   (threat T5).</item>
///   <item>The PARTIAL UNIQUE index on (<c>session_id</c>) filtered to <c>generated_by IS NULL</c> enforces AT
///   MOST ONE SYSTEM recap per session (CORE-RCP-001): the background generation job can run in any number of
///   worker replicas with overlapping sweeps, each minting a fresh UUIDv7 primary key, so without this index
///   two concurrent sweeps would both insert and a session would carry duplicate system recaps. The filter is
///   what keeps HOST recaps unconstrained — a session may still have many host recaps — so the constraint is
///   expressed as a filtered index exactly like the <c>visibility_rules</c> per-dimension uniqueness
///   (CORE-SVIS-002) and the <c>templates(organization_id)</c> nullable uniqueness.</item>
/// </list>
/// There is deliberately no unique index over the SURROGATE id beyond the primary key, and host recaps carry
/// no natural-key uniqueness (a session may have many host recaps); only the at-most-one-SYSTEM-recap rule
/// above is enforced (CORE-RCP-001).
///
/// FOREIGN KEYS / CASCADE. <c>organization_id</c>, <c>workspace_id</c> and <c>session_id</c> are foreign keys
/// into <c>organizations(id)</c>, <c>workspaces(id)</c> and <c>sessions(id)</c> that CASCADE on delete (a
/// recap has no meaning without its tenant, workspace or session — mirrors <c>session_events</c>); the
/// <c>generated_by</c> user foreign key SETS NULL on delete (the record of who produced a recap is retained
/// when its producer is removed — mirrors <c>export_jobs.requested_by</c>). A recap is WRITE-ONCE — there is
/// no update or delete path on the aggregate or repository — exactly as the append-only audit log and the
/// write-once export manifest are immutable (docs/10_DATABASE_SCHEMA.md).
///
/// The <c>summary</c> is the recap's host content — a bounded generic body, never a vertical term (AGENTS.md);
/// it is host-only until a separate reveal (docs/09_EVENT_CATALOG.md) and is kept out of logs by the
/// aggregate's identifier-only <see cref="Recap.ToString"/> (threat T7).
///
/// Deployment requirement: equality on the id columns is binary, so no collation concern applies. Pinning any
/// column collation, if ever needed, is planned with the first story that runs migrations against real
/// PostgreSQL, exactly as noted for the <c>export_jobs</c>, <c>sessions</c> and <c>session_events</c> tables.
/// </summary>
internal sealed class RecapConfiguration : IEntityTypeConfiguration<Recap>
{
    public void Configure(EntityTypeBuilder<Recap> builder)
    {
        builder.ToTable("recaps");

        builder.HasKey(recap => recap.Id);

        builder.Property(recap => recap.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(recap => recap.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(recap => recap.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(recap => recap.SessionId)
            .HasColumnName("session_id")
            .IsRequired();

        builder.Property(recap => recap.GeneratedByUserProfileId)
            .HasColumnName("generated_by")
            .IsRequired(false);

        builder.Property(recap => recap.RecapVersion)
            .HasColumnName("recap_version")
            .IsRequired();

        builder.Property(recap => recap.Summary)
            .HasColumnName("summary")
            .HasMaxLength(Recap.MaxSummaryLength)
            .IsRequired();

        builder.Property(recap => recap.GeneratedAt)
            .HasColumnName("generated_at")
            .IsRequired();

        // Documented critical index recaps(workspace_id, id): workspace reads lead with the workspace column
        // (docs/10_DATABASE_SCHEMA.md critical indexes).
        builder.HasIndex(recap => new { recap.WorkspaceId, recap.Id })
            .HasDatabaseName("ix_recaps_workspace_id_id");

        // Session-stream index: list a session's recaps in produced (time-ordered surrogate id) order.
        builder.HasIndex(recap => new { recap.SessionId, recap.Id })
            .HasDatabaseName("ix_recaps_session_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the organization boundary check
        // (checked before the workspace boundary) and tenant-scoped reads efficient (threat T5).
        builder.HasIndex(recap => new { recap.OrganizationId, recap.WorkspaceId })
            .HasDatabaseName("ix_recaps_organization_id_workspace_id");

        // AT MOST ONE SYSTEM recap per session (CORE-RCP-001): a PARTIAL UNIQUE index on session_id filtered to
        // the system recaps (generated_by IS NULL). The background generation job can run in any number of
        // worker replicas with overlapping sweeps — eligibility is a NOT EXISTS read decoupled from the insert,
        // and each GenerateBySystem mints a fresh UUIDv7 primary key — so without this index two concurrent
        // sweeps would both insert, leaving a session with duplicate system recaps. The unique index makes the
        // losing insert fail, which RecapRepository.TryAppendSystemRecapAsync converges onto the existing recap
        // (insert-on-conflict), so "a second concurrent sweep produces no duplicate". The filter leaves HOST
        // recaps (generated_by IS NOT NULL) unconstrained (a session may have many). The filter SQL is portable
        // across both providers (plain column name + IS NULL), so EnsureCreated builds it for the SQLite test
        // schema and the migration builds it for PostgreSQL identically — the same pattern as the
        // visibility_rules per-dimension uniqueness (CORE-SVIS-002).
        builder.HasIndex(recap => recap.SessionId)
            .IsUnique()
            .HasFilter("\"generated_by\" IS NULL")
            .HasDatabaseName("ix_recaps_session_id_system_recap");

        // Tenant foreign key: every recap hangs off exactly one organization (the owner of its workspace).
        // Cascade delete removes recaps with their tenant (a recap has no meaning without its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(recap => recap.OrganizationId)
            .HasConstraintName("fk_recaps_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every recap hangs off exactly one workspace. Cascade delete removes recaps
        // with their workspace.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(recap => recap.WorkspaceId)
            .HasConstraintName("fk_recaps_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Session foreign key: a recap summarizes exactly one session; cascade delete removes a session's
        // recaps with the session.
        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(recap => recap.SessionId)
            .HasConstraintName("fk_recaps_sessions_session_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Optional producer foreign key: a recap may reference at most one user profile as its producer. Set
        // null on delete so removing the user anonymizes the recap record rather than deleting it (the record
        // of who produced a recap survives; mirrors export_jobs.requested_by).
        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(recap => recap.GeneratedByUserProfileId)
            .HasConstraintName("fk_recaps_users_generated_by")
            .OnDelete(DeleteBehavior.SetNull);
    }
}
