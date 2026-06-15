using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Maps the PostgreSQL system column <c>xmin</c> as an optimistic-concurrency row-version token on the
/// <c>workspace_invitations</c> aggregate (CORE-WS-006). The invitation becomes an in-place-updated
/// aggregate for the first time in this story: the accept/redeem command transitions it
/// <c>Pending -&gt; Accepted</c>, so it joins the mutable aggregates CORE-CONC-001/006 already tokened.
///
/// <para>
/// This migration is intentionally a NO-OP at the schema level, exactly like
/// <see cref="AddOptimisticConcurrencyTokens"/>. <c>xmin</c> is a hidden system column every PostgreSQL row
/// already carries (the id of the transaction that last wrote the row), so there is NO column to create:
/// the <c>AddColumn</c> the scaffolder emits would in fact FAIL, because <c>xmin</c> conflicts with a
/// reserved system column name. The token lives purely in the EF model/snapshot (a read-only shadow
/// <c>uint</c> property mapped to the existing <c>xmin</c> column), which is what makes EF append
/// <c>WHERE ... AND xmin = @original</c> to the redeem UPDATE so two concurrent redemptions of one scoped
/// token cannot both grant a membership (the single-use guarantee, threat T6). The model snapshot is
/// updated alongside this migration so the model-drift gate stays green; the database itself needs no
/// change.
/// </para>
/// </summary>
/// <inheritdoc />
public partial class AddWorkspaceInvitationConcurrencyToken : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty: xmin is a pre-existing PostgreSQL system column, so the concurrency token is
        // mapped in the model only (see the type summary).
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty: there is no xmin column to drop (it is a system column), so unmapping the
        // token is a model-only change.
    }
}
