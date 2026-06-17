using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the paged-read covering index <c>audit_logs(organization_id, id)</c>
/// (<c>ix_audit_logs_organization_id_id</c>) — CORE-PERF-007, the "Index the tenant audit-log page read"
/// story of the "Performance and Scalability" epic (docs/10_DATABASE_SCHEMA.md).
///
/// The view-audit-log page read (<c>AuditLogRepository.ListPageByOrganizationAsync</c>, reached live by
/// <c>GET /api/v1/audit-logs</c>) and the full id-ordered read (<c>ListByOrganizationAsync</c>) are
/// <c>WHERE organization_id = X ORDER BY id</c> (the time-ordered UUIDv7 surrogate) with <c>OFFSET</c>/<c>LIMIT</c>.
/// The only declared <c>audit_logs</c> indexes before this story — <c>(organization_id, created_at)</c> and the
/// unique <c>(organization_id, sequence)</c> — lead with the tenant but order by a DIFFERENT column, so neither
/// satisfies the id ordering: PostgreSQL matched the tenant prefix and then SORTED it on every page, an
/// ever-growing cost as the unbounded log grows. This composite leads with <c>organization_id</c> then <c>id</c>,
/// so the equality fixes the tenant and the index already yields rows in <c>id</c> order — the page is
/// index-backed with no full sort, independent of offset depth. It is the organization-scoped analogue of the
/// <c>(workspace_id, id)</c> index every workspace-scoped id-ordered list carries (<c>assets</c>,
/// <c>export_jobs</c>, <c>recaps</c>).
///
/// It is NON-unique: the id is already unique on its own, so this is purely a covering ordering aid, not a
/// constraint. Index-only change: no column or row data is touched, and <see cref="Down"/> drops the index, so
/// this is not a data-destroying Down (csv/migration_destructive_down_review.csv unaffected).
/// </summary>
public partial class AddAuditLogOrganizationIdIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_organization_id_id",
            table: "audit_logs",
            columns: new[] { "organization_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_audit_logs_organization_id_id",
            table: "audit_logs");
    }
}
