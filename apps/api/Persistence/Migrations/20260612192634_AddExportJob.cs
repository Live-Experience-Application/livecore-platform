using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>export_jobs</c> table: the generic, asynchronous EXPORT JOB of the Exports module
/// (CORE-AUD-002, the first story of the "Audit, Export and Recap" epic; the Exports module's first table;
/// csv/database_tables.csv: module Exports, scope <c>workspace</c>, "Async exports"). A row records one
/// async export request: the explicit export <c>scope</c> (workspace-wide or a single user's own data),
/// the lifecycle <c>status</c>, the <c>requested_by</c> requester, an optional generic <c>failure_reason</c>
/// and the audit timestamps (docs/02_ARCHITECTURE.md: <c>apps/worker</c> owns "background jobs, exports,
/// cleanup, async processing"). The exported DATA is never stored here — this row is the job record only;
/// the produced export MANIFEST is the later story CORE-AUD-003.
///
/// GENERIC AND AUTHORIZED (the epic acceptance criterion "Audit/export/recap are generic and authorized").
/// The job is tenant- and workspace-scoped, so it carries <c>organization_id</c> and <c>workspace_id</c>
/// columns (docs/10_DATABASE_SCHEMA.md). The single documented critical index is
/// <c>export_jobs(workspace_id, id)</c>: workspace reads lead with the workspace column. A second composite
/// index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant boundary check (checked before
/// the workspace boundary) efficient (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The explicit
/// <c>scope</c> column is the "explicit host/admin export scopes" control for threat T8 ("Export leak"). EF
/// additionally indexes the <c>requested_by</c> foreign-key column.
///
/// FOREIGN KEYS. <c>organization_id</c> and <c>workspace_id</c> are foreign keys into
/// <c>organizations(id)</c> and <c>workspaces(id)</c> that CASCADE on delete (an export job has no meaning
/// without its tenant or workspace; threat T5). The optional <c>requested_by</c> foreign key into
/// <c>users(id)</c> SETS NULL on delete: the record of who requested an export must survive the later
/// deletion of that user, so deleting the user anonymizes the requester rather than cascade-deleting the
/// job (mirrors <c>assets.created_by</c>).
///
/// The <c>scope</c> and <c>status</c> columns persist their enum NAME (Workspace/UserData and
/// Pending/Running/Completed/Failed), never an integer, exactly like <c>assets.status</c>. The
/// <c>failure_reason</c> is a bounded, single-line diagnostic and never carries exported content (threats
/// T7/T8). Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddExportJob : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "export_jobs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                failure_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_export_jobs", x => x.id);
                table.ForeignKey(
                    name: "fk_export_jobs_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_export_jobs_users_requested_by",
                    column: x => x.requested_by,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_export_jobs_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_export_jobs_organization_id_workspace_id",
            table: "export_jobs",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "IX_export_jobs_requested_by",
            table: "export_jobs",
            column: "requested_by");

        migrationBuilder.CreateIndex(
            name: "ix_export_jobs_workspace_id_id",
            table: "export_jobs",
            columns: new[] { "workspace_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "export_jobs");
    }
}
