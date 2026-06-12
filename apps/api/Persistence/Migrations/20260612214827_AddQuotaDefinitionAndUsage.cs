using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the Entitlements module's <c>quota_definitions</c> and <c>quota_usage</c> tables — the quota DEFINITION
/// and per-subject quota USAGE (CORE-ENTL-003, the quota definition and quota status story of the "Entitlements
/// and Quotas" epic; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions":
/// <c>quota_definitions</c>, <c>quota_usage</c>; csv/database_tables.csv: module Entitlements). CORE-ENTL-001/002
/// created the catalog (entitlements/plans) and the per-subject assignment (the granted LIMITS); these two tables
/// add the measurement semantics and the recorded usage the server-side quota-status calculation combines.
///
/// THE EPIC ACCEPTANCE CRITERION — "Quota status is calculated server-side for subjects and workspaces".
/// <c>quota_definitions</c> is the GLOBAL catalog (no <c>organization_id</c>, like <c>entitlement_definitions</c>)
/// saying how a numeric quota entitlement is measured: the measured <c>entitlement_definition_id</c> with its
/// denormalized stable <c>entitlement_key</c>, the <c>subject_type</c> the usage is counted for, the <c>unit</c>,
/// the <c>is_active</c> soft-lifecycle flag and the timestamps. <c>quota_usage</c> is PER-SUBJECT (no
/// <c>organization_id</c>; keyed by the (<c>subject_type</c>, <c>subject_id</c>) pair, the subject id a generic
/// polymorphic reference with no foreign key — mirrors <c>subject_entitlements.subject_id</c>) and records the
/// current <c>used_amount</c> of a quota.
///
/// INDEXES AND FOREIGN KEYS. <c>ix_quota_definitions_entitlement_definition_id</c> is unique (one entitlement is
/// measured by at most one quota); <c>ix_quota_definitions_subject_type</c> backs the active-quotas-by-subject-kind
/// lookup the calculation enumerates. <c>ix_quota_usage_subject_type_subject_id_quota_definition_id</c> is the
/// per-subject natural key (a subject records each quota at most once); its (<c>subject_type</c>, <c>subject_id</c>)
/// prefix is the critical lookup index that reads a subject's usage. <c>quota_definitions.entitlement_definition_id</c>
/// -&gt; <c>entitlement_definitions(id)</c> and <c>quota_usage.quota_definition_id</c> -&gt;
/// <c>quota_definitions(id)</c> are both RESTRICTED on delete (a measured definition / quota is soft-retired via
/// <c>is_active</c>, never hard-deleted; docs/10_DATABASE_SCHEMA.md), so neither row can ever dangle. Rollback:
/// <see cref="Down"/> drops <c>quota_usage</c> first (its foreign key references <c>quota_definitions</c>), then
/// <c>quota_definitions</c>.
/// </summary>
public partial class AddQuotaDefinitionAndUsage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "quota_definitions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                entitlement_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                entitlement_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                subject_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_quota_definitions", x => x.id);
                table.ForeignKey(
                    name: "fk_quota_definitions_entitlement_definitions_entitlement_definition_id",
                    column: x => x.entitlement_definition_id,
                    principalTable: "entitlement_definitions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "quota_usage",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                subject_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                quota_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                entitlement_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                used_amount = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_quota_usage", x => x.id);
                table.ForeignKey(
                    name: "fk_quota_usage_quota_definitions_quota_definition_id",
                    column: x => x.quota_definition_id,
                    principalTable: "quota_definitions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_quota_definitions_entitlement_definition_id",
            table: "quota_definitions",
            column: "entitlement_definition_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_quota_definitions_subject_type",
            table: "quota_definitions",
            column: "subject_type");

        migrationBuilder.CreateIndex(
            name: "IX_quota_usage_quota_definition_id",
            table: "quota_usage",
            column: "quota_definition_id");

        migrationBuilder.CreateIndex(
            name: "ix_quota_usage_subject_type_subject_id_quota_definition_id",
            table: "quota_usage",
            columns: new[] { "subject_type", "subject_id", "quota_definition_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "quota_usage");

        migrationBuilder.DropTable(
            name: "quota_definitions");
    }
}
