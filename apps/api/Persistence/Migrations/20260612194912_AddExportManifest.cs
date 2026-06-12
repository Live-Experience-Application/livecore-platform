using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>export_manifests</c> table and its <c>export_manifest_entries</c> child table: the
/// produced workspace export MANIFEST of the Exports module (CORE-AUD-003, the workspace export manifest
/// story of the "Audit, Export and Recap" epic; the Exports module's second/third tables;
/// docs/05_MODULE_CONTRACTS.md: the Exports module owns "export manifests"). A manifest row is the
/// table-of-contents of a completed workspace export: which <c>export_job</c> produced it, the tenant
/// (<c>organization_id</c>) and workspace (<c>workspace_id</c>) it belongs to, the explicit export
/// <c>scope</c>, the manifest <c>manifest_version</c> and the <c>generated_at</c> timestamp. The per-kind
/// inventory lives in <c>export_manifest_entries</c> (one row per generic
/// <c>ExportResourceKind</c> with a non-negative <c>item_count</c>). The exported DATA is never stored — the
/// manifest carries identifiers, an enum scope, a version, a timestamp and counts only (threats T7/T8 in
/// docs/07_SECURITY_THREAT_MODEL.md). CORE-AUD-002 modeled the export job and left "the produced export
/// manifest" to this story.
///
/// GENERIC AND AUTHORIZED (the epic acceptance criterion "Audit/export/recap are generic and authorized").
/// The manifest is tenant- and workspace-scoped, so it carries <c>organization_id</c> and <c>workspace_id</c>
/// columns (docs/10_DATABASE_SCHEMA.md). The documented critical index is
/// <c>export_manifests(workspace_id, id)</c>: workspace reads lead with the workspace column. A second
/// composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant boundary check (checked
/// before the workspace boundary) efficient (threat T5). The UNIQUE index on (<c>export_job_id</c>) is the
/// per-job natural key — one manifest per export job — and backs the find-by-job lookup. The child entries
/// carry a UNIQUE (<c>export_manifest_id</c>, <c>kind</c>) index so a kind is counted at most once per
/// manifest.
///
/// FOREIGN KEYS. <c>export_job_id</c>, <c>organization_id</c> and <c>workspace_id</c> are foreign keys into
/// <c>export_jobs(id)</c>, <c>organizations(id)</c> and <c>workspaces(id)</c> that CASCADE on delete (a
/// manifest has no meaning without its job, tenant or workspace; threat T5). The child
/// <c>export_manifest_id</c> foreign key into <c>export_manifests(id)</c> also cascades, so a manifest's
/// inventory entries are removed with it.
///
/// The <c>scope</c> and <c>kind</c> columns persist their enum NAME (Workspace; and
/// Session/Scene/ContentBlock/Entity/Participant/Asset), never an integer, exactly like
/// <c>export_jobs.scope</c>. A manifest is WRITE-ONCE (the produced output of a completed export): there is
/// no update or delete path on the aggregate or repository, exactly as the append-only audit log is
/// immutable (docs/10_DATABASE_SCHEMA.md). Rollback: <see cref="Down"/> drops both tables (the child first).
/// </summary>
public partial class AddExportManifest : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "export_manifests",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                export_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                manifest_version = table.Column<int>(type: "integer", nullable: false),
                generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_export_manifests", x => x.id);
                table.ForeignKey(
                    name: "fk_export_manifests_export_jobs_export_job_id",
                    column: x => x.export_job_id,
                    principalTable: "export_jobs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_export_manifests_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_export_manifests_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "export_manifest_entries",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                export_manifest_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                item_count = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_export_manifest_entries", x => x.id);
                table.ForeignKey(
                    name: "fk_export_manifest_entries_export_manifests_export_manifest_id",
                    column: x => x.export_manifest_id,
                    principalTable: "export_manifests",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_export_manifest_entries_export_manifest_id_kind",
            table: "export_manifest_entries",
            columns: new[] { "export_manifest_id", "kind" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_export_manifests_export_job_id",
            table: "export_manifests",
            column: "export_job_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_export_manifests_organization_id_workspace_id",
            table: "export_manifests",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_export_manifests_workspace_id_id",
            table: "export_manifests",
            columns: new[] { "workspace_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "export_manifest_entries");

        migrationBuilder.DropTable(
            name: "export_manifests");
    }
}
