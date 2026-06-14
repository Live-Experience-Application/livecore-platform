using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Enforces ONE active visibility rule per (session, resource, DIMENSION) on the <c>visibility_rules</c>
/// table (CORE-SVIS-002; docs/10_DATABASE_SCHEMA.md). The dimension is either audience-wide
/// (<c>target_participant_id IS NULL</c>) or one selected participant
/// (<c>target_participant_id IS NOT NULL</c>). Before this, two concurrent first-reveals of one resource
/// each inserted a visible rule and a later hide flipped only one, leaving the other Visible as an
/// un-hideable ghost reveal (threats T5/T3 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// Because <c>target_participant_id</c> is nullable and both PostgreSQL and SQLite treat NULLs as DISTINCT
/// in a unique index, a single unique index over the four columns would NOT reject a second audience-wide
/// rule (two NULL targets compare distinct). So the constraint is expressed as TWO FILTERED (partial)
/// unique indexes — the same nullable-uniqueness pattern as
/// <c>templates(organization_id IS NULL / IS NOT NULL)</c>: one over
/// (<c>session_id</c>, <c>resource_type</c>, <c>resource_id</c>) filtered to
/// <c>target_participant_id IS NULL</c> (at most one audience-wide rule), and one over those columns plus
/// <c>target_participant_id</c> filtered to <c>target_participant_id IS NOT NULL</c> (at most one rule per
/// selected participant). The non-unique read index
/// <c>ix_visibility_rules_session_id_resource_type_resource_id</c> (the documented critical index,
/// CORE-SVIS-001) is unchanged — it still backs the "all rules for this resource in this session" read.
///
/// This migration creates only indexes (no data or column change) and is reversible. It assumes the table
/// holds no pre-existing duplicate-dimension rows; CORE-SVIS-001 introduced <c>session_id</c> in the
/// immediately preceding migration, so a v1 deployment has none to conflict with.
/// </summary>
public partial class AddVisibilityRuleDimensionUniqueness : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_visibility_rules_session_resource_audience_dimension",
            table: "visibility_rules",
            columns: new[] { "session_id", "resource_type", "resource_id" },
            unique: true,
            filter: "\"target_participant_id\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "ix_visibility_rules_session_resource_participant_dimension",
            table: "visibility_rules",
            columns: new[] { "session_id", "resource_type", "resource_id", "target_participant_id" },
            unique: true,
            filter: "\"target_participant_id\" IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_visibility_rules_session_resource_audience_dimension",
            table: "visibility_rules");

        migrationBuilder.DropIndex(
            name: "ix_visibility_rules_session_resource_participant_dimension",
            table: "visibility_rules");
    }
}
