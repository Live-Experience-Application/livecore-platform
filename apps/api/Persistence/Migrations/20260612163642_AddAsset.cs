using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>assets</c> table: the generic asset METADATA of the Assets module (CORE-AST-001, the
/// first story of the "Asset Storage and Authorization" epic; the Assets module's first table;
/// csv/database_tables.csv: module Assets, scope <c>workspace</c>, "Metadata only"). A row records the
/// metadata of one stored file/media object (docs/12_STORAGE_ASSETS.md): the storage coordinates
/// (<c>storage_provider</c>, <c>bucket</c>, <c>object_key</c>), the <c>content_type</c>, the nullable
/// <c>size_bytes</c>/<c>checksum</c> (known only once the upload is confirmed), the <c>created_by</c>
/// creator, the lifecycle <c>status</c> and the audit timestamps. The binary content is NEVER stored
/// here — it lives in private S3-compatible object storage (docs/12_STORAGE_ASSETS.md: "Avoid storing
/// large binary files in PostgreSQL"; ADR 0006).
///
/// PRIVATE BY DEFAULT (the epic acceptance criterion; threat T4 "Asset leak" in
/// docs/07_SECURITY_THREAT_MODEL.md). The table carries no public-URL, no "is public" flag and no
/// shareable token; the storage coordinates address a private bucket reached only through an authorized,
/// short-lived signed URL after a permission check (the signed download flow is CORE-AST-004). No column
/// can make an asset public.
///
/// The asset is workspace-scoped and tenant-scoped, so it carries <c>workspace_id</c> and
/// <c>organization_id</c> columns (docs/10_DATABASE_SCHEMA.md). The single documented critical index is
/// <c>assets(workspace_id, id)</c>: workspace reads lead with the workspace column. A second composite
/// index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant boundary check (checked
/// before the workspace boundary) efficient (threat T5). EF additionally indexes the <c>created_by</c>
/// foreign-key column.
///
/// FOREIGN KEYS. <c>organization_id</c> and <c>workspace_id</c> are foreign keys into
/// <c>organizations(id)</c> and <c>workspaces(id)</c> that CASCADE on delete (an asset metadata row has
/// no meaning without its tenant or workspace; threat T5). The optional <c>created_by</c> foreign key
/// into <c>users(id)</c> SETS NULL on delete: an asset must survive the later deletion of the user who
/// created it (other content may link to it), so deleting the user anonymizes the creator rather than
/// cascade-deleting the asset (mirrors <c>participants.user_id</c>). Removing the orphaned stored
/// objects themselves is the later cleanup job (CORE-AST-006).
///
/// The <c>status</c> column persists the lifecycle status by its stable enum NAME (Pending/Available),
/// never an integer, exactly like <c>sessions.status</c>. Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddAsset : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "assets",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: true),
                storage_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                bucket = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                object_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                content_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                size_bytes = table.Column<long>(type: "bigint", nullable: true),
                checksum = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_assets", x => x.id);
                table.ForeignKey(
                    name: "fk_assets_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_assets_users_created_by",
                    column: x => x.created_by,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_assets_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_assets_created_by",
            table: "assets",
            column: "created_by");

        migrationBuilder.CreateIndex(
            name: "ix_assets_organization_id_workspace_id",
            table: "assets",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_assets_workspace_id_id",
            table: "assets",
            columns: new[] { "workspace_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "assets");
    }
}
