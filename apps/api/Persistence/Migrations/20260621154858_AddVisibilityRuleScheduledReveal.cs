using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the optional scheduled-reveal time to the <c>visibility_rules</c> table (CORE-VSEAL-002, the
/// "Scheduled and Sealed Visibility" epic, raised by a vertical adopter, ARC-GAP-002). A visibility rule can
/// carry an optional <c>scheduled_reveal_at</c> timestamp; when set on a Hidden rule the resource stays hidden
/// until that time and is then AUTOMATICALLY revealed by the worker's background sweep, which drives the SAME
/// central reveal command as a live host reveal (so the auto-reveal is gated through the Visibility engine and
/// emits the normal session events to exactly the authorized audience). The column is NULLABLE with no default,
/// so every pre-existing row has no schedule and behaves exactly as before (docs/10_DATABASE_SCHEMA.md:
/// authorization-relevant/server-fact fields are first-class columns, never inside arbitrary JSON). It is
/// orthogonal to the binary <c>visibility</c> column and reshapes no existing index.
///
/// A FILTERED (partial) index <c>ix_visibility_rules_scheduled_reveal_at</c> on <c>scheduled_reveal_at</c>,
/// restricted to the rows that actually carry a schedule (<c>scheduled_reveal_at IS NOT NULL</c> — the vast
/// majority do not), backs the worker's periodic due-rule sweep cheaply without bloating the index for every
/// scheduleless rule (the same partial-index technique the rule's dimension-uniqueness indexes use). The
/// <c>Down</c> drops the index and the column, fully reversing the change.
/// </summary>
public partial class AddVisibilityRuleScheduledReveal : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "scheduled_reveal_at",
            table: "visibility_rules",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_visibility_rules_scheduled_reveal_at",
            table: "visibility_rules",
            column: "scheduled_reveal_at",
            filter: "\"scheduled_reveal_at\" IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_visibility_rules_scheduled_reveal_at",
            table: "visibility_rules");

        migrationBuilder.DropColumn(
            name: "scheduled_reveal_at",
            table: "visibility_rules");
    }
}
