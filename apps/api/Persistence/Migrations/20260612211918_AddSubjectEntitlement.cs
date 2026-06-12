using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the Entitlements module's <c>subject_entitlements</c> table — the per-subject entitlement ASSIGNMENT
/// (CORE-ENTL-002, the subject entitlement assignment and lookup story of the "Entitlements and Quotas" epic;
/// docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions": <c>subject_entitlements</c>;
/// csv/database_tables.csv: module Entitlements). CORE-ENTL-001 created the GLOBAL catalog (what entitlements
/// and plans exist); this table records that ONE generic subject — a <c>User</c> or a <c>Workspace</c>
/// (<c>subject_type</c>) identified by <c>subject_id</c> — holds a granted entitlement at a concrete value, the
/// rows the server-side lookup resolves into a subject's premium state.
///
/// THE EPIC ACCEPTANCE CRITERION — "User-visible premium state comes only from server entitlements". A row is
/// the granted <c>entitlement_definition_id</c> with its denormalized stable <c>entitlement_key</c>, the
/// <c>value_kind</c> with the granted <c>flag_value</c>/<c>quota_limit</c> (NULL quota = unlimited/fair-use),
/// the optional <c>source_plan_definition_id</c> provenance, the <c>is_active</c> lifecycle flag (a revoked
/// assignment resolves to NOT entitled — the fail-closed default for refunds/cancellations/downgrades) and the
/// timestamps.
///
/// PER-SUBJECT, KEYED BY THE SUBJECT PAIR. The table carries NO <c>organization_id</c>: a subject's premium
/// state is keyed by the (<c>subject_type</c>, <c>subject_id</c>) pair, not by a tenant. The subject id is a
/// generic POLYMORPHIC reference with no database foreign key (mirrors <c>asset_links.target_id</c>,
/// <c>visibility_rules.resource_id</c> and <c>session_events.visibility_subject_id</c>), so a user subject and a
/// workspace subject that share a guid never collide.
///
/// INDEXES AND FOREIGN KEYS. The unique
/// <c>ix_subject_entitlements_subject_type_subject_id_entitlement_definition_id</c> is the per-subject natural
/// key (a subject holds each entitlement at most once); its (<c>subject_type</c>, <c>subject_id</c>) prefix is
/// the critical lookup index that resolves a subject's effective entitlements.
/// <c>entitlement_definition_id</c> -&gt; <c>entitlement_definitions(id)</c> is RESTRICTED on delete (a
/// referenced definition cannot be hard-deleted out from under an assignment; it is soft-retired via
/// <c>is_active</c> instead; docs/10_DATABASE_SCHEMA.md). <c>source_plan_definition_id</c> -&gt;
/// <c>plan_definitions(id)</c> is nullable and SET NULL on delete (provenance retained as a fact; mirrors
/// <c>export_jobs.requested_by</c>). Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddSubjectEntitlement : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "subject_entitlements",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                subject_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                entitlement_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                entitlement_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                value_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                flag_value = table.Column<bool>(type: "boolean", nullable: true),
                quota_limit = table.Column<long>(type: "bigint", nullable: true),
                source_plan_definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_subject_entitlements", x => x.id);
                table.ForeignKey(
                    name: "fk_subject_entitlements_entitlement_definitions_entitlement_definition_id",
                    column: x => x.entitlement_definition_id,
                    principalTable: "entitlement_definitions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_subject_entitlements_plan_definitions_source_plan_definition_id",
                    column: x => x.source_plan_definition_id,
                    principalTable: "plan_definitions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_subject_entitlements_entitlement_definition_id",
            table: "subject_entitlements",
            column: "entitlement_definition_id");

        migrationBuilder.CreateIndex(
            name: "IX_subject_entitlements_source_plan_definition_id",
            table: "subject_entitlements",
            column: "source_plan_definition_id");

        migrationBuilder.CreateIndex(
            name: "ix_subject_entitlements_subject_type_subject_id_entitlement_definition_id",
            table: "subject_entitlements",
            columns: new[] { "subject_type", "subject_id", "entitlement_definition_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "subject_entitlements");
    }
}
