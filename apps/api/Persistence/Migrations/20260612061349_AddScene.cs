using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>scenes</c> table: the workspace-prepared ordered segment record of
/// the Scenes module (CORE-SCENE-001, csv/database_tables.csv: scope <c>workspace</c>,
/// "Ordered segments"). The scene is workspace-scoped, so it carries a
/// <c>workspace_id</c> foreign key into <c>workspaces(id)</c>, the documented
/// <c>scenes(workspace_id, id)</c> shape (docs/10_DATABASE_SCHEMA.md critical
/// indexes — the documented index carries NO <c>session_id</c>). It is also
/// tenant-scoped, so it carries an <c>organization_id</c> foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principle: tenant-scoped
/// tables include <c>organization_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). There is deliberately no <c>session_id</c>
/// column: a scene is a workspace-prepared segment, and a session activates it
/// through its active scene pointer in a later story (docs/05_MODULE_CONTRACTS.md).
///
/// The ordering position is stored in the <c>scene_order</c> integer column rather
/// than a bare <c>order</c> column, because <c>order</c> is a SQL reserved word. The
/// column allows any non-negative integer (the aggregate validates non-negativity);
/// it is deliberately not unique.
///
/// The composite index on (<c>workspace_id</c>, <c>id</c>) is the documented critical
/// index; the composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps
/// the tenant boundary check (checked before the workspace boundary) efficient
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles); the NON-unique
/// composite index on (<c>workspace_id</c>, <c>scene_order</c>) backs the ordered
/// retrieval of a workspace's scenes (the ordering feature) — it is not unique on
/// purpose, since the repository sorts deterministically on (<c>scene_order</c>,
/// <c>id</c>) and duplicate/non-contiguous orders are allowed. There is deliberately
/// no unique natural-key index: a scene is identified only by its surrogate id. Both
/// foreign keys cascade on delete: a scene has no meaning without its workspace or its
/// tenant. Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddScene : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "scenes",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                scene_order = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_scenes", x => x.id);
                table.ForeignKey(
                    name: "fk_scenes_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_scenes_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_scenes_organization_id_workspace_id",
            table: "scenes",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_scenes_workspace_id_id",
            table: "scenes",
            columns: new[] { "workspace_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_scenes_workspace_id_scene_order",
            table: "scenes",
            columns: new[] { "workspace_id", "scene_order" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "scenes");
    }
}
