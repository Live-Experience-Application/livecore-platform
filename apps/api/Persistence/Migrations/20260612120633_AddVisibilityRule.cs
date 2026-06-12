using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>visibility_rules</c> table: the generic AUDIENCE RULE record of the Visibility
/// module (CORE-VIS-001, the first story of the "Visibility and Reveal Engine" epic; the Visibility
/// module's first table; csv/database_tables.csv: module Visibility, scope <c>workspace</c>,
/// "Audience rules"). A rule binds one Core resource — named generically by a
/// (<c>resource_type</c>, <c>resource_id</c>) pair — to a base audience <c>visibility</c> state
/// (Hidden from the audience, or Visible to it).
///
/// It is workspace-scoped, carrying a <c>workspace_id</c> foreign key into <c>workspaces(id)</c>,
/// and tenant-scoped, carrying an <c>organization_id</c> foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md principles: tenant-scoped tables include <c>organization_id</c>,
/// workspace-scoped tables include <c>workspace_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). Both foreign keys cascade on delete: a rule has no meaning
/// without its workspace or its tenant.
///
/// The authorization-relevant fields are REAL COLUMNS, never JSON (docs/10_DATABASE_SCHEMA.md: "Do
/// not store visibility rules only inside arbitrary JSON"; JSONB is "not for core authorization
/// fields"): <c>resource_type</c> stores the generic governed resource kind name
/// (Scene/ContentBlock/Entity), <c>resource_id</c> the governed resource's surrogate id, and
/// <c>visibility</c> the base audience state name (Hidden/Visible). The two enum columns are stored
/// by their stable name, mirroring <c>content_type</c> on <c>content_blocks</c>.
///
/// <c>resource_id</c> is intentionally NOT a foreign key — the reference is polymorphic across the
/// <c>scenes</c>, <c>content_blocks</c> and <c>entities</c> tables, and a single column cannot
/// foreign-key into three principals; the same-workspace coupling between a rule and its resource is
/// enforced by the application flow that creates rules (the later reveal / visibility-rule command),
/// mirroring <c>content_blocks.scene_id</c> and <c>entities.entity_type_id</c>.
///
/// Two indexes back the documented access patterns: the non-unique composite index on
/// (<c>workspace_id</c>, <c>resource_type</c>, <c>resource_id</c>) is the documented critical index
/// <c>visibility_rules(workspace_id, resource_type, resource_id)</c> (docs/10_DATABASE_SCHEMA.md) —
/// NON-unique on purpose, because a resource may accumulate more than one rule (the base audience
/// rule now; per-participant scoped rules in the later selected-participant reveal, CORE-VIS-005);
/// the non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant
/// boundary check (checked before the workspace boundary) efficient (threat T5). Rollback:
/// <see cref="Down"/> drops the table.
/// </summary>
public partial class AddVisibilityRule : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "visibility_rules",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                resource_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_visibility_rules", x => x.id);
                table.ForeignKey(
                    name: "fk_visibility_rules_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_visibility_rules_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_visibility_rules_organization_id_workspace_id",
            table: "visibility_rules",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_visibility_rules_workspace_id_resource_type_resource_id",
            table: "visibility_rules",
            columns: new[] { "workspace_id", "resource_type", "resource_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "visibility_rules");
    }
}
