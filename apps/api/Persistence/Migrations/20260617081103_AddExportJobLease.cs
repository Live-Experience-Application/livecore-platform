using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the worker job LEASE columns (<c>lease_owner</c>, <c>leased_until</c>) to the <c>export_jobs</c> table
/// (CORE-RES-003). They make export processing safe across multiple worker replicas: a replica ATOMICALLY claims
/// a job by compare-and-swapping its <c>lease_owner</c> and stamping <c>leased_until</c>, so two replicas never
/// both do the full work of one export, and a replica that crashes mid-job lets its lease expire so the next
/// sweep reclaims and finishes the job. Correctness rests on the claim rather than solely on the downstream
/// unique <c>export_manifests(export_job_id)</c> index.
///
/// <para>
/// This is an additive, expand-only schema change (two NULLABLE columns, no default, no index). An unleased job
/// simply has both null, so every existing row backfills to "unleased" and a freshly created job starts unleased.
/// The columns are operational metadata — the owner is an opaque per-replica id and the expiry is compared in
/// memory by the worker — never content (threat T7) and never a lookup key, so neither is indexed. The Down()
/// drops them, which is destructive and is acknowledged under the roll-forward-only policy (CORE-DR-004,
/// csv/migration_destructive_down_review.csv; docs/13_SELF_HOSTING_REQUIREMENTS.md).
/// </para>
/// </summary>
/// <inheritdoc />
public partial class AddExportJobLease : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "lease_owner",
            table: "export_jobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "leased_until",
            table: "export_jobs",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "lease_owner",
            table: "export_jobs");

        migrationBuilder.DropColumn(
            name: "leased_until",
            table: "export_jobs");
    }
}
