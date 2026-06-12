using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Makes the <c>audit_logs.new_state</c> column NULLABLE — the schema change of the generic append-only
/// audit log (CORE-AUD-001). CORE-VIS-006 created the table with a required <c>new_state</c> because its
/// only producer, the visibility reveal command, always records a resulting visibility state. The
/// generic creation API (<see cref="LiveCore.Api.Audit.AuditLogEntry.Create"/>) records ANY
/// security-relevant action, and a generic action is not necessarily a state transition (a session
/// start or a member invite has no before/after state), so the resulting-state column becomes optional.
/// No other column changes; the visibility producer still always writes a state, so existing rows are
/// unaffected.
///
/// Rollback: <see cref="Down"/> restores the NOT NULL constraint (with an empty-string default for any
/// row written without a state while the column was nullable). Because a stateless generic entry would
/// then violate NOT NULL, the rollback is only safe before any stateless action is recorded; otherwise
/// the forward fix is to keep the column nullable (docs/10_DATABASE_SCHEMA.md migration guidance).
/// </summary>
public partial class MakeAuditLogStateOptional : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "new_state",
            table: "audit_logs",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(32)",
            oldMaxLength: 32);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "new_state",
            table: "audit_logs",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "character varying(32)",
            oldMaxLength: 32,
            oldNullable: true);
    }
}
