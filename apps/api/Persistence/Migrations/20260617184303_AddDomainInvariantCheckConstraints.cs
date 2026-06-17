using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Enforces the high-value domain invariants the aggregates already guard in memory as database CHECK
/// constraints (CORE-CONC-009, the "Audit Integrity and Security Hardening" epic). Before this migration the
/// invariants lived ONLY in the aggregate guards (e.g. the <c>Session</c>/<c>Asset</c> state machines, the
/// <c>Scene</c>/<c>ContentBlock</c> validation, the quota math and <c>SessionEvent.AssignSequence</c>), so a
/// direct DB write (the migration/owner role legitimately retains DELETE for tenant teardown), a future
/// raw-SQL path or a mapping regression could persist a CODE-IMPOSSIBLE state the application can never
/// produce. These constraints close that gap at the database — the same tamper-resistance posture
/// CORE-SEC-004 pursues for the audit log.
///
/// The constraints are:
/// <list type="bullet">
///   <item>status-enum ranges — <c>workspaces</c>, <c>workspace_invitations</c>, <c>sessions</c>,
///   <c>participants</c>, <c>export_jobs</c> and <c>assets</c> each store their lifecycle status by its
///   stable enum NAME, so the constraint allowlists exactly the defined names;</item>
///   <item><c>scenes.scene_order &gt;= 0</c> — the non-negative ordering position;</item>
///   <item><c>content_blocks.revision_number &gt;= 1</c> — the monotonic revision counter starts at 1;</item>
///   <item><c>quota_usage.used_amount &gt;= 0</c> — recorded usage never goes negative;</item>
///   <item><c>assets.size_bytes</c> is null (pending) or non-negative — the byte counter;</item>
///   <item><c>session_events.sequence &gt;= 1</c> — the per-session position is strictly positive.</item>
/// </list>
///
/// ADDITIVE AND BEHAVIOUR-NEUTRAL (expand-only). Every value the aggregates produce already satisfies these
/// constraints, so valid writes are unaffected and only an out-of-range value is rejected. <see cref="Up"/>
/// only ADDs constraints; <see cref="Down"/> only DROPs them (no DropColumn/DropTable), so it loses no row
/// data and is not a data-destroying Down — the destructive-down review
/// (csv/migration_destructive_down_review.csv) is unaffected. This deliberately does NOT add the
/// intentionally-absent polymorphic foreign keys (CORE-SPEC-003 register).
/// </summary>
public partial class AddDomainInvariantCheckConstraints : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Status-enum ranges: each lifecycle status is stored by its stable enum NAME, so the allowlist is
        // exactly the defined names of the corresponding enum.
        migrationBuilder.AddCheckConstraint(
            name: "ck_workspaces_status",
            table: "workspaces",
            sql: "status IN ('Active', 'Archived')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_workspace_invitations_status",
            table: "workspace_invitations",
            sql: "status IN ('Pending', 'Accepted', 'Revoked')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_status",
            table: "sessions",
            sql: "status IN ('Prepared', 'Live', 'Ended', 'Cancelled')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_participants_status",
            table: "participants",
            sql: "status IN ('Active', 'Removed')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_export_jobs_status",
            table: "export_jobs",
            sql: "status IN ('Pending', 'Running', 'Completed', 'Failed')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_assets_status",
            table: "assets",
            sql: "status IN ('Pending', 'Available')");

        // Numeric / counter invariants the aggregates enforce.
        migrationBuilder.AddCheckConstraint(
            name: "ck_scenes_scene_order",
            table: "scenes",
            sql: "scene_order >= 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_content_blocks_revision_number",
            table: "content_blocks",
            sql: "revision_number >= 1");

        migrationBuilder.AddCheckConstraint(
            name: "ck_quota_usage_used_amount",
            table: "quota_usage",
            sql: "used_amount >= 0");

        // The size byte counter is null while the asset is pending and stamped (non-negative) on confirmation.
        migrationBuilder.AddCheckConstraint(
            name: "ck_assets_size_bytes",
            table: "assets",
            sql: "size_bytes IS NULL OR size_bytes >= 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_session_events_sequence",
            table: "session_events",
            sql: "sequence >= 1");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Drops only the constraints this migration added; loses no row data (not a destructive Down).
        migrationBuilder.DropCheckConstraint(
            name: "ck_workspaces_status",
            table: "workspaces");

        migrationBuilder.DropCheckConstraint(
            name: "ck_workspace_invitations_status",
            table: "workspace_invitations");

        migrationBuilder.DropCheckConstraint(
            name: "ck_sessions_status",
            table: "sessions");

        migrationBuilder.DropCheckConstraint(
            name: "ck_participants_status",
            table: "participants");

        migrationBuilder.DropCheckConstraint(
            name: "ck_export_jobs_status",
            table: "export_jobs");

        migrationBuilder.DropCheckConstraint(
            name: "ck_assets_status",
            table: "assets");

        migrationBuilder.DropCheckConstraint(
            name: "ck_scenes_scene_order",
            table: "scenes");

        migrationBuilder.DropCheckConstraint(
            name: "ck_content_blocks_revision_number",
            table: "content_blocks");

        migrationBuilder.DropCheckConstraint(
            name: "ck_quota_usage_used_amount",
            table: "quota_usage");

        migrationBuilder.DropCheckConstraint(
            name: "ck_assets_size_bytes",
            table: "assets");

        migrationBuilder.DropCheckConstraint(
            name: "ck_session_events_sequence",
            table: "session_events");
    }
}
