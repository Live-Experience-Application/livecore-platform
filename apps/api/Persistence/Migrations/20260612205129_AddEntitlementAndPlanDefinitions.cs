using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the Entitlements module's first three tables — the deployment-wide monetization CATALOG
/// (CORE-ENTL-001, the plan and entitlement definition model story of the "Entitlements and Quotas" epic;
/// docs/05_MODULE_CONTRACTS.md / docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions":
/// <c>entitlement_definitions</c>, <c>plan_definitions</c>; csv/database_tables.csv: module Entitlements, scope
/// <c>global</c>). The epic acceptance criterion is that "Generic entitlements can be defined without vertical
/// terminology": every key, display name and description is generic Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv); a vertical maps a key to its own paywall copy in its UI (docs/21).
///
/// <list type="bullet">
///   <item><c>entitlement_definitions</c>: one generic entitlement — a stable, lower-case dotted <c>key</c>
///   (e.g. <c>workspace.active.max</c>, <c>ads.disabled</c>), the <c>value_kind</c> (flag/quota), the generic
///   <c>display_name</c> and optional <c>description</c>, the <c>is_active</c> soft-lifecycle flag and the
///   timestamps. The unique index <c>ix_entitlement_definitions_key</c> is the catalog integrity guarantee
///   (one key maps to at most one definition).</item>
///   <item><c>plan_definitions</c>: one generic plan — a stable <c>key</c>, generic <c>display_name</c> and
///   optional <c>description</c>, the <c>is_active</c> flag and the timestamps. The unique index
///   <c>ix_plan_definitions_key</c> is the catalog integrity guarantee.</item>
///   <item><c>plan_entitlements</c>: the grants binding a plan to an entitlement definition at a concrete
///   value. <c>value_kind</c> fixes the shape of the value, which lives in two nullable columns
///   (<c>flag_value</c> for a flag grant, <c>quota_limit</c> for a quota grant — NULL means an unlimited
///   fair-use grant). The unique index
///   <c>ix_plan_entitlements_plan_definition_id_entitlement_definition_id</c> is the per-plan natural key (a
///   plan grants each entitlement at most once).</item>
/// </list>
///
/// GLOBAL SCOPE. All three are global catalog tables (scope <c>global</c>, like <c>organizations</c>,
/// <c>users</c> and a global <c>templates</c> row), so none carries an <c>organization_id</c> column or tenant
/// foreign key; there is no per-tenant copy to isolate (threat T5 tenant isolation applies to tenant-scoped
/// tables, not to a global definition catalog). The per-subject assignment ("user-visible premium state comes
/// only from server entitlements") is the later subject-entitlement story (CORE-ENTL-002).
///
/// FOREIGN KEYS. <c>plan_entitlements.plan_definition_id</c> -&gt; <c>plan_definitions(id)</c> CASCADES (a
/// plan's grants are removed with it); <c>plan_entitlements.entitlement_definition_id</c> -&gt;
/// <c>entitlement_definitions(id)</c> is RESTRICTED on delete (a referenced entitlement definition cannot be
/// hard-deleted out from under a plan grant — it is soft-retired via <c>is_active</c> instead;
/// docs/10_DATABASE_SCHEMA.md "avoid hard-delete for business data"). Rollback: <see cref="Down"/> drops the
/// three tables.
/// </summary>
public partial class AddEntitlementAndPlanDefinitions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "entitlement_definitions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                value_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entitlement_definitions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "plan_definitions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_plan_definitions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "plan_entitlements",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                plan_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                entitlement_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                value_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                flag_value = table.Column<bool>(type: "boolean", nullable: true),
                quota_limit = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_plan_entitlements", x => x.id);
                table.ForeignKey(
                    name: "fk_plan_entitlements_entitlement_definitions_entitlement_definition_id",
                    column: x => x.entitlement_definition_id,
                    principalTable: "entitlement_definitions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_plan_entitlements_plan_definitions_plan_definition_id",
                    column: x => x.plan_definition_id,
                    principalTable: "plan_definitions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_entitlement_definitions_key",
            table: "entitlement_definitions",
            column: "key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_plan_definitions_key",
            table: "plan_definitions",
            column: "key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_plan_entitlements_entitlement_definition_id",
            table: "plan_entitlements",
            column: "entitlement_definition_id");

        migrationBuilder.CreateIndex(
            name: "ix_plan_entitlements_plan_definition_id_entitlement_definition_id",
            table: "plan_entitlements",
            columns: new[] { "plan_definition_id", "entitlement_definition_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "plan_entitlements");

        migrationBuilder.DropTable(
            name: "entitlement_definitions");

        migrationBuilder.DropTable(
            name: "plan_definitions");
    }
}
