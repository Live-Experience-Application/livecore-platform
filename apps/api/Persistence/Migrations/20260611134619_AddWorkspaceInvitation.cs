using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>workspace_invitations</c> table: the workspace member invite
/// placeholder with a scoped token model of the Workspaces module (CORE-WS-004;
/// csv/api_routes.csv: <c>POST /api/v1/workspaces/{workspaceId}/members</c>,
/// "Invite/add member"). The invitation is workspace-scoped, so it carries a
/// <c>workspace_id</c> foreign key into <c>workspaces(id)</c>, and tenant-scoped,
/// so it carries an <c>organization_id</c> foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principles; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). Both foreign keys cascade on delete: an
/// invitation has no meaning without its organization or its workspace.
///
/// Token-at-rest model (threats T6 "Invite abuse" / T7 "Log leaks"): the table
/// stores only the SHA-256 <c>token_hash</c>, never the plaintext token. The
/// plaintext is returned to the inviter once at creation and never persisted, so
/// a leaked row cannot be replayed as an invite. The unique index on
/// <c>token_hash</c> guarantees one scoped token addresses at most one
/// invitation. The composite index on (<c>organization_id</c>,
/// <c>workspace_id</c>) keeps tenant-scoped lookups and the organization
/// boundary check (checked before the workspace boundary) efficient
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles). The <c>role</c>
/// column stores the generic membership role name from the authorization matrix,
/// and the <c>status</c> column the invitation lifecycle name (Pending /
/// Accepted / Revoked); <c>expires_at</c> bounds the token's validity (threat T6
/// expiry/single-use). Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddWorkspaceInvitation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "workspace_invitations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                invited_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                token_hash = table.Column<string>(type: "character varying(88)", maxLength: 88, nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workspace_invitations", x => x.id);
                table.ForeignKey(
                    name: "fk_workspace_invitations_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_workspace_invitations_workspaces_workspace_id",
                    column: x => x.workspace_id,
                    principalTable: "workspaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_workspace_invitations_organization_id_workspace_id",
            table: "workspace_invitations",
            columns: new[] { "organization_id", "workspace_id" });

        migrationBuilder.CreateIndex(
            name: "ix_workspace_invitations_token_hash",
            table: "workspace_invitations",
            column: "token_hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_workspace_invitations_workspace_id",
            table: "workspace_invitations",
            column: "workspace_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "workspace_invitations");
    }
}
