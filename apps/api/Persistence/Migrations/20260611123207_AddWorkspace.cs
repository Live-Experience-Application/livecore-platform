using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>workspaces</c> table: the generic workspace aggregate of the
/// Workspaces module (CORE-WS-001, csv/database_tables.csv: module Workspaces,
/// scope <c>organization</c>, "Generic workspace"). The table is tenant-scoped,
/// so it carries an <c>organization_id</c> foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables
/// include <c>organization_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// The primary key <c>id</c> is the foreign-key target workspace-scoped tables
/// will reference through their own <c>workspace_id</c>
/// (docs/10_DATABASE_SCHEMA.md).
///
/// The non-unique index on (<c>organization_id</c>, <c>id</c>) is the documented
/// critical index <c>workspaces(organization_id, id)</c>: tenant-scoped reads
/// lead with the organization column. The unique index on
/// (<c>organization_id</c>, <c>slug</c>) enforces that a workspace slug is unique
/// WITHIN one organization (not globally): the same slug may exist in two
/// different organizations, but a second writer can never create two workspaces
/// with one slug in one tenant (threat T5). The organization foreign key
/// cascades on delete: a workspace has no meaning without its organization.
/// Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddWorkspace : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "workspaces",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workspaces", x => x.id);
                table.ForeignKey(
                    name: "fk_workspaces_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_workspaces_organization_id_id",
            table: "workspaces",
            columns: new[] { "organization_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_workspaces_organization_id_slug",
            table: "workspaces",
            columns: new[] { "organization_id", "slug" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "workspaces");
    }
}
