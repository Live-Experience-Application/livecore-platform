using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>sessions</c> table: the live/prepared run record of the Sessions
/// module (CORE-SES-002, csv/database_tables.csv: scope <c>workspace</c>,
/// "Live/prepared run"). The session is workspace-scoped, so it carries a
/// <c>workspace_id</c> foreign key into <c>workspaces(id)</c>, the documented
/// <c>sessions(workspace_id, id)</c> shape (docs/10_DATABASE_SCHEMA.md critical
/// indexes). It is also tenant-scoped, so it carries an <c>organization_id</c>
/// foreign key into <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principle:
/// tenant-scoped tables include <c>organization_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// The <c>status</c> column stores the lifecycle status name (Prepared/Live/Ended),
/// and the NULLABLE <c>started_at</c>/<c>ended_at</c> timestamptz columns capture
/// the live-timeline boundaries — null while the session is still prepared, then
/// stamped by the start and end transitions (docs/09_EVENT_CATALOG.md:
/// <c>SessionStarted</c> "starts live timeline", <c>SessionEnded</c> "ends live
/// timeline").
///
/// The composite index on (<c>workspace_id</c>, <c>id</c>) is the documented
/// critical index; the composite index on (<c>organization_id</c>,
/// <c>workspace_id</c>) keeps the tenant boundary check (checked before the
/// workspace boundary) efficient (docs/06_AUTHORIZATION_MATRIX.md authorization
/// principles). There is deliberately no unique natural-key index: a session is
/// identified only by its surrogate id. Both foreign keys cascade on delete: a
/// session has no meaning without its workspace or its tenant. There is no
/// active-scene column/foreign key yet: the <c>scenes</c> table does not exist
/// (CORE-SCENE-001), so it is a follow-up. Rollback: <see cref="Down"/> drops the
/// table.
/// </summary>
public partial class AddSession : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "sessions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sessions", x => x.id);
                table.ForeignKey(
                    name: "fk_sessions_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_sessions_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_sessions_organization_id_workspace_id",
            table: "sessions",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_sessions_workspace_id_id",
            table: "sessions",
            columns: new[] { "workspace_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "sessions");
    }
}
