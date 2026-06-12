using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>entity_relationships</c> table: the generic GRAPH EDGE record of the Entities
/// module (CORE-ENT-003, third story of the "Entity System and Templates" epic;
/// csv/database_tables.csv: module Entities, scope <c>workspace</c>, "Graph edges"). A relationship
/// is a DIRECTED edge between two <see cref="LiveCore.Api.Entities.Entity"/> instances
/// (CORE-ENT-002), so it carries a <c>source_entity_id</c> and a <c>target_entity_id</c> foreign
/// key, each into <c>entities(id)</c>. It is workspace-scoped, carrying a <c>workspace_id</c>
/// foreign key into <c>workspaces(id)</c>, and tenant-scoped, carrying an <c>organization_id</c>
/// foreign key into <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principles: tenant-scoped
/// tables include <c>organization_id</c>, workspace-scoped tables include <c>workspace_id</c>;
/// threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// FK design mirrors <c>entities</c>: FOUR SIMPLE single-column foreign keys (NOT composite FKs).
/// The two endpoint FKs guarantee both referenced entities EXIST but not that they are in the SAME
/// workspace — exactly like <c>entities.entity_type_id</c> and <c>content_blocks.scene_id</c>; that
/// same-workspace-endpoints coupling is enforced by the future create-relationship application
/// flow, not by a composite FK or a unique index on <c>entities</c> (which would touch the
/// CORE-ENT-002 table). Both endpoint FKs cascade to the SAME principal table (<c>entities</c>)
/// with DISTINCT constraint names; PostgreSQL and SQLite both permit multiple cascade paths to one
/// principal, so deleting an entity removes every edge it is an endpoint of (as source or target),
/// consistent with the rest of the schema.
///
/// The <c>relationship_kind</c> column stores the edge's generic, canonical slug label (the DATA a
/// template supplies; Core never branches on its value — the template boundary, docs/04).
/// docs/10_DATABASE_SCHEMA.md documents no critical index for <c>entity_relationships</c>, so the
/// indexes follow the established Scene/Entity pattern: the composite index on
/// (<c>workspace_id</c>, <c>id</c>) is the chosen critical index; the composite index on
/// (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant boundary check (checked before
/// the workspace boundary) efficient; the composite indexes on (<c>workspace_id</c>,
/// <c>source_entity_id</c>) and (<c>workspace_id</c>, <c>target_entity_id</c>) back the
/// list-by-source and by-entity access paths; and the UNIQUE composite index on
/// (<c>workspace_id</c>, <c>source_entity_id</c>, <c>target_entity_id</c>, <c>relationship_kind</c>)
/// is the per-workspace natural key — the same directed edge of the same kind cannot be created
/// twice. The standalone <c>IX_entity_relationships_source_entity_id</c> /
/// <c>..._target_entity_id</c> indexes are EF's default indexes for the two endpoint foreign keys,
/// exactly as <c>IX_entities_entity_type_id</c> is for <c>entities</c>. Rollback: <see cref="Down"/>
/// drops the table.
/// </summary>
public partial class AddEntityRelationship : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "entity_relationships",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                target_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                relationship_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entity_relationships", x => x.id);
                table.ForeignKey(
                    name: "fk_entity_relationships_entities_source_entity_id",
                    column: x => x.source_entity_id,
                    principalTable: "entities",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_entity_relationships_entities_target_entity_id",
                    column: x => x.target_entity_id,
                    principalTable: "entities",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_entity_relationships_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_entity_relationships_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_entity_relationships_organization_id_workspace_id",
            table: "entity_relationships",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "IX_entity_relationships_source_entity_id",
            table: "entity_relationships",
            column: "source_entity_id");

        migrationBuilder.CreateIndex(
            name: "IX_entity_relationships_target_entity_id",
            table: "entity_relationships",
            column: "target_entity_id");

        migrationBuilder.CreateIndex(
            name: "ix_entity_relationships_workspace_id_id",
            table: "entity_relationships",
            columns: new[] { "workspace_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_entity_relationships_workspace_id_source_entity_id",
            table: "entity_relationships",
            columns: new[] { "workspace_id", "source_entity_id" });

        migrationBuilder.CreateIndex(
            name: "ix_entity_relationships_workspace_id_source_target_kind",
            table: "entity_relationships",
            columns: new[] { "workspace_id", "source_entity_id", "target_entity_id", "relationship_kind" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_entity_relationships_workspace_id_target_entity_id",
            table: "entity_relationships",
            columns: new[] { "workspace_id", "target_entity_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "entity_relationships");
    }
}
