using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the append-only <c>session_events</c> table: the Realtime module's session-scoped event
/// stream (CORE-RT-003, the Realtime module's first table; csv/database_tables.csv: module Realtime,
/// scope <c>session</c>, "Append-only event stream"). A row is one immutable thing that happened in a
/// session, persisted as the "persist event" step of the delivery flow (docs/11_REALTIME_SYNC.md) and
/// the source of truth reconnect replay reconstructs from (CORE-RT-005).
///
/// The event is session-scoped, so it carries <c>organization_id</c> (the tenant), <c>workspace_id</c>
/// and <c>session_id</c> columns (docs/10_DATABASE_SCHEMA.md; threat T5). The single documented critical
/// index is <c>session_events(session_id, created_at, event_id)</c>: the stream is read per session in
/// append order. EF also adds single-column indexes backing the tenant and workspace foreign keys.
///
/// All three reference columns are foreign keys that CASCADE on delete — an event has no meaning without
/// its session/workspace/tenant, and the live event stream is removed with its session (unlike the
/// long-retained audit log). The <c>created_by</c> and <c>target_participant_id</c> columns are recorded
/// references and are deliberately NOT foreign keys: an appended event is an immutable historical fact
/// that must survive a later user/participant deletion (mirrors <c>visibility_rules.resource_id</c>).
///
/// <c>event_type</c> and the recipient-routing <c>target_participant_id</c> are real columns; only
/// <c>payload</c> is server-composed JSON text (resource identifiers, never resolved content; threat T7).
/// Append-only: there is no soft-delete column and the repository exposes no update or delete. Rollback:
/// <see cref="Down"/> drops the table.
/// </summary>
public partial class AddSessionEvent : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "session_events",
            columns: table => new
            {
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: true),
                target_participant_id = table.Column<Guid>(type: "uuid", nullable: true),
                payload = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                schema_version = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_session_events", x => x.event_id);
                table.ForeignKey(
                    name: "fk_session_events_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_session_events_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_session_events_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_session_events_organization_id",
            table: "session_events",
            column: "organization_id");

        migrationBuilder.CreateIndex(
            name: "ix_session_events_session_id_created_at_event_id",
            table: "session_events",
            columns: new[] { "session_id", "created_at", "event_id" });

        migrationBuilder.CreateIndex(
            name: "IX_session_events_workspace_id",
            table: "session_events",
            column: "workspace_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "session_events");
    }
}
