using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>participants</c> table: the session-facing participant record
/// of the Participants module (CORE-SES-001, csv/database_tables.csv: scope
/// <c>workspace</c>, "Session-facing participant"). The participant is
/// workspace-scoped, so it carries a <c>workspace_id</c> foreign key into
/// <c>workspaces(id)</c>, the documented <c>participants(workspace_id, id)</c>
/// shape (docs/10_DATABASE_SCHEMA.md critical indexes). It is also tenant-scoped,
/// so it carries an <c>organization_id</c> foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principle: tenant-scoped
/// tables include <c>organization_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). The optional <c>user_id</c> column is a
/// NULLABLE foreign key into <c>users(id)</c>: a participant may be linked to a
/// user or be anonymous (docs/03_DOMAIN_LANGUAGE.md).
///
/// The composite index on (<c>workspace_id</c>, <c>id</c>) is the documented
/// critical index; the composite index on (<c>organization_id</c>,
/// <c>workspace_id</c>) keeps the tenant boundary check (checked before the
/// workspace boundary) efficient (docs/06_AUTHORIZATION_MATRIX.md authorization
/// principles). The FILTERED unique index on (<c>workspace_id</c>,
/// <c>user_id</c>) where <c>user_id IS NOT NULL</c> enforces at most one
/// participant per user per workspace at the database level, so a second writer
/// can never create a duplicate participant for one user in one workspace, while
/// anonymous participants (null <c>user_id</c>) are excluded from the constraint
/// (threat T5). The organization and workspace foreign keys cascade on delete; the
/// optional user foreign key sets null on delete so removing a user anonymizes the
/// participant record rather than deleting the history of who took part. The
/// <c>status</c> column stores the participant lifecycle status name, the
/// soft-delete dimension (docs/10_DATABASE_SCHEMA.md). Rollback:
/// <see cref="Down"/> drops the table.
/// </summary>
public partial class AddParticipant : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "participants",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: true),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_participants", x => x.id);
                table.ForeignKey(
                    name: "fk_participants_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_participants_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_participants_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_participants_organization_id_workspace_id",
            table: "participants",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "IX_participants_user_id",
            table: "participants",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_participants_workspace_id_id",
            table: "participants",
            columns: new[] { "workspace_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_participants_workspace_id_user_id",
            table: "participants",
            columns: new[] { "workspace_id", "user_id" },
            unique: true,
            filter: "\"user_id\" IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "participants");
    }
}
