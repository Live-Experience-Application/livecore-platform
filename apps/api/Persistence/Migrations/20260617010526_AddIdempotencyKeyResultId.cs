using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the nullable <c>result_id</c> column to the <c>idempotency_keys</c> table (CORE-DX-004). A key may now
/// record the surrogate id of the single resource its request produced, so a retry under the same client
/// <c>Idempotency-Key</c> can RETURN THE ORIGINAL RESULT by re-loading that resource — a create route records
/// the created resource's id and a purchase-verification route records the recorded purchase transaction's id,
/// so a client/network retry cannot double-create a resource or re-run an external verifier
/// (docs/08_API_CONTRACTS.md).
///
/// The column is NULLABLE and additive: it carries no value for the keys recorded by the existing state-
/// idempotent reveal/hide commands (they produce no single addressable resource and re-derive their result
/// from state on a retry), and an existing row needs no backfill. The unique <c>idempotency_keys(scope, key)</c>
/// index that IS the idempotency guarantee is unchanged; <c>result_id</c> is a correlation identifier, never
/// response content (threat T7). Rollback: <see cref="Down"/> drops the column.
/// </summary>
public partial class AddIdempotencyKeyResultId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "result_id",
            table: "idempotency_keys",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "result_id",
            table: "idempotency_keys");
    }
}
