using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the optional object-storage artifact coordinates (<c>artifact_bucket</c>, <c>artifact_object_key</c>)
/// to the <c>export_jobs</c> table (CORE-PRIV-003). A completed export's downloadable artifact may live as an
/// object in private storage; recording WHERE it lives lets the data-retention sweep purge the object together
/// with the row when the export is past its retention window (the "completed export artifacts (DB row + the S3
/// object)" acceptance criterion). Both columns are NULLABLE (Core's manifest-only export pipeline writes no
/// blob and leaves them null) and are storage NAMING only — never exported content or a credential (threats
/// T4/T7) — so they need no index (they are never a lookup key, only a purge coordinate).
///
/// <para>
/// This is an additive, expand-only schema change (two nullable columns); the Down() drops them, which is
/// destructive and is acknowledged under the roll-forward-only policy (CORE-DR-004,
/// csv/migration_destructive_down_review.csv; docs/13_SELF_HOSTING_REQUIREMENTS.md).
/// </para>
/// </summary>
/// <inheritdoc />
public partial class AddExportArtifactCoordinates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "artifact_bucket",
            table: "export_jobs",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "artifact_object_key",
            table: "export_jobs",
            type: "character varying(1024)",
            maxLength: 1024,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "artifact_bucket",
            table: "export_jobs");

        migrationBuilder.DropColumn(
            name: "artifact_object_key",
            table: "export_jobs");
    }
}
