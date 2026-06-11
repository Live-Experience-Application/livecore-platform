using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>workspace_members</c> table: workspace membership of the
/// Workspaces module (CORE-WS-002, csv/database_tables.csv: scope
/// <c>workspace</c>, "Workspace roles"). The membership is workspace-scoped, so
/// it carries a <c>workspace_id</c> foreign key into <c>workspaces(id)</c> and a
/// <c>user_id</c> foreign key into <c>users(id)</c>, the documented
/// <c>workspace_members(workspace_id, user_id)</c> shape
/// (docs/10_DATABASE_SCHEMA.md critical indexes). It is also tenant-scoped, so
/// it carries an <c>organization_id</c> foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md principle: tenant-scoped tables include
/// <c>organization_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// The unique index on (<c>workspace_id</c>, <c>user_id</c>) enforces at most
/// one membership per subject per workspace at the database level, so a second
/// writer can never create a conflicting standing for the same subject in the
/// same workspace. The composite index on
/// (<c>organization_id</c>, <c>workspace_id</c>) keeps tenant-scoped lookups and
/// the organization boundary check (checked before the workspace boundary)
/// efficient (docs/06_AUTHORIZATION_MATRIX.md authorization principles). All
/// three foreign keys cascade on delete: a membership has no meaning without its
/// organization, its workspace or its subject. The <c>role</c> column stores the
/// generic membership role name from the authorization matrix
/// (docs/06_AUTHORIZATION_MATRIX.md). Rollback: <see cref="Down"/> drops the
/// table.
/// </summary>
public partial class AddWorkspaceMember : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "workspace_members",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workspace_members", x => x.id);
                table.ForeignKey(
                    name: "fk_workspace_members_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_workspace_members_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_workspace_members_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_workspace_members_organization_id_workspace_id",
            table: "workspace_members",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "IX_workspace_members_user_id",
            table: "workspace_members",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_workspace_members_workspace_id_user_id",
            table: "workspace_members",
            columns: new[] { "workspace_id", "user_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "workspace_members");
    }
}
