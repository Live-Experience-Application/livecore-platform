using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>asset_links</c> table: the Assets module's record that an <c>assets</c> row is attached
/// to a host-prepared resource — a content block or an entity (CORE-AST-005, the asset-linking story of
/// the "Asset Storage and Authorization" epic; csv/database_tables.csv: module Assets, scope
/// <c>workspace</c>, "Asset-to-content/entity links"). A row records the link's tenant/workspace boundary
/// (<c>organization_id</c>, <c>workspace_id</c>), the linked <c>asset_id</c>, the linked target as a
/// (<c>target_type</c>, <c>target_id</c>) pair, the optional <c>created_by</c> creator and the creation
/// timestamp. This is the last step of the asset lifecycle: "asset can be linked to ContentBlock or Entity
/// -&gt; visibility controls whether it can be accessed" (docs/12_STORAGE_ASSETS.md).
///
/// PRIVATE BY DEFAULT (the epic acceptance criterion; threat T4 "Asset leak" in
/// docs/07_SECURITY_THREAT_MODEL.md). A link never makes an asset public: it only records the attachment
/// whose audience visibility the central Visibility engine then governs, so an asset stays reachable only
/// through an authorized, short-lived signed URL after a permission check.
///
/// The link is workspace-scoped and tenant-scoped, so it carries <c>workspace_id</c> and
/// <c>organization_id</c> columns (docs/10_DATABASE_SCHEMA.md). The documented critical index is
/// <c>asset_links(workspace_id, asset_id)</c>: the list-by-asset access path (the download authorization)
/// leads with the workspace column. A second composite index on (<c>organization_id</c>,
/// <c>workspace_id</c>) keeps the tenant boundary check (checked before the workspace boundary) efficient
/// (threat T5). A UNIQUE composite index on (<c>workspace_id</c>, <c>asset_id</c>, <c>target_type</c>,
/// <c>target_id</c>) is the per-workspace natural key — the same asset cannot be linked to the same target
/// twice. EF additionally indexes the <c>asset_id</c> and <c>created_by</c> foreign-key columns.
///
/// FOREIGN KEYS. <c>organization_id</c>, <c>workspace_id</c> and <c>asset_id</c> are foreign keys into
/// <c>organizations(id)</c>, <c>workspaces(id)</c> and <c>assets(id)</c> that CASCADE on delete (a link has
/// no meaning without its tenant, workspace or asset; threat T5). The optional <c>created_by</c> foreign
/// key into <c>users(id)</c> SETS NULL on delete: a link survives the deletion of the user who created it,
/// anonymized rather than cascade-deleted (mirrors <c>assets.created_by</c>). <c>target_id</c> is
/// deliberately NOT a foreign key — the reference is polymorphic across <c>content_blocks</c> and
/// <c>entities</c>; the same-workspace coupling is enforced by the application flow that creates links
/// (mirrors <c>visibility_rules.resource_id</c>).
///
/// The <c>target_type</c> column persists the target kind by its stable enum NAME (ContentBlock/Entity),
/// never an integer, exactly like <c>visibility_rules.resource_type</c>. Rollback: <see cref="Down"/> drops
/// the table.
/// </summary>
public partial class AddAssetLink : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "asset_links",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                target_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                target_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_asset_links", x => x.id);
                table.ForeignKey(
                    name: "fk_asset_links_assets_asset_id",
                    column: x => x.asset_id,
                    principalTable: "assets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_asset_links_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_asset_links_users_created_by",
                    column: x => x.created_by,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_asset_links_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_asset_links_asset_id",
            table: "asset_links",
            column: "asset_id");

        migrationBuilder.CreateIndex(
            name: "IX_asset_links_created_by",
            table: "asset_links",
            column: "created_by");

        migrationBuilder.CreateIndex(
            name: "ix_asset_links_organization_id_workspace_id",
            table: "asset_links",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_asset_links_workspace_id_asset_id",
            table: "asset_links",
            columns: new[] { "workspace_id", "asset_id" });

        migrationBuilder.CreateIndex(
            name: "ix_asset_links_workspace_id_asset_id_target_type_target_id",
            table: "asset_links",
            columns: new[] { "workspace_id", "asset_id", "target_type", "target_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "asset_links");
    }
}
