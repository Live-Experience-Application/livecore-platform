using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the selected-participant target to the <c>visibility_rules</c> table (CORE-VIS-005,
/// selected-participant reveal). A new NULLABLE <c>target_participant_id</c> column scopes a
/// visibility rule to a single participant: <c>NULL</c> = an AUDIENCE-WIDE rule (visible to everyone,
/// the CORE-VIS-001 behavior), a set value = a rule visible ONLY to that participant
/// (docs/06_AUTHORIZATION_MATRIX.md "Send private content"; docs/09_EVENT_CATALOG.md "selected
/// recipients"). It is a REAL column (never JSON), the authorization-relevant field for per-participant
/// visibility (docs/10_DATABASE_SCHEMA.md).
///
/// The column is a foreign key into <c>participants(id)</c> that CASCADES on delete: a
/// participant-scoped rule has no meaning without its target participant, and cascade — not set-null —
/// is the SAFE behavior, because set-null would silently turn a participant-scoped rule into an
/// audience-wide one and over-share the resource (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). EF's
/// default single-column index backs the foreign key. An audience-wide rule keeps a NULL target and is
/// unaffected. Rollback: <see cref="Down"/> drops the foreign key, the index and the column.
/// </summary>
public partial class AddVisibilityRuleTargetParticipant : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "target_participant_id",
            table: "visibility_rules",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_visibility_rules_target_participant_id",
            table: "visibility_rules",
            column: "target_participant_id");

        migrationBuilder.AddForeignKey(
            name: "fk_visibility_rules_participants_target_participant_id",
            table: "visibility_rules",
            column: "target_participant_id",
            principalTable: "participants",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_visibility_rules_participants_target_participant_id",
            table: "visibility_rules");

        migrationBuilder.DropIndex(
            name: "IX_visibility_rules_target_participant_id",
            table: "visibility_rules");

        migrationBuilder.DropColumn(
            name: "target_participant_id",
            table: "visibility_rules");
    }
}
