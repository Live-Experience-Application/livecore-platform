using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the worker processing-attempt counter (<c>attempt_count</c>) to the <c>export_jobs</c> table
/// (CORE-RES-002). It records how many times the background worker has attempted to process a job and failed,
/// so the worker can DEAD-LETTER a permanently-failing (poison) job — drive it to the terminal
/// <c>Failed</c> state via <c>ExportJob.Fail()</c> once it reaches the configured maximum — instead of
/// re-attempting it every sweep forever and starving newer work in its batch.
///
/// <para>
/// This is an additive, expand-only schema change (one column). The column is NOT NULL with a server default of
/// 0, so every existing row backfills to "no failed attempts yet" and a freshly created job starts at zero. It
/// carries no content (threat T7) and is never a lookup key, so it needs no index. The Down() drops it, which is
/// destructive and is acknowledged under the roll-forward-only policy (CORE-DR-004,
/// csv/migration_destructive_down_review.csv; docs/13_SELF_HOSTING_REQUIREMENTS.md).
/// </para>
/// </summary>
/// <inheritdoc />
public partial class AddExportJobAttemptCount : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "attempt_count",
            table: "export_jobs",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "attempt_count",
            table: "export_jobs");
    }
}
