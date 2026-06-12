using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>entities</c> table: the generic entity INSTANCE record of the Entities module
/// (CORE-ENT-002, second story of the "Entity System and Templates" epic; csv/database_tables.csv:
/// module Entities, scope <c>workspace</c>, "Generic objects"). An entity is one concrete object
/// of an <see cref="LiveCore.Api.Entities.EntityType"/> (CORE-ENT-001), so it carries an
/// <c>entity_type_id</c> foreign key into <c>entity_types(id)</c>. It is workspace-scoped, carrying
/// a <c>workspace_id</c> foreign key into <c>workspaces(id)</c>, and tenant-scoped, carrying an
/// <c>organization_id</c> foreign key into <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md
/// principles: tenant-scoped tables include <c>organization_id</c>, workspace-scoped tables include
/// <c>workspace_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// FK design mirrors <c>content_blocks</c>: three SIMPLE single-column foreign keys (NOT composite
/// FKs). The <c>entity_type_id</c> FK guarantees the referenced type EXISTS but not that it is in
/// the SAME workspace — exactly like <c>content_blocks.scene_id</c>; that same-workspace-type
/// coupling is enforced by the future create-entity application flow, not by a composite FK or a
/// unique index on <c>entity_types</c> (which would touch the CORE-ENT-001 table).
///
/// The <c>name</c> column stores the instance's human label; <c>attribute_values</c> stores the
/// instance's actual attribute JSON document as plain text (validated for JSON well-formedness
/// only, not against the entity type's attribute schema — schema-conformance is the template
/// engine / CORE-ENT-004). docs/10_DATABASE_SCHEMA.md documents no critical index for
/// <c>entities</c>, so the indexes follow the established Scene/ContentBlock pattern: the composite
/// index on (<c>workspace_id</c>, <c>id</c>) is the chosen critical index; the composite index on
/// (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant boundary check (checked before
/// the workspace boundary) efficient; and the composite index on (<c>workspace_id</c>,
/// <c>entity_type_id</c>) backs the list-by-type access path (the standalone
/// <c>IX_entities_entity_type_id</c> is EF's default index for the type foreign key, exactly as
/// <c>IX_content_blocks_scene_id</c> is for <c>content_blocks</c>). All three foreign keys cascade
/// on delete: an entity has no meaning without its tenant, its workspace or its type, so deleting a
/// type cascades to its instances, consistent with the rest of the schema. Rollback:
/// <see cref="Down"/> drops the table.
/// </summary>
public partial class AddEntity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "entities",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                entity_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                attribute_values = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entities", x => x.id);
                table.ForeignKey(
                    name: "fk_entities_entity_types_entity_type_id",
                    column: x => x.entity_type_id,
                    principalTable: "entity_types",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_entities_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_entities_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_entities_entity_type_id",
            table: "entities",
            column: "entity_type_id");

        migrationBuilder.CreateIndex(
            name: "ix_entities_organization_id_workspace_id",
            table: "entities",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_entities_workspace_id_entity_type_id",
            table: "entities",
            columns: new[] { "workspace_id", "entity_type_id" });

        migrationBuilder.CreateIndex(
            name: "ix_entities_workspace_id_id",
            table: "entities",
            columns: new[] { "workspace_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "entities");
    }
}
