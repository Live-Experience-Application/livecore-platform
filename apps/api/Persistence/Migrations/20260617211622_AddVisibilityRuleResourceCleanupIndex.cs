using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Restores the documented resource-cleanup index <c>visibility_rules(workspace_id, resource_type,
/// resource_id)</c> (CORE-PERF-004; docs/10_DATABASE_SCHEMA.md). The resource-deletion cleanup path
/// <c>VisibilityRuleRepository.RemoveByResourceAsync</c> removes ALL of a resource's rules — the
/// audience-wide rule and every selected-participant rule, ACROSS the workspace's sessions — when a
/// polymorphic resource is deleted (<c>resource_id</c> is not a foreign key, so the database cannot cascade
/// it; docs/adr/0012-resource-deletion-cascades-dependents.md). Its delete predicate is led by
/// <c>organization_id</c> then <c>workspace_id</c>, <c>resource_type</c>, <c>resource_id</c> — a
/// workspace-wide, session-AGNOSTIC shape.
///
/// CORE-SVIS-001 (<c>AddVisibilityRuleSessionScope</c>) re-led the resource READ index with
/// <c>session_id</c> and dropped the old workspace-led composite, which left this cleanup predicate with no
/// covering index (only the single-column workspace index the foreign key carried). This migration adds the
/// composite back. Because <c>workspace_id</c> is its prefix, EF's foreign-key index convention treats the
/// workspace foreign key as covered, so the separate single-column <c>IX_visibility_rules_workspace_id</c>
/// is dropped in favor of the composite — the exact symmetric reversal of CORE-SVIS-001's swap. Index-only
/// change: no column or row data is touched, and <see cref="Down"/> restores the single-column index, so
/// this is not a data-destroying Down (csv/migration_destructive_down_review.csv unaffected).
/// </summary>
public partial class AddVisibilityRuleResourceCleanupIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_visibility_rules_workspace_id",
            table: "visibility_rules");

        migrationBuilder.CreateIndex(
            name: "ix_visibility_rules_workspace_id_resource_type_resource_id",
            table: "visibility_rules",
            columns: new[] { "workspace_id", "resource_type", "resource_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_visibility_rules_workspace_id_resource_type_resource_id",
            table: "visibility_rules");

        migrationBuilder.CreateIndex(
            name: "IX_visibility_rules_workspace_id",
            table: "visibility_rules",
            column: "workspace_id");
    }
}
