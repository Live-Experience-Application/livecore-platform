using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>organizations</c> table: the tenant root of the
/// Organizations module (CORE-ID-003, csv/database_tables.csv: scope
/// <c>global</c>, "Tenant root"). The primary key <c>id</c> is the
/// foreign-key target every tenant-scoped table will reference through its
/// own <c>organization_id</c> (docs/10_DATABASE_SCHEMA.md). The unique index
/// on <c>slug</c> enforces one tenant per slug at the database level, so the
/// natural key can never address two organizations (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). Rollback: <see cref="Down"/> drops the
/// table.
/// </summary>
public partial class AddOrganization : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organizations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_organizations", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_organizations_slug",
            table: "organizations",
            column: "slug",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "organizations");
    }
}
