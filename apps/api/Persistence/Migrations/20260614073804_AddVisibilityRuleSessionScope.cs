using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Scopes the <c>visibility_rules</c> table to the SESSION (CORE-SVIS-001;
/// docs/adr/0013-session-scoped-visibility-rules.md). A reveal is session-scoped: a workspace may run
/// several concurrent sessions, and a resource revealed in one session must be visible ONLY within it,
/// so a participant connected to a different concurrent session of the same workspace can never see it
/// (the cross-session data leak; threats T5/T3 in docs/07_SECURITY_THREAT_MODEL.md). This migration adds
/// the required <c>session_id</c> column with a foreign key into <c>sessions(id)</c> (<c>ON DELETE
/// CASCADE</c>, mirroring the organization and workspace cascades), and replaces the resource lookup
/// index so it leads with the session — the documented critical index becomes
/// <c>visibility_rules(session_id, resource_type, resource_id)</c> (docs/10_DATABASE_SCHEMA.md). The
/// EF-managed single-column index on <c>workspace_id</c> backs the workspace foreign key and the
/// workspace-wide, session-agnostic role-level reads (asset download / entity search). The index stays
/// NON-unique (a resource may carry the audience-wide rule plus per-participant rules within a session;
/// the single-rule-per-(session, resource, dimension) constraint is the later CORE-SVIS-002).
/// </summary>
public partial class AddVisibilityRuleSessionScope : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_visibility_rules_workspace_id_resource_type_resource_id",
            table: "visibility_rules");

        migrationBuilder.AddColumn<Guid>(
            name: "session_id",
            table: "visibility_rules",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.CreateIndex(
            name: "ix_visibility_rules_session_id_resource_type_resource_id",
            table: "visibility_rules",
            columns: new[] { "session_id", "resource_type", "resource_id" });

        migrationBuilder.CreateIndex(
            name: "IX_visibility_rules_workspace_id",
            table: "visibility_rules",
            column: "workspace_id");

        migrationBuilder.AddForeignKey(
            name: "fk_visibility_rules_sessions_session_id",
            table: "visibility_rules",
            column: "session_id",
            principalTable: "sessions",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_visibility_rules_sessions_session_id",
            table: "visibility_rules");

        migrationBuilder.DropIndex(
            name: "ix_visibility_rules_session_id_resource_type_resource_id",
            table: "visibility_rules");

        migrationBuilder.DropIndex(
            name: "IX_visibility_rules_workspace_id",
            table: "visibility_rules");

        migrationBuilder.DropColumn(
            name: "session_id",
            table: "visibility_rules");

        migrationBuilder.CreateIndex(
            name: "ix_visibility_rules_workspace_id_resource_type_resource_id",
            table: "visibility_rules",
            columns: new[] { "workspace_id", "resource_type", "resource_id" });
    }
}
