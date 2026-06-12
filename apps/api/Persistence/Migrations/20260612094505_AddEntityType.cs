using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>entity_types</c> table: the generic, template-defined entity TYPE
/// definition record of the Entities module (CORE-ENT-001, first story of the "Entity System
/// and Templates" epic; csv/database_tables.csv: module Entities/Templates, scope
/// <c>workspace/template</c>, "Template-defined types"). The "template" half of the scope is
/// the later CORE-ENT-004 template loading; THIS story is the workspace half, so the entity
/// type carries a <c>workspace_id</c> foreign key into <c>workspaces(id)</c> and is also
/// tenant-scoped, carrying an <c>organization_id</c> foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md principles: tenant-scoped tables include <c>organization_id</c>,
/// workspace-scoped tables include <c>workspace_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). There is deliberately no <c>template_id</c> column: there
/// is no templates table yet, and binding a type to a loaded template is the later CORE-ENT-004
/// story (docs/05_MODULE_CONTRACTS.md: the Templates module owns the template loader).
///
/// The <c>type_key</c> column stores the canonical, lower-case natural key (the DATA a template
/// supplies; Core never branches on its value — the template boundary, docs/04); <c>display_name</c>
/// stores the human-readable label; <c>attribute_schema</c> stores the template-defined attribute
/// JSON document as plain text (validated for JSON well-formedness only, not against a template
/// schema — CORE-ENT-004). docs/10_DATABASE_SCHEMA.md documents no critical index for
/// <c>entity_types</c>, so the indexes follow the established Scene/Participant pattern: the
/// composite index on (<c>workspace_id</c>, <c>id</c>) is the chosen critical index; the composite
/// index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant boundary check (checked
/// before the workspace boundary) efficient; and the UNIQUE composite index on
/// (<c>workspace_id</c>, <c>type_key</c>) enforces that a workspace cannot define the same type
/// twice (the same key may still exist in another workspace). Both foreign keys cascade on delete:
/// an entity type has no meaning without its workspace or its tenant. Rollback: <see cref="Down"/>
/// drops the table.
/// </summary>
public partial class AddEntityType : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "entity_types",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                type_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                attribute_schema = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entity_types", x => x.id);
                table.ForeignKey(
                    name: "fk_entity_types_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_entity_types_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_entity_types_organization_id_workspace_id",
            table: "entity_types",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_entity_types_workspace_id_id",
            table: "entity_types",
            columns: new[] { "workspace_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_entity_types_workspace_id_type_key",
            table: "entity_types",
            columns: new[] { "workspace_id", "type_key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "entity_types");
    }
}
