using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Maps the PostgreSQL system column <c>xmin</c> as an optimistic-concurrency
/// row-version token on the REMAINING mutable aggregates (CORE-CONC-006), the eight
/// that CORE-CONC-001 left without one: <c>content_blocks</c>, <c>entities</c>,
/// <c>entity_types</c>, <c>scenes</c>, <c>assets</c>, <c>subject_entitlements</c>,
/// <c>export_jobs</c> and <c>users</c>.
///
/// <para>
/// Exactly like the <see cref="AddOptimisticConcurrencyTokens"/> migration it
/// follows, this migration is intentionally a NO-OP at the schema level. <c>xmin</c>
/// is a hidden system column every PostgreSQL row already carries (the id of the
/// transaction that last wrote the row), so there is NO column to create: the
/// <c>ALTER TABLE ... ADD COLUMN xmin</c> EF scaffolds by default would in fact fail,
/// because <c>xmin</c> conflicts with a reserved system column name. The token lives
/// purely in the EF model/snapshot (a read-only shadow <c>uint</c> property mapped to
/// the existing <c>xmin</c> column), which is what makes EF append <c>WHERE ... AND
/// xmin = @original</c> to UPDATE/DELETE so a stale write is detected. The model
/// snapshot is updated alongside this migration so the model-drift gate stays green;
/// the database itself needs no change.
/// </para>
/// </summary>
/// <inheritdoc />
public partial class AddOptimisticConcurrencyTokensToRemainingAggregates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty: xmin is a pre-existing PostgreSQL system column, so the
        // concurrency token is mapped in the model only (see the type summary).
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty: there is no xmin column to drop (it is a system
        // column), so unmapping the token is a model-only change.
    }
}
