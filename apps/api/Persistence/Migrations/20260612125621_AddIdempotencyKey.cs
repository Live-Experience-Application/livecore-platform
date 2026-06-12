using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>idempotency_keys</c> table: the generic RETRY-SAFETY record of the System module
/// (CORE-VIS-004, the reveal command's idempotency store; the System module's first table;
/// csv/database_tables.csv: module System, scope <c>scope</c>, "Retry safety"). A row records that one
/// client request — identified by a generic (<c>scope</c>, <c>key</c>) pair — has been processed, so a
/// retry of the same request is recognized and not executed twice (docs/08_API_CONTRACTS.md).
///
/// The table is generic infrastructure and is NOT tenant-scoped at the row level: there is no
/// <c>organization_id</c> column and no foreign key, because the tenant (and any other context) is
/// folded into the opaque <c>scope</c> partition string the calling feature composes (for the reveal
/// command, <c>reveal:{organizationId}</c>). So the System module stays decoupled from the tenant
/// tables.
///
/// The single documented critical index is the UNIQUE composite index on (<c>scope</c>, <c>key</c>)
/// — <c>idempotency_keys(scope, key)</c> (docs/10_DATABASE_SCHEMA.md). Its uniqueness IS the
/// idempotency guarantee: the same client key within a scope can be recorded only once, so a
/// concurrent or repeated insert is rejected at the database. Both columns are bounded strings.
/// Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddIdempotencyKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "idempotency_keys",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                scope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_idempotency_keys", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_idempotency_keys_scope_key",
            table: "idempotency_keys",
            columns: new[] { "scope", "key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "idempotency_keys");
    }
}
