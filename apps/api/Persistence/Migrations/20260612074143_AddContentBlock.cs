using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>content_blocks</c> table: the host-prepared Text/media/data unit of
/// the Content module (CORE-SCENE-002, csv/database_tables.csv: scope <c>workspace</c>,
/// "Text/media/data block"). The content block is scene-scoped within a workspace, so
/// it carries a <c>scene_id</c> foreign key into <c>scenes(id)</c> and a
/// <c>workspace_id</c> foreign key into <c>workspaces(id)</c>, the documented
/// <c>content_blocks(workspace_id, scene_id)</c> shape (docs/10_DATABASE_SCHEMA.md
/// critical indexes). It is also tenant-scoped, so it carries an <c>organization_id</c>
/// foreign key into <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principle:
/// tenant-scoped tables include <c>organization_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// The generic kind is stored in the <c>content_type</c> column by its stable name
/// (Text/Media/Data), mirroring how the session and participant statuses are persisted.
/// The content payload is stored in the <c>body</c> column; the monotonic revision
/// counter is stored in the <c>revision_number</c> column (the revisions feature) —
/// there is deliberately no separate revisions table, since csv/database_tables.csv
/// lists only <c>content_blocks</c>.
///
/// The composite index on (<c>workspace_id</c>, <c>scene_id</c>) is the documented
/// critical index; the composite index on (<c>organization_id</c>, <c>workspace_id</c>)
/// keeps the tenant boundary check (checked before the workspace boundary) efficient
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles); the single-column index
/// on (<c>scene_id</c>) is scaffolded by EF to cover the scene foreign key (the
/// documented critical index leads with <c>workspace_id</c>). There is deliberately no
/// unique natural-key index: a content block is identified only by its surrogate id.
/// All three foreign keys cascade on delete: a content block has no meaning without its
/// scene, its workspace or its tenant. Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddContentBlock : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "content_blocks",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                scene_id = table.Column<Guid>(type: "uuid", nullable: false),
                content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                body = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                revision_number = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_content_blocks", x => x.id);
                table.ForeignKey(
                    name: "fk_content_blocks_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_content_blocks_scenes_scene_id",
                    column: x => x.scene_id,
                    principalTable: "scenes",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_content_blocks_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_content_blocks_organization_id_workspace_id",
            table: "content_blocks",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "IX_content_blocks_scene_id",
            table: "content_blocks",
            column: "scene_id");

        migrationBuilder.CreateIndex(
            name: "ix_content_blocks_workspace_id_scene_id",
            table: "content_blocks",
            columns: new[] { "workspace_id", "scene_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "content_blocks");
    }
}
