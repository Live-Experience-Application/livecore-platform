using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>recaps</c> table: the produced session RECAP of the Recaps module (CORE-AUD-004, the basic
/// recap model story of the "Audit, Export and Recap" epic; the Recaps module's first table;
/// docs/05_MODULE_CONTRACTS.md: the Recaps module owns "session recaps"; csv/database_tables.csv: module
/// Recaps, scope <c>session</c>, "Produced session recap"). A recap row is the produced session summary or
/// structured continuation output (docs/03_DOMAIN_LANGUAGE.md "Recap"): which <c>session</c> it summarizes,
/// the tenant (<c>organization_id</c>) and workspace (<c>workspace_id</c>) it belongs to, the optional
/// producing user (<c>generated_by</c>), the recap <c>recap_version</c>, the generic <c>summary</c> body and
/// the <c>generated_at</c> timestamp.
///
/// GENERIC AND AUTHORIZED (the epic acceptance criterion "Audit/export/recap are generic and authorized").
/// The recap is session-scoped, so it carries <c>organization_id</c>, <c>workspace_id</c> and
/// <c>session_id</c> columns (docs/10_DATABASE_SCHEMA.md; threat T5 in docs/07_SECURITY_THREAT_MODEL.md),
/// mirroring <c>session_events</c>. The documented critical index is <c>recaps(workspace_id, id)</c>:
/// workspace reads lead with the workspace column. A second composite index on (<c>session_id</c>, <c>id</c>)
/// backs the list-by-session read in produced order, and a third on (<c>organization_id</c>,
/// <c>workspace_id</c>) keeps the tenant boundary check (checked before the workspace boundary) efficient
/// (threat T5). EF additionally indexes the nullable <c>generated_by</c> foreign-key column.
///
/// FOREIGN KEYS. <c>organization_id</c>, <c>workspace_id</c> and <c>session_id</c> are foreign keys into
/// <c>organizations(id)</c>, <c>workspaces(id)</c> and <c>sessions(id)</c> that CASCADE on delete (a recap has
/// no meaning without its tenant, workspace or session; threat T5). The optional <c>generated_by</c> foreign
/// key into <c>users(id)</c> SETS NULL on delete, so deleting the producing user anonymizes the recap rather
/// than cascade-deleting it (the record of who produced a recap survives; mirrors
/// <c>export_jobs.requested_by</c> and <c>assets.created_by</c>).
///
/// The <c>summary</c> is the recap's host content — a bounded generic body, never a vertical term (AGENTS.md);
/// it is host-only until a separate reveal (docs/09_EVENT_CATALOG.md <c>RecapGenerated</c>: "Participant-visible
/// only after separate reveal"). A recap is WRITE-ONCE (the produced output of a session): there is no update
/// or delete path on the aggregate or repository, exactly as the append-only audit log and the write-once
/// export manifest are immutable (docs/10_DATABASE_SCHEMA.md). Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddRecap : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "recaps",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                generated_by = table.Column<Guid>(type: "uuid", nullable: true),
                recap_version = table.Column<int>(type: "integer", nullable: false),
                summary = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_recaps", x => x.id);
                table.ForeignKey(
                    name: "fk_recaps_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_recaps_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_recaps_users_generated_by",
                    column: x => x.generated_by,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_recaps_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_recaps_generated_by",
            table: "recaps",
            column: "generated_by");

        migrationBuilder.CreateIndex(
            name: "ix_recaps_organization_id_workspace_id",
            table: "recaps",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_recaps_session_id_id",
            table: "recaps",
            columns: new[] { "session_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_recaps_workspace_id_id",
            table: "recaps",
            columns: new[] { "workspace_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "recaps");
    }
}
