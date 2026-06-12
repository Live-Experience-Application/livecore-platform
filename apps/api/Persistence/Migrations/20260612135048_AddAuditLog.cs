using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the append-only <c>audit_logs</c> table: the immutable SECURITY EVENT records of the Audit
/// module (CORE-VIS-006, the last story of the "Visibility and Reveal Engine" epic; the Audit module's
/// first table; csv/database_tables.csv: module Audit, scope <c>organization</c>, "Append-only audit").
/// A row records one security-relevant action — for this story a visibility rule change
/// (<c>VisibilityRuleChanged</c>, docs/09_EVENT_CATALOG.md) — so every visibility change is auditable
/// (docs/07_SECURITY_THREAT_MODEL.md required control "audit creation for visibility changes").
///
/// The log is tenant-scoped, so it carries an <c>organization_id</c> column
/// (docs/10_DATABASE_SCHEMA.md). The single documented critical index is
/// <c>audit_logs(organization_id, created_at)</c>: the log is read per tenant in time order, so the
/// composite index leads with the tenant column then the timestamp. It is NON-unique (a tenant has many
/// entries).
///
/// FOREIGN KEYS — ONLY THE TENANT. <c>organization_id</c> is a foreign key into <c>organizations(id)</c>
/// so tenant isolation is enforced at the row level (threat T5 in docs/07_SECURITY_THREAT_MODEL.md); it
/// CASCADES because a tenant teardown removes the whole tenant including its audit log. The other
/// reference columns — <c>workspace_id</c>, <c>actor_user_profile_id</c>, <c>resource_id</c>,
/// <c>target_participant_id</c> — are deliberately NOT foreign keys: an append-only audit trail must
/// SURVIVE the later deletion of the workspace, user, resource or participant it references and must
/// never be cascade-erased. They are recorded facts (mirrors the polymorphic, non-foreign-key
/// <c>visibility_rules.resource_id</c>).
///
/// The action, the optional resource-type name and the before/after state names are REAL string columns
/// (persisted by their stable enum NAME), never JSON, and no free-form scene/content body is stored
/// (threat T7). Append-only: the table has no soft-delete column and the repository exposes no update or
/// delete. Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddAuditLog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "audit_logs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                actor_user_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                resource_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                target_participant_id = table.Column<Guid>(type: "uuid", nullable: true),
                previous_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                new_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_logs", x => x.id);
                table.ForeignKey(
                    name: "fk_audit_logs_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_organization_id_created_at",
            table: "audit_logs",
            columns: new[] { "organization_id", "created_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_logs");
    }
}
