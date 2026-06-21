using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the sealed/locked authoring flag to the <c>visibility_rules</c> table (CORE-VSEAL-001, the
/// "Scheduled and Sealed Visibility" epic, raised by a vertical adopter, ARC-GAP-002). A visibility rule can
/// carry a server-asserted <c>locked</c> flag that makes the governed resource permanently-restricted: while
/// a rule is locked, a reveal/hide/change targeting it is refused fail-closed (the reveal command resolves the
/// locked rule and returns a 409). The flag is an ORTHOGONAL authoring concern, NOT a third
/// <c>visibility</c> state, so this migration only adds a single boolean column — it reshapes no existing
/// index and does not touch the binary <c>visibility</c> column (the recipient resolver and every projection
/// that branches on the binary state are unchanged for an unlocked rule). The column is <c>NOT NULL</c> with a
/// default of <c>false</c>, so every pre-existing row is unlocked and behaves exactly as before
/// (docs/10_DATABASE_SCHEMA.md: authorization-relevant fields are first-class columns, never inside arbitrary
/// JSON). The <c>Down</c> drops the column, fully reversing the change.
/// </summary>
public partial class AddVisibilityRuleLock : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "locked",
            table: "visibility_rules",
            type: "boolean",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "locked",
            table: "visibility_rules");
    }
}
